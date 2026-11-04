// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
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
        public void AComeHomeLegFromTheMunIsAtKerbinForTheMarginCheckAndTheChuteAllocationAlike()
        {
            // Stage 2 at 9 kN is 0.89 on the Mun. The come-home leg starts in stage 3 (1,200 m/s into its 1,678) and runs into stage 2;
            // the ascent after it is also on Kerbin (#726). Read at the Mun, both would be a TWR finding.
            var weak = InKerbinOrbit();
            weak.Stages[1].ThrustVacKn = 9;
            var rest = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Come home from the Mun and land", "Come home from the Mun and land under parachute.", 650),
                Leg("Ascend and circularize", "Ascend to orbit and circularize.", 300),
            };

            var check = Check(weak, rest);
            Assert.DoesNotContain("twr_below_one", Codes(check));
            Assert.DoesNotContain("landing_twr_low", Codes(check));

            var entry = Assert.Single(ChuteAllocation.Of(weak, rest, 0, false, null).Entries);
            Assert.Equal("Kerbin", entry.Body);
            Assert.True(entry.Return);
        }

        [Fact]
        public void ALandOnTheMunAndReturnToKerbinKeepsTheMunTwrCheckChuteEntryAtKerbinAndTheNextLegAtKerbin()
        {
            // Stage 2 at 9 kN is 0.89 on the Mun (#726). The leg lands on the Mun, then returns to Kerbin: the Margin check reads its TWR
            // at the Mun; Chute Allocation counts its entry at Kerbin; the ascent after it is at Kerbin, so it has no TWR line.
            var weak = InKerbinOrbit();
            weak.Stages[1].ThrustVacKn = 9;
            var rest = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Land on the Mun and return to Kerbin", "Land on the Mun, then return to Kerbin under parachute.", 650),
                Leg("Ascend and circularize", "Ascend to orbit and circularize.", 300),
            };

            var check = Check(weak, rest);
            var twr = check.Findings.Single(f => f.Code == "twr_below_one").Explanation;
            Assert.Contains("on Mun for \"Land on the Mun and return to Kerbin\"", twr);
            Assert.DoesNotContain("Ascend and circularize", twr);

            var entry = Assert.Single(ChuteAllocation.Of(weak, rest, 0, false, null).Entries);
            Assert.Equal("Kerbin", entry.Body);
            Assert.True(entry.Return);
        }

        [Theory]
        [InlineData("Land on the surface of the Mun and return to Kerbin")]
        [InlineData("Land in the Mun highlands and return to Kerbin")]
        [InlineData("Descend to the Mun and land, then return to Kerbin")]
        [InlineData("Ascend from the Mun and return home")]
        public void AReturnHomeLegThatLandsOrAscendsOnTheMunIsJudgedAtTheMunForItsTwr(string name)
        {
            // Stage 2 at 9 kN is 0.89 on the Mun (#740): the return's surface work is at the Mun, not at Kerbin, whatever the phrasing.
            var weak = InKerbinOrbit();
            weak.Stages[1].ThrustVacKn = 9;
            var rest = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg(name, string.Empty, 650),
            };

            var twr = Check(weak, rest).Findings.Single(f => f.Code == "twr_below_one").Explanation;
            Assert.Contains("on Mun for \"" + name + "\"", twr);
        }

        [Fact]
        public void TheLegAfterAReturnHomeIsAtKerbinSoAMunAscentBelowOneTwrIsNotFlaggedThere()
        {
            // Stage 2 at 9 kN is 0.89 on the Mun. The same ascent is flagged when it follows a landing on the Mun (the control), so
            // its absence after the return proves the leg is at Kerbin, where it has no TWR line (#740).
            var weak = InKerbinOrbit();
            weak.Stages[1].ThrustVacKn = 9;
            var control = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Land on the Mun", "Land on the Mun and stay there.", 650),
                Leg("Ascend and circularize", "Ascend to orbit and circularize.", 300),
            };
            Assert.Contains("on Mun for \"Ascend and circularize\"", Check(weak, control).Findings.Single(f => f.Code == "twr_below_one").Explanation);

            var rest = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Land on the Mun and return to Kerbin", "Land on the Mun, then return to Kerbin under parachute.", 650),
                Leg("Ascend and circularize", "Ascend to orbit and circularize.", 300),
            };
            var check = Check(weak, rest);
            Assert.DoesNotContain("Ascend and circularize", check.Findings.Single(f => f.Code == "twr_below_one").Explanation);
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
            // A return that doesn't come down through Kerbin's air needs no parachute.
            var toOrbit = new List<PlannedSequence> { Leg("Return to orbit", "Ascend from the Mun and return to low Mun orbit.", 600) };
            Assert.DoesNotContain("no_parachutes_for_return", Codes(Check(snapshot, toOrbit)));

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

        // ---- dock (#456): a docking port, RCS and monopropellant, and port sizes the Target Vessel has free ----

        private static List<PlannedSequence> DockAtTheStation()
        {
            return new List<PlannedSequence> { Leg("Dock with the station", "Close from the rendezvous hand-off and dock with Kerbin Station's free port.", 10) };
        }

        /// <summary>The fixture's craft with a size1 port, RCS and 30 units of monopropellant, and a Target Vessel with free ports.</summary>
        private static GameSnapshot ReadyToDock(string craftPort = "size1", params string[] targetPorts)
        {
            var snapshot = InKerbinOrbit();
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 9001, Name = "dockingPort2", Title = "Clamp-O-Tron Docking Port", PortSize = craftPort, Tags = { "docking_port" } });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 9002, Name = "RCSBlock.v2", Title = "RV-105 RCS Thruster Block", Tags = { "rcs" } });
            snapshot.Resources.Add(new SnapshotResource { Name = "MonoPropellant", Amount = 30, Max = 30 });
            snapshot.Target = new SnapshotTarget { Name = "Kerbin Station", Body = "Kerbin", Situation = "ORBITING" };
            foreach (var port in targetPorts.Length > 0 ? targetPorts : new[] { "size1", "size2" }) snapshot.Target.FreePorts[port] = 1;
            return snapshot;
        }

        [Fact]
        public void ADockingWithAPortRcsAndMonopropellantOfAMatchingSizeHasNoDockingFinding()
        {
            var check = Check(ReadyToDock(), DockAtTheStation());
            Assert.Empty(check.Findings);
            Assert.Equal(MarginLevel.Enough, check.Level);
        }

        [Theory]
        [InlineData("docking_port", "A docking is still to fly and the craft has no docking port")]
        [InlineData("rcs", "A docking is still to fly and the craft has no RCS")]
        [InlineData("MonoPropellant", "A docking is still to fly and the craft has no monopropellant")]
        public void ADockingWithoutAPortRcsOrMonopropellantIsABlocker(string missing, string explanation)
        {
            var snapshot = ReadyToDock();
            if (missing == "MonoPropellant") snapshot.Resources.RemoveAll(r => r.Name == missing);
            else snapshot.Craft.Parts.RemoveAll(p => p.Tags.Contains(missing));
            var check = Check(snapshot, DockAtTheStation());
            Assert.Equal(MarginLevel.Short, check.Level);
            var finding = Assert.Single(check.Findings);
            Assert.Equal(CraftCheckRules.DockingKit, finding.Code);
            Assert.Equal("Blocker: Nothing to dock with. " + explanation, finding.Describe());
        }

        [Fact]
        public void APortSizeTheTargetHasNoneFreeOfIsAWarning()
        {
            var check = Check(ReadyToDock("size0", "size1", "size2"), DockAtTheStation());
            var finding = Assert.Single(check.Findings);
            Assert.Equal(CraftCheckRules.DockingPortSize, finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Equal("Warning: Docking port sizes may not match. The craft's ports (size0) match none of Kerbin Station's free ports (1 size1, 1 size2)", finding.Describe());
            Assert.Equal(MarginLevel.Enough, check.Level);
        }

        [Fact]
        public void OnlyADockingTheBlockFliesIsJudged()
        {
            // No docking still to fly, or one the player flies by hand: no dock findings, whatever the craft carries.
            var bare = InKerbinOrbit();
            Assert.Empty(Check(bare, MunLandingAndReturn()).Findings);
            var byHand = new List<PlannedSequence> { Leg("Dock by hand", "The player docks with the station.", 0, manual: true) };
            Assert.DoesNotContain(CraftCheckRules.DockingKit, Codes(Check(bare, byHand)));
        }

        [Fact]
        public void TheDockingRulesAreTheModsOwnAndOutOfClaudesPrompt()
        {
            // The mod decides them from the craft's parts and the target's free ports, so the Craft Check prompt doesn't grow.
            var kit = CraftCheckRules.Find(CraftCheckRules.DockingKit);
            Assert.Equal(CraftFindingSeverity.Blocker, kit.Severity);
            Assert.True(kit.ModOnly);
            var size = CraftCheckRules.Find(CraftCheckRules.DockingPortSize);
            Assert.Equal(CraftFindingSeverity.Warning, size.Severity);
            Assert.True(size.ModOnly);
            // Docking is the dock block's now, not a stretch the player flies by hand (#456).
            Assert.DoesNotContain("by hand (docking", CraftCheckRules.Describe());
        }

        [Fact]
        public void TheCraftChecksDockingFindingsUseKspsTargetBeforeTheMissionHasOne()
        {
            var snapshot = ReadyToDock("size0");
            snapshot.KspTarget = snapshot.Target;
            snapshot.Target = null;
            var finding = Assert.Single(MarginCheck.Docking(snapshot, "The Mission Goal docks"));
            Assert.Equal(CraftCheckRules.DockingPortSize, finding.Code);
            snapshot.Craft.Parts.RemoveAll(p => p.Tags.Contains("rcs"));
            Assert.Equal("Blocker: Nothing to dock with. The Mission Goal docks and the craft has no RCS",
                Assert.Single(MarginCheck.Docking(snapshot, "The Mission Goal docks")).Describe());
        }
    }
}
