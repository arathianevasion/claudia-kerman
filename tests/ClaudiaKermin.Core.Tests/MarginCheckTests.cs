// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The mod's own margin check at a Debrief (#574): the level and findings from a recorded Game Snapshot of the Mun Shot v2
    /// in Kerbin orbit (stage 3: 1,678 m/s on 215 kN at 16.2 t; stage 2: 2,226 m/s on 60 kN at 6.2 t; 3,904 m/s in all) against
    /// the Maneuver Sequences still to fly, with no Claude.
    /// </summary>
    public class MarginCheckTests
    {
        private static GameSnapshot InKerbinOrbit()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
        }

        private static PlannedSequence Leg(string name, string intent, double deltaV, bool manual = false)
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = deltaV, Manual = manual };
        }

        /// <summary>b5f3's plan after the launch: 2,850 m/s still to fly.</summary>
        private static List<PlannedSequence> MunLandingAndReturn()
        {
            return new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Land East Crater and crew report", "Land in the Mun's East Crater biome in daylight, then run the crew report on the surface.", 650),
                Leg("Ascend, return and reentry", "Ascend to low Mun orbit, fly the Kerbin return with a 35 km periapsis, then reenter and land under parachute.", 1000),
            };
        }

        private static MarginCheck Check(GameSnapshot snapshot, IList<PlannedSequence> rest, bool remote = false, double? monopropellantUsed = null)
        {
            return MarginCheck.Of(snapshot, rest, 15, 10, remote, monopropellantUsed);
        }

        private static IList<string> Codes(MarginCheck check)
        {
            return check.Findings.Select(f => f.Code).ToList();
        }

        [Fact]
        public void ACraftWithTheRestOfThePlanAndItsMarginToSpareIsEnoughWithNoFindings()
        {
            var check = Check(InKerbinOrbit(), MunLandingAndReturn());

            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.Empty(check.Findings);
            Assert.Equal("3,904 m/s left against 2,850 m/s still to fly (+37%; 15% wanted): enough.", check.Line());
        }

        [Fact]
        public void LessThanTheWantedMarginIsTightAndReportsNotEnoughDeltaVAsTheRecheckWould()
        {
            // 3,500 m/s still to fly needs 4,025 with the 15% margin: the craft has the legs but not the margin.
            var rest = MunLandingAndReturn();
            rest[0].DeltaV = 1850;

            var check = Check(InKerbinOrbit(), rest);

            Assert.Equal(MarginLevel.Tight, check.Level);
            var finding = Assert.Single(check.Findings);
            Assert.Equal("delta_v_short", finding.Code);
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
        }

        [Fact]
        public void LessThanTheRestOfThePlanIsShort()
        {
            var rest = MunLandingAndReturn();
            rest[0].DeltaV = 2500;

            var check = Check(InKerbinOrbit(), rest);

            Assert.Equal(MarginLevel.Short, check.Level);
            Assert.Equal(new[] { "delta_v_short" }, Codes(check));
        }

        [Fact]
        public void AMarginUnderTwentyFivePercentIsAWarningThatLeavesTheLevelEnough()
        {
            // 3,300 m/s still to fly: +18%, over the 15% wanted but under 25%.
            var rest = MunLandingAndReturn();
            rest[0].DeltaV = 1650;

            var check = Check(InKerbinOrbit(), rest);

            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.Equal(new[] { "margin_under_25" }, Codes(check));
        }

        [Fact]
        public void AnUnreliableDeltaVReadingIsNotJudgedAndNeverReportedShort()
        {
            // #234: stock reads 0 at a trickle throttle with an engine lit. Unknown, not 0 m/s.
            var snapshot = InKerbinOrbit();
            snapshot.Stages[0].DeltaVVac = 0;
            snapshot.Stages[0].DeltaVUnreliable = true;

            var check = Check(snapshot, MunLandingAndReturn());

            Assert.Null(check.Margin);
            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.DoesNotContain("delta_v_short", Codes(check));
            Assert.DoesNotContain("margin_under_25", Codes(check));
            Assert.Contains("unknown", check.Line());
            Assert.DoesNotContain("0 m/s left", check.Line());
        }

        [Fact]
        public void ALandingStageUnderOneTwrOnTheMunIsShortAndUnderOneAndAHalfIsAWarning()
        {
            // The landing starts in stage 3 (1,200 m/s into its 1,678) and runs into stage 2. At 9 kN, stage 2 has a TWR of
            // 9,000 / (6,224 × 1.63) = 0.89 on the Mun.
            var weak = InKerbinOrbit();
            weak.Stages[1].ThrustVacKn = 9;
            var check = Check(weak, MunLandingAndReturn());
            Assert.Equal(MarginLevel.Short, check.Level);
            Assert.Contains("twr_below_one", Codes(check));
            Assert.Contains("Mun", check.Findings.Single(f => f.Code == "twr_below_one").Explanation);

            // At 14 kN it is 1.38: a Warning, which the start Craft Check already raised and the player acknowledged.
            var low = InKerbinOrbit();
            low.Stages[1].ThrustVacKn = 14;
            check = Check(low, MunLandingAndReturn());
            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.Equal(new[] { "landing_twr_low" }, Codes(check));
        }

        [Fact]
        public void LandingUnderParachuteOnKerbinIsNotATwrCheck()
        {
            var weak = InKerbinOrbit();
            weak.Stages[1].ThrustVacKn = 9;
            var rest = new List<PlannedSequence> { Leg("Deorbit and land", "Deorbit over the KSC and land under parachute on Kerbin.", 100) };

            Assert.DoesNotContain("twr_below_one", Codes(Check(weak, rest)));
        }

        [Fact]
        public void NoElectricGenerationIsAWarningAndABatteryUnderTheTelemetryCheckLineMakesItTight()
        {
            var snapshot = InKerbinOrbit();
            foreach (var part in snapshot.Craft.Parts) part.Tags.Remove("electric_generator");
            var check = Check(snapshot, MunLandingAndReturn());
            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.Equal(new[] { "no_electric_generation_or_low_battery" }, Codes(check));

            snapshot.Resources.Single(r => r.Name == "ElectricCharge").Amount = 80;
            check = Check(snapshot, MunLandingAndReturn());
            Assert.Equal(MarginLevel.Tight, check.Level);

            snapshot.Resources.Single(r => r.Name == "ElectricCharge").Amount = 0;
            Assert.Equal(MarginLevel.Short, Check(snapshot, MunLandingAndReturn()).Level);
        }

        [Fact]
        public void MonopropellantUnderWhatTheLastRunUsedForEachLegLeftIsTight()
        {
            // 128 units left; the run used 50, and three legs are left to fly: 150 wanted.
            var check = Check(InKerbinOrbit(), MunLandingAndReturn(), monopropellantUsed: 50);
            Assert.Equal(MarginLevel.Tight, check.Level);
            Assert.Equal(new[] { "" }, Codes(check));
            Assert.Contains("Monopropellant", check.Findings[0].Title);

            Assert.Equal(MarginLevel.Enough, Check(InKerbinOrbit(), MunLandingAndReturn(), monopropellantUsed: 2).Level);
        }

        [Fact]
        public void AManualStretchWithOnlyClaudiaAboardIsAWarningEvenWithHerTraitTitled()
        {
            // The recorded crew list gives her trait as "Super Intelligence".
            var rest = MunLandingAndReturn();
            rest.Insert(2, Leg("Manual control", "The player flies the surface stretch by hand.", 0, manual: true));

            var check = Check(InKerbinOrbit(), rest);

            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.Equal(new[] { "manual_stretches_without_crew" }, Codes(check));
        }

        [Fact]
        public void ACrewedReturnWithNoParachutesAndARemoteCraftWithNoProbeCoreAreShort()
        {
            var snapshot = InKerbinOrbit();
            foreach (var part in snapshot.Craft.Parts) part.Tags.Remove("parachute");
            Assert.Equal(new[] { "no_parachutes_for_return" }, Codes(Check(snapshot, MunLandingAndReturn())));
            Assert.Equal(MarginLevel.Short, Check(snapshot, MunLandingAndReturn()).Level);

            var remote = InKerbinOrbit();
            foreach (var part in remote.Craft.Parts) part.Tags.Remove("probe_core");
            var check = Check(remote, MunLandingAndReturn(), remote: true);
            Assert.Equal(MarginLevel.Short, check.Level);
            Assert.Contains("no_probe_core_or_antenna", Codes(check));
        }

        [Fact]
        public void TheDebriefTextCarriesTheLineAndEachFindingWithItsSeverity()
        {
            var rest = MunLandingAndReturn();
            rest[0].DeltaV = 1850;

            var text = Check(InKerbinOrbit(), rest).Text();

            Assert.StartsWith("The mod's margin check (the craft as it is now against the Maneuver Sequences still to fly): 3,904 m/s left against 3,500 m/s still to fly", text);
            Assert.Contains("Blocker: Not enough delta-v", text);
        }

        [Fact]
        public void NothingLeftToFlyIsEnoughWithNoFigure()
        {
            var check = Check(InKerbinOrbit(), new List<PlannedSequence>());

            Assert.Equal(MarginLevel.Enough, check.Level);
            Assert.Null(check.Margin);
            Assert.Empty(check.Findings);
        }
    }
}
