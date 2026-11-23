// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1260: the Contracts as the game saves them, read from the in-game captures of the Career Test save (2026-11-14): a body by its
    /// index in Exploration and Survey Requirements and by name in a part test, a survey's band as a number.
    /// </summary>
    public sealed class RecordedContractsTests
    {
        private static SnapshotContract Recorded(string file)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", file);
            return GameSnapshot.Parse(File.ReadAllText(path)).Contracts.Accepted.Single();
        }

        [Fact]
        public void ReachSpaceOverKerbinSavedByIndexIsAHopOverKerbin()
        {
            var read = Assert.Single(ContractKinds.Read(Recorded("ExplorationContract-e087d8d1-ut789.json"), "Kerbin"));

            Assert.True(read.Plannable, read.NotYet);
            Assert.Equal("Kerbin", read.Body);
            Assert.Equal(ContractReach.Hop, read.Reach);
        }

        [Fact]
        public void AnOrbitOfKerbinSavedByIndexIsTheLaunchToOrbit()
        {
            var contract = Recorded("ExplorationContract-e087d8d1-ut789.json");
            contract.Requirements[0].Settings = contract.Requirements[0].Settings
                .Select(s => s.Name == "targetType" ? new ContractSetting("targetType", "ORBIT") : s).ToList();

            var read = Assert.Single(ContractKinds.Read(contract, "Kerbin"));

            Assert.True(read.Plannable, read.NotYet);
            Assert.Equal("Kerbin", read.Body);
            Assert.Equal(ContractReach.Orbit, read.Reach);
            Assert.Equal("orbit Kerbin", read.Words);
        }

        [Fact]
        public void AnExplorationBodyIndexBeyondTheStockSystemIsNotYet()
        {
            var contract = Recorded("ExplorationContract-e087d8d1-ut789.json");
            contract.Requirements[0].Settings = contract.Requirements[0].Settings
                .Select(s => s.Name == "targetBody" ? new ContractSetting("targetBody", "17") : s).ToList();

            var read = Assert.Single(ContractKinds.Read(contract, "Kerbin"));

            Assert.Equal("not yet: the Contract's body isn't one Claudia knows", read.NotYet);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("Kerbin")]
        public void APartTestsBodyResolvesByIndexOrName(string body)
        {
            var contract = Recorded("PartTest-20ebfabe-ut789.json");
            var test = contract.Requirements[0];
            test.Settings = test.Settings.Select(s => s.Name == "body" ? new ContractSetting("body", body) : s).ToList();

            var read = Assert.Single(ContractKinds.Read(contract, "Kerbin"));

            Assert.True(read.Plannable, read.NotYet);
            Assert.Equal("Kerbin", read.Body);
        }

        [Fact]
        public void APartTestWithItsPartAboardStillPlansItsEnvelopes()
        {
            var contract = Recorded("PartTest-20ebfabe-ut789.json");
            contract.Requirements[0].State = "Complete";

            var read = Assert.Single(ContractKinds.Read(contract, "Kerbin"));

            Assert.True(read.Plannable, read.NotYet);
            Assert.Equal(ContractReach.Hop, read.Reach);
            Assert.Equal(51000, read.Band.MinAltitude);
            Assert.Equal(56000, read.Band.MaxAltitude);
            Assert.Equal(100, read.Band.MinSpeed);
            Assert.Equal(260, read.Band.MaxSpeed);
        }

        [Fact]
        public void APartTestWithEveryConditionMetHasNothingLeftToPlan()
        {
            var contract = Recorded("PartTest-20ebfabe-ut789.json");
            foreach (var requirement in contract.Requirements) requirement.State = "Complete";

            Assert.Empty(ContractKinds.Read(contract, "Kerbin"));
        }

        [Fact]
        public void ASurveyBandSavedAsANumberReadsAsItsBand()
        {
            var read = ContractKinds.Read(Recorded("SurveyContract-9f444a7c-ut789.json"), "Kerbin");

            Assert.Equal(5, read.Count);
            Assert.All(read, r =>
            {
                Assert.True(r.Plannable, r.NotYet);
                Assert.Equal("Mun", r.Body);
                Assert.Equal(ContractReach.Orbit, r.Reach);
                Assert.Equal("gravityScan", r.Experiment);
                Assert.NotNull(r.Waypoint.MinAltitude);
            });
        }

        [Theory]
        [InlineData("2", "LOW")]
        [InlineData("3", "HIGH")]
        [InlineData("LOW", "LOW")]
        public void SurveyBandsByNumberOrName(string saved, string band)
        {
            var contract = Recorded("SurveyContract-9f444a7c-ut789.json");
            var waypoint = contract.Requirements[0];
            waypoint.Settings = waypoint.Settings.Select(s => s.Name == "band" ? new ContractSetting("band", saved) : s).ToList();

            var read = ContractKinds.Read(contract, "Kerbin")[0];

            Assert.True(read.Plannable, read.NotYet);
            Assert.Equal(band == "LOW", read.Waypoint.MaxAltitude.HasValue);
            Assert.Equal(band == "HIGH", read.Waypoint.MinAltitude.HasValue);
        }

        [Fact]
        public void TheGamesTitleComesBackFromTheRecordingForm()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", "PartTest-20ebfabe-ut789.json")));
            snapshot.Contracts.Accepted[0].Title = "Test RT-10 \"Hammer\" Solid Fuel Booster";

            var read = GameSnapshot.Parse(snapshot.ToJson()).Contracts.Accepted[0];

            Assert.Equal("Test RT-10 \"Hammer\" Solid Fuel Booster", read.Title);
        }

        [Fact]
        public void ARecordingWithoutATitleReadsAsNone()
        {
            Assert.Equal("", Recorded("PartTest-20ebfabe-ut789.json").Title);
        }
    }
}
