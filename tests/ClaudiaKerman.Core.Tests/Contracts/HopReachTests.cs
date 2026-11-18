// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using Xunit;
using static ClaudiaKerman.Core.Tests.Contracts.ContractCheckTests;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>#1082: a hop is a reach that is set, never the default; it sorts below Flyby, so a launch to orbit meets it.</summary>
    public sealed class HopReachTests
    {
        [Fact]
        public void ARequirementBuiltWithoutAReachIsNotAHop()
        {
            Assert.NotEqual(ContractReach.Hop, new ReadRequirement().Reach);
            Assert.NotEqual(ContractReach.Hop, default(ContractReach));
            Assert.True(ContractReach.Hop < ContractReach.Flyby);
        }

        [Fact]
        public void AHopWithAnOrbitContractIsPricedToOrbitLikeTheOrbitContractAlone()
        {
            var check = ContractCheck.Work(With(BoosterTest("c-hop"), Exploration("c-orbit", 1000, "Kerbin:ORBIT")), "", false, Margin);

            var alone = check.Choose(new[] { "c-orbit" });
            var both = check.Choose(new[] { "c-hop", "c-orbit" });

            Assert.Equal(new[] { ContractReach.Hop, ContractReach.Orbit }, both.Requirements.Select(r => r.Reach).OrderBy(r => r).ToArray());
            Assert.Equal(alone.RouteWords, both.RouteWords);
            Assert.Equal(alone.Legs.Select(l => l.Text), both.Legs.Select(l => l.Text));
            Assert.DoesNotContain("Fly the hop straight up", both.Section);
        }
    }
}
