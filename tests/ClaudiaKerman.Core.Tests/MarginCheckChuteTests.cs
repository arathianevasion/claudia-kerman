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
    /// The Margin Check's chute rules (#629, ADR 0019) on the Chute Groups' figures: the Duna benchmark's shape (land under chutes, then
    /// home) on the recorded Mun Shot v2 craft (3,904 m/s, a stage-0 Mk16 on the pod, a stage-1 lander group below the shield), with the
    /// Mission Goal and the plan's legs varied.
    /// </summary>
    public class MarginCheckChuteTests
    {
        private const uint PodChute = 1117472593;
        private const uint LanderTank = 1454380882;
        private const string Home = "Land on Duna and come home.";
        private const string Repack = "Land on Duna and come home; an engineer repacks the chutes after the landing.";

        private static SnapshotPart Chute(uint id, uint parent, int stage)
        {
            return new SnapshotPart
            {
                Id = id, ParentId = parent, Name = "parachuteSingle", Title = "Mk16 " + id, Stage = stage, MassT = 0.1,
                Tags = new List<string> { "parachute" }, Chute = ReentrySim.Mk16(),
            };
        }

        private static GameSnapshot Craft(int landerChutes = 1)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Craft.Parts.Single(p => p.Id == PodChute).Chute = ReentrySim.Mk16();
            for (var i = 0; i < landerChutes; i++) snapshot.Craft.Parts.Add(Chute((uint)(900 + i), LanderTank, 1));
            return snapshot;
        }

        private static List<PlannedSequence> Plan(double transfer, double landing = 100, double home = 1000)
        {
            return new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Duna transfer", Intent = "Transfer to Duna and capture into low Duna orbit.", DeltaV = transfer },
                new PlannedSequence { Name = "Land on Duna", Intent = "Deorbit and land on Duna under parachute.", DeltaV = landing },
                new PlannedSequence { Name = "Ascend and return", Intent = "Ascend to low Duna orbit, fly the return to Kerbin, then reenter and land under parachute.", DeltaV = home },
            };
        }

        private static MarginCheck Check(GameSnapshot snapshot, IList<PlannedSequence> plan, string goal)
        {
            var chutes = ChuteGroups.Of(snapshot, plan, 0, false, null);
            return MarginCheck.Of(snapshot, plan, 15, 10, false, null, chutes, goal, 0);
        }

        private static double FinishSpeed(GameSnapshot snapshot, IList<PlannedSequence> plan)
        {
            return ChuteGroups.Of(snapshot, plan, 0, false, null).Entries[0].TouchdownSpeed;
        }

        /// <summary>A plan whose Duna landing leaves <paramref name="spare"/> times the finish's delta-v in the stages (the finish's speed depends on the mass the legs leave).</summary>
        private static List<PlannedSequence> PlanLeaving(GameSnapshot snapshot, double spare)
        {
            var transfer = 1000.0;
            for (var i = 0; i < 12; i++) transfer = 3904 - 100 - spare * FinishSpeed(snapshot, Plan(transfer));
            return Plan(transfer);
        }

        [Fact]
        public void TheBenchmarkCraftWithAWeakGroupAndNoDeltaVForTheFinishIsABlocker()
        {
            // The legs through the landing take 3,900 of 3,904 m/s: 4 m/s are left for a finish from under one Mk16.
            var snapshot = Craft();
            var plan = Plan(3800, 100, 1000);
            Assert.True(FinishSpeed(snapshot, plan) > 4);

            var check = Check(snapshot, plan, Home);

            var finding = check.Findings.Single(f => f.Code == "chutes_short_for_landing");
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
            Assert.Equal(MarginLevel.Short, check.Level);
            Assert.Contains("Duna", finding.Explanation);
        }

        [Fact]
        public void AFinishShortfallIsReportedOnceNotAlsoAsTheMarginsDeltaVShort()
        {
            var snapshot = Craft();
            var finish = FinishSpeed(snapshot, Plan(3500));

            // The legs alone leave a healthy margin; only the finish's own delta-v takes it away. The return is a reentry alone, as #757
            // counts an ascent and return at the Delta-v Map's figure, and this craft hasn't that as well.
            var plan = Plan(3904 - 100 - finish / 2, 100, 0).Take(2).ToList();
            plan.Add(new PlannedSequence { Name = "Reentry at Kerbin", Intent = "Reenter Kerbin and land under parachute." });

            var check = MarginCheck.Of(snapshot, plan, 0, 10, false, null, ChuteGroups.Of(snapshot, plan, 0, false, null), Home, 0);

            Assert.Single(check.Findings, f => f.Code == "chutes_short_for_landing");
            Assert.DoesNotContain(check.Findings, f => f.Code == "delta_v_short");
        }

        [Fact]
        public void AGroupThatLandsTooFastWithTheDeltaVForTheFinishIsAWarning()
        {
            var snapshot = Craft();

            var check = Check(snapshot, PlanLeaving(snapshot, 2), Home);

            var finding = Assert.Single(check.Findings, f => f.Code == CraftCheckRules.ChuteGroupTooFast);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("Duna", finding.Explanation);
            Assert.DoesNotContain(check.Findings, f => f.Code.StartsWith("chutes_"));
        }

        [Fact]
        public void AGroupThatLandsSlowEnoughHasNoFinding()
        {
            var check = Check(Craft(landerChutes: 4), Plan(3500), Home);

            Assert.DoesNotContain(check.Findings, f => f.Code.StartsWith("chutes_") || f.Code.StartsWith("chute_group"));
        }

        [Fact]
        public void AFinishWithUnderAQuarterSpareIsAWarning()
        {
            var snapshot = Craft();

            var check = Check(snapshot, PlanLeaving(snapshot, 1.1), Home);

            var finding = Assert.Single(check.Findings, f => f.Code.StartsWith("chutes_"));
            Assert.Equal("chutes_thin_for_landing", finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
        }

        [Fact]
        public void TheFinishesDeltaVJoinsTheMarginsNeed()
        {
            var snapshot = Craft();
            // Budgets over the Delta-v Map's figures (#757), so each leg counts at its budget.
            var plan = Plan(1800, 100, 2000);
            var need = FinishSpeed(snapshot, plan);

            var without = MarginCheck.Of(snapshot, plan, 15, 10, false, null);
            var with = Check(snapshot, plan, Home);

            Assert.Equal(3900, without.Margin.RestOfPlan, 3);
            Assert.Equal(3900 + need, with.Margin.RestOfPlan, 3);
        }

        [Fact]
        public void AnEngineerRepackingTheChutesTurnsAFinishBlockerIntoAWarningThatSaysSo()
        {
            var check = Check(Craft(), Plan(3800, 100, 2000), Repack);

            var finding = Assert.Single(check.Findings, f => f.Code.StartsWith("chutes_"));
            Assert.Equal("chutes_thin_for_landing", finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("relies on a repack at Duna", finding.Explanation);
            Assert.Equal(5900, check.Margin.RestOfPlan, 3);
        }

        [Fact]
        public void AKerbinOnlyReturnWithEnoughChutesHasNoFinding()
        {
            var plan = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Reentry", Intent = "Reenter Kerbin and land under parachute.", DeltaV = 100 },
            };

            var check = Check(Craft(), plan, "Come home.");

            Assert.DoesNotContain(check.Findings, f => f.Code.StartsWith("chutes_"));
        }

        [Fact]
        public void AReturnItsChutesCantCoverIsABlockerOrWithARepackAWarning()
        {
            // A heavy return: the pod's mass grown past what one Mk16 lands.
            var snapshot = Craft();
            var chute = snapshot.Craft.Parts.Single(p => p.Id == PodChute);
            snapshot.Craft.MassTonnes += 40 - chute.MassT; // EntryCraft reads the craft's own total, as a snapshot has it (#930)
            chute.MassT = 40;

            var blocked = Check(snapshot, Plan(1000), Home);
            var repacked = Check(snapshot, Plan(1000), Repack);

            Assert.Contains(blocked.Findings, f => f.Code == "chutes_short_for_landing" && f.Explanation.Contains("Kerbin return"));
            var warned = Assert.Single(repacked.Findings, f => f.Code.StartsWith("chutes_") && f.Explanation.Contains("Kerbin return"));
            Assert.Equal(CraftFindingSeverity.Warning, warned.Severity);
            Assert.Contains("relies on a repack at Kerbin", warned.Explanation);
        }

        // ---- Fewer groups than Entries ----

        [Fact]
        public void Stage0OnlyOverDunaThenKerbinIsABlockerNamingTheEntryWithNone()
        {
            var check = Check(Craft(landerChutes: 0), Plan(1000), Home);

            var finding = Assert.Single(check.Findings, f => f.Code == "chutes_short_for_landing" && f.Explanation.Contains("no Chute Group"));
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
            Assert.Contains("The Kerbin entry", finding.Explanation);
            Assert.Equal(MarginLevel.Short, check.Level);
            // The return with no group is that one finding, not an uncovered return too.
            Assert.DoesNotContain(check.Findings, f => f.Explanation.Contains("Kerbin return's chutes"));
        }

        [Fact]
        public void Stage0OnlyWithAPlannedRepackIsAWarning()
        {
            var check = Check(Craft(landerChutes: 0), Plan(1000), Repack);

            var finding = Assert.Single(check.Findings, f => f.Code == "chutes_thin_for_landing" && f.Explanation.Contains("no Chute Group"));
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("relies on a repack at Duna", finding.Explanation);
        }

        // ---- A group that leaves before its Entry ----

        /// <summary>The fixture, with a four-chute group on radial tanks a stage-3 decoupler drops: burning through stage 4's delta-v takes it.</summary>
        private static GameSnapshot OnRadialTanks()
        {
            var snapshot = Craft(landerChutes: 0);
            snapshot.Stages.Insert(0, new SnapshotStage { Number = 4, DeltaVVac = 500, StartMassT = 20, EndMassT = 16.5 });
            snapshot.Craft.Stage = 5;
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 800, ParentId = LanderTank, Name = "radialDecoupler", Stage = 3, MassT = 0.1, Tags = new List<string> { "decoupler" } });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 801, ParentId = 800, Name = "radialTank", Stage = 3, MassT = 1 });
            for (var i = 0; i < 4; i++) snapshot.Craft.Parts.Add(Chute((uint)(810 + i), 801, 2));
            return snapshot;
        }

        [Fact]
        public void AGroupOnTanksDroppedBeforeItsEntryIsAWarningSayingTheRuleMovesOn()
        {
            var check = Check(OnRadialTanks(), Plan(1000), Home);

            var finding = Assert.Single(check.Findings, f => f.Code == CraftCheckRules.ChuteGroupLeaves);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("Stage 2's chutes", finding.Explanation);
            Assert.Contains("stage 0 would open at Duna, leaving Kerbin none", finding.Explanation);
        }

        [Fact]
        public void AGroupPartlyOnDroppedTanksSaysTheRestOpen()
        {
            var snapshot = OnRadialTanks();
            snapshot.Craft.Parts.Add(Chute(820, PodChute, 2));

            var finding = Assert.Single(Check(snapshot, Plan(1000), Home).Findings, f => f.Code == CraftCheckRules.ChuteGroupLeaves);

            Assert.Contains("the rest of its chutes open without them", finding.Explanation);
            Assert.DoesNotContain("would open at", finding.Explanation);
        }

        [Fact]
        public void AGroupThePlanDoesntBurnDownToHasNoWarning()
        {
            var plan = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Land on Duna", Intent = "Deorbit and land on Duna under parachute.", DeltaV = 100 },
                new PlannedSequence { Name = "Reentry at Kerbin", Intent = "Reenter Kerbin and land under parachute." },
            };
            var snapshot = OnRadialTanks();
            snapshot.Situation = "ORBITING";
            snapshot.Orbit.Body = "Duna";

            Assert.DoesNotContain(Check(snapshot, plan, Home).Findings, f => f.Code == CraftCheckRules.ChuteGroupLeaves);
        }

        // ---- A group above an engine ----

        /// <summary>A two-entry craft whose stage-2 chutes sit above the next engine (stage 1), with only stage 3 before them.</summary>
        private static GameSnapshot AboveEngine(bool lit = false)
        {
            var snapshot = Craft(landerChutes: 0);
            snapshot.Stages.Clear();
            snapshot.Stages.Add(new SnapshotStage { Number = 3, DeltaVVac = 500, StartMassT = 12, EndMassT = 9 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, DeltaVVac = 3000, StartMassT = 8, EndMassT = 2 });
            snapshot.Craft.Stage = 4;
            foreach (var part in snapshot.Craft.Parts.Where(p => p.Tags.Contains("engine")).ToList()) snapshot.Craft.Parts.Remove(part);
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 700, ParentId = LanderTank, Name = "liquidEngine", Title = "Terrier", Stage = 1, MassT = 0.5, Tags = new List<string> { "engine" }, EngineLit = lit });
            snapshot.Craft.Parts.Add(Chute(701, LanderTank, 2));
            return snapshot;
        }

        [Fact]
        public void AGroupAboveAnEngineStillNeededIsABlocker()
        {
            var check = Check(AboveEngine(), Plan(1000), Home);

            var finding = Assert.Single(check.Findings, f => f.Code == CraftCheckRules.ChuteGroupAboveEngine);
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
            Assert.Contains("Stage 2 holds chutes and Terrier in stage 1", finding.Explanation);
            Assert.Equal(MarginLevel.Short, check.Level);
        }

        [Fact]
        public void AnEngineAlreadyLitIsNotFlagged()
        {
            Assert.DoesNotContain(Check(AboveEngine(lit: true), Plan(1000), Home).Findings, f => f.Code == CraftCheckRules.ChuteGroupAboveEngine);
        }

        [Fact]
        public void AnEngineThePlanDoesntNeedIsNotFlagged()
        {
            var plan = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Land on Duna", Intent = "Deorbit and land on Duna under parachute.", DeltaV = 100 },
                new PlannedSequence { Name = "Reentry at Kerbin", Intent = "Reenter Kerbin and land under parachute." },
            };
            var snapshot = AboveEngine();
            snapshot.Situation = "ORBITING";
            snapshot.Orbit.Body = "Duna";

            Assert.DoesNotContain(Check(snapshot, plan, Home).Findings, f => f.Code == CraftCheckRules.ChuteGroupAboveEngine);
        }

        [Fact]
        public void AnOpenOrCutChuteStillStopsTheStageFromFiring()
        {
            // claudia_stage_unsafe looks only for the part, so spent stage-2 chutes still end in "out of fuel".
            var snapshot = AboveEngine();
            snapshot.Craft.Parts.Single(p => p.Id == 701).Chute = null;

            Assert.Single(Check(snapshot, Plan(1000), Home).Findings, f => f.Code == CraftCheckRules.ChuteGroupAboveEngine);
        }

        [Fact]
        public void AChuteWithStagingDisabledIsNeverAboveAnEngine()
        {
            var snapshot = AboveEngine();
            snapshot.Craft.Parts.Single(p => p.Id == 701).Stage = -1;

            Assert.DoesNotContain(Check(snapshot, Plan(1000), Home).Findings, f => f.Code == CraftCheckRules.ChuteGroupAboveEngine);
        }

        [Fact]
        public void TheGroupRulesAreTheMods()
        {
            foreach (var code in new[] { CraftCheckRules.ChuteGroupLeaves, CraftCheckRules.ChuteGroupAboveEngine, CraftCheckRules.ChuteGroupTooFast })
            {
                Assert.True(CraftCheckRules.Find(code).ModOnly);
                Assert.DoesNotContain(code, CraftCheckRules.Describe());
                Assert.False(string.IsNullOrEmpty(ChuteGroupRules.Fix(code)));
            }
        }
    }
}
