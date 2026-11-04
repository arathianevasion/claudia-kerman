// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The Craft Check's chute rules (#641): before any Mission Plan, the Chute Allocation is worked out from the Mission Goal's
    /// own words and the craft, and the mod raises the Margin Check's chutes_short_for_landing and chutes_thin_for_landing from it.
    /// The craft is the recorded Mun Shot v2 in Kerbin orbit with a Mk16 on the pod; Claude's reply is a hand-written pass.
    /// </summary>
    public sealed class CraftCheckChuteTests : IDisposable
    {
        private const uint PodChute = 1117472593;
        private const string Duna = "Land on Duna and return to Kerbin.";
        private const string Model = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        /// <summary>The craft, with its stages' delta-v cut to <paramref name="deltaVShare"/> of what they have.</summary>
        private static GameSnapshot Craft(double deltaVShare = 1)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Craft.Parts.Single(p => p.Id == PodChute).Chute = ReentrySim.Mk16();
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= deltaVShare;
            return snapshot;
        }

        private MissionControl Checked(GameSnapshot craft, string goal)
        {
            var client = new ClaudeClient(new StreamingFakeTransport(ScriptedResponse.Fixture("craft-check-pass.sse", 64)), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(craft);
            mission.Send(new RunCraftCheck(goal, 10m, Model));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.CraftCheck.State == CraftCheckState.Running && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
            return mission;
        }

        [Fact]
        public void TheGoalsClausesAreItsEntriesTheAirLandingThenTheReturn()
        {
            var snapshot = Craft();

            var allocation = ChuteAllocation.ForGoal(snapshot, ChuteAllocation.GoalLegs(snapshot, Duna), false, null);

            Assert.Equal(new[] { "Duna", "Kerbin" }, allocation.Entries.Select(e => e.Body).ToArray());
            Assert.True(allocation.Entries[1].Return);
            Assert.Equal("", allocation.ForPlan);
        }

        [Fact]
        public void ALaunchFromTheGroundSpendsTheDeltaVMapsLaunchLegFirst()
        {
            var snapshot = Craft();
            snapshot.Situation = "PRELAUNCH";
            snapshot.Orbit = null;

            var legs = ChuteAllocation.GoalLegs(snapshot, Duna);

            Assert.Equal(DeltaVMap.Compute(snapshot.Bodies, "Kerbin").Leg("launch:Kerbin").DeltaV, legs[0].DeltaV, 3);
            Assert.True(legs.Skip(1).All(l => l.DeltaV == 0));
        }

        [Fact]
        public void ADunaLandingWithTooFewChutesAndNoDeltaVForTheFinishIsABlockerAndStartStaysClosed()
        {
            var mission = Checked(Craft(0.001), Duna);

            var finding = Assert.Single(mission.View.CraftCheck.Findings, f => f.Code == "chutes_short_for_landing");
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
            Assert.StartsWith("The powered finish at Duna for \"Land on Duna\" needs ", finding.Explanation);
            Assert.False(string.IsNullOrEmpty(finding.Fix));
            Assert.Equal(CraftVerdict.Fail, mission.View.CraftCheck.Verdict);
            Assert.False(mission.View.CraftCheck.StartAllowed);
        }

        [Fact]
        public void TheCraftChecksChuteFindingsAreTheMarginChecksWordForWord()
        {
            var snapshot = Craft(0.001);
            var legs = ChuteAllocation.GoalLegs(snapshot, Duna);

            var mod = MarginCheck.ChuteFindings(snapshot, legs, ChuteAllocation.ForGoal(snapshot, legs, false, null), Duna);
            var margin = MarginCheck.Of(snapshot, legs, 15, 10, false, null, ChuteAllocation.Of(snapshot, legs, 0, false, null), Duna, 0);

            Assert.NotEmpty(mod);
            Assert.Equal(margin.Findings.Where(f => f.Code.StartsWith("chutes_")).Select(f => f.Describe()), mod.Select(f => f.Describe()));
        }

        [Fact]
        public void AnEngineerRepackingTheChutesMakesItAWarningAndANegatedOneDoesNot()
        {
            var repack = Checked(Craft(0.001), "Land on Duna, where an engineer repacks the chutes, and return to Kerbin.");
            var none = Checked(Craft(0.001), "Land on Duna with no engineer to repack the chutes, and return to Kerbin.");

            var warned = Assert.Single(repack.View.CraftCheck.Findings, f => f.Code.StartsWith("chutes_"));
            Assert.Equal("chutes_thin_for_landing", warned.Code);
            Assert.Equal(CraftFindingSeverity.Warning, warned.Severity);
            Assert.Contains("relies on a repack at Duna", warned.Explanation);
            Assert.Contains(none.View.CraftCheck.Findings, f => f.Code == "chutes_short_for_landing");
        }

        [Fact]
        public void AGoalWithNoAirLandingRaisesNeitherRule()
        {
            foreach (var goal in new[] { "Orbit Duna and come home.", "Land on the Mun and return to Kerbin." })
            {
                var mission = Checked(Craft(0.001), goal);
                Assert.DoesNotContain(mission.View.CraftCheck.Findings, f => f.Code.StartsWith("chutes_"));
            }
        }

        [Fact]
        public void AFinishTheCraftHasTheDeltaVForRaisesNeitherRule()
        {
            var mission = Checked(Craft(), Duna);

            Assert.DoesNotContain(mission.View.CraftCheck.Findings, f => f.Code.StartsWith("chutes_"));
        }

        [Fact]
        public void ACraftWithNoParachuteAtAllIsLeftToTheReturnsOwnRule()
        {
            var craft = Craft(0.001);
            craft.Craft.Parts.RemoveAll(p => p.Tags.Contains("parachute"));

            var mission = Checked(craft, Duna);

            Assert.DoesNotContain(mission.View.CraftCheck.Findings, f => f.Code.StartsWith("chutes_"));
        }

        [Fact]
        public void ChutesTheAdapterCouldNotReadAreNotJudged()
        {
            var craft = Craft(0.001);
            craft.Craft.Parts.Single(p => p.Id == PodChute).Chute = null;

            var mission = Checked(craft, Duna);

            Assert.DoesNotContain(mission.View.CraftCheck.Findings, f => f.Code.StartsWith("chutes_"));
        }
    }
}
