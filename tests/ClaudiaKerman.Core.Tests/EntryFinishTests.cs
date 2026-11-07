// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// reentry's own choice of its engines (#824, ADR 0020), by the real predictor on the bodies of the recorded snapshot, with stock's Mk16 numbers:
    /// a craft of the flight's shape (pod and its chute, shield, Decoupler.1, a core tank with radial tanks carrying stage 2's chutes, an engine).
    /// </summary>
    public class EntryFinishTests
    {
        private const uint Pod = 1, PodChute = 2, Shield = 3, Dec = 4, Tank = 5, Radial = 6, Engine = 7;

        private static SnapshotPart Part(uint id, uint? parent, string name, double massT, int stage, params string[] tags)
        {
            return new SnapshotPart { Id = id, ParentId = parent, Name = name, Title = name, MassT = massT, Stage = stage, Tags = tags.ToList() };
        }

        private static SnapshotPart Chute(uint id, uint parent, int stage)
        {
            var part = Part(id, parent, "parachuteSingle", 0.1, stage, "parachute");
            part.Chute = ReentrySim.Mk16();
            return part;
        }

        /// <summary>The craft at <paramref name="body"/>, of total mass <paramref name="massT"/> with a capsule (pod and chute) of <paramref name="capsuleT"/>.</summary>
        private static GameSnapshot Flight(string body, double massT, double capsuleT = 1.4, int radialChutes = 4, bool podChute = true, bool engine = true, bool fuelled = true)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Orbit.Body = body;
            var parts = snapshot.Craft.Parts;
            parts.Clear();
            parts.Add(Part(Pod, null, "mk1pod.v2", capsuleT - (podChute ? 0.1 : 0), 0, "command", "crew_seat"));
            if (podChute) parts.Add(Chute(PodChute, Pod, 0));
            parts.Add(Part(Shield, Pod, "HeatShield1", 0, -1, "heat_shield", "decoupler"));
            parts.Add(Part(Dec, Shield, "Decoupler.1", 0, 3, "decoupler"));
            parts.Add(Part(Tank, Dec, "fuelTank", 0, 3));
            parts.Add(Part(Radial, Tank, "radialTank", 0, 3));
            for (var i = 0; i < radialChutes; i++) parts.Add(Chute((uint)(100 + i), Radial, 2));
            if (engine)
            {
                parts.Add(Part(Engine, Tank, "toroidalAerospike", 0, 3, "engine"));
                parts.Last().EngineFuelled = fuelled;
            }
            parts.Single(p => p.Id == Tank).MassT = massT - parts.Sum(p => p.MassT);
            snapshot.Craft.MassTonnes = massT;
            return snapshot;
        }

        private static string Uid(uint id)
        {
            return "uid" + id;
        }

        private static IDictionary<string, object> Ask(GameSnapshot snapshot, IList<PlannedSequence> legs, bool chutes = true, bool drop = true)
        {
            return BlockCalls.EntryFinishCall(new Dictionary<string, object> { { "home", Uid(Pod) }, { "chutes", chutes }, { "drop", drop } }, snapshot, legs, 0, "", Uid);
        }

        private static readonly IList<PlannedSequence> DunaThenKerbin = new[]
        {
            new PlannedSequence { Name = "Land on Duna", DeltaV = 100 }, new PlannedSequence { Name = "Ascend from Duna", DeltaV = 1336 }, new PlannedSequence { Name = "Return to Kerbin", DeltaV = 623 },
        };
        private static readonly IList<PlannedSequence> DunaLast = new[] { new PlannedSequence { Name = "Land on Duna", DeltaV = 100 } };
        private static readonly IList<PlannedSequence> KerbinReturn = new[] { new PlannedSequence { Name = "Return to Kerbin", DeltaV = 623 } };

        private static double Speed(IDictionary<string, object> answer)
        {
            return (double)answer["speed"];
        }

        /// <summary>The total mass, by bisection, at which <paramref name="craft"/>'s chutes predict <paramref name="speed"/> m/s, as a lander on its one group.</summary>
        private static double MassFor(double speed, Func<double, GameSnapshot> craft)
        {
            double low = 0.3, high = 40;
            for (var i = 0; i < 40; i++)
            {
                var mid = (low + high) / 2;
                if (ChuteGroups.ForEntryNow(craft(mid), false).TouchdownSpeed > speed) high = mid;
                else low = mid;
            }
            return low;
        }

        // ---- The worked examples ----

        [Fact]
        public void The_Duna_flight_at_Duna_before_Kerbin_lands_on_its_engines_under_stage_2s_chutes()
        {
            var snapshot = Flight("Duna", 3.9);

            var answer = Ask(snapshot, DunaThenKerbin);

            Assert.Equal("", answer["error"]);
            Assert.Equal(true, answer["powered"]);
            Assert.InRange(Speed(answer), 10.01, 40);
            Assert.Equal(new object[] { Uid(Engine) }, (List<object>)answer["engines"]);
            // Nothing drops at Duna, so the two predictions are of the same craft.
            Assert.Equal(Speed(answer), (double)answer["kept_speed"], 6);
            // And the group is stage 2's: the pod's stays for Kerbin.
            Assert.Equal(new[] { 2, 2, 2, 2 }, ChuteGroups.ForEntryNow(snapshot, false).Chutes.Select(c => c.Stage));
        }

        [Fact]
        public void The_same_craft_as_Duna_Last_Entry_drops_the_stack_if_its_capsule_lands_at_or_under_10()
        {
            var light = Flight("Duna", 3.9, 0.4);

            var answer = Ask(light, DunaLast);

            Assert.Equal(false, answer["powered"]);
            Assert.InRange(Speed(answer), 0.1, 10);
            Assert.Equal(-1.0, (double)answer["kept_speed"]);
            Assert.Empty((List<object>)answer["engines"]);
        }

        [Fact]
        public void The_same_craft_as_Duna_Last_Entry_keeps_everything_if_its_capsule_lands_over_10_and_predicts_again_for_the_kept_craft()
        {
            var heavy = Flight("Duna", 3.9, 2.5);

            var answer = Ask(heavy, DunaLast);

            Assert.Equal(true, answer["powered"]);
            Assert.True(Speed(answer) > EntryFinish.EngineThreshold, "capsule at " + Speed(answer));
            // The second prediction is the kept craft's: all 3.9 t under all five chutes, not the capsule's 2.5 t under one.
            var kept = (double)answer["kept_speed"];
            Assert.Equal(ChuteGroups.ForEntryNow(heavy, true).TouchdownSpeed, kept, 6);
            Assert.NotEqual(Speed(answer), kept, 3);
            var capsuleAlone = ChuteGroups.ForEntryNow(heavy, true, new uint[] { Tank, Radial, Engine, 100, 101, 102, 103 });
            Assert.Equal(capsuleAlone.TouchdownSpeed, Speed(answer), 6);
        }

        [Fact]
        public void A_capsule_on_its_pod_chute_home_from_Kerbin_orbit_is_on_chutes_and_the_stack_drops()
        {
            var snapshot = Flight("Kerbin", 3.9, 1.4);

            var answer = Ask(snapshot, KerbinReturn);

            Assert.Equal(false, answer["powered"]);
            Assert.InRange(Speed(answer), 5, 10);
            var drop = BlockCalls.EntryDropCall(new Dictionary<string, object> { { "home", Uid(Pod) } }, snapshot, KerbinReturn, 0, "", Uid);
            Assert.Equal(new object[] { Uid(Dec) }, (List<object>)drop["decouplers"]);
        }

        [Fact]
        public void A_lander_with_chutes_none_and_an_engine_lands_on_it()
        {
            var answer = Ask(Flight("Duna", 3.9), DunaThenKerbin, chutes: false);

            Assert.Equal(true, answer["powered"]);
            Assert.True(Speed(answer) > 15);
            Assert.Equal(Speed(answer), (double)answer["kept_speed"], 6);
        }

        [Fact]
        public void A_lander_predicting_12_with_an_engine_lands_on_it_and_with_none_stays_on_chutes()
        {
            var mass = MassFor(12, m => Flight("Duna", m));

            var engine = Ask(Flight("Duna", mass), DunaThenKerbin);
            Assert.InRange(Speed(engine), 11.9, 12.1);
            Assert.Equal(true, engine["powered"]);

            var none = Ask(Flight("Duna", mass, engine: false), DunaThenKerbin);
            Assert.InRange(Speed(none), 11.9, 12.1);
            Assert.Equal(false, none["powered"]);
        }

        // ---- The threshold, and what counts as an engine ----

        [Theory]
        [InlineData(10.0, false)]
        [InlineData(10.01, true)]
        [InlineData(9.99, false)]
        [InlineData(15.01, true)]
        public void Exactly_10_stays_on_chutes_and_just_over_uses_the_engines(double speed, bool engines)
        {
            Assert.Equal(engines, EntryFinish.NeedsEngines(new EntryChutes { TouchdownSpeed = speed }));
        }

        // ---- Kerbin, Eve and Laythe stay on chutes up to the failure limit (#840) ----

        [Theory]
        [InlineData("Kerbin", 15.0, false)]
        [InlineData("Kerbin", 15.01, true)]
        [InlineData("Kerbin", 12.0, false)]
        [InlineData("Eve", 12.0, false)]
        [InlineData("Laythe", 12.0, false)]
        [InlineData("Laythe", 15.01, true)]
        [InlineData("Duna", 12.0, true)]
        [InlineData("Duna", 10.0, false)]
        [InlineData("Mars", 12.0, true)]
        [InlineData("", 12.0, true)]
        public void The_threshold_is_15_on_Kerbin_Eve_and_Laythe_and_10_everywhere_else(string body, double speed, bool engines)
        {
            Assert.Equal(engines, EntryFinish.NeedsEngines(new EntryChutes { Body = body, TouchdownSpeed = speed }));
        }

        /// <summary>The capsule's mass, by bisection, at which its pod chute alone predicts <paramref name="speed"/> m/s with the stack dropped.</summary>
        private static double CapsuleFor(string body, double speed)
        {
            var gone = new uint[] { Dec, Tank, Radial, Engine, 100, 101, 102, 103 };
            double low = 0.2, high = 20;
            for (var i = 0; i < 40; i++)
            {
                var mid = (low + high) / 2;
                if (ChuteGroups.ForEntryNow(Flight(body, 3.9 + mid, mid), true, gone).TouchdownSpeed > speed) high = mid;
                else low = mid;
            }
            return low;
        }

        private static IList<PlannedSequence> LandOn(string body)
        {
            return new[] { new PlannedSequence { Name = "Land on " + body, DeltaV = 100 } };
        }

        [Theory]
        [InlineData("Kerbin", 12, false)]
        [InlineData("Kerbin", 14.9, false)]
        [InlineData("Kerbin", 16, true)]
        [InlineData("Duna", 12, true)]
        [InlineData("Eve", 12, false)]
        [InlineData("Laythe", 12, false)]
        [InlineData("Laythe", 16, true)]
        public void Decide_by_the_worked_examples_with_a_fuelled_engine(string body, double predicted, bool powered)
        {
            var capsule = CapsuleFor(body, predicted);
            var answer = Ask(Flight(body, 3.9 + capsule, capsule), LandOn(body));

            Assert.InRange(Speed(answer), predicted - 0.2, predicted + 0.2);
            Assert.Equal(powered, answer["powered"]);
        }

        [Fact]
        public void Kerbin_over_15_with_no_engine_stays_on_chutes()
        {
            var capsule = CapsuleFor("Kerbin", 16);
            var answer = Ask(Flight("Kerbin", 3.9 + capsule, capsule, engine: false), KerbinReturn);
            Assert.Equal(false, answer["powered"]);
        }

        [Theory]
        [InlineData("Kerbin", false)]
        [InlineData("Duna", true)]
        public void The_Craft_Checks_powered_finish_agrees_with_Decide_at_12(string body, bool powered)
        {
            // Duna before Kerbin drops nothing, so the walk and the block predict the same craft.
            var legs = body == "Kerbin" ? KerbinReturn : DunaThenKerbin;
            double low = 0.2, high = 20;
            for (var i = 0; i < 40; i++)
            {
                var mid = (low + high) / 2;
                var walked = ChuteGroups.Of(Flight(body, 3.9 + mid, mid), legs, 0, false, null).Entries[0].TouchdownSpeed;
                if (walked > 12) high = mid;
                else low = mid;
            }
            var at = Flight(body, 3.9 + low, low);
            var groups = ChuteGroups.Of(at, legs, 0, false, null);

            Assert.InRange(groups.Entries[0].TouchdownSpeed, 10.5, 15);
            Assert.Equal(powered, groups.Entries[0].EngineFinish);
            Assert.Equal(powered, groups.Describe().Contains("powered finish"));
            Assert.Equal(powered, Ask(at, legs)["powered"]);
        }

        [Fact]
        public void No_prediction_stays_on_the_chutes_it_has_and_with_none_it_needs_the_engines()
        {
            var entry = new EntryChutes { TouchdownSpeed = -1 };
            Assert.True(EntryFinish.NeedsEngines(entry));
            entry.Chutes.Add(new GroupChute());
            Assert.False(EntryFinish.NeedsEngines(entry));
        }

        [Fact]
        public void An_engine_with_no_fuel_by_stocks_flow_is_the_chute_path_whatever_the_speed()
        {
            var answer = Ask(Flight("Duna", 3.9, fuelled: false), DunaThenKerbin);

            Assert.Equal(false, answer["powered"]);
            Assert.True(Speed(answer) > EntryFinish.EngineThreshold);
            Assert.Empty((List<object>)answer["engines"]);
            Assert.EndsWith("no engine with fuel", (string)answer["why"]);
        }

        [Fact]
        public void The_highest_stage_with_fuel_is_chosen_all_of_it_whatever_the_current_stage_and_the_rest_are_spare()
        {
            var snapshot = Flight("Duna", 3.9);
            snapshot.Craft.Stage = 0;
            var parts = snapshot.Craft.Parts;
            parts.Add(Part(20, Tank, "dry", 0, 6, "engine"));
            parts.Add(Part(21, Tank, "cluster1", 0, 4, "engine"));
            parts.Add(Part(22, Tank, "cluster2", 0, 4, "engine"));
            parts.Add(Part(23, Tank, "noStaging", 0, -1, "engine"));
            foreach (var part in parts.Where(p => p.Id >= 21 && p.Id <= 23)) part.EngineFuelled = true;

            var answer = Ask(snapshot, DunaThenKerbin);

            Assert.Equal(new object[] { "uid21", "uid22" }, (List<object>)answer["engines"]);
            Assert.Equal(new object[] { Uid(Engine), "uid23" }, (List<object>)answer["spare"]);
        }

        [Fact]
        public void Separate_none_predicts_with_no_drop()
        {
            var heavy = Flight("Duna", 3.9, 2.5);

            var answer = Ask(heavy, DunaLast, drop: false);

            Assert.Equal(ChuteGroups.ForEntryNow(heavy, true).TouchdownSpeed, Speed(answer), 6);
        }

        [Fact]
        public void The_log_line_gives_the_decision_the_speeds_and_the_engine()
        {
            var why = (string)Ask(Flight("Duna", 3.9), DunaThenKerbin)["why"];
            Assert.StartsWith("engines, keeping everything: ", why);
            Assert.Contains(" m/s as kept, on toroidalAerospike", why);
            Assert.StartsWith("chutes, ", (string)Ask(Flight("Kerbin", 3.9), KerbinReturn)["why"]);
            Assert.Equal("there is no Game Snapshot of the craft", BlockCalls.EntryFinishCall(new Dictionary<string, object>(), null, null, 0, "", Uid)["error"]);
        }

        // ---- The highest stage that can stop the fall (#842) ----

        /// <summary>A fuelled engine in <paramref name="stage"/> of <paramref name="vac"/> kN in vacuum and <paramref name="asl"/> at 1 atm; the Duna craft of 3.9 t weighs 11.5 kN there and needs 17.2.</summary>
        private static GameSnapshot WithEngines(string body, params double[][] engines)
        {
            var snapshot = Flight(body, 3.9, engine: false);
            uint id = 40;
            foreach (var e in engines)
            {
                var part = Part(id++, Tank, "engine" + (id - 1), 0, (int)e[0], "engine");
                part.EngineFuelled = true;
                part.ThrustVacKn = e[1];
                part.ThrustAslKn = e[2];
                part.IspVac = 300;
                snapshot.Craft.Parts.Add(part);
            }
            return snapshot;
        }

        private static IDictionary<string, object> Chosen(GameSnapshot snapshot)
        {
            return Ask(snapshot, DunaThenKerbin, chutes: false);
        }

        [Fact]
        public void An_ion_in_the_top_stage_gives_way_to_the_Terrier_below_that_can_stop_the_fall()
        {
            var answer = Chosen(WithEngines("Duna", new double[] { 3, 2, 2 }, new double[] { 1, 60, 50 }));

            Assert.Equal(new object[] { "uid41" }, (List<object>)answer["engines"]);
            Assert.Equal(new object[] { "uid40" }, (List<object>)answer["spare"]);
            Assert.Contains("stage 1, the highest that can stop the fall: stage 3 gives 2 kN at the surface, needing 17.2 kN", (string)answer["why"]);
        }

        [Fact]
        public void Two_able_stages_land_on_the_higher_and_say_nothing_of_it()
        {
            var answer = Chosen(WithEngines("Duna", new double[] { 3, 60, 50 }, new double[] { 1, 60, 50 }));

            Assert.Equal(new object[] { "uid40" }, (List<object>)answer["engines"]);
            Assert.Equal(new object[] { "uid41" }, (List<object>)answer["spare"]);
            Assert.EndsWith("on engine40", (string)answer["why"]);
        }

        [Fact]
        public void With_no_stage_able_the_one_with_the_most_thrust_and_the_whole_cluster_lights()
        {
            var answer = Chosen(WithEngines("Duna", new double[] { 3, 2, 2 }, new double[] { 1, 6, 5 }, new double[] { 1, 6, 5 }));

            Assert.Equal(new object[] { "uid41", "uid42" }, (List<object>)answer["engines"]);
            Assert.Equal(new object[] { "uid40" }, (List<object>)answer["spare"]);
            Assert.Contains("stage 1, none can stop the fall (needs 17.2 kN at the surface), so the most thrust: ", (string)answer["why"]);
        }

        [Fact]
        public void One_engine_that_cannot_stop_the_fall_is_still_the_one_chosen()
        {
            var answer = Chosen(WithEngines("Duna", new double[] { 3, 2, 2 }));

            Assert.Equal(new object[] { "uid40" }, (List<object>)answer["engines"]);
            Assert.Empty((List<object>)answer["spare"]);
            Assert.Contains("stage 3, none can stop the fall", (string)answer["why"]);
        }

        [Fact]
        public void A_body_the_snapshot_lacks_keeps_the_highest_stage_and_says_so()
        {
            var snapshot = WithEngines("Duna", new double[] { 3, 2, 2 }, new double[] { 1, 60, 50 });
            string note;

            Assert.Equal(3, EntryFinish.Choose(snapshot, "Nowhere", snapshot.Craft.Parts.Where(p => p.EngineFuelled).ToList(), out note));
            Assert.Contains("no body Nowhere", note);
        }

        [Fact]
        public void Thrust_is_judged_at_the_bodys_surface_so_a_vacuum_engine_at_Kerbins_sea_level_counts_for_its_low_thrust()
        {
            // 3.9 t on Kerbin weighs 38 kN and needs 57 kN. The vacuum engine gives 80 kN in vacuum but 20 at 1 atm; the sea-level engine 60.
            var snapshot = WithEngines("Kerbin", new double[] { 3, 80, 20 }, new double[] { 1, 70, 60 });
            var fuelled = snapshot.Craft.Parts.Where(p => p.EngineFuelled).ToList();
            string note;

            Assert.Equal(1, EntryFinish.Choose(snapshot, "Kerbin", fuelled, out note));
            Assert.Contains("stage 3 gives 20 kN", note);
            // The same engines at Duna's thin air: the vacuum engine's 80 kN is nearly all there, so the higher stage stands.
            Assert.Equal(3, EntryFinish.Choose(snapshot, "Duna", fuelled, out note));
        }

        [Fact]
        public void A_throttle_locked_engine_is_never_chosen()
        {
            // The adapter leaves a throttle-locked engine unfuelled, so it is neither chosen nor a spare, however strong.
            var snapshot = WithEngines("Duna", new double[] { 3, 500, 500 }, new double[] { 1, 60, 50 });
            snapshot.Craft.Parts.Single(p => p.Name == "engine40").EngineFuelled = false;

            var answer = Chosen(snapshot);

            Assert.Equal(new object[] { "uid41" }, (List<object>)answer["engines"]);
            Assert.Empty((List<object>)answer["spare"]);
        }

        // ---- The Margin Check and the Craft Check ----

        private static GameSnapshot Budgeted(double massT, bool engine)
        {
            var snapshot = Flight("Duna", massT, engine: engine);
            snapshot.DeltaVSimulator = true;
            snapshot.Stages.Clear();
            snapshot.Stages.Add(new SnapshotStage { Number = 3, DeltaVVac = 3338, StartMassT = massT, EndMassT = massT / 3 });
            return snapshot;
        }

        [Fact]
        public void Over_10_with_an_engine_the_budget_carries_the_finish_and_under_15_there_is_no_finding()
        {
            var mass = MassFor(12, m => Flight("Duna", m));
            var snapshot = Budgeted(mass, true);
            var groups = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null);
            var duna = groups.Entries[0];
            Assert.InRange(duna.TouchdownSpeed, 10.01, 15);
            Assert.True(duna.EngineFinish);
            Assert.False(duna.PoweredFinish);
            // The card's Chutes line.
            Assert.Contains("powered finish", groups.Describe());

            var with = MarginCheck.Of(snapshot, DunaThenKerbin, 0, 10, false, null, groups, "", 0);
            var without = MarginCheck.Of(snapshot, DunaThenKerbin, 0, 10, false, null, null, "", 0);

            Assert.Equal(without.Margin.RestOfPlan + duna.TouchdownSpeed, with.Margin.RestOfPlan, 6);
            Assert.DoesNotContain(with.Findings, f => f.Code == "chutes_short_for_landing" || f.Code == CraftCheckRules.ChuteGroupTooFast || f.Code == "chutes_thin_for_landing");
        }

        [Fact]
        public void Over_10_with_no_engine_nothing_is_budgeted_and_there_is_no_finding_under_15()
        {
            var mass = MassFor(12, m => Flight("Duna", m));
            var snapshot = Budgeted(mass, false);
            var groups = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null);
            Assert.False(groups.Entries[0].EngineFinish);
            Assert.DoesNotContain("powered finish", groups.Describe());

            var with = MarginCheck.Of(snapshot, DunaThenKerbin, 0, 10, false, null, groups, "", 0);
            var without = MarginCheck.Of(snapshot, DunaThenKerbin, 0, 10, false, null, null, "", 0);

            Assert.Equal(without.Margin.RestOfPlan, with.Margin.RestOfPlan, 6);
        }

        [Fact]
        public void An_Entry_that_keeps_its_stack_flies_with_that_stacks_chutes_in_the_checks_walk_and_the_next_keeps_its_own()
        {
            // Two shields: at Duna the outer one's stack, with the engine and stage 2's chutes, would drop. The pod's chute alone is far too weak, so it keeps them.
            var snapshot = Flight("Duna", 12, 1.4);
            var parts = snapshot.Craft.Parts;
            parts.Add(Part(30, Tank, "shield2", 0, -1, "heat_shield", "decoupler"));
            parts.Single(p => p.Id == Dec).ParentId = 30;
            parts.Add(Part(31, Shield, "dec1", 0, 4, "decoupler"));
            parts.Add(Part(32, 31, "ascentTank", 0, 4));
            parts.Single(p => p.Id == 30).ParentId = 32;
            parts.Single(p => p.Id == Tank).MassT = 8;

            var groups = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null);

            Assert.True(groups.Entries[0].EngineFinish);
            Assert.Equal(new[] { 2, 2, 2, 2 }, groups.Entries[0].Chutes.Select(c => c.Stage));
            Assert.Equal(new[] { 0 }, groups.Return.Chutes.Select(c => c.Stage));
            Assert.DoesNotContain(MarginCheck.ChuteFindings(snapshot, DunaThenKerbin, groups, ""), f => f.Explanation.Contains("no Chute Group"));
        }

        [Fact]
        public void Chutes_in_one_stage_over_two_Entries_stay_a_Blocker_though_the_first_lands_on_its_engines()
        {
            // The first Entry opens that group under its engines all the same, so the Kerbin return has none (#826's case).
            var snapshot = Budgeted(3.9, true);
            snapshot.Craft.Parts.RemoveAll(p => p.Id >= 100 && p.Id < 110);
            Assert.Equal(true, Ask(snapshot, DunaThenKerbin)["powered"]);

            var groups = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null);
            Assert.Empty(groups.Return.Chutes);

            var finding = Assert.Single(MarginCheck.ChuteFindings(snapshot, DunaThenKerbin, groups, ""), f => f.Code == "chutes_short_for_landing" && f.Explanation.Contains("Kerbin"));
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
        }

        [Fact]
        public void A_Kerbin_return_too_fast_on_its_chutes_stays_a_Blocker_though_in_flight_the_engines_would_finish_it()
        {
            var snapshot = Flight("Kerbin", 14, 9, radialChutes: 0);

            var groups = ChuteGroups.Of(snapshot, KerbinReturn, 0, false, null);

            Assert.True(groups.ReturnUncovered);
            Assert.Single(MarginCheck.ChuteFindings(snapshot, KerbinReturn, groups, ""), f => f.Code == "chutes_short_for_landing");
            Assert.Equal(true, Ask(snapshot, KerbinReturn)["powered"]);
        }
    }
}
