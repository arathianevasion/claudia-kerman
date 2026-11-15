// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1030, seam 1: collect-science and survey Contracts ride along. Collect science is the science block run at the body; a survey's
    /// waypoints in a space band are passes over them from orbit (the plane change that puts the ground track over them and the wait for
    /// the pass), on the ground landings at them; low-flying surveys are "not yet"; the experiment must be on the craft.
    /// </summary>
    public sealed class ScienceSurveyContractTests
    {
        /// <summary>The Mun's GM and radius in the pad fixture's body table, for figures worked by hand.</summary>
        private const double MunGm = 6.5138398e10;
        private const double MunRadius = 200000;
        private const double MunRotation = 138984.37657447575;

        private static ContractCheck Check(GameSnapshot snapshot, string words = "")
        {
            return ContractCheck.Work(snapshot, words, false, ContractCheckTests.Margin);
        }

        private static SnapshotContract Science(string id, string body, string location)
        {
            var contract = ContractCheckTests.Fixture("CollectScience.json");
            contract.Guid = id;
            contract.DeadlineUt = null;
            var requirement = contract.Requirements[0];
            requirement.Key = ContractRequirement.KeyOf(id, new[] { 0 });
            requirement.Settings = requirement.Settings.Select(s => s.Name == "body" ? new ContractSetting("body", body) : s.Name == "location" ? new ContractSetting("location", location) : s).ToList();
            return contract;
        }

        /// <summary>
        /// The SurveyContract fixture with its own GUID and one waypoint per <paramref name="waypoints"/> entry, "lat,lon": each a copy of
        /// the fixture's first, on body index <paramref name="body"/> in <paramref name="band"/> with <paramref name="experiment"/>.
        /// </summary>
        internal static SnapshotContract Survey(string id, string body, string band, string experiment, double threshold, params string[] waypoints)
        {
            var contract = ContractCheckTests.Fixture("SurveyContract.json");
            contract.Guid = id;
            contract.DeadlineUt = null;
            var template = contract.Requirements[0];
            contract.Requirements = waypoints.Select((w, i) =>
            {
                var at = w.Split(',');
                var values = new Dictionary<string, string>
                {
                    { "targetBody", body }, { "band", band }, { "experiment", experiment }, { "wpLatitude", at[0] }, { "wpLongitude", at[1] },
                    { "flightThreshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                };
                return new ContractRequirement
                {
                    Key = ContractRequirement.KeyOf(id, new[] { i }), Kind = template.Kind, State = "Incomplete",
                    Settings = template.Settings.Select(s => values.ContainsKey(s.Name) ? new ContractSetting(s.Name, values[s.Name]) : s).ToList(),
                };
            }).ToList();
            return contract;
        }

        private static DeltaVMap Map(GameSnapshot snapshot)
        {
            return DeltaVMap.Compute(snapshot.Bodies, "Kerbin");
        }

        [Fact]
        public void ACollectScienceContractInSpaceNearTheMunIsPlannedAsAnOrbitOfTheMun()
        {
            var snapshot = ContractCheckTests.With(ContractCheckTests.Fixture("CollectScience.json"));
            var orbit = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, "Mun:ORBIT")));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal("Mun", row.Phase);
            Assert.Equal(orbit.RouteDeltaV, check.RouteDeltaV, 3);
            Assert.Empty(check.NotThisFlight);
        }
    }
}
