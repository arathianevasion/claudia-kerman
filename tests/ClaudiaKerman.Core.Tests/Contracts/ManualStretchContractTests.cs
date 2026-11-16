// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1032, seam 1: Contracts needing hands-on work become Manual stretches Claudia plans. A plant-flag Contract, a Breaking Ground
    /// robotic-arm scan and a surface sample are each a landing on the body Claudia flies, then a Manual stretch the player flies ("you
    /// fly"): the flag planted on EVA, the drive or walk to the surface feature and the scan or sample.
    /// </summary>
    public sealed class ManualStretchContractTests
    {
        private static ContractCheck Check(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
        }

        private static SnapshotContract Fixture(string file, string id)
        {
            var contract = ContractCheckTests.Fixture(file);
            contract.Guid = id;
            contract.DeadlineUt = null;
            for (var i = 0; i < contract.Requirements.Count; i++) contract.Requirements[i].Key = ContractRequirement.KeyOf(id, new[] { i });
            return contract;
        }

        private static double LandingOn(string body)
        {
            return Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-land", 10000, body + ":LANDING"))).RouteDeltaV;
        }

        // ---- The card ----

        [Fact]
        public void APlantFlagContractIsPlannedAsALandingOnItsBodyWithAManualStretchYouFly()
        {
            var check = Check(ContractCheckTests.With(Fixture("PlantFlag.json", "c-flag")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal("Mun", row.Phase);
            Assert.True(row.YouFly);
            Assert.Contains("you fly", row.Detail(0));
            Assert.Equal(LandingOn("Mun"), check.RouteDeltaV, 3);
        }

        [Fact]
        public void ARoboticArmScanIsPlannedAsALandingNearTheFeatureWithTheDriveAManualStretch()
        {
            var snapshot = ContractCheckTests.With(Fixture("ROCScienceArmContract.json", "c-arm"));
            snapshot.Craft.Parts[0].Tags.Add(ManualKinds.ArmTag);

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Mun", row.Phase);
            Assert.True(row.YouFly);
            Assert.Equal(LandingOn("Mun"), check.RouteDeltaV, 3);
            var chosen = Assert.Single(check.Choose(check.TickedGuids).Requirements);
            Assert.Equal("drive to the MunBoulder and scan it with the robotic arm", chosen.Manual);
        }

        [Fact]
        public void ASurfaceSampleIsPlannedAsALandingNearTheFeatureWithTheWalkAndSampleAManualStretch()
        {
            var check = Check(ContractCheckTests.With(Fixture("ROCScienceRetrievalContract.json", "c-sample")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.YouFly);
            var chosen = Assert.Single(check.Choose(check.TickedGuids).Requirements);
            Assert.Equal("walk to the MunBoulder and take a sample of it on EVA", chosen.Manual);
        }

        [Fact]
        public void AnExplorationContractsFlagIsPlannedAsAManualStretchToo()
        {
            var check = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-explore", 10000, "Mun:LANDING", "Mun:FLAGPLANT")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.YouFly);
            Assert.Equal(LandingOn("Mun"), check.RouteDeltaV, 3);
        }

        // ---- The request and the plan check ----

        private static ContractChoice FlagChoice(out GameSnapshot snapshot)
        {
            snapshot = ContractCheckTests.With(Fixture("PlantFlag.json", "c-flag"));
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

        [Fact]
        public void TheRequestAsksClaudiaToPlanTheManualStretchAfterTheLandingThatServesIt()
        {
            var section = FlagChoice(out _).Section;

            Assert.Contains("Manual stretches the player flies (you fly)", section);
            Assert.Contains("plant a flag on EVA, landed on the Mun", section);
            Assert.Contains("manual true and an empty script", section);
        }

        [Fact]
        public void AManualStretchAfterTheLandingServesTheRequirementAndCarriesItsServesTag()
        {
            var choice = FlagChoice(out var snapshot);

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and land", "*Plant the flag", "Return to Kerbin"), choice.Requirements, "", "Kerbin");

            Assert.Empty(served.Problems);
            Assert.Equal(2, Assert.Single(served.Record).Leg);
            Assert.Equal(new[] { "PlantFlag" }, served.Legs[2]);
            Assert.Empty(served.Legs[1]);
        }

        [Fact]
        public void APlanThatLandsButPlansNoManualStretchThereGoesBackToClaudiaAsAFinding()
        {
            var choice = FlagChoice(out var snapshot);

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and land", "Return to Kerbin", "*Plant the flag"), choice.Requirements, "", "Kerbin");

            var problem = Assert.Single(served.Problems);
            Assert.Contains("plant a flag on the Mun", problem);
            Assert.Contains("no Manual stretch", problem);
            Assert.Contains("after leg 2 \"Transfer to the Mun and land\"", problem);
            Assert.Equal(-1, Assert.Single(served.Record).Leg);
        }

        [Fact]
        public void APlanThatNeverLandsGetsTheLandingFindingFirst()
        {
            var choice = FlagChoice(out var snapshot);

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "*Plant the flag"), choice.Requirements, "", "Kerbin");

            var problem = Assert.Single(served.Problems);
            Assert.Contains("only orbits the Mun", problem);
        }

        // ---- A retry after Hand back ----

        /// <summary>The exploration Contract "Explore the Mun", landing met, its flag not met at Hand back: droppable, with the revised plan's say.</summary>
        private static ContractReplan Retry(MissionPlan plan, int firstUnflown)
        {
            var contract = ContractCheckTests.Exploration("c-explore", 10000, "Mun:LANDING", "Mun:FLAGPLANT");
            var check = Check(ContractCheckTests.With(contract));
            var chosen = check.Choose(check.TickedGuids).Requirements;
            contract.Requirements[0].State = "Complete";
            var snapshot = ContractCheckTests.With(contract);
            snapshot.Body = "Mun";
            snapshot.Situation = "LANDED";
            var droppable = new Dictionary<string, string> { { "c-explore", "\"plant a flag on the Mun\" isn't met: " + ContractWatch.NotMetAtHandBack } };
            return ContractReplan.Check(snapshot, plan, chosen, "", "Kerbin", droppable, new HashSet<string>(), new List<string>(), new HashSet<string>(), firstUnflown);
        }

        [Fact]
        public void ARetryStretchKeepsAContractWhoseOtherRequirementsWereMetOnLegsAlreadyFlown()
        {
            var replan = Retry(Plan("Transfer to the Mun and land", "*Plant the flag", "Return to Kerbin"), 1);

            Assert.Empty(replan.Drops);
            Assert.Empty(replan.Problems);
        }

        [Fact]
        public void ARetryStretchAfterTheFlownOneCarriesTheServesTagNotTheFlownOne()
        {
            var replan = Retry(Plan("Transfer to the Mun and land", "*Plant the flag", "*Plant the flag again", "Return to Kerbin"), 2);

            Assert.Empty(replan.Drops);
            Assert.Equal(2, replan.Record.Single(r => r.Words == "plant a flag on the Mun").Leg);
            Assert.Contains("Explore the Mun", replan.Legs[2]);
            Assert.DoesNotContain("Explore the Mun", replan.Legs[1]);
        }

        [Fact]
        public void ARevisedPlanWithNoRetryStretchStillDropsIt()
        {
            var replan = Retry(Plan("Transfer to the Mun and land", "*Plant the flag", "Return to Kerbin"), 2);

            Assert.Contains("c-explore", replan.Drops.Keys);
        }

        // ---- What the craft needs: "this craft can't: …" ----

        [Fact]
        public void AnArmScanWithNoRoboticArmAboardIsThisCraftCant()
        {
            var left = Assert.Single(Check(ContractCheckTests.With(Fixture("ROCScienceArmContract.json", "c-arm"))).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no robotic scanner arm aboard", left.Reason);
        }

        [Fact]
        public void AFlagOrASampleWithNoKerbalAboardIsThisCraftCant()
        {
            var snapshot = ContractCheckTests.With(Fixture("PlantFlag.json", "c-flag"), Fixture("ROCScienceRetrievalContract.json", "c-sample"));
            snapshot.Crew.Clear();

            var check = ContractCheck.Work(snapshot, "", true, ContractCheckTests.Margin);

            Assert.Empty(check.Fitting);
            Assert.Equal("this craft can't: planting a flag needs a kerbal aboard to go on EVA", check.NotThisFlight.Single(l => l.Guid == "c-flag").Reason);
            Assert.Equal("this craft can't: a surface sample needs a kerbal aboard to go on EVA", check.NotThisFlight.Single(l => l.Guid == "c-sample").Reason);
            Assert.All(check.NotThisFlight, l => Assert.Equal(LeftOutKind.CraftCant, l.Kind));
        }

        [Fact]
        public void FlagsAndSurfaceFeaturesOnKerbinAreNotYet()
        {
            var flag = Fixture("PlantFlag.json", "c-flag");
            var sample = Fixture("ROCScienceRetrievalContract.json", "c-sample");
            foreach (var contract in new[] { flag, sample })
                contract.Requirements[0].Settings = contract.Requirements[0].Settings.Select(s => s.Name == "targetBody" ? new ContractSetting(s.Name, "1") : s).ToList();

            var check = Check(ContractCheckTests.With(flag, sample));

            Assert.Empty(check.Fitting);
            Assert.Equal(ManualKinds.HomeFlagNotYet, check.NotThisFlight.Single(l => l.Guid == "c-flag").Reason);
            Assert.Equal(ManualKinds.HomeSurfaceNotYet, check.NotThisFlight.Single(l => l.Guid == "c-sample").Reason);
        }
    }
}
