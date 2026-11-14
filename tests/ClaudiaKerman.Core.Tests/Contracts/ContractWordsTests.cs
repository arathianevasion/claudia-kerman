// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1036 (seam 1: the Mission in Core, driven by a Game Snapshot with Contracts): the player's typed words and the Contracts are one
    /// route. A leg both need is flown once and costs once, with Serves tags naming both; where the words rule out what a Contract
    /// needs, the words win and the Contract is left out with "can't with your words: ...".
    /// </summary>
    public sealed class ContractWordsTests
    {
        private const string WordsTag = "your words";

        private static ContractCheck Check(string words, params string[] targets)
        {
            return ContractCheck.Work(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, targets)), words, false, ContractCheckTests.Margin);
        }

        [Fact]
        public void WordsAndAContractThatLandOnTheSameBodyShareTheLegAndItsCost()
        {
            var contractAlone = Check("", "Mun:LANDING");
            var together = Check("Land on the Mun and return to Kerbin", "Mun:LANDING");

            var row = Assert.Single(together.Fitting);
            // The words add nothing over the Contract: one landing, one trip home.
            Assert.Equal(contractAlone.RouteDeltaV, together.RouteDeltaV, 3);
            // Its own cost is the route with it less the route without: the words already fly the leg.
            Assert.Equal(0, row.AddedDeltaV, 3);
            var choice = together.Choose(new[] { row.Guid });
            var landing = Assert.Single(choice.Legs, l => l.Text == "land on the Mun");
            Assert.Contains(WordsTag, landing.Serves);
            Assert.Contains(row.Title, landing.Serves);
            Assert.Equal(2, landing.Serves.Length);
        }

        [Fact]
        public void WordsThatOnlyOrbitShareTheLandingLegAContractNeedsAndBothAreTaggedOnIt()
        {
            var contractAlone = Check("", "Mun:LANDING");
            var together = Check("Orbit the Mun", "Mun:LANDING");

            Assert.Equal(contractAlone.RouteDeltaV, together.RouteDeltaV, 3);
            var choice = together.Choose(together.TickedGuids);
            var landing = Assert.Single(choice.Legs, l => l.Text == "land on the Mun");
            Assert.Contains(WordsTag, landing.Serves);
            Assert.Equal(2, landing.Serves.Length);
            Assert.Contains("[serves: " + WordsTag, choice.Section);
        }

        [Fact]
        public void AContractOnAnotherLegIsTaggedWithItselfAndTheWordsOnTheirOwn()
        {
            var together = Check("Orbit Minmus", "Mun:ORBIT");

            var choice = together.Choose(together.TickedGuids);
            Assert.Equal(new[] { WordsTag }, choice.Legs.Single(l => l.Text == "orbit Minmus").Serves);
            Assert.Equal(new[] { together.Fitting[0].Title }, choice.Legs.Single(l => l.Text == "orbit the Mun").Serves);
        }

        [Fact]
        public void TypedWordsAClauseWithATripWordCountsEvenWhenItIsJustOrbitAndABody()
        {
            var bare = Check("", "Mun:ORBIT");
            var withMinmus = Check("Orbit Minmus", "Mun:ORBIT");

            Assert.True(withMinmus.RouteDeltaV > bare.RouteDeltaV, bare.RouteDeltaV + " vs " + withMinmus.RouteDeltaV);
        }

        [Fact]
        public void AContractTheWordsRuleOutIsLeftOutWithWhyAndTheTypedRequirementIsKept()
        {
            var check = Check("Orbit Minmus and don't land on the Mun", "Mun:LANDING");

            Assert.Empty(check.Fitting);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.CantWithWords, left.Kind);
            Assert.StartsWith("can't with your words: ", left.Reason);
            Assert.Contains("land", left.Reason);
            Assert.False(left.Tickable);
            // The typed requirement stays: the route still goes to Minmus.
            Assert.True(check.WordsFit);
            Assert.True(check.RouteDeltaV > 500);
            var minmus = check.Choose(new string[0]);
            Assert.Contains("Orbit Minmus", minmus.Words);
        }

        [Fact]
        public void ANoLandingWithNoBodyNamedRulesOutEveryLandingButNotAnOrbit()
        {
            var check = ContractCheck.Work(ContractCheckTests.With(
                ContractCheckTests.Exploration("land", 20000, "Mun:LANDING"), ContractCheckTests.Exploration("orbit", 10000, "Minmus:ORBIT")),
                "Without landing anywhere", false, ContractCheckTests.Margin);

            Assert.Equal("orbit", Assert.Single(check.Fitting).Guid);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal("land", left.Guid);
            Assert.Equal(LeftOutKind.CantWithWords, left.Kind);
        }

        [Fact]
        public void AFlybyOnlyWordRulesOutAnOrbitContractAtThatBody()
        {
            var check = Check("Only fly by the Mun", "Mun:ORBIT");

            Assert.Empty(check.Fitting);
            Assert.Equal(LeftOutKind.CantWithWords, Assert.Single(check.NotThisFlight).Kind);
        }

        [Fact]
        public void TheLeftOutContractShowsInTheRequestsNotThisFlightWithItsReason()
        {
            var check = Check("Orbit Minmus and don't land on the Mun", "Mun:LANDING");
            var chosen = ContractCheck.Work(ContractCheckTests.With(
                ContractCheckTests.Exploration("land", 20000, "Mun:LANDING"), ContractCheckTests.Exploration("orbit", 10000, "Minmus:ORBIT")),
                "Don't land on the Mun", false, ContractCheckTests.Margin).Choose(new[] { "orbit" });

            Assert.Empty(check.Fitting);
            Assert.Contains("Not this flight: ", chosen.Section);
            Assert.Contains("can't with your words: ", chosen.Section);
        }
    }
}
