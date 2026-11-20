// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1029 (and the triage of #1048): the plan check judges a part test's band on the leg that serves it, as pricing does: a launch to
    /// orbit too fast through the band is a finding for Claudia, a hop straight up meets it, and a test on the way down needs a way down.
    /// </summary>
    public sealed class PartTestPlanTests
    {
        private static ChosenRequirement Requirement(TestWindow window)
        {
            var check = ContractCheck.Work(ContractCheckTests.With(ContractCheckTests.BoosterTest("c-test")), "", false, ContractCheckTests.Margin);
            var chosen = check.Choose(check.TickedGuids).Requirements.Single();
            chosen.Test.Window = window;
            return chosen;
        }

        private static PlanServes Check(ChosenRequirement requirement, params string[] legs)
        {
            var plan = new MissionPlan();
            foreach (var leg in legs) plan.Sequences.Add(new PlannedSequence { Name = leg, Intent = "Fly it.", DeltaV = 100 });
            return PlanServes.Check(ContractCheckTests.With(ContractCheckTests.BoosterTest("c-test")), plan, new[] { requirement }, "", "Kerbin");
        }

        private static PlanServes CheckLegs(ChosenRequirement requirement, params string[][] legs)
        {
            var plan = new MissionPlan();
            foreach (var leg in legs) plan.Sequences.Add(new PlannedSequence { Name = leg[0], Intent = leg[1], DeltaV = 100 });
            return PlanServes.Check(ContractCheckTests.With(ContractCheckTests.BoosterTest("c-test")), plan, new[] { requirement }, "", "Kerbin");
        }

        [Fact]
        public void ALegWrittenAsAHopByItsLaunchOptionsServesAPartTestInItsBand()
        {
            var band = Requirement(TestWindow.Ascent).Test.Band;
            var ap = System.Math.Round(band.TargetAp(ContractCheckTests.With(ContractCheckTests.BoosterTest("c-test")).Bodies.First(b => b.Name == "Kerbin")));

            var served = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Climb and fall", "Launch with target_ap " + ap + " and turn_start_speed 100000." });

            Assert.Empty(served.Problems);
            Assert.Equal(0, served.Record.Single().Leg);
        }

        [Fact]
        public void LaunchOptionsThatMissTheBandAreAFindingEvenWhereTheDefaultShapeWouldPassIt()
        {
            // A hop to a ceiling far under the band never goes through it; the words never say hop.
            var served = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Climb and fall", "Launch with target_ap 20000 and turn_start_speed 100000." });

            var problem = Assert.Single(served.Problems);
            Assert.Contains("leg 1 \"Climb and fall\" misses it: on the hop: ", problem);
            Assert.Contains("51–56 km", problem);
        }

        [Fact]
        public void WrittenLaunchOptionsAreReadByNameAndUnit()
        {
            var read = WrittenLaunch.Read("Launch with target_ap: 55 km, turn_start_speed=100000 and stage_limit 2; target_ap_max 9");

            Assert.Equal(55000, read.TargetAp);
            Assert.Equal(100000, read.TurnStartSpeed);
            Assert.Equal(2, read.StageLimit);
            Assert.Null(WrittenLaunch.Read("Launch to orbit, target_ap_max 9"));
        }

        [Fact]
        public void WrittenHopOptionsPassWhereTheDefaultShapeMisses()
        {
            var band = Requirement(TestWindow.Ascent).Test.Band;
            var ap = System.Math.Round(band.TargetAp(ContractCheckTests.With(ContractCheckTests.BoosterTest("c-test")).Bodies.First(b => b.Name == "Kerbin")));

            Assert.Single(CheckLegs(Requirement(TestWindow.Ascent), new[] { "Climb and fall", "Fly it." }).Problems);
            Assert.Empty(CheckLegs(Requirement(TestWindow.Ascent), new[] { "Climb and fall", "Launch with target_ap " + ap + " and turn_start_speed 100000." }).Problems);
        }

        private static ClaudiaKerman.Core.Missions.SnapshotBody Kerbin()
        {
            return ContractCheckTests.With(ContractCheckTests.BoosterTest("c-test")).Bodies.First(b => b.Name == "Kerbin");
        }

        [Fact]
        public void ALaunchThatNeverTurnsIsAHopWhateverItsTargetAp()
        {
            var kerbin = Kerbin();
            var above = kerbin.AtmosphereHeight * 1.05;

            Assert.True(WrittenLaunch.Read("Launch with target_ap " + above + " and turn_start_speed 100000").IsHop(kerbin));
            Assert.True(WrittenLaunch.Read("Launch with target_ap 80 km and turn_start_speed 100000").IsHop(kerbin));
            Assert.True(WrittenLaunch.Read("Launch with turn_start_speed 100000").IsHop(kerbin));
            Assert.False(WrittenLaunch.Read("Launch with target_ap 20000 and turn_start_speed 100").IsHop(kerbin));
            Assert.False(WrittenLaunch.Read("Launch with target_ap " + above).IsHop(kerbin));
        }

        [Fact]
        public void TheHopTheRouteWritesAboveTheAirIsJudgedAsAHopAndATurningSuborbitalLegAsAnAscent()
        {
            var above = System.Math.Round(Kerbin().AtmosphereHeight * 1.05);

            var hop = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Climb and fall", "Launch with target_ap " + above + " and turn_start_speed 100000." });
            var turning = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Climb and fall", "Launch with target_ap 20000 and turn_start_speed 100." });

            Assert.DoesNotContain(hop.Problems, p => p.Contains("on the ascent: "));
            Assert.Contains(hop.Problems, p => p.Contains("on the hop: "));
            Assert.NotEmpty(turning.Problems);
        }

        [Fact]
        public void ALegWithNoLaunchOptionsIsJudgedOnTheDefaultShapeAsBefore()
        {
            var served = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Launch to low Kerbin orbit", "Fly it." });

            Assert.Contains("misses it: on the ascent: ", Assert.Single(served.Problems));
        }

        [Fact]
        public void WrittenOrbitalOptionsAreFlownNotTheDefaultShape()
        {
            var plain = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Launch to low Kerbin orbit", "Fly it." }).Problems.Single();
            var shaped = CheckLegs(Requirement(TestWindow.Ascent), new[] { "Launch to low Kerbin orbit", "Launch with target_ap 80000 and turn_shape 0.8 and throttle_max_q 8000." });

            Assert.NotEqual(plain, shaped.Problems.SingleOrDefault() ?? "");
        }

        [Fact]
        public void AHopStraightUpToTheBandServesTheTest()
        {
            var served = Check(Requirement(TestWindow.Ascent), "Hop straight up through 51-56 km over Kerbin");

            Assert.Empty(served.Problems);
            Assert.Equal(0, served.Record.Single().Leg);
        }

        [Fact]
        public void ALaunchToOrbitTooFastThroughTheBandIsAFindingWithTheSpeed()
        {
            var served = Check(Requirement(TestWindow.Ascent), "Launch to low Kerbin orbit", "Return to Kerbin");

            var problem = Assert.Single(served.Problems);
            Assert.Contains("leg 1 \"Launch to low Kerbin orbit\" misses it: on the ascent: speed ", problem);
            Assert.Contains("above the band, through 51–56 km", problem);
            Assert.Contains("fly it as a hop straight up to the band", problem);
        }

        [Fact]
        public void ATestOnTheWayDownWithNoWayDownIsAFinding()
        {
            var served = Check(Requirement(TestWindow.Descent), "Launch to low Kerbin orbit");

            Assert.Contains("no leg brings the craft down home through it", Assert.Single(served.Problems));
        }
    }
}
