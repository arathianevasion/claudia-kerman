// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
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
            var mark = ContractMarks.Of(Fixture("TourismContract.json"), false);

            Assert.False(mark.Plannable);
            Assert.Equal("not yet: tourism aren't planned yet", mark.Hover);
        }

        /// <summary>#1048, #1029: a part test met on a hop below orbit over Kerbin is planned, and so is a haul into the Mun's orbit.</summary>
        [Fact]
        public void APartTestOnAHopAndAHaulIntoOrbitBothGetTheFlag()
        {
            var test = ContractMarks.Of(Fixture("PartTest.json"), false);
            var haul = ContractMarks.Of(Fixture("PartTest-haul.json"), false);

            Assert.True(test.Plannable);
            Assert.Equal("Claudia can plan this: test solidBooster.sm.v2 through 6,000–10,000 m at 210–280 m/s over Kerbin", test.Hover);
            Assert.True(haul.Plannable);
            Assert.Equal("Claudia can plan this: carry batteryBank into orbit round the Mun", haul.Hover);
        }

        /// <summary>#1162: a ground test at speed reads "not yet: aircraft or rover" on the mark, as in the Contract check.</summary>
        [Fact]
        public void AGroundTestAtSpeedIsNotYetAircraftOrRover()
        {
            var contract = Fixture("PartTest.json");
            contract.Requirements[0].Settings = contract.Requirements[0].Settings
                .Select(x => x.Name == "situation" ? new ContractSetting("situation", "LANDED") : x).ToList();

            var mark = ContractMarks.Of(contract, false);

            Assert.False(mark.Plannable);
            Assert.Equal("not yet: aircraft or rover", mark.Hover);
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

    

        /// <summary>#1030: a low-flying survey is "not yet" with its reason; a collect-science Contract gets the flag.</summary>
        [Fact]
        public void ALowFlyingSurveyGetsTheHollowMarkAndCollectScienceTheFlag()
        {
            var survey = ContractMarks.Of(Fixture("SurveyContract.json"), false);
            var science = ContractMarks.Of(Fixture("CollectScience.json"), false);

            Assert.False(survey.Plannable);
            Assert.Equal("not yet: low-flying surveys need an aircraft over the waypoint", survey.Hover);
            Assert.True(science.Plannable);
            Assert.Equal("Claudia can plan this: collect science in space near the Mun", science.Hover);
        }
    }
}
