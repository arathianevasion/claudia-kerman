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
        public void It_says_when_the_block_drops_the_stack_600_s_before_the_air()
        {
            // #815: reentry waits to this point, not block start, so the drop's kick barely moves the landing (EntryDropLeadTests).
            Assert.Equal(600.0, Call(PodShieldStage(), "Kerbin", DunaThenKerbin, step: 2)["lead"]);
            Assert.Equal(EntryStack.DropLeadS, Call(PodShieldStage(), "Duna", DunaThenKerbin)["lead"]);
        }

        // ---- The block's refusal (#815: reentry_stack_refusal, moved to Core; read at block start and again after the drop) ----

        private static readonly IList<PlannedSequence> HomeToKerbin = new[] { Leg("Return to Kerbin", 600) };

        /// <summary>Pod, shield, then a tank with no decoupler between it and the shield.</summary>
        private static Craft Stuck()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            c.Add("fuelTank", c.Shield(pod));
            return c;
        }

        /// <summary>ENTRYDROP's error as reentry asks it: separate, chutes and core, and after its drop drop_error; the craft at altitude m.</summary>
        private static string Refused(Craft craft, string at, IList<PlannedSequence> legs, double altitude = 100000, string separate = "auto", string core = "1", string dropError = null)
        {
            var ask = new Dictionary<string, object> { { "home", "1" }, { "separate", separate }, { "chutes", true }, { "core", core } };
            if (dropError != null) ask["drop_error"] = dropError;
            var snapshot = Snapshot(craft, at);
            snapshot.Altitude = altitude;
            return (string)BlockCalls.EntryDropCall(ask, snapshot, legs, 0, "", Uid)["error"];
        }

        [Fact]
        public void At_block_start_a_stuck_stack_above_the_air_is_refused_as_the_block_said_it()
        {
            Assert.Equal("stack attached below the heat shield: fuelTank; drop it first or use separate (separate: fuelTank: no decoupler between it and the heat shield)",
                Refused(Stuck(), "Kerbin", HomeToKerbin));
        }

        [Fact]
        public void A_stack_that_can_drop_is_not_refused_at_block_start_it_drops_at_the_drop_point()
        {
            Assert.Equal("", Refused(PodShieldStage(), "Kerbin", HomeToKerbin));
        }

        [Fact]
        public void In_the_air_with_separate_none_or_with_no_stack_for_this_shield_nothing_is_refused()
        {
            Assert.Equal("", Refused(Stuck(), "Kerbin", HomeToKerbin, altitude: 60000));
            Assert.Equal("", Refused(Stuck(), "Kerbin", HomeToKerbin, separate: "none"));
            // Below another shield (Duna before Kerbin) the tank is no stack of this Entry's.
            Assert.Equal("", Refused(Stuck(), "Duna", DunaThenKerbin));
            // Without separate (return, deorbit, today's callers) the answer is only what drops.
            Assert.Equal("", Call(Stuck(), "Kerbin", HomeToKerbin)["error"]);
        }

        [Fact]
        public void A_stack_that_takes_the_part_running_the_script_is_refused()
        {
            var c = PodShieldStage();
            Assert.Equal("stack attached below the heat shield: fuelTank, liquidEngine; drop it first or use separate (separate: it would drop the part running the script)",
                Refused(c, "Kerbin", HomeToKerbin, core: Uid(c, "fuelTank")));
        }

        [Fact]
        public void After_the_drop_a_stack_still_on_is_refused_with_what_the_drop_said_and_one_gone_is_not()
        {
            Assert.Equal("stack attached below the heat shield: fuelTank, liquidEngine; drop it first or use separate (separate: nothing separated: the decouplers did not fire)",
                Refused(PodShieldStage(), "Kerbin", HomeToKerbin, dropError: "nothing separated: the decouplers did not fire"));
            Assert.Equal("stack attached below the heat shield: fuelTank, liquidEngine; drop it first or use separate",
                Refused(PodShieldStage(), "Kerbin", HomeToKerbin, dropError: ""));
            var gone = PodShieldStage();
            gone.Parts.RemoveAll(p => p.Name == "fuelTank" || p.Name == "liquidEngine");
            Assert.Equal("", Refused(gone, "Kerbin", HomeToKerbin, dropError: ""));
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

        // ---- #838: what a leg does, not prose about what comes next ----

        private static PlannedSequence Said(string name, string intent, double dv = 100)
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = dv };
        }

        private static string[] Found(string at, params PlannedSequence[] legs)
        {
            return Entries.Find(Snapshot(new Craft(), at), legs, 0).Select(e => e.Step + ":" + e.Body).ToArray();
        }

        private static readonly PlannedSequence[] LandBeforeTheReturn =
        {
            Said("Land on Duna", "Land on Duna before the return to Kerbin"),
            Leg("Ascend from Duna", 1336),
            Leg("Return to Kerbin", 623),
        };

        [Fact]
        public void A_later_return_in_an_intent_leaves_Duna_one_Entry_and_the_return_leg_the_second()
        {
            var entries = Entries.Find(Snapshot(new Craft(), "Duna"), LandBeforeTheReturn, 0);
            Assert.Equal(new[] { "0:Duna", "2:Kerbin" }, entries.Select(e => e.Step + ":" + e.Body).ToArray());
            Assert.False(entries[0].Return);
            Assert.True(entries[1].Return);
        }

        [Fact]
        public void A_two_shield_craft_at_Duna_on_that_plan_uses_shield_2()
        {
            var craft = TwoShields();
            var answer = Call(craft, "Duna", LandBeforeTheReturn);
            Assert.Equal(2.0, answer["number"]);
            Assert.Equal(Uid(craft, "shield2"), answer["shield"]);
        }

        [Fact]
        public void A_capture_with_the_landing_only_in_its_intent_is_still_an_Entry()
        {
            Assert.Equal(new[] { "0:Duna" }, Found("Duna", Said("Capture at Duna", "Capture and land")));
            Assert.Equal(new[] { "0:Duna", "2:Kerbin" }, Found("Duna", Said("Capture at Duna", "Capture and land before the return to Kerbin"), Leg("Ascend from Duna", 1336), Leg("Return to Kerbin", 623)));
        }

        [Fact]
        public void A_name_that_says_nothing_with_a_landing_intent_is_still_an_Entry()
        {
            Assert.Equal(new[] { "0:Duna" }, Found("Duna", Said("Phase three", "Land on Duna")));
            Assert.Equal(new[] { "0:Duna", "2:Kerbin" }, Found("Duna", Said("Phase three", "Land on Duna before the return to Kerbin"), Leg("Ascend from Duna", 1336), Leg("Return to Kerbin", 623)));
        }

        [Fact]
        public void A_return_only_in_an_intent_counts_when_no_later_leg_flies_it()
        {
            // The recorded plans' shape: the leg that ascends also goes home, and its intent is the only place that says so.
            Assert.Equal(new[] { "0:Kerbin" }, Found("Mun", Said("Ascend from the Mun", "Ascend from the Mun surface to a low circular orbit, then return toward Kerbin with a periapsis near 35 km at Kerbin.")));
            Assert.Equal(new[] { "0:Kerbin" }, Found("Mun", Said("Mun return burn", "Eject from Mun orbit onto a Kerbin return path with periapsis near 35 km at Kerbin.")));
            // No return leg on the plan: the words are all there is, so the doubt keeps the stage (n is 2 at Duna, as it was).
            Assert.Equal(2.0, Call(PodShieldStage(), "Duna", new[] { Said("Land on Duna", "Land on Duna before the return to Kerbin") })["number"]);
        }

        [Fact]
        public void An_ascent_that_goes_home_in_its_intent_and_a_later_reentry_are_one_Kerbin_Entry()
        {
            Assert.Equal(new[] { "1:Kerbin" }, Found("Duna", Said("Ascend from Duna", "Ascend from Duna, return to Kerbin, then re-enter and land"), Leg("Reentry at Kerbin", 0)));
        }

        [Fact]
        public void A_Kerbin_landing_in_an_intent_before_a_relaunch_stays_its_own_Entry()
        {
            // Nothing else is named, so the return in this intent isn't about somewhere else: two trips home, two Entries.
            Assert.Equal(new[] { "0:Kerbin", "3:Kerbin" }, Found("Mun", Said("Phase one", "Return to Kerbin and land"), Leg("Launch from Kerbin", 3400), Leg("Transfer to the Mun", 860), Leg("Return to Kerbin", 300)));
            // A trip from home between this leg and the later return: that return isn't this one's.
            Assert.Equal(new[] { "0:Duna", "1:Kerbin", "4:Kerbin" }, Found("Duna", Said("Land on Duna", "Land on Duna"), Said("Phase two", "Ascend from Duna and return to Kerbin"), Leg("Launch from Kerbin", 3400), Leg("Transfer to the Mun", 860), Leg("Return to Kerbin", 300)));
        }

        [Fact]
        public void A_later_leg_only_takes_the_return_if_it_is_an_Entry_itself()
        {
            // By hand or only aerobraking, the later leg isn't counted, so the intent's return still is: the doubt keeps the stage.
            var manual = new PlannedSequence { Name = "Return to Kerbin by hand", Manual = true };
            Assert.Equal(new[] { "0:Kerbin" }, Found("Duna", Said("Land on Duna", "Land on Duna before the return to Kerbin"), manual));
            Assert.Equal(new[] { "0:Kerbin" }, Found("Duna", Said("Land on Duna", "Land on Duna before the return to Kerbin"), Leg("Aerobrake on the return to Kerbin", 0)));
        }

        [Fact]
        public void A_later_leg_another_vessel_flies_doesnt_take_the_return()
        {
            var legs = new[] { Said("Land on Duna", "Land on Duna before the return to Kerbin"), Leg("Return to Kerbin", 623, "Mothership") };
            var found = Entries.Find(Snapshot(new Craft(), "Duna"), legs, 0, s => s.Vessel.Length == 0);
            Assert.Equal(new[] { "0:Kerbin" }, found.Select(e => e.Step + ":" + e.Body).ToArray());
        }

        [Fact]
        public void A_prose_return_gives_way_to_the_next_return_even_when_the_plan_goes_home_twice()
        {
            Assert.Equal(new[] { "0:Duna", "1:Kerbin", "3:Kerbin" }, Found("Duna", Said("Land on Duna", "Land on Duna before the return to Kerbin"), Leg("Return to Kerbin", 623), Leg("Launch from Kerbin", 3400), Leg("Return to Kerbin", 300)));
        }

        [Fact]
        public void A_body_named_only_after_the_return_is_a_doubt_and_the_return_stands()
        {
            Assert.Equal(new[] { "1:Kerbin" }, Found("Duna", Said("Phase three", "Before the return to Kerbin, land on Duna"), Leg("Return to Kerbin", 623)));
            Assert.Equal(new[] { "0:Kerbin", "3:Kerbin" }, Found("Kerbin", Said("Phase one", "Return to Kerbin and land, then head for the Mun"), Leg("Launch for the Mun", 3400), Leg("Transfer to the Mun", 860), Leg("Return to Kerbin", 300)));
        }

        [Fact]
        public void Two_bodies_named_keep_both_Entries_in_the_count()
        {
            // The Mun is where it leaves, not where it lands: reading the Mun would lose this Entry.
            var found = Found("Duna", Said("Phase three", "Leave the Mun and land on Duna before the return to Kerbin"), Leg("Ascend from Duna", 1336), Leg("Return to Kerbin", 623));
            Assert.Equal(2, found.Length);
        }

        [Theory]
        [InlineData("Land on Kerbin")]
        [InlineData("Reentry and landing")]
        public void A_Kerbin_Entry_the_plan_reaches_by_a_transfer_is_the_later_return(string last)
        {
            Assert.Equal(new[] { "0:Duna", "3:Kerbin" }, Found("Duna", Said("Land on Duna", "Land on Duna before the return to Kerbin"), Leg("Ascend from Duna", 1336), Leg("Transfer to Kerbin", 623), Leg(last, 0)));
        }

        [Fact]
        public void A_launch_from_Kerbin_ends_the_search_for_the_later_return()
        {
            var manual = new PlannedSequence { Name = "Launch from Kerbin", Manual = true };
            Assert.Equal(new[] { "0:Kerbin", "3:Kerbin" }, Found("Kerbin", Said("Phase one", "Land on Duna, return to Kerbin"), manual, Leg("Transfer to Duna", 900), Leg("Return to Kerbin", 300)));
            Assert.Equal(new[] { "0:Kerbin", "1:Kerbin" }, Found("Kerbin", Said("Phase one", "Land on the Mun, then return to Kerbin"), Said("Launch from Kerbin", "Launch, fly by the Mun and return to Kerbin")));
        }

        [Fact]
        public void A_real_return_on_an_airless_trip_keeps_its_Entry_before_a_relaunch_the_words_dont_place()
        {
            // "Launch for the Mun" doesn't say it leaves Kerbin. Giving way would lose an Entry here, as the Mun has no air to take it.
            Assert.Equal(new[] { "0:Kerbin", "3:Kerbin" }, Found("Mun", Said("Phase one", "Land on the Mun, then return to Kerbin"), Leg("Launch for the Mun", 3400), Leg("Transfer to the Mun", 860), Leg("Return to Kerbin", 300)));
        }

        [Fact]
        public void Words_after_the_return_belong_to_the_return()
        {
            // Nothing lands at Duna on these: the landing and the reentry come after the return in the words.
            Assert.Equal(new[] { "1:Kerbin" }, Found("Duna", Said("Duna departure", "Depart Duna, return to Kerbin, then land"), Leg("Reentry at Kerbin", 0)));
            Assert.Equal(new[] { "1:Kerbin" }, Found("Duna", Said("Duna ejection", "Ascend from Duna, return to Kerbin and land"), Leg("Reentry at Kerbin", 0)));
            Assert.Equal(new[] { "1:Kerbin" }, Found("Duna", Said("Duna to Kerbin reentry", "Re-enter after the return from Duna"), Leg("Land on Kerbin", 0)));
        }

        [Fact]
        public void A_later_leg_that_ascends_and_returns_home_is_the_later_return()
        {
            Assert.Equal(new[] { "0:Duna", "1:Kerbin" }, Found("Duna", Said("Land on Duna", "Land on Duna before the return to Kerbin"), Leg("Ascend and return to Kerbin", 1959)));
        }

        [Fact]
        public void A_leg_that_names_Kerbin_is_at_Kerbin_whatever_its_intent_says_of_a_return()
        {
            Assert.Equal(new[] { "1:Kerbin" }, Found("Mun", Said("Reentry at Kerbin", "Coming down after the return from the Mun"), Leg("Return to Kerbin", 0)));
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

        // ---- #843: only shields on the stack count ----

        private static readonly IList<PlannedSequence> KerbinReturn = new[] { Leg("Return to Kerbin", 623), Leg("Reentry", 0) };

        private static uint Radial(Craft c, string name, uint parent, params string[] tags)
        {
            var id = c.Add(name, parent, tags);
            c.Parts[c.Parts.Count - 1].SurfaceAttached = true;
            return id;
        }

        [Fact]
        public void Pod_shield_tanks_shield_tanks_counts_two_shields()
        {
            var craft = TwoShields();
            Assert.Equal(Uid(craft, "shield2"), Call(craft, "Duna", DunaThenKerbin)["shield"]);
            Assert.Equal(Uid(craft, "shield1"), Call(craft, "Kerbin", DunaThenKerbin, step: 2)["shield"]);
            Assert.Null(EntryStack.Of(craft.Parts, 1, 3).Shield);
        }

        [Fact]
        public void A_radial_pod_and_shield_on_a_side_decoupler_are_not_counted()
        {
            var craft = PodShieldStage();
            var side = Radial(craft, "sideDecoupler", 4, "decoupler");
            var radialPod = craft.Add("radialPod", side, "command");
            craft.Shield(radialPod, "sideShield");
            Assert.Equal(Uid(craft, "HeatShield1"), Call(craft, "Kerbin", KerbinReturn)["shield"]);
            Assert.Null(EntryStack.Of(craft.Parts, 1, 2).Shield);
        }

        [Fact]
        public void A_shield_past_a_docking_port_is_another_vessel_and_is_not_counted()
        {
            var craft = new Craft();
            var pod = craft.Add("mk1pod.v2", null, "command");
            var shield = craft.Shield(pod, "podShield");
            var port = craft.Add("dockingPort", shield, "docking_port");
            var lander = craft.Add("landerTank", port);
            craft.Shield(lander, "landerShield");
            Assert.Equal(Uid(craft, "podShield"), Call(craft, "Kerbin", KerbinReturn)["shield"]);
            Assert.Null(EntryStack.Of(craft.Parts, 1, 2).Shield);
        }

        [Fact]
        public void A_side_branch_shield_nearer_home_does_not_take_the_stacks_number()
        {
            var craft = new Craft();
            var pod = craft.Add("mk1pod.v2", null, "command");
            Radial(craft, "sideShield", pod, "heat_shield", "decoupler");
            var s1 = craft.Shield(pod, "stackShield");
            var d1 = craft.Add("dec1", s1, "decoupler");
            var tank = craft.Add("tank", d1);
            craft.Shield(tank, "stackShield2");
            Assert.Equal(Uid(craft, "stackShield"), Call(craft, "Kerbin", KerbinReturn)["shield"]);
            Assert.Equal(Uid(craft, "stackShield2"), Call(craft, "Duna", DunaThenKerbin)["shield"]);
        }

        [Fact]
        public void A_radial_shield_is_never_the_Entry_Shield()
        {
            var craft = PodShieldStage();
            Radial(craft, "sideShield", 4, "heat_shield", "decoupler");
            Assert.Equal(Uid(craft, "HeatShield1"), Call(craft, "Kerbin", KerbinReturn)["shield"]);
        }

        [Fact]
        public void Attach_mode_is_not_in_the_snapshots_json()
        {
            var craft = PodShieldStage();
            craft.Parts[3].SurfaceAttached = true;
            var radial = Snapshot(craft, "Kerbin").ToJson();
            craft.Parts[3].SurfaceAttached = false;
            Assert.Equal(Snapshot(craft, "Kerbin").ToJson(), radial);
            Assert.DoesNotContain("attach", radial.ToLowerInvariant());
        }

        // ---- #845: a shield past a docking port is warned about ----

        private static IList<MarginFinding> PastPort(Craft craft, params PlannedSequence[] legs)
        {
            return Entries.ShieldPastPort(Snapshot(craft, "Kerbin"), legs);
        }

        [Fact]
        public void Pod_port_shield_stack_with_an_Entry_warns_that_the_stack_stays_on()
        {
            var craft = new Craft();
            var pod = craft.Add("mk1pod.v2", null, "command");
            var port = craft.Add("dockingPort", pod, "docking_port");
            var shield = craft.Shield(port, "pastShield");
            craft.Add("stackTank", shield);
            var finding = Assert.Single(PastPort(craft, KerbinReturn.ToArray()));
            Assert.Equal(CraftCheckRules.ShieldPastPort, finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("the stack stays on", finding.Explanation);
            Assert.Contains("pastShield", finding.Explanation);
            Assert.Null(EntryStack.Of(craft.Parts, null, 1).Shield);
        }

        [Fact]
        public void Pod_shield_stack_has_no_warning()
        {
            Assert.Empty(PastPort(PodShieldStage(), KerbinReturn.ToArray()));
        }

        [Fact]
        public void A_docked_landers_shield_past_a_port_says_nothing_when_the_Entry_has_its_own_and_it_still_counts()
        {
            var craft = new Craft();
            var pod = craft.Add("mk1pod.v2", null, "command");
            var shield = craft.Shield(pod, "podShield");
            var tank = craft.Add("stackTank", shield);
            var port = craft.Add("dockingPort", tank, "docking_port");
            var lander = craft.Add("landerTank", port);
            craft.Shield(lander, "landerShield");
            Assert.Empty(PastPort(craft, KerbinReturn.ToArray()));
            Assert.Equal(Uid(craft, "podShield"), Call(craft, "Kerbin", KerbinReturn)["shield"]);
        }

        [Fact]
        public void A_port_between_shields_warns_when_any_Entry_has_no_shield_of_its_own()
        {
            var craft = new Craft();
            var pod = craft.Add("mk1pod.v2", null, "command");
            var shieldA = craft.Shield(pod, "shieldA");
            var dec = craft.Add("dec1", shieldA, "decoupler");
            var tank = craft.Add("tank1", dec);
            var port = craft.Add("dockingPort", tank, "docking_port");
            var tank2 = craft.Add("tank2", port);
            craft.Shield(tank2, "shieldB");
            var finding = Assert.Single(PastPort(craft, DunaThenKerbin.ToArray()));
            Assert.Contains("shieldB", finding.Explanation);
        }

        [Fact]
        public void A_radial_shield_does_not_warn()
        {
            var craft = PodShieldStage();
            Radial(craft, "sideShield", 4, "heat_shield", "decoupler");
            Assert.Empty(PastPort(craft, KerbinReturn.ToArray()));
        }

        [Fact]
        public void A_shield_past_a_port_with_no_Entry_in_the_goal_does_not_warn()
        {
            var craft = new Craft();
            var pod = craft.Add("mk1pod.v2", null, "command");
            var port = craft.Add("dockingPort", pod, "docking_port");
            craft.Shield(port, "pastShield");
            Assert.Empty(PastPort(craft, Leg("Circularize in orbit")));
        }

        [Fact]
        public void The_shield_past_a_port_rule_is_the_mods_own_Warning()
        {
            var rule = CraftCheckRules.Find(CraftCheckRules.ShieldPastPort);
            Assert.True(rule.ModOnly);
            Assert.Equal(CraftFindingSeverity.Warning, rule.Severity);
        }
    }
}
