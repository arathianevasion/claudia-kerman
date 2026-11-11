// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #979: what may leave with an empty tank in drop_empty's "auto" drop besides trim. A heat shield always; landing legs once no
    /// landing is left in the Mission Plan. From flight m20261110-203402-45d1 (Duna round trip): four empty radial FL-T400s, each with a
    /// heat shield (and, after the player's redesign, legs), rode home from Duna.
    /// </summary>
    public sealed class AutoDropTests
    {
        private static PlannedSequence Leg(string name, string intent, bool manual = false)
        {
            return new PlannedSequence { Name = name, Intent = intent, Manual = manual };
        }

        /// <summary>The Mission Plan in hand for the Duna ascent (index 5), as Claudia proposed it, words copied from the flight's transcript.</summary>
        private static List<PlannedSequence> ThisFlight()
        {
            return new List<PlannedSequence>
            {
                Leg("Launch to parking orbit", "Launch from KSC to an 80 km apoapsis and circularize (flown, Nominal)."),
                Leg("Transfer to Duna, optimal window", "Depart in the optimal Kerbin-Duna window, correct, and capture into a 60 km orbit (flown, Nominal)."),
                Leg("Plane change to Duna equator", "Set the Duna orbit's inclination to 0 deg at the nearest equator crossing (flown, Nominal)."),
                Leg("Land at Duna equator", "Hold for the daylight pass, deorbit onto the Lowlands site 0.025 N, 0.169 E, and descend with reentry (flown, Off-Nominal: 809 m/s against 700)."),
                Leg("Crew report at Duna", "Run the crewReport experiment from the landed pod and transmit it."),
                Leg("Ascend from Duna", "Launch from the Duna surface to a 60 km Duna orbit and circularize (Duna has air, so launch, not ascend), budget at the minimum of 1,336 m/s."),
                Leg("Transfer to Kerbin, soonest window", "Depart Duna in the soonest Duna-Kerbin window (UT 178625238, as the player picked), coast to Kerbin, and capture into an 80 km orbit."),
                Leg("Return to KSC", "Deorbit from Kerbin orbit and reenter to KSC with the heat shield and the Mk16 parachute, with a powered finish if the chute is too fast."),
            };
        }

        private const int DunaAscent = 5;

        [Fact]
        public void AfterTheDunaLandingThisFlightsLegsAreFree()
        {
            Assert.True(AutoDrop.LegsFree(ThisFlight(), DunaAscent));
            Assert.True(AutoDrop.LegsFree(ThisFlight(), DunaAscent + 1));
        }

        [Fact]
        public void BeforeOrDuringTheDunaLandingTheyAreNot()
        {
            for (var step = -1; step <= 3; step++) Assert.False(AutoDrop.LegsFree(ThisFlight(), step), "step " + step);
        }

        [Fact]
        public void ALaterLandingKeepsTheLegs()
        {
            var plan = ThisFlight();
            plan.Insert(7, Leg("Land on Ike", "Deorbit and touch down near the Ike equator."));
            Assert.False(AutoDrop.LegsFree(plan, DunaAscent));
        }

        [Theory]
        [InlineData("Descend to the Mun", "Deorbit over the crater and descend on the engine.", false)]
        [InlineData("Mun landing", "Powered descent.", false)]
        [InlineData("Duna entry", "Reenter Duna's air and come down under the chutes.", false)]
        [InlineData("Touch down", "", false)]
        [InlineData("Hop", "Land on the next ridge, flown by hand.", true)]
        public void LandDeorbitAndReentryStillToFlyAreLandings(string name, string intent, bool manual)
        {
            var plan = new List<PlannedSequence> { Leg("Ascend from Duna", "Launch to orbit."), Leg(name, intent, manual) };
            Assert.False(AutoDrop.LegsFree(plan, 0));
        }

        [Fact]
        public void TheSequenceInHandCountsToo()
        {
            var plan = new List<PlannedSequence> { Leg("Land on the Mun", "Deorbit and touch down."), Leg("Return to Kerbin", "Ascend and come home.") };
            Assert.False(AutoDrop.LegsFree(plan, 0));
            Assert.True(AutoDrop.LegsFree(plan, 1));
        }

        /// <summary>
        /// Flight m20261111-033300-95d4: the Mission Plan in hand for the Duna ascent, after the Duna landing. The Kerbin return says it
        /// lands, and the empty radials (legs on them) still rode to orbit. Both intents the step had in flight, verbatim.
        /// </summary>
        [Theory]
        [InlineData("Eject at the soonest Duna-to-Kerbin window toward a 35 km periapsis at Kerbin, coast to the encounter, then reenter and land.")]
        [InlineData("Eject from Duna orbit at the soonest Duna-to-Kerbin window toward a 35 km periapsis at Kerbin, coast to the encounter, then reenter and land.")]
        public void AfterTheDunaLandingAKerbinReturnThatSaysItLandsLeavesTheLegsFree(string intent)
        {
            var plan = new List<PlannedSequence>
            {
                Leg("Launch from Duna to orbit", "Launch from the Duna surface to a 60 km orbit and circularize."),
                Leg("Return to Kerbin and reentry", intent),
            };
            Assert.True(AutoDrop.LegsFree(plan, 0));
            Assert.Equal(new[] { "shield", "leg" }, AutoDrop.FreeKinds(plan, 0).ToArray());
        }

        // The player's ruling (second fix round): a Kerbin return never counts as a landing on its own, whatever its words; only one that
        // also comes down on another stock body is.
        [Theory]
        [InlineData("Return to Kerbin", "Reenter and land on the KSC runway.")]
        [InlineData("Return to Kerbin and touch down at KSC", "")]
        [InlineData("Return to KSC", "Deorbit from Kerbin orbit, reenter and descend under the Mk16 chute.")]
        [InlineData("Return from the Mun to Kerbin and land", "")]
        [InlineData("Return from the Mun to Kerbin", "Deorbit and reenter over KSC under the chutes.")]
        [InlineData("Come home from Duna", "Reentry and descent under chutes.")]
        [InlineData("Return to Kerbin", "Depart Duna in the soonest window, then reenter and land.")]
        [InlineData("Return to Kerbin", "Leave Mun orbit, then reenter and land.")]
        [InlineData("Return to Kerbin", "Burn Ike to Kerbin, then reenter and land.")]
        [InlineData("Return to Kerbin", "Eject from low Duna orbit at the soonest window, then reenter and land.")]
        [InlineData("Return to Kerbin", "Burn from low Duna orbit at the soonest window, then reenter and land.")]
        [InlineData("Return to Kerbin", "Ejection from Duna at the soonest window, then reenter and land.")]
        // Fix round 3: more ways of leaving Duna (or the Mun) for home.
        [InlineData("Return to Kerbin", "Escape Duna's SOI toward Kerbin, then reenter and land.")]
        [InlineData("Return to Kerbin", "Burn out of Duna orbit at the soonest window, coast to Kerbin, then reenter and land.")]
        [InlineData("Return to Kerbin", "Leave low Duna orbit at the soonest window, then reenter and land.")]
        [InlineData("Return to Kerbin", "Duna–Kerbin transfer at the soonest window, then reenter and land.")]
        [InlineData("Return to Kerbin", "Duna → Kerbin transfer, then reenter and land.")]
        [InlineData("Return to Kerbin", "Ejection burn at Duna, then reenter Kerbin's atmosphere and land.")]
        [InlineData("Return to Kerbin", "Burn at Mun periapsis for Kerbin, then reenter and land.")]
        [InlineData("Return to Kerbin", "Wait in Duna orbit for the window, then eject, reenter and land.")]
        [InlineData("Return to Kerbin", "Eject from Duna orbit; coast past Ike; reenter and land.")]
        // A line break doesn't hide the return.
        [InlineData("Duna departure", "Eject toward Kerbin.\nReturn, reenter and land.")]
        public void AKerbinReturnUnderChutesIsNoLanding(string name, string intent)
        {
            var plan = new List<PlannedSequence> { Leg("Ascend from Duna", "Launch to orbit."), Leg(name, intent) };
            Assert.True(AutoDrop.LegsFree(plan, 0));
        }

        /// <summary>
        /// Fix round 3: a landing word after the body, before the next reentry word or Kerbin, comes down there, even when the body follows
        /// "leave", "depart" or "from ... orbit". The name says nothing of the body, so only the intent tells.
        /// </summary>
        [Theory]
        [InlineData("Leave Mun orbit and land at the crater, then return to Kerbin")]
        [InlineData("Depart Mun orbit and land at the crater, then return to Kerbin.")]
        [InlineData("Leave Minmus orbit and touch down on the flats, then return to Kerbin.")]
        [InlineData("From low Mun orbit, burn retrograde and land near the crater, then return to Kerbin.")]
        [InlineData("From Mun orbit, land at the crater for science, then return to Kerbin.")]
        [InlineData("Burn from Mun orbit and touch down at the crater, then return to Kerbin.")]
        [InlineData("Come down from Mun orbit and land at the crater, then return to Kerbin.")]
        [InlineData("Depart Mun orbit for a powered descent to the crater, then return to Kerbin.")]
        [InlineData("Leaving Mun orbit, descend to the crater; later return to Kerbin.")]
        public void LeavingAMoonsOrbitToLandThereIsALanding(string intent)
        {
            Assert.False(AutoDrop.LegsFree(new List<PlannedSequence> { Leg("Ascend from Duna", "Launch to orbit."), Leg("Second stop", intent) }, 0));
        }

        [Fact]
        public void LeavingAMoonsOrbitToLandThereIsALandingAsTheNameAlone()
        {
            Assert.False(AutoDrop.LegsFree(new List<PlannedSequence> { Leg("Leave Mun orbit and land at the crater, then return to Kerbin", "") }, 0));
        }

        /// <summary>The name and intent don't read as one clause: "Return to Kerbin" then "From Mun orbit, land ..." lands on the Mun.</summary>
        [Fact]
        public void TheNameAndIntentAreSeparateClauses()
        {
            Assert.False(AutoDrop.LegsFree(new List<PlannedSequence> { Leg("Return to Kerbin", "From Mun orbit, land at the crater for science, then reenter at Kerbin.") }, 0));
        }

        [Theory]
        [InlineData("Transfer from Kerbin orbit to the Mun and land", "")]
        [InlineData("Return to Kerbin orbit, refuel, then depart for the Mun and land", "")]
        public void AFlightOutToLandIsALanding(string name, string intent)
        {
            Assert.False(AutoDrop.LegsFree(new List<PlannedSequence> { Leg(name, intent) }, 0));
        }

        [Theory]
        [InlineData("Mun and home", "Deorbit to the Mun crater, then return to Kerbin.")]
        [InlineData("Deorbit to the Mun crater, then return to Kerbin and land", "")]
        [InlineData("Return to Kerbin via Minmus", "Descend to the Minmus flats for a sample, then come home.")]
        [InlineData("Second stop", "Deorbit from Mun orbit to the crater, then return to Kerbin.")]
        [InlineData("Land at the Mun's crater, then return to Kerbin", "")]
        [InlineData("Mun and home", "Descend from Mun orbit to the crater, then return to Kerbin.")]
        [InlineData("Mun and home", "Do the deorbit burn from the Mun’s orbit, then return to Kerbin.")]
        public void AReturnThatAlsoComesDownOnAnotherBodyIsALanding(string name, string intent)
        {
            var plan = new List<PlannedSequence> { Leg("Ascend from Duna", "Launch to orbit."), Leg(name, intent) };
            Assert.False(AutoDrop.LegsFree(plan, 0));
        }

        [Theory]
        [InlineData("Mun descent", "Descend on the engine to the crater.")]
        [InlineData("Duna", "Powered descent to the Lowlands.")]
        [InlineData("Ike", "Descending to the equator.")]
        public void DescendAndDescentAreLandings(string name, string intent)
        {
            var plan = new List<PlannedSequence> { Leg("Ascend from Duna", "Launch to orbit."), Leg(name, intent) };
            Assert.False(AutoDrop.LegsFree(plan, 0));
        }

        [Fact]
        public void WithNoPlanTheLegsStay()
        {
            Assert.False(AutoDrop.LegsFree(null, 0));
            Assert.False(AutoDrop.LegsFree(null, -1));
        }

        [Fact]
        public void AShieldIsAlwaysFreeAndLegsOnlyWithNoLandingLeft()
        {
            Assert.Equal(new[] { "shield" }, AutoDrop.FreeKinds(null, 0).ToArray());
            Assert.Equal(new[] { "shield" }, AutoDrop.FreeKinds(ThisFlight(), 3).ToArray());
            Assert.Equal(new[] { "shield", "leg" }, AutoDrop.FreeKinds(ThisFlight(), DunaAscent).ToArray());
        }

        [Fact]
        public void TheBlockCallGivesTheFreeKinds()
        {
            Assert.Equal(new object[] { "shield", "leg" }, ((IList<object>)BlockCalls.AutoDropFree(ThisFlight(), DunaAscent)["free"]).ToArray());
            Assert.Equal(new object[] { "shield" }, ((IList<object>)BlockCalls.AutoDropFree(null, 0)["free"]).ToArray());
            Assert.Equal("", BlockCalls.AutoDropFree(null, 0)["error"]);
        }

        /// <summary>drop_empty asks every look, twice a second: the words of a ten-leg plan, read well under a millisecond.</summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void OneAnswerTakesUnderAMillisecond()
        {
            var plan = ThisFlight();
            plan.AddRange(ThisFlight().Take(2));
            Timing.Within(1, () => BlockCalls.AutoDropFree(plan, DunaAscent));
        }
    }
}
