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
    /// A landing always drops everything below the landing legs (#940, ADR 0020), on a craft of the shape of flight m20261108-220659-ac85's Duna ship: the pod over its
    /// shield, the lander (tank, legs, chutes and an aerospike) over shield 2, and the launch stack's tank and Mainsail below that.
    /// </summary>
    public class EntryFinishLegsTests
    {
        private const uint Pod = 1, PodChute = 2, InnerShield = 3, InnerDecoupler = 4, LanderTank = 20, Leg = 21, Aerospike = 25, Ring = 26, OuterShield = 27, StackDecoupler = 28, StackTank = 29, Mainsail = 30;

        private static SnapshotPart Part(uint id, uint? parent, string name, double massT, int stage, params string[] tags)
        {
            return new SnapshotPart { Id = id, ParentId = parent, Name = name, Title = name, MassT = massT, Stage = stage, Tags = tags.ToList() };
        }

        private static SnapshotPart Fuelled(SnapshotPart engine, double thrustKn)
        {
            engine.EngineFuelled = true;
            engine.ThrustVacKn = thrustKn;
            engine.ThrustAslKn = thrustKn;
            engine.IspVac = 300;
            return engine;
        }

        private static SnapshotPart Chute(uint id, uint parent, int stage)
        {
            var part = Part(id, parent, "parachuteSingle", 0.1, stage, "parachute");
            part.Chute = ReentrySim.Mk16();
            return part;
        }

        /// <summary>The ship at Duna, the lander <paramref name="landerT"/> t in all above shield 2, the stack <paramref name="stackT"/> t below it.</summary>
        private static GameSnapshot Ship(double landerT = 3.9, double stackT = 50, bool legs = true, bool landerEngine = true, string body = "Duna")
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Orbit.Body = body;
            var parts = snapshot.Craft.Parts;
            parts.Clear();
            parts.Add(Part(Pod, null, "mk1pod.v2", 0.3, -1, "command", "crew_seat"));
            parts.Add(Chute(PodChute, Pod, 0));
            parts.Add(Part(InnerShield, Pod, "HeatShield1", 0.1, -1, "heat_shield", "decoupler"));
            parts.Add(Part(InnerDecoupler, InnerShield, "Decoupler.1", 0.05, 1, "decoupler"));
            parts.Add(Part(LanderTank, InnerDecoupler, "fuelTank", 0, 1));
            if (legs)
                for (uint i = 0; i < 4; i++) parts.Add(Part(Leg + i, LanderTank, "landingLeg1", 0.05, 1, "landing_leg"));
            for (uint i = 0; i < 4; i++) parts.Add(Chute(100 + i, LanderTank, 2));
            if (landerEngine) parts.Add(Fuelled(Part(Aerospike, LanderTank, "toroidalAerospike", 0.2, 5, "engine"), 60));
            parts.Add(Part(Ring, landerEngine ? Aerospike : LanderTank, "Decoupler.1", 0.05, 5, "decoupler"));
            parts.Add(Part(OuterShield, Ring, "HeatShield1", 0.3, 5, "heat_shield", "decoupler"));
            parts.Add(Part(StackDecoupler, OuterShield, "Decoupler.1", 0.04, 6, "decoupler"));
            parts.Add(Part(StackTank, StackDecoupler, "fuelTank.long", stackT - 3.04, 6));
            parts.Add(Fuelled(Part(Mainsail, StackTank, "liquidEngine1-2", 3, 6, "engine"), 1500));
            parts.Single(p => p.Id == LanderTank).MassT = landerT - parts.Where(p => !Below(p.Id)).Sum(p => p.MassT);
            snapshot.Craft.MassTonnes = parts.Sum(p => p.MassT);
            return snapshot;
        }

        private static bool Below(uint id)
        {
            return id == StackDecoupler || id == StackTank || id == Mainsail;
        }

        /// <summary><paramref name="snapshot"/> as it reads once the stack below shield 2 has dropped, its mass included.</summary>
        private static GameSnapshot WithoutTheStack(GameSnapshot snapshot)
        {
            snapshot.Craft.Parts.RemoveAll(p => Below(p.Id));
            snapshot.Craft.MassTonnes = snapshot.Craft.Parts.Sum(p => p.MassT);
            return snapshot;
        }

        private static string Uid(uint id)
        {
            return "uid" + id;
        }

        private static IDictionary<string, object> Ask(GameSnapshot snapshot, IList<PlannedSequence> legs = null, bool drop = true, uint? core = null)
        {
            var ask = new Dictionary<string, object> { { "home", Uid(Pod) }, { "chutes", true }, { "drop", drop } };
            if (core.HasValue) ask["core"] = Uid(core.Value);
            return BlockCalls.EntryFinishCall(ask, snapshot, legs ?? DunaThenKerbin, 0, "", Uid);
        }

        private static readonly IList<PlannedSequence> DunaThenKerbin = new[]
        {
            new PlannedSequence { Name = "Land on Duna", DeltaV = 100 }, new PlannedSequence { Name = "Ascend from Duna", DeltaV = 1336 }, new PlannedSequence { Name = "Return to Kerbin", DeltaV = 623 },
        };

        private static List<object> Engines(IDictionary<string, object> answer)
        {
            return (List<object>)answer["engines"];
        }

        private static double Speed(IDictionary<string, object> answer)
        {
            return (double)answer["speed"];
        }

        private static EntryStack Stack(GameSnapshot snapshot)
        {
            return EntryStack.Of(snapshot.Craft.Parts, Pod, 2);
        }

        // ---- The decision ----

        [Fact]
        public void Legs_above_the_shield_over_the_threshold_land_on_the_engine_that_stays_and_drop_the_stack()
        {
            var answer = Ask(Ship());

            Assert.Equal("", answer["error"]);
            Assert.Equal(true, answer["powered"]);
            Assert.Equal(false, answer["keeps_stack"]);
            Assert.InRange(Speed(answer), 10.01, 15);
            Assert.Equal(new object[] { Uid(Aerospike) }, Engines(answer));
            Assert.Empty((List<object>)answer["spare"]);
            // Both predictions are of the craft that stays: nothing else is left to keep.
            Assert.Equal(Speed(answer), (double)answer["kept_speed"], 6);
        }

        [Fact]
        public void Legs_above_the_shield_under_the_threshold_stay_on_their_chutes_and_drop_the_stack()
        {
            var answer = Ask(Ship(landerT: 1.5));

            Assert.Equal(false, answer["powered"]);
            Assert.Equal(false, answer["keeps_stack"]);
            Assert.InRange(Speed(answer), 0.1, 10);
            Assert.Empty(Engines(answer));
            Assert.Equal(-1.0, (double)answer["kept_speed"]);
        }

        [Fact]
        public void The_Mainsail_below_the_shield_is_never_chosen_or_spare_while_an_aerospike_above_it_can_be()
        {
            var snapshot = Ship();
            Assert.DoesNotContain(Uid(Mainsail), Engines(Ask(snapshot)));
            Assert.DoesNotContain(Uid(Mainsail), (List<object>)Ask(snapshot)["spare"]);
        }

        [Fact]
        public void Legs_above_the_shield_with_no_engine_staying_take_the_chute_path_though_the_Mainsail_below_has_fuel()
        {
            var answer = Ask(Ship(landerEngine: false));

            Assert.Equal(false, answer["powered"]);
            Assert.InRange(Speed(answer), 10.01, 40);
            Assert.Contains("no engine with fuel", (string)answer["why"]);
        }

        [Fact]
        public void An_engine_that_stays_with_its_propellant_only_in_the_stack_counts_as_no_engine()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.Single(p => p.Id == Aerospike).EnginePropellants.Add("LiquidFuel");
            snapshot.Craft.Parts.Single(p => p.Id == StackTank).Resources.Add(new SnapshotPartResource { Name = "LiquidFuel", Amount = 100, Density = 0.005 });
            // A pod's charge: the craft that stays reads as one with its resources read.
            snapshot.Craft.Parts.Single(p => p.Id == Pod).Resources.Add(new SnapshotPartResource { Name = "ElectricCharge", Amount = 50, Density = 0 });

            Assert.Equal(false, Ask(snapshot)["powered"]);
        }

        [Fact]
        public void The_stage_it_lights_is_judged_by_the_weight_of_the_craft_that_stays()
        {
            var snapshot = Ship(landerEngine: false);
            var parts = snapshot.Craft.Parts;
            parts.Add(Fuelled(Part(40, LanderTank, "weaker", 0.1, 5, "engine"), 60));
            parts.Add(Fuelled(Part(41, LanderTank, "stronger", 0.1, 3, "engine"), 300));
            parts.Single(p => p.Id == LanderTank).MassT -= 0.2;

            // 60 kN stops 3.9 t at Duna; it could never stop the whole 53 t ship, so reading the ship's weight would pass over it.
            Assert.Equal(new object[] { "uid40" }, Engines(Ask(snapshot)));
        }

        [Fact]
        public void No_legs_above_the_shield_keeps_todays_result_and_the_stack()
        {
            var answer = Ask(Ship(legs: false));

            Assert.Equal(true, answer["powered"]);
            Assert.Equal(true, answer["keeps_stack"]);
            Assert.Contains(Uid(Mainsail), Engines(answer).Concat((List<object>)answer["spare"]));
            Assert.StartsWith("engines, keeping everything: ", (string)answer["why"]);
        }

        [Fact]
        public void A_scripts_separate_none_still_stands_with_legs_above_the_shield()
        {
            var answer = Ask(Ship(), drop: false);

            Assert.Equal(true, answer["powered"]);
            Assert.Equal(true, answer["keeps_stack"]);
            Assert.StartsWith("engines, keeping everything: ", (string)answer["why"]);
        }

        [Fact]
        public void A_stack_with_no_decoupler_cant_drop_so_it_is_kept_as_today_with_legs_above_the_shield()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.Single(p => p.Id == StackDecoupler).Tags.Remove("decoupler");

            var answer = Ask(snapshot);

            Assert.Equal(true, answer["powered"]);
            Assert.Equal(true, answer["keeps_stack"]);
        }

        [Fact]
        public void A_stack_holding_the_part_running_the_script_is_kept_as_today_with_legs_above_the_shield()
        {
            var answer = Ask(Ship(), core: StackTank);

            Assert.Equal(true, answer["powered"]);
            Assert.Equal(true, answer["keeps_stack"]);
        }

        [Fact]
        public void The_log_line_says_it_drops_the_stack_and_never_keeping_everything()
        {
            var why = (string)Ask(Ship())["why"];

            Assert.StartsWith("engines, dropping the stack: ", why);
            Assert.Contains(" with its chutes, on toroidalAerospike", why);
            Assert.DoesNotContain("keeping everything", why);
        }

        // ---- The craft that enters ----

        [Fact]
        public void The_flights_lander_reaches_a_powered_finish_with_the_stack_in_Leaves_and_the_shield_jettisoned()
        {
            var snapshot = Ship();
            var stack = Stack(snapshot);
            Assert.Equal(new[] { StackTank, Mainsail }, stack.Leaves.Select(p => p.Id).OrderBy(id => id));

            var finish = EntryFinish.For(snapshot, stack, true, false, true);
            var craft = EntryCraft.From(snapshot, null, stack, finish, true);

            Assert.True(finish.Powered);
            Assert.False(finish.KeepsStack);
            Assert.Equal(new[] { Aerospike }, finish.Engines.Select(p => p.Id));
            Assert.Equal(new[] { StackTank, Mainsail }, craft.LeftOut.Select(p => p.Id).OrderBy(id => id));
            Assert.Equal(OuterShield, craft.ShieldDropped.Id);
            Assert.StartsWith("without its stack (", craft.Describe());
        }

        [Fact]
        public void EntryCraft_leaves_the_stack_out_for_return_and_deorbit_too()
        {
            var craft = EntryCraft.Of(Ship(), null, Pod, 2, true, true);

            Assert.Contains(Mainsail, craft.LeftOut.Select(p => p.Id));
            Assert.Equal(OuterShield, craft.ShieldDropped.Id);
            Assert.Equal(3.94, craft.KeptT, 2);
        }

        [Fact]
        public void With_no_legs_EntryCraft_keeps_the_stack_and_the_shield_on_a_powered_finish()
        {
            var craft = EntryCraft.Of(Ship(legs: false), null, Pod, 2, true, true);

            Assert.DoesNotContain(Mainsail, craft.LeftOut.Select(p => p.Id));
            Assert.Null(craft.ShieldDropped);
        }

        // ---- The Craft Check ----

        [Fact]
        public void The_Craft_Checks_touchdown_mass_and_speed_are_for_the_craft_that_stays()
        {
            var snapshot = Ship();

            var entry = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null).Entries[0];

            Assert.Equal("Duna", entry.Body);
            // The lander, less shield 2 which goes at its usual timing.
            Assert.Equal(3640, entry.MassKg, 0);
            var whole = snapshot.Craft.MassTonnes * 1000;
            Assert.True(entry.MassKg < whole / 5, "the ship is " + whole + " kg");
            Assert.True(entry.EngineFinish);
        }

        [Fact]
        public void The_Craft_Checks_walk_leaves_the_stacks_chutes_with_the_stack_when_legs_stand_above_the_shield()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.Add(Chute(200, StackTank, 2));
            snapshot.Craft.MassTonnes += 0.1;

            var entry = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null).Entries[0];

            Assert.True(entry.EngineFinish);
            Assert.DoesNotContain(entry.Chutes, c => c.PartId == 200);
            Assert.Equal(4, entry.Chutes.Count);
        }

        [Fact]
        public void The_Craft_Check_has_no_engine_finish_when_only_the_stack_has_engines()
        {
            var entry = ChuteGroups.Of(Ship(landerEngine: false), DunaThenKerbin, 0, false, null).Entries[0];

            Assert.InRange(entry.TouchdownSpeed, 10.01, 40);
            Assert.False(entry.EngineFinish);
        }

        [Fact]
        public void With_no_legs_the_Craft_Checks_walk_still_puts_the_stacks_chutes_back_and_flies_the_whole_mass()
        {
            var snapshot = Ship(legs: false);
            snapshot.Craft.Parts.Add(Chute(200, StackTank, 2));
            snapshot.Craft.MassTonnes += 0.1;

            var entry = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null).Entries[0];

            Assert.True(entry.EngineFinish);
            Assert.Contains(entry.Chutes, c => c.PartId == 200);
            Assert.Equal(snapshot.Craft.MassTonnes * 1000, entry.MassKg, 0);
        }

        // ---- The Entry Shield goes before the terminal phase (the addendum to #940) ----

        private static IDictionary<string, object> Drop(GameSnapshot snapshot, string separate = "auto")
        {
            var ask = new Dictionary<string, object> { { "home", Uid(Pod) }, { "separate", separate }, { "jettison_stage", "auto" } };
            return BlockCalls.EntryDropCall(ask, snapshot, DunaThenKerbin, 0, "", Uid);
        }

        [Fact]
        public void The_decoupler_between_the_engine_and_the_shield_is_the_one_fired_first()
        {
            Assert.Equal(Ring, Stack(Ship()).DecouplerAbove.Id);
            Assert.Equal(Uid(Ring), Drop(Ship())["jettison_first"]);
        }

        [Fact]
        public void A_shield_with_no_decoupler_above_it_names_none_and_goes_as_today()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.RemoveAll(p => p.Id == Ring);
            snapshot.Craft.Parts.Single(p => p.Id == OuterShield).ParentId = Aerospike;

            Assert.Null(Stack(snapshot).DecouplerAbove);
            Assert.Equal("", Drop(snapshot)["jettison_first"]);
            Assert.Equal(Uid(OuterShield), Drop(snapshot)["jettison"]);
        }

        [Fact]
        public void Another_heat_shield_above_the_Entry_Shield_is_never_the_one_fired_first()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.Single(p => p.Id == Ring).Tags.Add("heat_shield");

            Assert.Null(Stack(snapshot).DecouplerAbove);
        }

        [Fact]
        public void A_shield_the_script_keeps_names_nothing_to_fire()
        {
            Assert.Equal("", Drop(Ship(), "none")["jettison_first"]);
        }

        [Fact]
        public void On_a_powered_finish_with_legs_the_shield_comes_back_as_the_jettison_once_the_stack_has_gone()
        {
            var snapshot = Ship();
            Assert.Equal(true, Ask(snapshot)["powered"]);
            Assert.Equal(false, Ask(snapshot)["keeps_stack"]);

            var after = Drop(WithoutTheStack(snapshot));

            Assert.Equal("", after["error"]);
            Assert.Empty((List<object>)after["names"]);
            Assert.Equal(Uid(OuterShield), after["jettison"]);
            Assert.Equal(Uid(Ring), after["jettison_first"]);
        }

        [Fact]
        public void The_decoupler_that_lets_go_of_the_engine_leaves_with_the_shield_and_is_not_in_the_touchdown_mass()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.Single(p => p.Id == Ring).Releases.Add(Aerospike);

            var entry = ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null).Entries[0];
            var craft = EntryCraft.Of(snapshot, null, Pod, 2, true, true);

            Assert.Equal(new[] { OuterShield, Ring }, craft.ShieldLeaves.Select(p => p.Id).OrderByDescending(id => id));
            Assert.Equal(3590, entry.MassKg, 0);
        }

        [Fact]
        public void A_decoupler_above_that_lets_go_of_the_shield_stays_on_the_engine_and_in_the_touchdown_mass()
        {
            var snapshot = Ship();
            snapshot.Craft.Parts.Single(p => p.Id == Ring).Releases.Add(OuterShield);

            var craft = EntryCraft.Of(snapshot, null, Pod, 2, true, true);

            Assert.Equal(new[] { OuterShield }, craft.ShieldLeaves.Select(p => p.Id));
            Assert.Equal(3640, ChuteGroups.Of(snapshot, DunaThenKerbin, 0, false, null).Entries[0].MassKg, 0);
        }

        [Fact]
        public void A_shield_that_stays_takes_nothing_with_it()
        {
            Assert.Empty(EntryCraft.Of(Ship(legs: false), null, Pod, 2, true, true).ShieldLeaves);
        }

        // ---- On every Entry, the chute path included, so the shield is gone for liftoff (the player, 2026-11-08) ----

        [Theory]
        [InlineData(1.5, true)] // under the threshold, an engine aboard
        [InlineData(3.9, false)] // over it, no engine staying
        public void On_the_chute_path_with_legs_the_shield_comes_back_as_the_jettison_once_the_stack_has_gone(double landerT, bool landerEngine)
        {
            var snapshot = Ship(landerT: landerT, landerEngine: landerEngine);
            Assert.Equal(false, Ask(snapshot)["powered"]);
            Assert.Equal(OuterShield, EntryCraft.Of(snapshot, null, Pod, 2, true, true).ShieldDropped.Id);

            var after = Drop(WithoutTheStack(snapshot));

            Assert.Equal("", after["error"]);
            Assert.Empty((List<object>)after["names"]);
            Assert.Equal(Uid(OuterShield), after["jettison"]);
        }

        [Theory]
        [InlineData(1.5, true, true, false)] // chutes, legs
        [InlineData(3.9, true, false, false)] // chutes, legs, no engine staying
        [InlineData(1.5, false, true, false)] // chutes, no legs
        [InlineData(3.9, true, true, true)] // engines, legs
        [InlineData(3.9, false, true, true)] // engines, no legs, nothing below the shield
        public void The_decoupler_above_the_shield_is_named_first_on_every_Entry_that_jettisons(double landerT, bool legs, bool landerEngine, bool powered)
        {
            var snapshot = WithoutTheStack(Ship(landerT: landerT, legs: legs, landerEngine: landerEngine));
            Assert.Equal(powered, Ask(snapshot)["powered"]);

            var after = Drop(snapshot);

            Assert.Equal(Uid(OuterShield), after["jettison"]);
            Assert.Equal(Uid(Ring), after["jettison_first"]);
        }
    }
}
