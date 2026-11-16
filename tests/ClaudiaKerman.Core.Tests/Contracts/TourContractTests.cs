// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1034, seam 1: tourists fly with Claudia, and Grand Tours are possible. A tourism Contract is each tourist's destinations, then the
    /// tourist brought home; the tourist must be aboard, in a seat. The high-gee (blackout) variant is "not yet". A Grand Tour is one
    /// craft visiting every body it lists, priced as one route, so the Contract check judges whether one craft reaches them all.
    /// </summary>
    public sealed class TourContractTests
    {
        private const string Tourist = "Lodfrid Kerman";

        private static ContractCheck Check(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
        }

        private static SnapshotContract Tourism(string id = "c-tour")
        {
            var contract = ContractCheckTests.Fixture("TourismContract.json");
            contract.Guid = id;
            contract.DeadlineUt = null;
            return contract;
        }

        /// <summary>The pad craft with a seat for the tourist and the tourist in it.</summary>
        private static GameSnapshot Aboard(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Seats = 2;
            snapshot.Crew.Add(new SnapshotCrewMember { Name = Tourist, Trait = "Tourist" });
            return snapshot;
        }

        private static double OrbitOfKerbinAndHome()
        {
            return Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, "Kerbin:ORBIT"))).RouteDeltaV;
        }

        // ---- Tourism ----

        [Fact]
        public void ATourismContractWithItsTouristAboardIsPlannedAsTheirDestinationsAndTheTripHome()
        {
            var check = Check(Aboard(Tourism()));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal(OrbitOfKerbinAndHome(), check.RouteDeltaV, 3);
            var words = check.Choose(check.TickedGuids).Requirements.Select(r => r.Words).ToList();
            Assert.Contains("orbit Kerbin", words);
            Assert.Contains("bring tourist " + Tourist + " home", words);
        }
    }
}
