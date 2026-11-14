// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1037: the stock-type Contract fixtures, one Game Snapshot per Contract as the capture button writes them. Provisional
    /// ones come from the research; the player's in-game recordings replace them (Fixtures/contracts/README.md says which is which).
    /// </summary>
    public sealed class ContractFixturesTests
    {
        private static string Folder
        {
            get { return Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts"); }
        }

        public static IEnumerable<object[]> Fixtures()
        {
            return Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void EachFixtureIsOneAcceptedContractOfTheKindItIsNamedFor(string file)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(Folder, file)));

            Assert.Equal("CAREER", snapshot.GameMode);
            var contract = Assert.Single(snapshot.Contracts.Accepted);
            Assert.Equal(file.Split('-', '.')[0], contract.Kind);
            Assert.Equal("Active", contract.State);
            Assert.NotEmpty(contract.Requirements);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void EveryRequirementIsKeyedByItsPlaceInTheTree(string file)
        {
            var contract = GameSnapshot.Parse(File.ReadAllText(Path.Combine(Folder, file))).Contracts.Accepted[0];
            var keys = new List<string>();
            CheckKeys(contract.Guid, contract.Requirements, new List<int>(), keys);
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        private static void CheckKeys(string guid, List<ContractRequirement> requirements, List<int> path, List<string> keys)
        {
            for (var i = 0; i < requirements.Count; i++)
            {
                var at = new List<int>(path) { i };
                Assert.Equal(ContractRequirement.KeyOf(guid, at), requirements[i].Key);
                keys.Add(requirements[i].Key);
                CheckKeys(guid, requirements[i].Requirements, at, keys);
            }
        }

        /// <summary>
        /// The keys' GUID is the one the save file writes for the Contract (its saved <c>guid</c>), which a load reads back, so a
        /// recording whose keys used any other id would fail here.
        /// </summary>
        [Theory]
        [MemberData(nameof(Fixtures))]
        public void TheKeysGuidIsTheOneTheSaveWrites(string file)
        {
            var contract = GameSnapshot.Parse(File.ReadAllText(Path.Combine(Folder, file))).Contracts.Accepted[0];

            Assert.Equal(contract.Guid, Assert.Single(contract.Settings, s => s.Name == "guid").Value);
            Assert.StartsWith(contract.Guid + "/", contract.Requirements[0].Key);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void EachFixtureSurvivesTheRecordingForm(string file)
        {
            var once = GameSnapshot.Parse(File.ReadAllText(Path.Combine(Folder, file))).ToJson();
            Assert.Equal(once, GameSnapshot.Parse(once).ToJson());
        }

        [Fact]
        public void TheReadmeSaysWhereEachFixtureCameFrom()
        {
            var readme = File.ReadAllText(Path.Combine(Folder, "README.md"));
            foreach (var file in Directory.GetFiles(Folder, "*.json")) Assert.Contains("`" + Path.GetFileName(file) + "`", readme);
        }

        [Fact]
        public void TheKeyIsTheGuidAndThePathFromTheTop()
        {
            Assert.Equal("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f/0", ContractRequirement.KeyOf("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f", new[] { 0 }));
            Assert.Equal("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f/1.0.12", ContractRequirement.KeyOf("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f", new[] { 1, 0, 12 }));
        }
    }
}
