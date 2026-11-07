// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Heat shields decide what an Entry drops, working back from the Last Entry (#823, ADR 0018), through <c>ENTRYDROP</c>'s call:
    /// the Mission Plan's words and the craft's part tree in, the Entry Shield and what its decouplers take out. Part uids here are
    /// the persistent ids written out.
    /// </summary>
    public class EntryDropTests
    {
        private sealed class Craft
        {
            public readonly List<SnapshotPart> Parts = new List<SnapshotPart>();

            public uint Add(string name, uint? parent, params string[] tags)
            {
                var id = (uint)(Parts.Count + 1);
                Parts.Add(new SnapshotPart { Id = id, ParentId = parent, Name = name, Tags = tags.ToList() });
                return id;
            }

            public uint Shield(uint? parent, string name = "HeatShield1")
            {
                var id = Add(name, parent, "heat_shield", "decoupler");
                // Stock shields aren't in the staging list, so a stage number can't count them.
                Parts[Parts.Count - 1].Stage = -1;
                return id;
            }
        }

        /// <summary>Pod, shield, Decoupler.1, then the ascent stage.</summary>
        private static Craft PodShieldStage()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var shield = c.Shield(pod);
            var dec = c.Add("Decoupler.1", shield, "decoupler");
            var tank = c.Add("fuelTank", dec);
            c.Add("liquidEngine", tank, "engine");
            return c;
        }

        /// <summary>Pod, shield 1, the ascent stage, shield 2, the transfer stage.</summary>
        private static Craft TwoShields()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var s1 = c.Shield(pod, "shield1");
            var d1 = c.Add("dec1", s1, "decoupler");
            var ascent = c.Add("ascentTank", d1);
            var s2 = c.Shield(ascent, "shield2");
            var d2 = c.Add("dec2", s2, "decoupler");
            c.Add("transferTank", d2);
            return c;
        }

        /// <summary>
        /// Flight m20261106-203509-34a6's layout, cut down: pod, chute, processor, HeatShield1, Decoupler.1, the core tank, on it
        /// radial tanks through their own decouplers (with stage-2 chutes), then the engine stage below.
        /// </summary>
        private static Craft TheFlight()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            c.Add("parachuteSingle", pod, "parachute");
            var shield = c.Shield(pod);
            var dec = c.Add("Decoupler.1", shield, "decoupler");
            var core = c.Add("fuelTank", dec);
            for (var i = 0; i < 4; i++)
            {
                var radial = c.Add("radialDecoupler", core, "decoupler");
                var tank = c.Add("radialTank", radial);
                c.Add("parachuteSingle", tank, "parachute");
            }
            c.Add("toroidalAerospike", core, "engine");
            return c;
        }

        private static PlannedSequence Leg(string name, double dv = 100, string vessel = "")
        {
            return new PlannedSequence { Name = name, DeltaV = dv, Vessel = vessel };
        }

        private static GameSnapshot Snapshot(Craft craft, string at)
        {
            return new GameSnapshot
            {
                Body = at,
                Orbit = new SnapshotOrbit { Body = at },
                Craft = new SnapshotCraft { Name = "Duna Ship", Parts = craft.Parts },
                Bodies = new List<SnapshotBody>
                {
                    new SnapshotBody { Name = "Kerbin", AtmosphereHeight = 70000, SeaLevelAtm = 1 },
                    new SnapshotBody { Name = "Duna", AtmosphereHeight = 50000, SeaLevelAtm = 0.07 },
                    new SnapshotBody { Name = "Mun" },
                },
            };
        }

        private static string Uid(uint id)
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }

        private static string Uid(Craft craft, string name)
        {
            return Uid(craft.Parts.First(p => p.Name == name).Id);
        }

        private static IDictionary<string, object> Call(Craft craft, string at, IList<PlannedSequence> legs, int step = 0, string child = "")
        {
            var ask = new Dictionary<string, object> { { "home", "1" } };
            return BlockCalls.EntryDropCall(ask, Snapshot(craft, at), legs, step, child, Uid);
        }

        private static string[] Strings(IDictionary<string, object> answer, string key)
        {
            return ((IEnumerable<object>)answer[key]).Cast<string>().ToArray();
        }

        private static void DropsNothing(IDictionary<string, object> answer)
        {
            Assert.Equal("", answer["shield"]);
            Assert.Empty(Strings(answer, "decouplers"));
            Assert.Empty(Strings(answer, "gone"));
            Assert.Empty(Strings(answer, "names"));
            Assert.Empty(Strings(answer, "stuck"));
        }

        private static readonly IList<PlannedSequence> DunaThenKerbin = new[] { Leg("Land on Duna", 300), Leg("Ascend from Duna", 1336), Leg("Return to Kerbin", 623) };

        // ---- The worked examples ----

        [Fact]
        public void One_shield_and_entries_at_Duna_then_Kerbin_keeps_the_stage_at_Duna_and_drops_it_at_Kerbin()
        {
            var craft = PodShieldStage();
            var duna = Call(craft, "Duna", DunaThenKerbin);
            Assert.Equal(2.0, duna["number"]);
            DropsNothing(duna);

            var kerbin = Call(craft, "Kerbin", DunaThenKerbin, step: 2);
            Assert.Equal(1.0, kerbin["number"]);
            Assert.Equal(Uid(craft, "HeatShield1"), kerbin["shield"]);
            Assert.Equal(new[] { Uid(craft, "Decoupler.1") }, Strings(kerbin, "decouplers"));
            Assert.Equal(new[] { Uid(craft, "fuelTank"), Uid(craft, "liquidEngine") }, Strings(kerbin, "gone"));
        }

        [Fact]
        public void Two_shields_are_spent_from_the_outside_in()
        {
            var craft = TwoShields();
            var duna = Call(craft, "Duna", DunaThenKerbin);
            Assert.Equal(Uid(craft, "shield2"), duna["shield"]);
            Assert.Equal(new[] { Uid(craft, "dec2") }, Strings(duna, "decouplers"));
            Assert.Equal(new[] { Uid(craft, "transferTank") }, Strings(duna, "gone"));

            var kerbin = Call(craft, "Kerbin", DunaThenKerbin, step: 2);
            Assert.Equal(Uid(craft, "shield1"), kerbin["shield"]);
            Assert.Equal(new[] { Uid(craft, "dec1") }, Strings(kerbin, "decouplers"));
            Assert.Equal(new[] { "ascentTank", "shield2", "dec2", "transferTank" }, Strings(kerbin, "names"));
        }

        [Fact]
        public void A_Kerbin_return_alone_with_two_shields_uses_the_first_and_everything_below_goes_the_second_included()
        {
            var craft = TwoShields();
            var answer = Call(craft, "Kerbin", new[] { Leg("Return to Kerbin", 800) });
            Assert.Equal(1.0, answer["number"]);
            Assert.Equal(Uid(craft, "shield1"), answer["shield"]);
            Assert.Equal(new[] { Uid(craft, "dec1") }, Strings(answer, "decouplers"));
            Assert.Equal(new[] { "ascentTank", "shield2", "dec2", "transferTank" }, Strings(answer, "names"));
        }

        [Fact]
        public void A_landing_with_burns_after_it_and_no_later_entry_keeps_its_stage()
        {
            var answer = Call(PodShieldStage(), "Duna", new[] { Leg("Land on Duna"), Leg("Ascend from Duna", 1336), Leg("Dock with the station", 50) });
            Assert.Equal(2.0, answer["number"]);
            DropsNothing(answer);
        }

        [Fact]
        public void A_landing_with_no_burns_after_it_is_the_Last_Entry_and_the_stage_drops()
        {
            var craft = PodShieldStage();
            var answer = Call(craft, "Duna", new[] { Leg("Land on Duna"), Leg("Plant a flag", 0) });
            Assert.Equal(1.0, answer["number"]);
            Assert.Equal(Uid(craft, "HeatShield1"), answer["shield"]);
            Assert.NotEmpty(Strings(answer, "gone"));
        }

        // ---- The count ----

        [Fact]
        public void A_plan_revised_mid_Mission_changes_n_at_the_next_block_start()
        {
            var craft = PodShieldStage();
            Assert.Equal(2.0, Call(craft, "Duna", DunaThenKerbin)["number"]);
            // The player gives up on the ascent: Duna is now the Last Entry.
            Assert.Equal(1.0, Call(craft, "Duna", new[] { Leg("Land on Duna", 300) })["number"]);
        }

        [Fact]
        public void A_return_and_its_reentry_count_once()
        {
            Assert.Equal(1.0, Call(PodShieldStage(), "Kerbin", new[] { Leg("Return to Kerbin", 623), Leg("Reentry", 0) })["number"]);
        }

        [Fact]
        public void A_deorbit_and_its_landing_count_once()
        {
            Assert.Equal(1.0, Call(PodShieldStage(), "Duna", new[] { Leg("Deorbit at Duna", 60), Leg("Land on Duna", 0) })["number"]);
        }

        [Fact]
        public void A_Duna_hop_with_an_ascent_between_counts_twice()
        {
            Assert.Equal(2.0, Call(PodShieldStage(), "Duna", new[] { Leg("Land on Duna", 0), Leg("Ascend from Duna", 1336), Leg("Land on Duna again", 0) })["number"]);
        }

        [Fact]
        public void Airless_bodies_manual_legs_and_aerobraking_are_not_entries()
        {
            // The Mun's landing, the manual stretch and the aerobraking aren't counted: only the return is, and none of them burns after it.
            var legs = new[] { Leg("Land on the Mun", 0), new PlannedSequence { Name = "Land on Duna by hand", Manual = true }, Leg("Aerobrake at Kerbin", 0), Leg("Return to Kerbin", 0) };
            Assert.Equal(1.0, Call(PodShieldStage(), "Kerbin", legs)["number"]);
        }

        [Fact]
        public void A_Child_Vessels_count_uses_its_own_sequences()
        {
            var legs = new[] { Leg("Land on Duna", 0, "Lander"), Leg("Ascend from Duna", 1336), Leg("Return to Kerbin", 623), Leg("Land on Duna again", 0, "Rover") };
            // The lander has only its landing and no burn after: its Last Entry.
            Assert.Equal(1.0, Call(PodShieldStage(), "Duna", legs, child: "Lander")["number"]);
            // The craft's plan here is the Ascend and the Return: its one Entry is the Return, and this reentry at Duna is one more.
            Assert.Equal(2.0, Call(PodShieldStage(), "Duna", legs)["number"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void No_Mission_Plan_is_the_first_shields_rule(bool nullLegs)
        {
            var craft = TwoShields();
            var answer = Call(craft, "Kerbin", nullLegs ? null : new PlannedSequence[0]);
            Assert.Equal(1.0, answer["number"]);
            Assert.Equal(Uid(craft, "shield1"), answer["shield"]);
            Assert.Equal(new[] { Uid(craft, "dec1") }, Strings(answer, "decouplers"));
        }

        [Fact]
        public void A_reentry_at_home_before_a_trip_doesnt_take_the_later_return_for_itself()
        {
            // At Kerbin, but the plan's next Entry is Duna: the return at the end isn't this one, so the stage stays.
            var legs = new[] { Leg("Suborbital hop", 0), Leg("Transfer to Duna", 1000), Leg("Land on Duna", 100), Leg("Ascend from Duna", 1336), Leg("Return to Kerbin", 623) };
            var answer = Call(PodShieldStage(), "Kerbin", legs);
            Assert.Equal(3.0, answer["number"]);
            DropsNothing(answer);
        }

        // ---- The Last Entry for find_landing_site's water (#828) ----

        [Theory]
        [InlineData("Duna", 3, false)]
        [InlineData("Duna", 4, false)]
        // Words the reader can't place: the craft is still at Duna, so not the Last Entry.
        [InlineData("Duna", 7, false)]
        [InlineData("Kerbin", 6, true)]
        [InlineData("Kerbin", 7, true)]
        public void The_flights_plan_is_at_its_Last_Entry_only_on_the_way_home(string at, int step, bool last)
        {
            Assert.Equal(last, Entries.IsLastEntry(Snapshot(new Craft(), at), TheFlightsPlan, step));
        }

        [Fact]
        public void No_Mission_Plan_or_a_step_past_it_is_not_a_Last_Entry()
        {
            var snapshot = Snapshot(new Craft(), "Kerbin");
            Assert.False(Entries.IsLastEntry(snapshot, null, 0));
            Assert.False(Entries.IsLastEntry(snapshot, new PlannedSequence[0], 0));
            Assert.False(Entries.IsLastEntry(snapshot, TheFlightsPlan, TheFlightsPlan.Count));
        }

        [Fact]
        public void An_entry_with_burns_to_come_or_a_plan_with_no_entry_is_not_a_Last_Entry()
        {
            var snapshot = Snapshot(new Craft(), "Kerbin");
            Assert.False(Entries.IsLastEntry(snapshot, new[] { Leg("Reenter at Kerbin", 0), Leg("Hop to the island", 500) }, 0));
            Assert.False(Entries.IsLastEntry(snapshot, new[] { Leg("Circularize at Kerbin", 50) }, 0));
            Assert.True(Entries.IsLastEntry(snapshot, new[] { Leg("Reenter at Kerbin", 0), Leg("Crew report", 0) }, 0));
        }

        [Fact]
        public void A_Child_Vessels_step_is_read_by_its_own_legs()
        {
            var snapshot = Snapshot(new Craft(), "Kerbin");
            var legs = new[] { Leg("Reenter at Kerbin", 0, "Probe"), Leg("Transfer to Duna", 900) };
            Assert.True(Entries.IsLastEntry(snapshot, legs, 0));
        }

        /// <summary>m20261106-203509-34a6's Mission Plan, in its own words.</summary>
        private static readonly IList<PlannedSequence> TheFlightsPlan = new[]
        {
            new PlannedSequence { Name = "Launch to parking orbit", DeltaV = 3089, Intent = "Launch from the pad at the optimal Duna window (UT 162366330, inclination -3.03) to an 80 km circular Kerbin orbit." },
            new PlannedSequence { Name = "Transfer to Duna", DeltaV = 1071, Intent = "Ejection burn and corrections onto the optimal Duna transfer, periapsis 60 km at Duna, departing UT 162382130." },
            new PlannedSequence { Name = "Capture into Duna orbit", DeltaV = 617, Intent = "Arrival burn into a circular 60 km orbit at Duna." },
            new PlannedSequence { Name = "Crew report", Intent = "Run the Crew Report experiment in Duna orbit and transmit it home." },
            new PlannedSequence { Name = "Land near equator in daylight", DeltaV = 22, Intent = "Land on a daylight site near the equator; the site is chosen with find_landing_site at the pass." },
            new PlannedSequence { Name = "Ascend from Duna", DeltaV = 1336, Intent = "Launch from the Duna surface to a circular Duna orbit." },
            new PlannedSequence { Name = "Return to Kerbin", DeltaV = 623, Intent = "Ejection from Duna onto a path with 35 km periapsis at Kerbin, with a mid-course correction." },
            new PlannedSequence { Name = "Reentry at Kerbin", Intent = "Aerobrake and land at Kerbin on the stage 0 pod parachute; the stage 2 chutes stay with the Duna lander." },
        };

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        // A step counted ahead of where the craft is (extra Sequence Plans): still at Duna, so still not the Last Entry.
        [InlineData(6)]
        [InlineData(7)]
        public void The_flights_own_plan_keeps_its_stage_at_Duna(int step)
        {
            var answer = Call(TheFlight(), "Duna", TheFlightsPlan, step);
            Assert.Equal(2.0, answer["number"]);
            DropsNothing(answer);
        }

        [Theory]
        [InlineData(6)]
        [InlineData(7)]
        public void The_flights_own_plan_drops_its_stage_at_Kerbin(int step)
        {
            var craft = TheFlight();
            var answer = Call(craft, "Kerbin", TheFlightsPlan, step);
            Assert.Equal(1.0, answer["number"]);
            Assert.Equal(new[] { Uid(craft, "Decoupler.1") }, Strings(answer, "decouplers"));
        }

        [Fact]
        public void A_reentry_the_plans_words_dont_place_still_counts_as_an_entry()
        {
            // "Corrective" isn't an Entry by its words, but this reentry is flying one: the Kerbin return still needs its shield.
            Assert.Equal(2.0, Call(PodShieldStage(), "Duna", new[] { Leg("Corrective", 0), Leg("Return to Kerbin", 623) })["number"]);
        }

        // ---- The walk ----

        [Fact]
        public void On_the_flights_layout_a_non_last_entry_keeps_everything()
        {
            DropsNothing(Call(TheFlight(), "Duna", DunaThenKerbin));
        }

        [Fact]
        public void On_the_flights_layout_the_Last_Entry_drops_all_of_it_through_the_decoupler_under_the_shield()
        {
            var craft = TheFlight();
            var answer = Call(craft, "Kerbin", DunaThenKerbin, step: 2);
            Assert.Equal(new[] { Uid(craft, "Decoupler.1") }, Strings(answer, "decouplers"));
            Assert.Equal(new[] { "fuelTank", "radialDecoupler", "radialTank", "parachuteSingle", "toroidalAerospike" }, Strings(answer, "names"));
        }

        [Fact]
        public void A_part_with_no_decoupler_below_the_Entry_Shield_is_stuck_but_below_another_shield_it_is_not()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var shield = c.Shield(pod);
            c.Add("fuelTank", shield);
            Assert.Equal(new[] { "fuelTank" }, Strings(Call(c, "Kerbin", new[] { Leg("Return to Kerbin") }), "stuck"));
            DropsNothing(Call(c, "Duna", DunaThenKerbin));
        }

        [Fact]
        public void A_craft_built_from_the_far_side_has_its_stack_stuck_rather_than_dropping_the_pod()
        {
            var c = new Craft();
            var tank = c.Add("fuelTank", null);
            var dec = c.Add("Decoupler.1", tank, "decoupler");
            var shield = c.Shield(dec);
            var pod = c.Add("mk1pod.v2", shield, "command");
            var ask = new Dictionary<string, object> { { "home", Uid(pod) } };
            var answer = BlockCalls.EntryDropCall(ask, Snapshot(c, "Kerbin"), null, 0, "", Uid);
            Assert.Equal(Uid(shield), answer["shield"]);
            Assert.Equal(new[] { "fuelTank", "Decoupler.1" }, Strings(answer, "stuck"));
            Assert.Empty(Strings(answer, "decouplers"));
        }

        [Fact]
        public void A_pod_and_shield_with_nothing_below_or_only_a_spent_decoupler_have_no_stack()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var shield = c.Shield(pod);
            var bare = Call(c, "Kerbin", null);
            Assert.Equal(Uid(shield), bare["shield"]);
            Assert.Empty(Strings(bare, "names"));
            c.Add("Decoupler.1", shield, "decoupler");
            var spent = Call(c, "Kerbin", null);
            Assert.Equal(Uid(shield), spent["shield"]);
            Assert.Empty(Strings(spent, "names"));
            Assert.Empty(Strings(spent, "decouplers"));
        }

        [Fact]
        public void A_craft_with_no_shield_drops_nothing()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var dec = c.Add("Decoupler.1", pod, "decoupler");
            c.Add("fuelTank", dec);
            DropsNothing(Call(c, "Kerbin", null));
        }

        [Fact]
        public void Shields_count_from_the_pod_whatever_the_part_order()
        {
            // The outer shield listed first: it is still number 2.
            var c = new Craft();
            c.Parts.Add(new SnapshotPart { Id = 5, ParentId = 4, Name = "outer", Tags = new List<string> { "heat_shield", "decoupler" }, Stage = -1 });
            c.Parts.Add(new SnapshotPart { Id = 1, Name = "pod", Tags = new List<string> { "command" } });
            c.Parts.Add(new SnapshotPart { Id = 2, ParentId = 1, Name = "inner", Tags = new List<string> { "heat_shield", "decoupler" }, Stage = -1 });
            c.Parts.Add(new SnapshotPart { Id = 3, ParentId = 2, Name = "dec", Tags = new List<string> { "decoupler" } });
            c.Parts.Add(new SnapshotPart { Id = 4, ParentId = 3, Name = "tank" });
            Assert.Equal("2", Call(c, "Kerbin", null)["shield"]);
            Assert.Equal("5", Call(c, "Duna", DunaThenKerbin)["shield"]);
        }

        [Fact]
        public void A_craft_with_no_snapshot_says_so()
        {
            Assert.Equal("there is no Game Snapshot of the craft", BlockCalls.EntryDropCall(new Dictionary<string, object>(), null, null, 0, "", Uid)["error"]);
        }
    }
}
