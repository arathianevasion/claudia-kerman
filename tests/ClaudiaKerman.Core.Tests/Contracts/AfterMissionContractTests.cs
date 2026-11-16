// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1035, seam 1: Contracts that complete after the Mission. Sentinel, comet-detection and deployed-science Contracts are planned up
    /// to placement (a Sentinel telescope in solar orbit, a deployed science station landed and set up on EVA); the time-based Requirement
    /// stays open, shown "completes after the Mission", and the Contract counts only when Core's wait estimate fits its deadline with the
    /// spare rule.
    /// </summary>
    public sealed class AfterMissionContractTests
    {
        private static SnapshotContract Fixture(string file, string id)
        {
            var contract = ContractCheckTests.Fixture(file);
            contract.Guid = id;
            for (var i = 0; i < contract.Requirements.Count; i++) contract.Requirements[i].Key = ContractRequirement.KeyOf(id, new[] { i });
            return contract;
        }

        /// <summary>The pad craft carrying a Sentinel telescope, an antenna and a power source.</summary>
        private static GameSnapshot WithTelescope(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Parts[0].Tags.Add("antenna");
            snapshot.Craft.Parts[0].Tags.Add("electric_generator");
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 990001, Name = PlacementKinds.TelescopePart, Title = "SENTINEL Infrared Telescope", ParentId = snapshot.Craft.Parts[0].Id });
            return snapshot;
        }

        /// <summary>Remote: the craft itself may stay in solar orbit as the telescope.</summary>
        private static ContractCheck Remote(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", true, ContractCheckTests.Margin);
        }

        [Fact]
        public void ASentinelContractIsPlannedUpToPlacingTheTelescopeInSolarOrbitAndShownCompletesAfterTheMission()
        {
            var check = Remote(WithTelescope(Fixture("SentinelContract.json", "c-sentinel")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.True(row.AfterMission);
            Assert.Equal("Solar orbit near Kerbin", row.Phase);
            Assert.Contains("completes after the Mission", row.Detail(0));
        }
    }
}
