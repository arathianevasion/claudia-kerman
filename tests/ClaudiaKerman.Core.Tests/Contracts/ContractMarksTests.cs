// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Contracts;
using Xunit;
using static ClaudiaKerman.Core.Tests.Contracts.ContractCheckTests;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1027: Mission Control's mark on an offered or accepted Contract: the Claudia Flag when Claudia can plan it, judged by its kind
    /// and settings only (never a craft), else a hollow "not yet" mark; the hover text gives the reason, from the kind table.
    /// </summary>
    public sealed class ContractMarksTests
    {
        [Fact]
        public void AnExplorationContractClaudiaCanPlanGetsTheFlagAndSaysWhatSheWouldFly()
        {
            var mark = ContractMarks.Of(Fixture("ExplorationContract.json"), false);

            Assert.True(mark.Plannable);
            Assert.Equal("Claudia can plan this: fly by the Mun, orbit the Mun", mark.Hover);
        }

        [Fact]
        public void AStockTypeClaudiaDoesNotPlanYetGetsTheHollowMarkWithTheKindTablesReason()
        {
            var mark = ContractMarks.Of(Fixture("PartTest.json"), false);

            Assert.False(mark.Plannable);
            Assert.Equal("not yet: part tests aren't planned yet", mark.Hover);
        }

        /// <summary>A mod's Contract is "not yet" even when its type shares a stock name: its Requirements may mean something else.</summary>
        [Fact]
        public void AModAddedContractIsNotYetWhateverItsTypeIsCalled()
        {
            var mark = ContractMarks.Of(Fixture("ExplorationContract.json"), true);

            Assert.False(mark.Plannable);
            Assert.Equal(ContractMarks.FromMod, mark.Hover);
            Assert.StartsWith("not yet: ", mark.Hover);
        }

        [Fact]
        public void AnExplorationContractWithAMilestoneClaudiaCantPlanYetIsNotYetForThatMilestone()
        {
            var mark = ContractMarks.Of(Exploration("g", 0, "Mun:ORBIT", "Mun:RENDEZVOUS"), false);

            Assert.False(mark.Plannable);
            Assert.Equal("not yet: needs a second vessel", mark.Hover);
        }

        [Fact]
        public void AnOptionalMilestoneClaudiaCantPlanIsSkippedAndTheHoverSaysSo()
        {
            var mark = ContractMarks.Of(Exploration("g", 0, "Minmus:LANDING", "?Minmus:SURFACEEVA"), false);

            Assert.True(mark.Plannable);
            Assert.Equal("Claudia can plan this: land on Minmus (optional Requirement skipped: needs an EVA in a Manual stretch, not planned yet)", mark.Hover);
        }

        [Fact]
        public void AContractWhoseOnlyMilestonesAreOptionalOnesClaudiaCantPlanIsNotYet()
        {
            var mark = ContractMarks.Of(Exploration("g", 0, "?Mun:SPACEWALK"), false);

            Assert.False(mark.Plannable);
            Assert.Equal("not yet: needs an EVA in a Manual stretch, not planned yet", mark.Hover);
        }

        [Fact]
        public void AContractWithEveryMilestoneMetHasNothingLeftToPlan()
        {
            var contract = Exploration("g", 0, "Mun:FLYBY");
            contract.Requirements[0].State = "Complete";

            var mark = ContractMarks.Of(contract, false);

            Assert.False(mark.Plannable);
            Assert.Equal("not yet: no Requirement is left to fly for", mark.Hover);
        }

    }
}
