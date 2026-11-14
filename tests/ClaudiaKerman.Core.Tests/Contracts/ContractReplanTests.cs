// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1025: the plan check on a revised Mission Plan. A replan only ever drops Contracts, with the mod's reason, and only those the watch
    /// found can't complete; a Contract the player didn't choose is refused unless they asked for it, and then it is priced like a chosen one.
    /// </summary>
    public sealed class ContractReplanTests
    {
        private static readonly SnapshotContract Mun = ContractCheckTests.Exploration("mun", 10000, "Mun:ORBIT");
        private static readonly SnapshotContract Minmus = ContractCheckTests.Exploration("minmus", 8000, "Minmus:ORBIT");

        private static List<ChosenRequirement> ChosenMun()
        {
            var check = ContractCheck.Work(ContractCheckTests.With(Mun), "", false, ContractCheckTests.Margin);
            return check.Choose(new[] { "mun" }).Requirements;
        }

        private static MissionPlan Plan(string why, params string[] legs)
        {
            var plan = new MissionPlan { Why = why };
            foreach (var leg in legs) plan.Sequences.Add(new PlannedSequence { Name = leg, Intent = "Fly it.", DeltaV = 100 });
            return plan;
        }

        private static ContractReplan Check(MissionPlan plan, IDictionary<string, string> droppable = null, IList<string> asked = null, ICollection<string> completed = null,
            ICollection<string> ended = null, int firstUnflown = 0)
        {
            return ContractReplan.Check(ContractCheckTests.With(Mun, Minmus), plan, ChosenMun(), "", "Kerbin",
                droppable ?? new Dictionary<string, string>(), completed ?? new HashSet<string>(), asked ?? new List<string>(), ended, firstUnflown);
        }

        [Fact]
        public void AFailedContractIsDroppedEvenWhenTheFlownLegStillServesIt()
        {
            var replan = Check(Plan("Home.", "Transfer to the Mun and capture into low orbit", "Return to Kerbin"),
                new Dictionary<string, string> { { "mun", "the game failed it" } }, ended: new HashSet<string> { "mun" }, firstUnflown: 1);

            Assert.Empty(replan.Problems);
            Assert.Equal("Explore the Mun: the game failed it", replan.Drops["mun"]);
            Assert.All(replan.Legs, l => Assert.Empty(l));
        }

        [Fact]
        public void AFallenBackContractIsKeptOnlyWhenALegStillToFlyServesIt()
        {
            var droppable = new Dictionary<string, string> { { "mun", "\"orbit the Mun\" fell back to unmet" } };
            var home = Check(Plan("Home.", "Transfer to the Mun and capture into low orbit", "Return to Kerbin"), droppable, firstUnflown: 1);
            var retry = Check(Plan("Retry.", "Transfer to the Mun and capture into low orbit", "Capture into low Mun orbit again", "Return to Kerbin"), droppable, firstUnflown: 1);

            Assert.Equal("Explore the Mun: \"orbit the Mun\" fell back to unmet", home.Drops["mun"]);
            Assert.Empty(retry.Drops);
            Assert.Empty(retry.Problems);
        }

        [Fact]
        public void ARevisionLeavingAChosenContractUnservedIsRefusedWhileItCanStillComplete()
        {
            var replan = Check(Plan("Home.", "Return to Kerbin"));

            Assert.Contains("no leg of the plan goes there", Assert.Single(replan.Problems));
            Assert.Empty(replan.Drops);
        }

        [Fact]
        public void AContractTheWatchFoundCantCompleteIsDroppedWithTheModsReason()
        {
            var replan = Check(Plan("Home.", "Return to Kerbin"), new Dictionary<string, string> { { "mun", "the game failed it" } });

            Assert.Empty(replan.Problems);
            Assert.Equal("Explore the Mun: the game failed it", replan.Drops["mun"]);
        }

        [Fact]
        public void ADroppableContractStillServedIsKept()
        {
            var replan = Check(Plan("Trim and retry.", "Transfer to the Mun and capture into low orbit"), new Dictionary<string, string> { { "mun", "\"orbit the Mun\" fell back to unmet" } });

            Assert.Empty(replan.Problems);
            Assert.Empty(replan.Drops);
            Assert.Equal(new[] { "Explore the Mun" }, replan.Legs[0]);
        }

        [Fact]
        public void ACompletedContractNeedsNoLeg()
        {
            var replan = Check(Plan("Home.", "Return to Kerbin"), completed: new HashSet<string> { "mun" });

            Assert.Empty(replan.Problems);
            Assert.Empty(replan.Drops);
        }

        [Fact]
        public void ARevisionThatAddsAContractThePlayerDidntAskForIsRefused()
        {
            var replan = Check(Plan("Also flying Explore Minmus on the way.", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit"));

            Assert.Contains("\"Explore Minmus\", a Contract the player didn't choose or ask for", Assert.Single(replan.Problems));
            Assert.Empty(replan.Added);
        }

        [Fact]
        public void AContractThePlayerAskedForIsAddedAndPricedLikeAChosenOne()
        {
            var asked = new List<string> { "also do the Minmus survey" };
            var serving = Check(Plan("Adds Explore Minmus, as the player asked.", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit"), asked: asked);
            var notServing = Check(Plan("Adds Explore Minmus, as the player asked.", "Transfer to the Mun and capture into low orbit"), asked: asked);

            Assert.Empty(serving.Problems);
            Assert.Equal("orbit Minmus", Assert.Single(serving.Added).Words);
            Assert.Equal(new[] { "Explore Minmus" }, serving.Legs[1]);
            Assert.Contains("\"Explore Minmus\" needs \"orbit Minmus\", and no leg of the plan goes there", Assert.Single(notServing.Problems));
        }
    }
}
