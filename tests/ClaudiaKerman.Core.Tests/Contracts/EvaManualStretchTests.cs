// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1246, at #1032's seams (the Contract check and the plan check): every EVA-only Requirement is a Manual stretch Claudia plans (spec
    /// #1021 decision 11). A spacewalk milestone is one in orbit of its body; a surface-EVA milestone and a survey waypoint needing an EVA
    /// report or a surface sample are one after the landing. With no kerbal aboard each is "this craft can't"; on Kerbin's ground, "not yet".
    /// </summary>
    public sealed class EvaManualStretchTests
    {
        private static ContractCheck Check(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
        }

        private static double RouteFor(string target)
        {
            return Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-route", 10000, target))).RouteDeltaV;
        }

        private static ContractChoice Chosen(SnapshotContract contract, out GameSnapshot snapshot)
        {
            snapshot = ContractCheckTests.With(contract);
            var check = Check(snapshot);
            return check.Choose(check.TickedGuids);
        }

        /// <summary>A plan whose legs are named; a name starting "*" is a Manual stretch.</summary>
        private static MissionPlan Plan(params string[] legs)
        {
            var plan = new MissionPlan();
            foreach (var leg in legs)
                plan.Sequences.Add(new PlannedSequence { Name = leg.TrimStart('*'), Intent = "Fly it.", DeltaV = 100, Manual = leg.StartsWith("*", System.StringComparison.Ordinal) });
            return plan;
        }

        // ---- A spacewalk: a Manual stretch in orbit of its body ----

        [Fact]
        public void ASpacewalkMilestoneIsPlannedAsAnOrbitOfItsBodyWithAManualStretchYouFly()
        {
            var check = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-walk", 10000, "Mun:SPACEWALK")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal("Mun", row.Phase);
            Assert.True(row.YouFly);
            Assert.Equal(RouteFor("Mun:ORBIT"), check.RouteDeltaV, 3);
            var chosen = Assert.Single(check.Choose(check.TickedGuids).Requirements);
            Assert.Equal(ContractReach.Orbit, chosen.Reach);
            Assert.Equal("take a spacewalk on EVA", chosen.Manual);
        }

        [Fact]
        public void ASpacewalkInKerbinOrbitIsPlannedToo()
        {
            var check = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-walk", 10000, "Kerbin:SPACEWALK")));

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Kerbin orbit", row.Phase);
            Assert.True(row.YouFly);
        }

        [Fact]
        public void TheRequestAsksForTheSpacewalkInOrbitNotLanded()
        {
            var section = Chosen(ContractCheckTests.Exploration("c-walk", 10000, "Mun:SPACEWALK"), out _).Section;

            Assert.Contains("take a spacewalk on EVA, in orbit round the Mun", section);
        }

        [Fact]
        public void AManualStretchInOrbitServesTheSpacewalk()
        {
            var choice = Chosen(ContractCheckTests.Exploration("c-walk", 10000, "Mun:SPACEWALK"), out var snapshot);

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "*Spacewalk", "Return to Kerbin"), choice.Requirements, "", "Kerbin");

            Assert.Empty(served.Problems);
            Assert.Equal(2, Assert.Single(served.Record).Leg);
        }

        [Fact]
        public void APlanReachingTheOrbitWithNoManualStretchThereIsAFindingThatSaysSo()
        {
            var choice = Chosen(ContractCheckTests.Exploration("c-walk", 10000, "Mun:SPACEWALK"), out var snapshot);

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin"), choice.Requirements, "", "Kerbin");

            var problem = Assert.Single(served.Problems);
            Assert.Contains("no Manual stretch for the player to take a spacewalk on EVA", problem);
            Assert.Contains("after leg 2 \"Transfer to the Mun and capture into low orbit\" reaches orbit there", problem);
        }

        // ---- A surface EVA and an EVA survey waypoint: a Manual stretch after the landing ----

        [Fact]
        public void ASurfaceEvaMilestoneIsPlannedAsALandingWithAManualStretchAfterIt()
        {
            var choice = Chosen(ContractCheckTests.Exploration("c-eva", 10000, "Mun:SURFACEEVA"), out var snapshot);

            var chosen = Assert.Single(choice.Requirements);
            Assert.Equal(ContractReach.Land, chosen.Reach);
            Assert.Contains("on EVA, landed on the Mun", choice.Section);
            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and land", "*Walk on the Mun", "Return to Kerbin"), choice.Requirements, "", "Kerbin");
            Assert.Empty(served.Problems);
            Assert.Equal(2, Assert.Single(served.Record).Leg);
        }

        [Theory]
        [InlineData("evaReport", "take an EVA report")]
        [InlineData("surfaceSample", "take a surface sample")]
        public void AGroundSurveyWaypointNeedingAnEvaIsALandingAtItWithAManualStretchAfterIt(string experiment, string words)
        {
            var survey = ScienceSurveyContractTests.Survey("c-s", "2", "GROUND", experiment, 0, "3,10");
            var choice = Chosen(survey, out var snapshot);

            var chosen = Assert.Single(choice.Requirements);
            Assert.Equal(ContractReach.Land, chosen.Reach);
            Assert.Equal("walk to the waypoint 3.0° N 10.0° E and " + words + " there on EVA", chosen.Manual);
            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and land", "*Walk to the waypoint", "Return to Kerbin"), choice.Requirements, "", "Kerbin");
            Assert.Empty(served.Problems);
            Assert.Equal(2, Assert.Single(served.Record).Leg);
        }

        [Fact]
        public void AnEvaReportOverAWaypointFromSpaceIsAManualStretchInOrbit()
        {
            var choice = Chosen(ScienceSurveyContractTests.Survey("c-s", "2", "HIGH", "evaReport", 20000, "3,10"), out _);

            var chosen = Assert.Single(choice.Requirements);
            Assert.Equal(ContractReach.Orbit, chosen.Reach);
            Assert.Equal("take an EVA report on EVA over the waypoint 3.0° N 10.0° E, above 20 km", chosen.Manual);
        }

        // ---- What the craft needs, and Kerbin's ground ----

        [Fact]
        public void EachWithNoKerbalAboardIsThisCraftCant()
        {
            var snapshot = ContractCheckTests.With(
                ContractCheckTests.Exploration("c-walk", 10000, "Mun:SPACEWALK"),
                ContractCheckTests.Exploration("c-eva", 10000, "Minmus:SURFACEEVA"),
                ScienceSurveyContractTests.Survey("c-report", "2", "GROUND", "evaReport", 0, "3,10"),
                ScienceSurveyContractTests.Survey("c-sample", "3", "GROUND", "surfaceSample", 0, "3,10"));
            snapshot.Crew.Clear();

            var check = ContractCheck.Work(snapshot, "", true, ContractCheckTests.Margin);

            Assert.Empty(check.Fitting);
            Assert.Equal(4, check.NotThisFlight.Count);
            Assert.All(check.NotThisFlight, l => Assert.Equal(LeftOutKind.CraftCant, l.Kind));
            Assert.Equal("this craft can't: a spacewalk needs a kerbal aboard to go on EVA", check.NotThisFlight.Single(l => l.Guid == "c-walk").Reason);
            Assert.Equal("this craft can't: a surface EVA needs a kerbal aboard to go on EVA", check.NotThisFlight.Single(l => l.Guid == "c-eva").Reason);
            Assert.Equal("this craft can't: an EVA report needs a kerbal aboard to go on EVA", check.NotThisFlight.Single(l => l.Guid == "c-report").Reason);
            Assert.Equal("this craft can't: a surface sample needs a kerbal aboard to go on EVA", check.NotThisFlight.Single(l => l.Guid == "c-sample").Reason);
        }

        [Fact]
        public void OnKerbinsGroundTheyStayNotYet()
        {
            var check = Check(ContractCheckTests.With(
                ContractCheckTests.Exploration("c-eva", 10000, "Kerbin:SURFACEEVA"),
                ScienceSurveyContractTests.Survey("c-report", "1", "GROUND", "evaReport", 0, "0,-74")));

            Assert.Empty(check.Fitting);
            Assert.Equal(ManualKinds.HomeEvaNotYet, check.NotThisFlight.Single(l => l.Guid == "c-eva").Reason);
            Assert.Equal(ScienceKinds.HomeGroundNotYet, check.NotThisFlight.Single(l => l.Guid == "c-report").Reason);
        }

        [Fact]
        public void NoRequirementAwayFromKerbinsGroundIsLeftAsAnEvaNotPlannedYet()
        {
            var check = Check(ContractCheckTests.With(
                ContractCheckTests.Exploration("c-walk", 10000, "Mun:SPACEWALK", "Mun:SURFACEEVA"),
                ScienceSurveyContractTests.Survey("c-low", "3", "LOW", "evaReport", 20000, "3,10"),
                ScienceSurveyContractTests.Survey("c-ground", "3", "GROUND", "surfaceSample", 0, "3,10")));

            Assert.All(check.NotThisFlight, l => Assert.DoesNotContain("needs an EVA in a Manual stretch", l.Reason));
            Assert.Equal(3, check.Fitting.Count);
        }
    }
}
