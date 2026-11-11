// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#790: the landing target default, and the choice on a trip home from another planet, at Core's seam (<see cref="BlockCalls.LandingTarget"/>, plan_transfer).</summary>
    public sealed class HomeLandingTests
    {
        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48, RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };
        private static readonly SnapshotBody Mun = new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = 1.7 };
        private static readonly SnapshotBody Duna = new SnapshotBody
        {
            Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667, SoiRadius = 47921949, RotationPeriod = 65517.859, Sma = 20726155264, Eccentricity = 0.051, Inclination = 0.06, Lan = 135.5, MeanAnomalyAtEpoch = 3.14,
        };
        private static readonly SnapshotBody Ike = new SnapshotBody { Name = "Ike", Parent = "Duna", Gm = 1.8568369e10, Radius = 130000, SoiRadius = 1049598.9, RotationPeriod = 65517.862, Sma = 3200000, Eccentricity = 0.03, Inclination = 0.2, MeanAnomalyAtEpoch = 1.7 };

        private static GameSnapshot Round(SnapshotBody body, double altitude, string goal = "", double? dv = null)
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = body.Name, MissionGoal = goal };
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = body.Radius + altitude, Eccentricity = 0, Body = body.Name, Periapsis = altitude, Apoapsis = altitude, Epoch = 1000 };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, Mun, Duna, Ike });
            if (dv.HasValue)
            {
                snapshot.DeltaVSimulator = true;
                snapshot.Stages.Add(new SnapshotStage { DeltaVVac = dv.Value });
                snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1, Name = "pod", MassT = 2, Chute = new ReentryChute() });
                snapshot.Craft.MassTonnes = 2;
            }
            return snapshot;
        }

        private static string Default(GameSnapshot snapshot, Dictionary<string, object> options = null, string planet = "Kerbin")
        {
            var ask = new Dictionary<string, object>(options ?? new Dictionary<string, object>()) { { "planet", planet } };
            return (string)BlockCalls.LandingTarget(ask, snapshot)["target"];
        }

        // ---- The landing target default ----

        [Theory]
        [InlineData("")]
        [InlineData("Land on the Mun, plant a flag and come home.")]
        [InlineData("Land within 50 km of KSC after the Mun.")]
        public void A_return_to_kerbin_with_no_named_place_aims_for_ksc(string goal)
        {
            Assert.Equal(HomeLanding.Ksc, Default(Round(Mun, 30000, goal)));
        }

        [Theory]
        [InlineData("Orbit the Mun and splash down in the ocean.")]
        [InlineData("Return from the Mun and splash down.")]
        [InlineData("Come home and land at (12.5, -30.1).")]
        [InlineData("Come back and land on the island airfield.")]
        public void A_goal_that_names_another_place_keeps_its_own(string goal)
        {
            Assert.Equal(HomeLanding.None, Default(Round(Mun, 30000, goal)));
        }

        /// <summary>A place on another body says nothing of where at home.</summary>
        [Theory]
        [InlineData("Land at the Mun's north pole and come home.")]
        [InlineData("Land on the Mun at (3.1, 24.6), then come home.")]
        public void A_place_on_another_body_is_no_place_at_home(string goal)
        {
            Assert.Equal(HomeLanding.Ksc, Default(Round(Mun, 30000, goal)));
        }

        /// <summary>Claudia's own "splashdown" in a leg's words says nothing of where: only the player's goal does.</summary>
        [Fact]
        public void A_legs_word_for_the_ending_is_no_place()
        {
            var snapshot = Round(Mun, 30000, "Orbit the Mun and come home.");
            snapshot.LegsAhead = new List<PlannedSequence> { new PlannedSequence { Name = "Reentry and splashdown", Intent = "Come down under chutes" } };
            Assert.Equal(HomeLanding.Ksc, Default(snapshot));
        }

        [Fact]
        public void A_site_a_leg_names_or_one_picked_for_kerbin_keeps_its_own()
        {
            var snapshot = Round(Mun, 30000);
            snapshot.LegsAhead = new List<PlannedSequence> { new PlannedSequence { Name = "Reentry", Intent = "Come down at (12.5, -30.1)" } };
            Assert.Equal(HomeLanding.None, Default(snapshot));
            snapshot = Round(Mun, 30000);
            snapshot.PickedSite = new PickedSite { Body = "Kerbin", Latitude = 10 };
            Assert.Equal(HomeLanding.None, Default(snapshot));
            snapshot.PickedSite = new PickedSite { Body = "Mun", Latitude = 10 };
            Assert.Equal(HomeLanding.Ksc, Default(snapshot));
        }

        [Fact]
        public void Only_kerbin_has_a_default()
        {
            Assert.Equal(HomeLanding.None, Default(Round(Ike, 30000), planet: "Duna"));
        }

        /// <summary>A trip Claudia has shaped another way isn't a landing to aim: a periapsis over the air, a window that times the ejection, a plane.</summary>
        [Theory]
        [InlineData("target_pe", 80000.0, HomeLanding.None)]
        [InlineData("target_pe", 70000.0, HomeLanding.None)]
        [InlineData("target_pe", 45000.0, HomeLanding.Ksc)]
        [InlineData("target_inc", 30.0, HomeLanding.None)]
        [InlineData("window", 90000.0, HomeLanding.None)]
        public void Options_that_shape_the_trip_otherwise_win(string option, double value, string expected)
        {
            Assert.Equal(expected, Default(Round(Mun, 30000), new Dictionary<string, object> { { option, value } }));
        }

        [Fact]
        public void The_words_for_no_choice_leave_the_default()
        {
            Assert.Equal(HomeLanding.Ksc, Default(Round(Mun, 30000), new Dictionary<string, object> { { "target_inc", "any" }, { "window", "next" }, { "target_pe", 35000.0 } }));
        }

        /// <summary>Stock's Mission Plan stamps the goal, so a block call reads it as planning does (<see cref="MissionView.Stamp"/>).</summary>
        [Fact]
        public void The_view_stamps_the_goal_for_a_block_call()
        {
            var snapshot = Round(Mun, 30000);
            MissionView.Stamp(snapshot, new MissionView { Stage = MissionStage.Running, Goal = "Splash down in the ocean" });
            Assert.Equal("Splash down in the ocean", snapshot.MissionGoal);
            Assert.Equal(HomeLanding.None, Default(snapshot));
        }

        // ---- From another planet ----

        private static JsonElement Plan(GameSnapshot snapshot)
        {
            var outcome = PlanningTools.Default().Run("plan_transfer", "{\"target\":\"Kerbin\",\"target_pe\":80000}", snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            return JsonDocument.Parse(outcome.Json).RootElement;
        }

        /// <summary>#832 in flight: the Mission's view stamps the later legs' delta-v on a block call's snapshot, and the spare delta-v for a capture first keeps it back.</summary>
        [Fact]
        public void In_flight_the_later_legs_come_off_the_spare_delta_v_for_a_capture()
        {
            var free = HomeLanding.Arrival(Round(Duna, 60000, "Land on Duna and come home.", 5000), "Kerbin");
            Assert.True(free.CaptureFirst);
            var snapshot = Round(Duna, 60000, "Land on Duna and come home.", 5000);
            MissionView.Stamp(snapshot, new MissionView
            {
                Stage = MissionStage.Running, CurrentSequence = 0, CraftName = "Ship", Goal = "Land on Duna and come home.",
                PlanLegs = new List<PlannedSequence> { new PlannedSequence { Name = "Return", DeltaV = 1000 }, new PlannedSequence { Name = "Tour", DeltaV = 4000 } },
            });
            Assert.Equal(4000, snapshot.LaterLegsDv);
            var held = HomeLanding.Arrival(snapshot, "Kerbin");
            Assert.Equal(free.SpareDv - 4000, held.SpareDv, 6);
            Assert.False(held.CaptureFirst);
        }

        [Fact]
        public void A_duna_return_with_enough_delta_v_plans_capture_then_deorbit()
        {
            var snapshot = Round(Duna, 60000, "Land on Duna and come home.", 5000);
            var plan = HomeLanding.Arrival(snapshot, "Kerbin");
            Assert.True(plan.CaptureFirst);
            Assert.InRange(plan.CaptureDv, 300, 1500);
            Assert.InRange(plan.DeorbitDv, 20, 100);
            Assert.Equal(DeltaVMap.LowOrbit(Kerbin) - Kerbin.Radius, plan.ArrivalPe);
            var result = Plan(snapshot);
            Assert.True(result.GetProperty("capture").GetBoolean());
            var note = result.GetProperty("arrival_note").GetString();
            Assert.Contains("capture()", note);
            Assert.Contains("deorbit()", note);
            Assert.Contains("reentry()", note);
            Assert.True(result.GetProperty("optimal").TryGetProperty("capture_dv", out _));
        }

        [Fact]
        public void A_duna_return_without_comes_in_directly()
        {
            var snapshot = Round(Duna, 60000, "Land on Duna and come home.", 1200);
            var plan = HomeLanding.Arrival(snapshot, "Kerbin");
            Assert.False(plan.CaptureFirst);
            Assert.Equal(ArrivalRule.EntryPe(Kerbin), plan.ArrivalPe);
            var result = Plan(snapshot);
            Assert.False(result.GetProperty("capture").GetBoolean());
            Assert.Equal("arrival: entry at 21 km, no capture burn", result.GetProperty("arrival_note").GetString());
        }

        /// <summary>A planet's plan_transfer needs no target_pe for a trip home, and one under the air can't be the capture's.</summary>
        [Theory]
        [InlineData("{\"target\":\"Kerbin\"}")]
        [InlineData("{\"target\":\"Kerbin\",\"target_pe\":21000}")]
        public void The_capture_is_priced_at_low_orbit_whatever_was_asked(string input)
        {
            var snapshot = Round(Duna, 60000, "Come home.", 5000);
            var outcome = PlanningTools.Default().Run("plan_transfer", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            var asked = JsonDocument.Parse(outcome.Json).RootElement;
            Assert.True(asked.GetProperty("capture").GetBoolean());
            var full = Plan(snapshot);
            Assert.Equal(full.GetProperty("optimal").GetProperty("total_dv").GetDouble(), asked.GetProperty("optimal").GetProperty("total_dv").GetDouble(), 1);
        }

        /// <summary>On the ground the ascent comes first: the delta-v on arrival leaves it out.</summary>
        [Fact]
        public void A_craft_on_the_ground_pays_its_ascent_first()
        {
            var inOrbit = HomeLanding.Arrival(Round(Duna, 60000, "", 5000), "Kerbin");
            var landed = Round(Duna, 60000, "", 5000);
            landed.Situation = "LANDED";
            Assert.True(HomeLanding.Arrival(landed, "Kerbin").DvLeft < inOrbit.DvLeft - 500);
        }

        /// <summary>Stock can't say: nothing to spend on the capture, so the same direct way in.</summary>
        [Fact]
        public void Stock_saying_nothing_comes_in_directly()
        {
            Assert.False(HomeLanding.Arrival(Round(Duna, 60000), "Kerbin").CaptureFirst);
        }

        /// <summary>The reserve binds: delta-v that covers the two burns outright but not with the larger of 50 m/s and 10% kept back comes in directly.</summary>
        [Fact]
        public void The_reserve_is_respected()
        {
            var plan = HomeLanding.Arrival(Round(Duna, 60000, "", 5000), "Kerbin");
            var departure = 5000 - plan.DvLeft.Value;
            var need = plan.Need;
            Assert.False(HomeLanding.Arrival(Round(Duna, 60000, "", departure + need + 1), "Kerbin").CaptureFirst);
            Assert.False(HomeLanding.Arrival(Round(Duna, 60000, "", departure + need + 0.09 * need), "Kerbin").CaptureFirst);
            var enough = departure + Math.Max(need + 50, need / 0.9) + 1;
            Assert.True(HomeLanding.Arrival(Round(Duna, 60000, "", enough), "Kerbin").CaptureFirst);
        }

        /// <summary>A craft with no chutes keeps its powered landing's need, so the same delta-v comes in directly.</summary>
        [Fact]
        public void A_chuteless_craft_keeps_what_its_powered_landing_needs()
        {
            var chuted = HomeLanding.Arrival(Round(Duna, 60000, "", 5000), "Kerbin");
            var departure = 5000 - chuted.DvLeft.Value;
            var enough = departure + Math.Max(chuted.Need + 50, chuted.Need / 0.9) + 1;
            var snapshot = Round(Duna, 60000, "", enough);
            Assert.True(HomeLanding.Arrival(snapshot, "Kerbin").CaptureFirst);
            snapshot.Craft.Parts[0].Chute = null;
            Assert.False(HomeLanding.Arrival(snapshot, "Kerbin").CaptureFirst);
        }

        [Fact]
        public void A_goal_with_its_own_place_or_a_caller_who_says_keeps_the_rule_as_it_was()
        {
            Assert.Null(HomeLanding.Arrival(Round(Duna, 60000, "Come home and splash down.", 5000), "Kerbin"));
            var snapshot = Round(Duna, 60000, "Come home.", 5000);
            var outcome = PlanningTools.Default().Run("plan_transfer", "{\"target\":\"Kerbin\",\"target_pe\":80000,\"capture\":false}", snapshot, new NoPlanningBody());
            Assert.Contains("\"capture\":false", outcome.Json);
        }

        // ---- #981: a craft that can enter comes in directly ----

        /// <summary>
        /// Mission m20261110-203402-45d1 at its re-plan in Duna orbit (UT 179013843): 2,441 m/s by stock, a pod with a chute over an Entry Shield over the
        /// engine stack. <paramref name="shield"/> false takes the shield's tag off; <paramref name="air"/> false gives Kerbin no atmosphere;
        /// <paramref name="dunaSma"/> moves "Duna" round the Sun, for a faster arrival at Kerbin.
        /// </summary>
        private static GameSnapshot ShieldedAtDuna(bool shield = true, bool air = true, double? dunaSma = null)
        {
            const double ut = 179013843.02280834;
            var kerbin = new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = 3531599999999.9995, Radius = 600000, AtmosphereHeight = air ? 70000 : 0, SeaLevelAtm = air ? 1 : 0, SoiRadius = 84159286.479630485,
                RotationPeriod = 21549.425183089825, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14000010490417,
            };
            var snapshot = new GameSnapshot { Ut = ut, Situation = "ORBITING", Body = "Duna", MissionGoal = "return to kerbin in the soonest window" };
            snapshot.Orbit = new SnapshotOrbit
            {
                Body = "Duna", SemiMajorAxis = 379732.23702056933, Eccentricity = 0.0027712785379309032, Inclination = 0.013479989344370995, Lan = 296.90766784479143,
                ArgumentOfPeriapsis = 151.90871009589452, MeanAnomalyAtEpoch = 1.1051974785711709, Epoch = ut, Periapsis = 58679.893221953709, Apoapsis = 60784.580819184892,
            };
            var duna = !dunaSma.HasValue ? Duna : new SnapshotBody
            {
                Name = Duna.Name, Parent = Duna.Parent, Gm = Duna.Gm, Radius = Duna.Radius, AtmosphereHeight = Duna.AtmosphereHeight, SeaLevelAtm = Duna.SeaLevelAtm, SoiRadius = Duna.SoiRadius,
                RotationPeriod = Duna.RotationPeriod, Sma = dunaSma.Value, Eccentricity = Duna.Eccentricity, Inclination = Duna.Inclination, Lan = Duna.Lan, MeanAnomalyAtEpoch = Duna.MeanAnomalyAtEpoch,
            };
            snapshot.Bodies.AddRange(new[] { Sun, kerbin, Mun, duna, Ike });
            snapshot.DeltaVSimulator = true;
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 603.09 });
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 1837.83 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1, Name = "mk1pod.v2", MassT = 1.2, Chute = new ReentryChute() });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 2, ParentId = 1, Name = "HeatShield1", MassT = 0.3, Tags = shield ? new List<string> { "heat_shield", "decoupler" } : new List<string>() });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 3, ParentId = 4, Name = "liquidEngine3.v2", MassT = 10.03, Tags = new List<string> { "engine" } });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 4, ParentId = 2, Name = "Decoupler.1", MassT = 0.05, Tags = new List<string> { "decoupler" } });
            snapshot.Craft.MassTonnes = 11.583;
            return snapshot;
        }

        /// <summary>The flight's craft had a shield and a chute: the plan is transfer_to at the entry periapsis then reentry, about 671 m/s, not the 1,718 m/s capture.</summary>
        [Fact]
        public void A_craft_that_can_enter_comes_home_from_duna_by_direct_entry()
        {
            var snapshot = ShieldedAtDuna();
            var plan = HomeLanding.Arrival(snapshot, "Kerbin");
            Assert.False(plan.CaptureFirst);
            Assert.Equal(ArrivalRule.EntryPe(snapshot.Bodies.Find(b => b.Name == "Kerbin")), plan.ArrivalPe);
            var result = Plan(snapshot);
            Assert.False(result.GetProperty("capture").GetBoolean());
            var note = result.GetProperty("arrival_note").GetString();
            Assert.StartsWith("arrival: entry at 21 km, no capture burn", note);
            Assert.Contains("reentry()", note);
            Assert.False(result.GetProperty("optimal").TryGetProperty("capture_dv", out _));
            Assert.InRange(result.GetProperty("optimal").GetProperty("total_dv").GetDouble(), 660, 682);
        }

        /// <summary>The same craft without the shield keeps today's capture, deorbit and reentry; so does a home with no air, where nothing enters.</summary>
        [Fact]
        public void No_shield_or_no_air_keeps_the_capture()
        {
            var bare = ShieldedAtDuna(shield: false);
            Assert.True(HomeLanding.Arrival(bare, "Kerbin").CaptureFirst);
            var result = Plan(bare);
            Assert.True(result.GetProperty("capture").GetBoolean());
            Assert.Contains("capture()", result.GetProperty("arrival_note").GetString());

            var airless = ShieldedAtDuna(air: false);
            Assert.Null(HomeLanding.Arrival(airless, "Kerbin"));
            Assert.True(Plan(airless).GetProperty("capture").GetBoolean());
        }

        /// <summary>A capture stays allowed: the caller who asks for one gets it, shield or not.</summary>
        [Fact]
        public void A_capture_asked_for_is_still_priced_with_a_shield_aboard()
        {
            var outcome = PlanningTools.Default().Run("plan_transfer", "{\"target\":\"Kerbin\",\"target_pe\":80000,\"capture\":true}", ShieldedAtDuna(), new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            var result = JsonDocument.Parse(outcome.Json).RootElement;
            Assert.True(result.GetProperty("capture").GetBoolean());
            Assert.True(result.GetProperty("optimal").TryGetProperty("capture_dv", out _));
        }

        /// <summary>The Mission Plan's budget reads the Kerbin leg from the Mission request's stage-table row, which is plan_transfer's answer: the entry, no capture.</summary>
        [Fact]
        public void The_mission_requests_kerbin_row_budgets_the_entry()
        {
            var table = MissionPrompts.StageTable(ShieldedAtDuna(), "return to kerbin in the soonest window", null);
            var row = table.Substring(table.IndexOf("plan_transfer {\"target\":\"Kerbin\"", StringComparison.Ordinal));
            row = row.Substring(row.IndexOf(": ", StringComparison.Ordinal) + 2).Split('\n')[0];
            var result = JsonDocument.Parse(row).RootElement;
            Assert.False(result.GetProperty("capture").GetBoolean());
            Assert.Contains("transfer_to, reentry()", result.GetProperty("arrival_note").GetString());
            Assert.InRange(result.GetProperty("optimal").GetProperty("total_dv").GetDouble(), 660, 682);
        }

        /// <summary>
        /// Review of #981: the transfer engine under the shield doesn't make up for the chute. A shielded pod with no chute over a dry LV-909 can't land
        /// at Kerbin: on chutes there are none, and a powered finish keeps the stack ahead of the shield, which Kerbin's entry heat doesn't allow.
        /// </summary>
        [Fact]
        public void A_shielded_pod_with_no_chute_keeps_the_capture_whatever_the_engine()
        {
            var snapshot = ShieldedAtDuna();
            snapshot.Craft.Parts[0].Chute = null;
            Assert.Contains("engine", snapshot.Craft.Parts[2].Tags);
            snapshot.Craft.Parts[2].EngineFuelled = true;
            // With no chute the reserve holds a powered landing (HomeLanding.PoweredLanding): enough delta-v that, as before #981, it covers the capture too.
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 4000 });
            Assert.False(EntryCraft.CanEnter(snapshot, "Kerbin"));
            var plan = HomeLanding.Arrival(snapshot, "Kerbin");
            Assert.False(plan.CanEnter);
            Assert.True(plan.CaptureFirst);
            Assert.True(Plan(snapshot).GetProperty("capture").GetBoolean());
        }

        /// <summary>A chute only in the stack the Entry drops lands nothing.</summary>
        [Fact]
        public void A_chute_in_the_dropped_stack_does_not_count()
        {
            var snapshot = ShieldedAtDuna();
            Assert.True(EntryCraft.CanEnter(snapshot, "Kerbin"));
            snapshot.Craft.Parts[0].Chute = null;
            snapshot.Craft.Parts[2].Chute = new ReentryChute();
            Assert.False(EntryCraft.CanEnter(snapshot, "Kerbin"));
        }

        /// <summary>A chute opened on Duna (semi- or fully deployed) has nothing left to give at Kerbin, as <see cref="ChuteGroups.Usable"/> reads it.</summary>
        [Theory]
        [InlineData(ReentryChuteState.SemiDeployed)]
        [InlineData(ReentryChuteState.Deployed)]
        public void A_chute_used_on_duna_does_not_count(ReentryChuteState state)
        {
            var snapshot = ShieldedAtDuna();
            snapshot.Craft.Parts[0].Chute.State = state;
            Assert.False(EntryCraft.CanEnter(snapshot, "Kerbin"));
            // Nor does it spare the reserve a powered landing: with no chute to open, the capture is priced with one held back.
            Assert.True(HomeLanding.PoweredLanding(snapshot, snapshot.Bodies.Find(b => b.Name == "Kerbin")) > 0);
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 4000 });
            Assert.True(HomeLanding.Arrival(snapshot, "Kerbin").CaptureFirst);
        }

        /// <summary>A craft built from the engine up (its root under the shield) is the same craft: the pod's chute is on what lands, and it comes in directly.</summary>
        [Fact]
        public void A_craft_rooted_below_the_shield_can_enter()
        {
            var snapshot = ShieldedAtDuna();
            var parts = snapshot.Craft.Parts;
            parts[0].CrewCapacity = 1;
            parts[2].ParentId = null;
            parts[3].ParentId = 3;
            parts[1].ParentId = 4;
            parts[0].ParentId = 2;
            parts.Reverse();
            Assert.True(EntryCraft.CanEnter(snapshot, "Kerbin"));
            Assert.False(HomeLanding.Arrival(snapshot, "Kerbin").CaptureFirst);
        }

        /// <summary>A chute on a stage the craft will drop before home (a descent stage's, hung off the pod by a decoupler) isn't on what lands.</summary>
        [Fact]
        public void A_chute_on_a_stage_still_to_drop_does_not_count()
        {
            var snapshot = ShieldedAtDuna();
            snapshot.Craft.Parts[0].Chute = null;
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 5, ParentId = 1, Name = "Decoupler.1", MassT = 0.05, Stage = 2, Tags = new List<string> { "decoupler" } });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 6, ParentId = 5, Name = "parachuteRadial", MassT = 0.1, Stage = 2, Chute = new ReentryChute() });
            Assert.False(EntryCraft.CanEnter(snapshot, "Kerbin"));
        }

        /// <summary>A shield burnt below half its ablator (<see cref="EntryCraft.AblatorShareNeeded"/>) keeps the capture; a full one doesn't.</summary>
        [Fact]
        public void A_burnt_down_shield_keeps_the_capture()
        {
            var snapshot = ShieldedAtDuna();
            var ablator = new SnapshotPartResource { Name = "Ablator", Amount = 200, Max = 200, Density = 0.001 };
            snapshot.Craft.Parts[1].Resources.Add(ablator);
            Assert.False(HomeLanding.Arrival(snapshot, "Kerbin").CaptureFirst);
            ablator.Amount = 60;
            Assert.False(EntryCraft.CanEnter(snapshot, "Kerbin"));
            Assert.True(HomeLanding.Arrival(snapshot, "Kerbin").CaptureFirst);
        }

        /// <summary>
        /// An arrival faster than <see cref="HomeLanding.DirectEntryExcessLimit"/> keeps the capture, shield and chute or not: "Duna" moved out to Dres's orbit
        /// arrives at Kerbin with about 2.1 km/s of excess, over twice this flight's.
        /// </summary>
        [Fact]
        public void An_arrival_over_the_excess_limit_keeps_the_capture()
        {
            var near = HomeLanding.Arrival(ShieldedAtDuna(), "Kerbin");
            Assert.True(near.CanEnter);
            var snapshot = ShieldedAtDuna(dunaSma: 40839348203);
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 4000 });
            Assert.True(EntryCraft.CanEnter(snapshot, "Kerbin"));
            var plan = HomeLanding.Arrival(snapshot, "Kerbin");
            Assert.False(plan.CanEnter);
            Assert.True(plan.CaptureFirst);
        }

        /// <summary>From a moon of Kerbin the way home is <c>return</c>, and from a moon of another planet the same choice as its planet.</summary>
        [Fact]
        public void The_choice_is_for_another_planets_system_only()
        {
            Assert.Null(HomeLanding.Arrival(Round(Mun, 30000, "", 5000), "Kerbin"));
            Assert.Null(HomeLanding.Arrival(Round(Kerbin, 80000, "", 5000), "Kerbin"));
            Assert.Null(HomeLanding.Arrival(Round(Duna, 60000, "", 5000), "Duna"));
            Assert.True(HomeLanding.Arrival(Round(Ike, 30000, "", 5000), "Kerbin").CaptureFirst);
        }
    }
}
