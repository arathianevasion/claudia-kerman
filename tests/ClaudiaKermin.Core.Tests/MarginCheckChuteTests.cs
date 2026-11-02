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
    /// The Margin Check's chute rules (#629) on the Chute Allocation's figures: the Duna benchmark's shape (land under chutes, then
    /// home) on the recorded Mun Shot v2 craft (3,904 m/s, a Mk16 on the pod), with the Mission Goal and the plan's legs varied.
    /// </summary>
    public class MarginCheckChuteTests
    {
        private const uint PodChute = 1117472593;
        private const string Home = "Land on Duna and come home.";
        private const string Repack = "Land on Duna and come home; an engineer repacks the chutes after the landing.";

        private static GameSnapshot Craft(int landerChutes = 0)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Craft.Parts.Single(p => p.Id == PodChute).Chute = ReentrySim.Mk16();
            for (var i = 0; i < landerChutes; i++)
            {
                snapshot.Craft.Parts.Add(new SnapshotPart
                {
                    Id = (uint)(900 + i), ParentId = 1454380882, Name = "parachuteSingle", Title = "Lander Mk16 " + (i + 1), Stage = 1, MassT = 0.1,
                    Tags = new List<string> { "parachute" }, Chute = ReentrySim.Mk16(),
                });
            }
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
            var chutes = ChuteAllocation.Of(snapshot, plan, 0, false, null);
            return MarginCheck.Of(snapshot, plan, 15, 10, false, null, chutes, goal, 0);
        }

        private static double FinishSpeed(GameSnapshot snapshot, IList<PlannedSequence> plan)
        {
            return ChuteAllocation.Of(snapshot, plan, 0, false, null).Entries[0].TouchdownSpeed;
        }

        /// <summary>A plan whose Duna landing leaves <paramref name="spare"/> times the finish's delta-v in the stages (the finish's speed depends on the mass the legs leave).</summary>
        private static List<PlannedSequence> PlanLeaving(GameSnapshot snapshot, double spare)
        {
            var transfer = 1000.0;
            for (var i = 0; i < 12; i++) transfer = 3904 - 100 - spare * FinishSpeed(snapshot, Plan(transfer));
            return Plan(transfer);
        }

        [Fact]
        public void TheBenchmarkCraftWithOneMk16AndNoDeltaVForTheFinishIsABlocker()
        {
            // The legs through the landing take 3,900 of 3,904 m/s: 4 m/s are left for a finish from an unchuted Duna fall.
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

            // The legs alone leave a healthy margin; only the finish's own delta-v takes it away.
            var plan = Plan(3500, 100, 0);
            Assert.True(FinishSpeed(snapshot, plan) > 304);

            var check = MarginCheck.Of(snapshot, plan, 5, 10, false, null, ChuteAllocation.Of(snapshot, plan, 0, false, null), Home, 0);

            Assert.Single(check.Findings, f => f.Code == "chutes_short_for_landing");
            Assert.DoesNotContain(check.Findings, f => f.Code == "delta_v_short");
        }

        [Fact]
        public void TheBenchmarkCraftWithTheDeltaVForTheFinishHasNoChuteFinding()
        {
            var snapshot = Craft();

            var check = Check(snapshot, PlanLeaving(snapshot, 2), Home);

            Assert.DoesNotContain(check.Findings, f => f.Code.StartsWith("chutes_"));
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
            var plan = Plan(1800, 100, 1000);
            var need = FinishSpeed(snapshot, plan);

            var without = MarginCheck.Of(snapshot, plan, 15, 10, false, null);
            var with = Check(snapshot, plan, Home);

            Assert.Equal(2900, without.Margin.RestOfPlan, 3);
            Assert.Equal(2900 + need, with.Margin.RestOfPlan, 3);
        }

        [Fact]
        public void AnEngineerRepackingTheChutesTurnsTheBlockerIntoAWarningThatSaysSo()
        {
            var check = Check(Craft(), Plan(3800, 100, 1000), Repack);

            var finding = Assert.Single(check.Findings, f => f.Code.StartsWith("chutes_"));
            Assert.Equal("chutes_thin_for_landing", finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("relies on a repack at Duna", finding.Explanation);
            Assert.Equal(4900, check.Margin.RestOfPlan, 3);
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
        public void AReturnTheChutesCantCoverIsABlockerOrWithARepackAWarning()
        {
            var snapshot = Craft(landerChutes: 1);
            snapshot.Craft.Parts.Single(p => p.Id == PodChute).Chute = null;

            var blocked = Check(snapshot, Plan(1000), Home);
            var repacked = Check(snapshot, Plan(1000), Repack);

            Assert.Contains(blocked.Findings, f => f.Code == "chutes_short_for_landing" && f.Explanation.Contains("Kerbin return"));
            var warned = Assert.Single(repacked.Findings, f => f.Code.StartsWith("chutes_") && f.Explanation.Contains("Kerbin return"));
            Assert.Equal(CraftFindingSeverity.Warning, warned.Severity);
            Assert.Contains("relies on a repack at Kerbin", warned.Explanation);
        }
    }
}
