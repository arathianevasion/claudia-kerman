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
