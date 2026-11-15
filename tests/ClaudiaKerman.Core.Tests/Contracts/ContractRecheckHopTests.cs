// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>#1092: the deadline recheck times a hop Contract as a hop, the way the Contract check prices it.</summary>
    public sealed class ContractRecheckHopTests
    {
        private const double Day = 21600;

        private static GameSnapshot Hop(double? deadlineFromNow, out List<ChosenRequirement> chosen)
        {
            var contract = ContractCheckTests.BoosterTest("c-test");
            contract.State = "Active";
            contract.Requirements[0].Key = ContractRequirement.KeyOf("c-test", new[] { 0 });
            var snapshot = ContractCheckTests.With(contract);
            chosen = ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin).Choose(new[] { "c-test" }).Requirements;
            Assert.Equal(ContractReach.Hop, Assert.Single(chosen).Reach);
            snapshot.Contracts.Accepted[0].DeadlineUt = deadlineFromNow.HasValue ? snapshot.Ut + deadlineFromNow : null;
            return snapshot;
        }

        private static IList<KeyValuePair<SnapshotContract, List<ChosenRequirement>>> Open(GameSnapshot snapshot, List<ChosenRequirement> chosen)
        {
            return new[] { new KeyValuePair<SnapshotContract, List<ChosenRequirement>>(snapshot.Contracts.Accepted[0], chosen) };
        }

        [Fact]
        public void ASuborbitalPartTestThatFitsItsDeadlineAsAHopIsTimedAsAHopAndNotFlagged()
        {
            var snapshot = Hop(ContractRoutes.LaunchSeconds + 10 * Day, out var chosen);
            List<string> rechecked;

            var late = ContractRecheck.Check(snapshot, chosen, "", "Kerbin", new HashSet<string>(), out rechecked);

            Assert.Empty(late);
            Assert.Equal(new[] { "c-test" }, rechecked);
            var route = ContractRecheck.RouteOf(snapshot, Open(snapshot, chosen), "", "Kerbin");
            Assert.NotEqual("", route.Hop);
            Assert.NotNull(route.HopAp);
        }

        [Fact]
        public void TheRechecksRouteAndTimeForAHopEqualTheContractChecks()
        {
            var snapshot = Hop(ContractRoutes.LaunchSeconds + 10 * Day, out var chosen);
            var check = ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
            check.Choose(new[] { "c-test" });

            var route = ContractRecheck.RouteOf(snapshot, Open(snapshot, chosen), "", "Kerbin");

            Assert.Equal(check.RouteDeltaV, route.DeltaV);
            Assert.Equal(snapshot.Ut + ContractRoutes.LaunchSeconds, route.DoneUt(new ReadRequirement { Body = "Kerbin", Reach = ContractReach.Hop }, "Kerbin"));
        }

        [Fact]
        public void AHopTheSimCantPriceIsStillTimedAsTheLaunchToOrbit()
        {
            var snapshot = Hop(ContractRoutes.LaunchSeconds + 10 * Day, out var chosen);
            foreach (var stage in snapshot.Stages) { stage.DeltaVVac = 0; stage.ThrustVacKn = 0; }

            var route = ContractRecheck.RouteOf(snapshot, Open(snapshot, chosen), "", "Kerbin");

            Assert.Equal("", route.Hop);
            Assert.Null(route.HopAp);
        }
    }
}
