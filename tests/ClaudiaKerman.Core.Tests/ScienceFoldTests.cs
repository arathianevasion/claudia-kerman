// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #983: a Mission Plan step that is only science, 0 m/s, right before a sequence that starts where the craft already is, is flown
    /// as the first lines of that sequence's script, not as a Maneuver Sequence of its own. The plans are mission m20261110-203402-45d1's
    /// (a Duna round trip), copied by hand from its events.jsonl.
    /// </summary>
    public sealed class ScienceFoldTests
    {
        private const string CrewReportIntoAscent =
            "Step 5 \"Crew report at Duna\" is science only, 0 m/s, where step 6 \"Ascend from Duna\" starts: fly it as the first lines of step 6's script (plan_step 6), not as a sequence of its own.";

        private static PlannedSequence Leg(string name, string intent, double deltaV, string timing = "")
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = deltaV, Timing = timing, Vessel = "craft" };
        }

        /// <summary>The revised plan approved landed at Duna, flown legs first (the plan the crew report was flown from).</summary>
        private static List<PlannedSequence> RevisedLanded()
        {
            return new List<PlannedSequence>
            {
                Leg("Launch to parking orbit", "Launch from KSC to an 80 km apoapsis and circularize (flown, Nominal).", 3232, "Flown."),
                Leg("Transfer to Duna, optimal window", "Depart in the optimal Kerbin-Duna window, correct, and capture into a 60 km orbit (flown, Nominal).", 1645, "Flown."),
                Leg("Plane change to Duna equator", "Set the Duna orbit's inclination to 0 deg at the nearest equator crossing (flown, Nominal).", 806, "Flown."),
                Leg("Land at Duna equator", "Hold for the daylight pass, deorbit onto the Lowlands site 0.025 N, 0.169 E, and descend with reentry (flown, Off-Nominal: 809 m/s against 700).", 809, "Flown; landed 0.7 km from the site at 2 m/s."),
                Leg("Crew report at Duna", "Run the crewReport experiment from the landed pod and transmit it.", 0, "Now, on the surface."),
                Leg("Ascend from Duna", "Launch from the Duna surface to a 60 km Duna orbit and circularize (Duna has air, so launch, not ascend), budget at the minimum of 1,336 m/s.", 1336, "After the crew report, about 40 minutes of flight."),
                Leg("Transfer to Kerbin, soonest window", "Depart Duna in the soonest Duna-Kerbin window (UT 178625238, as the player picked), coast to Kerbin, and capture into an 80 km orbit.", 1977, "Departure about 118 days from now (in the Duna orbit until then); transit about 87 days."),
                Leg("Return to KSC", "Deorbit from Kerbin orbit and reenter to KSC with the heat shield and the Mk16 parachute, with a powered finish if the chute is too fast.", 100, "Right after the Kerbin capture."),
            };
        }

        /// <summary>The plan from the pad: the crew report is step 4, between the landing and the ascent.</summary>
        private static List<PlannedSequence> FromThePad()
        {
            return new List<PlannedSequence>
            {
                Leg("Launch to parking orbit", "Launch from KSC at the optimal launch window (inclination -3.03 deg) to an 80 km apoapsis, then circularize.", 3418, "Launch window UT 162366330, about 7.6 days from now."),
                Leg("Transfer to Duna, optimal window", "Depart in the optimal window and capture into a 60 km Duna orbit.", 1695, "Departure about 4.4 h after launch."),
                Leg("Land at Duna equator", "Deorbit onto the equator and descend with reentry.", 700, "Deorbit at the next pass over the chosen site after capture."),
                Leg("Crew report at Duna", "Run the crewReport experiment from the landed pod and transmit it.", 0, "Right after touchdown."),
                Leg("Ascend from Duna", "Launch from the Duna surface to a 60 km Duna orbit and circularize.", 1476, "After the crew report, about 40 minutes of flight."),
                Leg("Transfer to Kerbin, soonest window", "Depart Duna in the soonest window and capture into an 80 km Kerbin orbit.", 1977, "Soonest window, about 120 days after the Duna arrival."),
                Leg("Return to KSC", "Deorbit and reenter to KSC.", 100, "Right after the Kerbin capture."),
            };
        }

        [Fact]
        public void TheRevisedPlanLandedAtDunaFoldsTheCrewReportIntoTheAscent()
        {
            var legs = RevisedLanded();
            Assert.Equal(5, ScienceFold.FirstToFly(legs));
            Assert.Equal(CrewReportIntoAscent, ScienceFold.Line(legs, 5));
        }

        [Fact]
        public void APlanOfTheLegsStillToFlyFoldsItsFirstStepIntoItsSecond()
        {
            // The flight's plan revised right after touchdown listed only what was left, the crew report first.
            var legs = RevisedLanded().Skip(4).ToList();
            Assert.Equal(1, ScienceFold.FirstToFly(legs));
            Assert.Equal(
                "Step 1 \"Crew report at Duna\" is science only, 0 m/s, where step 2 \"Ascend from Duna\" starts: fly it as the first lines of step 2's script (plan_step 2), not as a sequence of its own.",
                ScienceFold.Line(legs, 1));
        }

        [Fact]
        public void ThePlanFromThePadSaysNothingAtLaunchAndFoldsTheCrewReportOnceItIsNext()
        {
            var legs = FromThePad();
            Assert.Equal(1, ScienceFold.FirstToFly(legs));
            Assert.Equal("", ScienceFold.Line(legs, 1));
            Assert.Equal("", ScienceFold.Line(legs, 3));
            Assert.Equal(
                "Step 4 \"Crew report at Duna\" is science only, 0 m/s, where step 5 \"Ascend from Duna\" starts: fly it as the first lines of step 5's script (plan_step 5), not as a sequence of its own.",
                ScienceFold.Line(legs, 4));
        }

        [Fact]
        public void APlanWithNoScienceOnlyStepGetsNoLineAtAnyStep()
        {
            var legs = RevisedLanded();
            legs.RemoveAt(4);
            Assert.All(Enumerable.Range(0, legs.Count + 2), step => Assert.Equal("", ScienceFold.Line(legs, step)));
        }

        [Fact]
        public void AZeroBurnStepThatIsNoScienceIsNoFold()
        {
            // The debrief tests' plan: a 0 m/s link check before a burn.
            var legs = new List<PlannedSequence> { Leg("Link check", "Prove the link.", 0, "now"), Leg("Second leg", "Go on.", 1000, "after") };
            Assert.Equal("", ScienceFold.Line(legs, 1));
        }

        [Fact]
        public void TheCrewReportStaysASequenceWhenItCannotRideTheNextScript()
        {
            Assert.Equal("", ScienceFold.Line(With(4, s => s.Manual = true), 5));
            Assert.Equal("", ScienceFold.Line(With(4, s => s.DeltaV = 30), 5));
            Assert.Equal("", ScienceFold.Line(With(4, s => s.DueTimeUt = 168430000), 5));
            Assert.Equal("", ScienceFold.Line(With(4, s => { s.Name = "Crew report over the Highlands"; s.Timing = "Next pass over the Highlands."; }), 5));
            Assert.Equal("", ScienceFold.Line(With(5, s => s.Manual = true), 5));
            Assert.Equal("", ScienceFold.Line(With(5, s => s.Vessel = "Lander"), 5));
            Assert.Equal("", ScienceFold.Line(With(5, s => s.DueTimeUt = 168430000), 5));
            // The last step has no script after it to ride.
            Assert.Equal("", ScienceFold.Line(RevisedLanded().Take(5).ToList(), 5));
        }

        [Theory]
        [InlineData("Crew report (landed)")]
        [InlineData("Crew report after landing")]
        [InlineData("Crew report after touchdown")]
        public void ACrewReportNamedForWhereTheCraftAlreadyIsStillFolds(string name)
        {
            var legs = With(4, s => s.Name = name);
            Assert.StartsWith("Step 5 \"" + name + "\" is science only", ScienceFold.Line(legs, 5));
        }

        [Fact]
        public void AStepThatLandsAndReportsIsNoScienceOnlyStep()
        {
            // 0 m/s on chutes alone is still a landing.
            Assert.Equal("", ScienceFold.Line(With(4, s => s.Name = "Land on chutes and send a crew report"), 5));
        }

        [Fact]
        public void TheCraftIsTheSameVesselWhetherNamedOrLeftBlank()
        {
            var legs = With(4, s => s.Vessel = "");
            Assert.Equal(CrewReportIntoAscent, ScienceFold.Line(legs, 5));
        }

        [Theory]
        [InlineData("Crew report in Duna orbit", "Return to Kerbin")]
        [InlineData("Science in Kerbin orbit", "Transfer to Duna")]
        [InlineData("Crew report at Duna", "Plane change to Duna equator")]
        public void ScienceBeforeAnythingButAnAscentIsNoFold(string science, string next)
        {
            // A script starting with science() hides an orbital departure's burn point from BurnPoints.First: no Due Time, no kept return window.
            var legs = new List<PlannedSequence> { Leg(science, "Run the experiments and transmit.", 0, "Now."), Leg(next, "Depart in the soonest window.", 1000, "Soonest window.") };
            Assert.Equal("", ScienceFold.Line(legs, 1));
        }

        [Fact]
        public void AnAscentNamedOnlyInItsIntentStillTakesTheFold()
        {
            var legs = With(5, s => { s.Name = "To Duna orbit"; s.Intent = "Lift off from the Duna surface to a 60 km orbit."; });
            Assert.StartsWith("Step 5 \"Crew report at Duna\" is science only", ScienceFold.Line(legs, 5));
        }

        [Theory]
        [InlineData("EVA report at Duna")]
        [InlineData("Surface sample at Duna")]
        [InlineData("Crew report and EVA report at Duna")]
        [InlineData("Crew report and samples at Duna")]
        public void EvaReportsAndSamplesAreThePlayersJobAndNoFold(string name)
        {
            // science.ks: "EVA reports and samples are the player's job"; folded into the ascent they would be lost at lift-off.
            Assert.Equal("", ScienceFold.Line(With(4, s => s.Name = name), 5));
        }

        private static List<PlannedSequence> With(int index, System.Action<PlannedSequence> change)
        {
            var legs = RevisedLanded();
            change(legs[index]);
            return legs;
        }
    }
}
