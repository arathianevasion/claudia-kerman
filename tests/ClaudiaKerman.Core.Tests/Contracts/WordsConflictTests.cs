// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Contracts;
using Xunit;
using static ClaudiaKerman.Core.Tests.Contracts.ContractCheckTests;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>#1163: when the player's words give conflicting orders, the route stays the cheapest and the Contract check card says so.</summary>
    public sealed class WordsConflictTests
    {
        private const string Conflict = "orbit Minmus then land on the Mun then orbit Minmus";
        private const string Line = "your words give conflicting orders: using the cheapest";

        private static ContractCheck Check(string words)
        {
            return ContractCheck.Work(With(Exploration("a", 10000, "Duna:FLYBY")), words, false, Margin);
        }

        [Fact]
        public void ConflictingOrdersShowTheLine()
        {
            Assert.Equal(Line, Check(Conflict).OrderNote);
        }

        [Theory]
        [InlineData("orbit Minmus then land on the Mun")]
        [InlineData("orbit the Mun and orbit Minmus")]
        [InlineData("")]
        public void OneConsistentOrderOrNoneShowsNoLine(string words)
        {
            Assert.Equal("", Check(words).OrderNote);
        }

        [Fact]
        public void TheRouteIsStillTheCheapestOrder()
        {
            var routes = new ContractRoutes(Pad(), Conflict, "Kerbin");
            var cheapest = new ContractRoutes(Pad(), "orbit Minmus and land on the Mun", "Kerbin").Best(new Dictionary<string, ContractReach>(), true, r => true).DeltaV;

            Assert.True(routes.ConflictingOrders);
            Assert.Equal(cheapest, routes.Best(new Dictionary<string, ContractReach>(), true, r => true).DeltaV, 6);
        }
    }
}
