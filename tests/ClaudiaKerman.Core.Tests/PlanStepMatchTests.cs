// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #977: a Sequence Plan's <c>plan_step</c> that names a different Mission Plan sequence from the one its name flies resolves to the
    /// one sequence that clearly matches. The figures are mission m20261110-203402-45d1's (a Duna round trip), copied by hand.
    /// </summary>
    public sealed class PlanStepMatchTests
    {
        private static readonly string[] Bodies = { "Sun", "Kerbin", "Mun", "Minmus", "Moho", "Eve", "Gilly", "Duna", "Ike", "Dres", "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo" };

        private static PlannedSequence Leg(string name, string intent, double deltaV)
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = deltaV, Vessel = "craft" };
        }

        /// <summary>The Mission Plan in force for the crew report and the ascent (the flight's revised plan of 8 sequences).</summary>
        private static List<PlannedSequence> BeforeAscent()
        {
            return new List<PlannedSequence>
            {
                Leg("Launch to parking orbit", "Launch from KSC to an 80 km apoapsis and circularize (flown, Nominal).", 3232),
                Leg("Transfer to Duna, optimal window", "Depart in the optimal Kerbin-Duna window, correct, and capture into a 60 km orbit (flown, Nominal).", 1645),
                Leg("Plane change to Duna equator", "Set the Duna orbit's inclination to 0 deg at the nearest equator crossing (flown, Nominal).", 806),
                Leg("Land at Duna equator", "Hold for the daylight pass, deorbit onto the Lowlands site 0.025 N, 0.169 E, and descend with reentry (flown, Off-Nominal: 809 m/s against 700).", 809),
                Leg("Crew report at Duna", "Run the crewReport experiment from the landed pod and transmit it.", 0),
                Leg("Ascend from Duna", "Launch from the Duna surface to a 60 km Duna orbit and circularize (Duna has air, so launch, not ascend), budget at the minimum of 1,336 m/s.", 1336),
                Leg("Transfer to Kerbin, soonest window", "Depart Duna in the soonest Duna-Kerbin window (UT 178625238, as the player picked), coast to Kerbin, and capture into an 80 km orbit.", 1977),
                Leg("Return to KSC", "Deorbit from Kerbin orbit and reenter to KSC with the heat shield and the Mk16 parachute, with a powered finish if the chute is too fast.", 100),
            };
        }

        /// <summary>The Mission Plan in force for the trip home: the ascent flown at 803 m/s.</summary>
        internal static List<PlannedSequence> AfterAscent()
        {
            var legs = BeforeAscent();
            legs[4] = Leg("Crew report at Duna", "Run the crewReport experiment and transmit it (flown, Nominal).", 0);
            legs[5] = Leg("Ascend from Duna", "Launch from the Duna surface to a 60 km equatorial orbit and circularize (flown, Off-Nominal: margin alert crossed; periapsis 2.1 km under apoapsis).", 803);
            legs[6] = Leg("Transfer to Kerbin, soonest window", "Depart Duna in the soonest Duna-Kerbin window (UT 178624876, as the player picked), coast to Kerbin, and capture into an 80 km orbit.", 1976);
            return legs;
        }

        private static int? Resolve(List<PlannedSequence> legs, string name, int sent, int lastBegun)
        {
            return PlanSteps.Resolve(legs, name, sent, lastBegun, Bodies);
        }

        [Fact]
        public void TheCrewReportSentAsThreeIsStepFive()
        {
            // The landing (step 4) was the last begun.
            Assert.Equal(5, Resolve(BeforeAscent(), "Crew report at Duna", 3, 4));
        }

        [Fact]
        public void TheAscentSentAsFiveIsStepSix()
        {
            Assert.Equal(6, Resolve(BeforeAscent(), "Ascend from Duna", 5, 5));
        }

        [Theory]
        [InlineData("Transfer to Kerbin, soonest window")]
        [InlineData("Transfer to Kerbin, next window")]
        [InlineData("Transfer to Kerbin, corrective")]
        [InlineData("Transfer to Kerbin, capture and lower")]
        public void EveryTryOfTheTransferSentAsFourIsStepSeven(string name)
        {
            // After the ascent (step 6), or as a rewrite of the transfer itself.
            Assert.Equal(7, Resolve(AfterAscent(), name, 4, 6));
            Assert.Equal(7, Resolve(AfterAscent(), name, 4, 7));
        }

        [Fact]
        public void WithTheTransferAtStepSevenTheLaterLegsAreTheReturnAloneAndTheSoonestWindowFits()
        {
            var legs = AfterAscent();
            var step = Resolve(legs, "Transfer to Kerbin, next window", 4, 6).Value;
            // The flight's figure, from the sent step: the crew report, the ascent already flown, the transfer and the return.
            Assert.Equal(2879, MissionCards.LaterLegsDv(legs, 4, true, "Claudia", ""), 3);
            // Still to fly from the transfer on (the Debrief's figure after the ascent): the transfer and the return.
            Assert.Equal(2076, legs.Skip(step - 1).Sum(s => s.DeltaV), 3);
            // plan_transfer keeps back the legs after the transfer: the return.
            var later = MissionCards.LaterLegsDv(legs, step, true, "Claudia", "");
            Assert.Equal(100, later, 3);
            // The flight's soonest window, 1,718 m/s against the craft's 2,441: it fits.
            Assert.True(1718 <= 2441 - later);
        }

        [Theory]
        [InlineData("Hold stable Kerbin orbit", 8)]
        [InlineData("Science at the site", 5)]
        [InlineData("Burn", 3)]
        public void NoClearMatchKeepsTheSentStep(string name, int sent)
        {
            Assert.Null(Resolve(AfterAscent(), name, sent, 7));
        }

        [Theory]
        [InlineData("Crew report at Duna", 5, 4)]
        [InlineData("Ascend from Duna", 6, 5)]
        [InlineData("Transfer to Kerbin, next window", 7, 7)]
        [InlineData("Plane change to Duna equator", 3, 4)]
        public void APlanStepNamingItsOwnSequenceStands(string name, int sent, int lastBegun)
        {
            // A retry of the step in hand, or a step flown again out of order (the plane change after a landing that came short), keeps its number.
            Assert.Null(Resolve(AfterAscent(), name, sent, lastBegun));
        }

        [Fact]
        public void AStepAlreadyFlownGivesWayToTheOneStillToFlyThatMatches()
        {
            var legs = new List<PlannedSequence>
            {
                Leg("Launch to parking orbit", "", 3400),
                Leg("Plane change", "Match the Mun's plane.", 100),
                Leg("Transfer to the Mun", "", 860),
                Leg("Plane change", "Polar orbit at the Mun.", 300),
                Leg("Land on the Mun", "", 600),
            };
            Assert.Equal(4, Resolve(legs, "Plane change", 2, 3));
            // Before the first plane change is flown, the step sent stands.
            Assert.Null(Resolve(legs, "Plane change", 2, 1));
            // Two still to fly and the one sent flown: no clear match.
            legs.Add(Leg("Plane change", "Back to equatorial.", 300));
            Assert.Null(Resolve(legs, "Plane change", 2, 3));
        }

        [Fact]
        public void TheSameActionAtTheSameBodyInOtherWordsMatches()
        {
            Assert.Equal(6, Resolve(BeforeAscent(), "Lift off from Duna", 5, 5));
            // A launch elsewhere is not the ascent from Duna.
            Assert.Null(Resolve(BeforeAscent(), "Lift off", 5, 5));
        }

        [Fact]
        public void ThePlayerAndClaudiaAreToldInOneLine()
        {
            var legs = AfterAscent();
            Assert.Equal("plan_step 4 is \"Land at Duna equator\"; this plan flies step 7, \"Transfer to Kerbin, soonest window\".",
                PlanSteps.Line(legs, 4, 7));
        }

        private static List<PlannedSequence> Legs(params string[] names)
        {
            return names.Select(n => Leg(n, "", 100)).ToList();
        }

        [Theory]
        [InlineData("Transfer to Duna, correction burn")]
        [InlineData("Duna transfer correction")]
        public void ACorrectionStillToFlyKeepsItsStepThoughTheTransferIsNamed(string name)
        {
            var legs = Legs("Launch to orbit", "Transfer to Duna", "Mid-course correction", "Duna capture", "Land");
            Assert.Null(Resolve(legs, name, 3, 2));
        }

        [Fact]
        public void ACircularizationStillToFlyKeepsItsStepThoughTheLowOrbitIsNamed()
        {
            var legs = Legs("Launch to orbit", "Transfer to the Mun", "Circularize", "Low Mun orbit", "Land");
            Assert.Null(Resolve(legs, "Circularize in low Mun orbit", 3, 2));
        }

        [Fact]
        public void AStepStillToFlyNeverGivesWayBackToTheLegJustFlown()
        {
            // The capture (step 3) was just flown; "Circularize" reads as a capture, but the sent step 4 is the next leg.
            var legs = Legs("Launch to orbit", "Transfer to Duna", "Capture into Duna orbit", "Lower to 60 km orbit", "Land");
            Assert.Null(Resolve(legs, "Circularize at Duna", 4, 3));
        }

        [Fact]
        public void ANumberedPrefixInTheNameNeverBeatsTheSentStep()
        {
            var legs = Legs("Leg 1: Launch", "Leg 2: Transfer to Mun", "Leg 3: Land at Mun");
            Assert.Null(Resolve(legs, "Leg 2: Land at Mun", 3, 2));
            Assert.Null(Resolve(legs, "Step 2: Land at Mun", 3, 0));
            // Without the prefix the names still match: the landing sent as the launch's step flies step 3.
            Assert.Equal(3, Resolve(legs, "Leg 1: Land at Mun", 1, 0));
        }

        [Fact]
        public void AStepOutsideTheMissionPlanStaysTheScriptChecksProblem()
        {
            Assert.Null(Resolve(AfterAscent(), "Transfer to Kerbin, next window", 9, 6));
            Assert.Null(Resolve(AfterAscent(), "Transfer to Kerbin, next window", 0, 6));
        }

        [Fact]
        public void APlanStepStillToFlyNeverGivesWayToALegAlreadyFlown()
        {
            // Step 7 sent for a landing named in words only the flown step 4 matches: the step sent stands.
            Assert.Null(Resolve(AfterAscent(), "Land at Duna equator", 7, 6));
            // Sent as a flown step, it takes the flown leg it names (a landing flown again).
            Assert.Equal(4, Resolve(AfterAscent(), "Land at Duna equator", 2, 6));
        }
    }
}
