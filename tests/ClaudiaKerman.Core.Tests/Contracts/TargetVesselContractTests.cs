// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1033, seam 1: Target Vessel Contracts (orbital rescue, repair, construction, comet sample). Claudia flies to the vessel the game
    /// spawned for the Contract (ADR 0013's amendment: the Contract's vessel is the Mission's one Target Vessel) and the player does the
    /// EVA in a Manual stretch; the route is priced by the rendezvous with it, and a set holds at most one such Contract.
    /// </summary>
    public sealed class TargetVesselContractTests
    {
        private static SnapshotContract Fixture(string file, string id)
        {
            var contract = ContractCheckTests.Fixture(file);
            contract.Guid = id;
            for (var i = 0; i < contract.Requirements.Count; i++) contract.Requirements[i].Key = ContractRequirement.KeyOf(id, new[] { i });
            return contract;
        }

        /// <summary>The vessel the game spawned for <paramref name="contract"/>, in a circular orbit <paramref name="km"/> above Kerbin.</summary>
        private static SnapshotVessel Spawned(SnapshotContract contract, uint id, string name, double km, string situation = "ORBITING", string body = "Kerbin")
        {
            return new SnapshotVessel
            {
                Id = id, Name = name, Body = body, Situation = situation, PeriapsisM = km * 1000, ApoapsisM = km * 1000, Inclination = 0, ContractGuid = contract.Guid,
            };
        }

        /// <summary>The pad craft with a second seat, Claudia in the first: one seat is empty.</summary>
        private static GameSnapshot WithSeat(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Seats = 2;
            return snapshot;
        }

        private static ContractCheck Check(GameSnapshot snapshot, string words = "", TargetVessel target = null)
        {
            return ContractCheck.Work(snapshot, words, false, ContractCheckTests.Margin, target);
        }

        [Fact]
        public void AnOrbitalRescueIsPlannedAsTheRendezvousAManualStretchForTheEvaAndTheTripHome()
        {
            var rescue = Fixture("RecoverAsset.json", "c-rescue");
            var snapshot = WithSeat(rescue);
            snapshot.ContractVessels.Add(Spawned(rescue, 4401, "Gusdun's Capsule", 120));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.True(row.YouFly);
            Assert.Equal("Kerbin orbit, Return", row.Phase);
            var choice = check.Choose(check.TickedGuids);
            var eva = choice.Requirements.Single(r => r.Manual.Length > 0);
            Assert.Equal("Kerbin", eva.Body);
            Assert.Equal(ContractReach.Orbit, eva.Reach);
            Assert.Contains("Gusdun Kerman", eva.Manual);
            Assert.Contains(choice.Requirements, r => r.Return);
            Assert.Equal((uint)4401, choice.Target.Id);
            Assert.Contains("Gusdun's Capsule", choice.Section);
        }

        [Fact]
        public void TheRouteIsPricedByTheRendezvousSoAHigherTargetCostsMore()
        {
            var low = Fixture("RecoverAsset.json", "c-low");
            var lowSnapshot = WithSeat(low);
            lowSnapshot.ContractVessels.Add(Spawned(low, 4401, "Gusdun's Capsule", 80));
            var high = Fixture("RecoverAsset.json", "c-high");
            var highSnapshot = WithSeat(high);
            highSnapshot.ContractVessels.Add(Spawned(high, 4401, "Gusdun's Capsule", 1200));

            var lowRoute = Check(lowSnapshot).RouteDeltaV;
            var highRoute = Check(highSnapshot).RouteDeltaV;

            // A Hohmann climb from 80 km to 1,200 km round Kerbin and back down is about 2 x 470 m/s; the rendezvous pays the climb and the circularisation there.
            Assert.InRange(highRoute - lowRoute, 500, 1500);
        }
    }
}
