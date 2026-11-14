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
    /// <summary>#1037: the Game Snapshot carries the accepted Contracts as plain data, in the recording form.</summary>
    public sealed class SnapshotContractsTests
    {
        private const string Guid = "6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f";

        /// <summary>A non-haul PartTest as kRPC's 1.12.5 career save holds one: the conditions are children of the PartTest Requirement.</summary>
        private static GameSnapshot CareerWithPartTest()
        {
            var partTest = new ContractRequirement
            {
                Key = ContractRequirement.KeyOf(Guid, new[] { 0 }),
                Kind = "PartTest",
                State = "Incomplete",
            };
            partTest.Settings.Add(new ContractSetting("part", "solidBooster.sm.v2"));
            partTest.Settings.Add(new ContractSetting("haul", "False"));
            var altitude = new ContractRequirement { Key = ContractRequirement.KeyOf(Guid, new[] { 0, 2 }), Kind = "ReachAltitudeEnvelope", State = "Complete", Optional = true };
            altitude.Settings.Add(new ContractSetting("minAlt", "6000"));
            altitude.Settings.Add(new ContractSetting("maxAlt", "10000"));
            partTest.Requirements.Add(new ContractRequirement { Key = ContractRequirement.KeyOf(Guid, new[] { 0, 0 }), Kind = "ReachDestination", State = "Failed" });
            partTest.Requirements.Add(new ContractRequirement { Key = ContractRequirement.KeyOf(Guid, new[] { 0, 1 }), Kind = "ReachSituation", State = "Incomplete" });
            partTest.Requirements.Add(altitude);
            var contract = new SnapshotContract
            {
                Guid = Guid,
                Kind = "PartTest",
                State = "Active",
                DeadlineUt = 9223437.94,
                Funds = 12345.5,
                Science = 3,
                Reputation = 4.5,
                Requirements = { partTest },
            };
            contract.Settings.Add(new ContractSetting("dest", "1"));
            var snapshot = new GameSnapshot { GameMode = "CAREER", Ut = 21900 };
            snapshot.Contracts = new SnapshotContracts { Limit = 2, Active = 1, Accepted = { contract } };
            return snapshot;
        }

        [Fact]
        public void AcceptedContractsComeBackFromTheRecordingForm()
        {
            var read = GameSnapshot.Parse(CareerWithPartTest().ToJson());

            Assert.NotNull(read.Contracts);
            Assert.Equal(2, read.Contracts.Limit);
            Assert.Equal(1, read.Contracts.Active);
            var contract = Assert.Single(read.Contracts.Accepted);
            Assert.Equal(Guid, contract.Guid);
            Assert.Equal("PartTest", contract.Kind);
            Assert.Equal("Active", contract.State);
            Assert.Equal(9223437.94, contract.DeadlineUt);
            Assert.Equal(12345.5, contract.Funds);
            Assert.Equal(3, contract.Science);
            Assert.Equal(4.5, contract.Reputation);
            Assert.Equal(new[] { "dest=1" }, contract.Settings.Select(s => s.Name + "=" + s.Value));

            var partTest = Assert.Single(contract.Requirements);
            Assert.Equal(Guid + "/0", partTest.Key);
            Assert.Equal("PartTest", partTest.Kind);
            Assert.Equal("Incomplete", partTest.State);
            Assert.False(partTest.Optional);
            Assert.Equal(new[] { "part=solidBooster.sm.v2", "haul=False" }, partTest.Settings.Select(s => s.Name + "=" + s.Value));
            Assert.Equal(new[] { Guid + "/0.0", Guid + "/0.1", Guid + "/0.2" }, partTest.Requirements.Select(r => r.Key));
            Assert.Equal(new[] { "Failed", "Incomplete", "Complete" }, partTest.Requirements.Select(r => r.State));
            var altitude = partTest.Requirements[2];
            Assert.True(altitude.Optional);
            Assert.Equal(new[] { "minAlt=6000", "maxAlt=10000" }, altitude.Settings.Select(s => s.Name + "=" + s.Value));
        }

        [Theory]
        [InlineData("CAREER", true)]
        [InlineData("CAREER", false)]
        [InlineData("SCIENCE_SANDBOX", false)]
        [InlineData("SANDBOX", false)]
        public void ASnapshotWithNoAcceptedContractIsAsBefore(string mode, bool contractsRead)
        {
            var before = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            before.GameMode = mode;
            var expected = before.ToJson();
            var expectedTurn = before.ToJson(false);
            // Career reads the limit and count with no Contract accepted; Science and Sandbox have no Contract system to read.
            if (contractsRead) before.Contracts = new SnapshotContracts { Limit = 2, Active = 0 };

            Assert.Equal(expected, before.ToJson());
            Assert.Equal(expectedTurn, before.ToJson(false));
            Assert.Null(GameSnapshot.Parse(expected).Contracts);
        }

        [Fact]
        public void ClaudiasTurnsDoNotCarryTheContracts()
        {
            Assert.DoesNotContain("contracts", CareerWithPartTest().ToJson(false));
            Assert.DoesNotContain("contracts", CareerWithPartTest().ToJson(false, false));
            Assert.Contains("\"contracts\"", CareerWithPartTest().ToJson());
        }

        [Fact]
        public void NoLimitAndNoDeadlineComeBackAsNone()
        {
            var snapshot = CareerWithPartTest();
            snapshot.Contracts.Limit = null;
            snapshot.Contracts.Accepted[0].DeadlineUt = null;

            var json = snapshot.ToJson();
            var read = GameSnapshot.Parse(json);

            Assert.Null(read.Contracts.Limit);
            Assert.Null(read.Contracts.Accepted[0].DeadlineUt);
            Assert.DoesNotContain("deadline_ut", json);
        }
    }
}
