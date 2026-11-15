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
