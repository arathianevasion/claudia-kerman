// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Contracts;
using Xunit;
using static ClaudiaKerman.Core.Tests.Contracts.ContractCheckTests;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>#1060: when the player's words give the bodies an order ("then", "after", "before"), the Contract Route keeps it.</summary>
    public sealed class WordsOrderTests
    {
        private static PricedRoute Route(string words, IDictionary<string, ContractReach> contractNeeds = null)
        {
            return new ContractRoutes(Pad(), words, "Kerbin").Best(contractNeeds ?? new Dictionary<string, ContractReach>(), true, r => true);
        }

        [Fact]
        public void ThenKeepsTheOrderTheWordsGiveBothWays()
        {
            Assert.Equal(new[] { "Minmus", "Mun" }, Route("orbit Minmus then land on the Mun").Order);
            Assert.Equal(new[] { "Mun", "Minmus" }, Route("land on the Mun then orbit Minmus").Order);
        }

        [Fact]
        public void TheOrderedRouteIsPricedInThePlayersOrderNotTheCheapest()
        {
            var free = Route("orbit Minmus and land on the Mun");
            var against = free.Order[0] == "Minmus" ? "land on the Mun then orbit Minmus" : "orbit Minmus then land on the Mun";
            var forced = Route(against);

            Assert.NotEqual(free.Order, forced.Order);
            Assert.True(forced.DeltaV > free.DeltaV, "forced " + forced.DeltaV + " against free " + free.DeltaV);
        }

        [Fact]
        public void AfterGivesTheSameOrderAsThen()
        {
            var then = Route("orbit Minmus then land on the Mun");
            var after = Route("land on the Mun after orbiting Minmus");

            Assert.Equal(new[] { "Minmus", "Mun" }, after.Order);
            Assert.Equal(then.DeltaV, after.DeltaV, 6);
        }

        [Fact]
        public void ALeadingConnectiveOrdersTheClausesAfterIt()
        {
            Assert.Equal(new[] { "Minmus", "Mun" }, Route("After orbiting Minmus, land on the Mun").Order);
            Assert.Equal(new[] { "Minmus", "Mun" }, Route("Before landing on the Mun, orbit Minmus").Order);
            Assert.Equal(new[] { "Minmus", "Mun" }, Route("orbit Minmus then, land on the Mun").Order);
        }

        [Fact]
        public void ABodyJoinedWithAndIsOrderedWithItsGroup()
        {
            var contract = new Dictionary<string, ContractReach> { { "Duna", ContractReach.Flyby } };
            var order = new ContractRoutes(Pad(), "orbit the Mun and orbit Minmus then fly by Duna", "Kerbin").Best(contract, true, r => true).Order;

            Assert.Equal("Duna", order[2]);
        }

        [Fact]
        public void BeforeKeepsTheOrder()
        {
            Assert.Equal(new[] { "Minmus", "Mun" }, Route("orbit Minmus before landing on the Mun").Order);
        }

        [Fact]
        public void APlainListKeepsTheCheapestOrder()
        {
            var list = Route("orbit the Mun and orbit Minmus");
            var comma = Route("orbit the Mun, orbit Minmus");
            var cheapest = System.Math.Min(Route("orbit the Mun then orbit Minmus").DeltaV, Route("orbit Minmus then orbit the Mun").DeltaV);

            Assert.Equal(cheapest, list.DeltaV, 6);
            Assert.Equal(cheapest, comma.DeltaV, 6);
        }

        [Fact]
        public void AContractForAThirdBodyGoesWhereItIsCheapestAroundTheOrderedLegs()
        {
            var contract = new Dictionary<string, ContractReach> { { "Duna", ContractReach.Flyby } };
            var ordered = Route("orbit Minmus then land on the Mun", contract);

            Assert.Equal(3, ordered.Order.Count);
            Assert.True(ordered.Order.IndexOf("Minmus") < ordered.Order.IndexOf("Mun"));
            var best = double.MaxValue;
            foreach (var order in new[] { new[] { "Duna", "Minmus", "Mun" }, new[] { "Minmus", "Duna", "Mun" }, new[] { "Minmus", "Mun", "Duna" } })
            {
                var all = new ContractRoutes(Pad(), "orbit Minmus then land on the Mun", "Kerbin");
                var priced = all.Best(new Dictionary<string, ContractReach> { { "Duna", ContractReach.Flyby } }, true, r => string.Join(",", r.Order) == string.Join(",", order));
                if (string.Join(",", priced.Order) == string.Join(",", order)) best = System.Math.Min(best, priced.DeltaV);
            }
            Assert.Equal(best, ordered.DeltaV, 6);
        }
    }
}
