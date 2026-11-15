// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
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
            var band = new HopBand { MinAltitude = 8000, MaxAltitude = 12000 };
            var orbit = new Dictionary<string, ContractReach> { { "Kerbin", ContractReach.Orbit } };
            var routes = new ContractRoutes(Pad(), "", "Kerbin");

            var alone = routes.Best(orbit, true, r => true);
            var withHop = routes.Best(routes.Needs(orbit.WithHop("Kerbin")), true, r => true, new[] { band });

            Assert.Equal(alone.DeltaV, withHop.DeltaV);
            Assert.Equal("", withHop.Hop);
            Assert.Null(withHop.HopAp);
        }
    }

    internal static class HopReachExtensions
    {
        /// <summary>The needs the Contract check builds for a set holding a hop at <paramref name="body"/>: the furthest reach wins.</summary>
        internal static IDictionary<string, ContractReach> WithHop(this IDictionary<string, ContractReach> needs, string body)
        {
            var merged = new Dictionary<string, ContractReach>(needs);
            ContractReach had;
            if (!merged.TryGetValue(body, out had) || ContractReach.Hop > had) merged[body] = ContractReach.Hop;
            return merged;
        }
    }
}
