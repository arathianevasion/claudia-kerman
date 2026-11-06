// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class BlockSelectionTests
    {
        private static readonly string[] Shipped =
            { "ascend", "circularize", "coast", "exec_node", "land", "launch", "reentry", "return", "science", "transfer_to", "future_block" };

        [Fact]
        public void AMunLandingGetsTheMunBlocksButNotReentryOrScience()
        {
            var picked = BlockSelection.For("Land on the Mun", Shipped);
            Assert.Contains("transfer_to", picked);
            Assert.Contains("land", picked);
            Assert.Contains("exec_node", picked);
            Assert.DoesNotContain("reentry", picked);
            Assert.DoesNotContain("science", picked);
        }

        [Theory]
        [InlineData("Land on Duna")]
        [InlineData("Fly to Duna and land on Duna")]
        [InlineData("Put a lander on Eve: land on Eve")]
        public void ALandingOnABodyWithAnAtmosphereGetsReentry(string goal)
        {
            Assert.Contains("reentry", BlockSelection.For(goal, Shipped));
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            Assert.Contains("reentry", BlockSelection.For(goal, names));
            Assert.DoesNotContain("land", BlockSelection.For(goal, Shipped));
            Assert.DoesNotContain("land", BlockSelection.For(goal, names));
        }

        [Fact]
        public void AGoalThatAlsoLandsOnAnAirlessBodyKeepsLandAndGetsReentry()
        {
            var picked = BlockSelection.For("Land on the Mun, then land on Duna", Shipped);
            Assert.Contains("land", picked);
            Assert.Contains("reentry", picked);
        }

        [Fact]
        public void ALandingOnAMoonOfAnAirBodyKeepsLand()
        {
            Assert.Contains("land", BlockSelection.For("Land on Duna's moon Ike", Shipped));
        }

        [Fact]
        public void AnAirlessLandingKeepsLandAndNotReentry()
        {
            var picked = BlockSelection.For("land on the Mun", Shipped);
            Assert.Contains("land", picked);
            Assert.DoesNotContain("reentry", picked);
        }

        [Fact]
        public void AReturnTripGetsReentryAndReturn()
        {
            var picked = BlockSelection.For("Go to Minmus and come home", Shipped);
            Assert.Contains("return", picked);
            Assert.Contains("reentry", picked);
        }

        [Fact]
        public void APolarOrPlaneMatchingGoalGetsChangeOrbitAndALandingDoesNot()
        {
            // #444: change_orbit's full docs come with the orbit changes it flies, not with every goal.
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            Assert.Contains("change_orbit", BlockSelection.For("Put a probe in a polar Mun orbit", names));
            Assert.Contains("change_orbit", BlockSelection.For("Orbit Kerbin in the orbital plane of Minmus", names));
            Assert.Contains("change_orbit", BlockSelection.For("Orbit Minmus", names));
            Assert.Contains("change_orbit", BlockSelection.For("Make the orbit polar", names));
            Assert.DoesNotContain("change_orbit", BlockSelection.For("Land on the Mun", names));
            Assert.DoesNotContain("change_orbit", BlockSelection.For("Orbit the planet Duna and land", names));
        }

        [Fact]
        public void AGoalThatPointsAtNothingGetsEveryBlockButChangeOrbit()
        {
            // Its summary stays in the base layer; its full docs come only with a goal that names an orbit change.
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            var picked = BlockSelection.For("Prove the kOS link from the launch pad.", names);
            Assert.Equal(names.Where(n => n != "change_orbit" && n != "rendezvous" && n != "dock"), picked);
        }

        [Fact]
        public void RendezvousComesOnlyWithAGoalThatNamesAnotherVessel()
        {
            // #455: its full docs come with a rendezvous or a docking, not with every goal; its summary stays in the base layer.
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            Assert.Contains("rendezvous", BlockSelection.For("Rendezvous with Kerbin Station One", names));
            Assert.Contains("rendezvous", BlockSelection.For("Launch and dock with the station", names));
            Assert.Contains("rendezvous", BlockSelection.For("Meet the tug in low Kerbin orbit", names));
            Assert.DoesNotContain("rendezvous", BlockSelection.For("Land on the Mun", names));
            Assert.DoesNotContain("rendezvous", BlockSelection.For("Put a satellite in a stationary orbit", names));
        }

        [Fact]
        public void DockComesOnlyWithAGoalThatDocks()
        {
            // #456: a goal that docks gets dock and the rendezvous before it; its summary stays in the base layer for the rest.
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            var picked = BlockSelection.For("Launch, rendezvous and dock with Kerbin Station", names);
            Assert.Contains("dock", picked);
            Assert.Contains("rendezvous", picked);
            Assert.Contains("dock", BlockSelection.For("Docking with the tug", names));
            Assert.DoesNotContain("dock", BlockSelection.For("Rendezvous with Kerbin Station One", names));
            Assert.DoesNotContain("dock", BlockSelection.For("Land on the Mun", names));
            Assert.DoesNotContain("dock", BlockSelection.For("", names));
        }

        [Fact]
        public void AGoalThatPointsAtNothingGetsEveryBlock()
        {
            Assert.Equal(Shipped.Length, BlockSelection.For("Do something cool", Shipped).Count);
            Assert.Equal(Shipped.Length, BlockSelection.For("", Shipped).Count);
        }

        [Fact]
        public void WordsInsideOtherWordsDoNotCount()
        {
            var picked = BlockSelection.For("Orbit the Mun and update the community", Shipped);
            Assert.DoesNotContain("science", picked);
            Assert.DoesNotContain("land", picked);
        }

        [Fact]
        public void AThinMatchGetsEveryBlock()
        {
            Assert.Equal(Shipped.Length, BlockSelection.For("Collect a goo sample", Shipped).Count);
        }

        [Fact]
        public void TheShippedBlocksAreSelectedByName()
        {
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            var narrow = BlockSelection.For("Land on the Mun", names);
            Assert.Contains("transfer_to", narrow);
            Assert.Contains("land", narrow);
            Assert.DoesNotContain("reentry", narrow);
            // change_orbit comes only with a goal that names an orbit change (#444), rendezvous with another vessel (#455), dock with docking (#456).
            Assert.Equal(names.Count - 3, BlockSelection.For("", names).Count);
        }

        [Fact]
        public void ABlockTheTableDoesNotKnowIsAlwaysIncluded()
        {
            Assert.Contains("future_block", BlockSelection.For("Do the science", Shipped));
        }

        [Fact]
        public void TheLayerHasFullDocsOnlyForThePickedBlocksAndTheBaseLayerNamesAll()
        {
            var layer = MissionPrompts.MissionLayer("Fly the science experiments in orbit");
            var launch = CoreLibrary.Blocks.First(b => b.Name == "launch").Docs;
            var reentry = CoreLibrary.Blocks.First(b => b.Name == "reentry").Docs;
            Assert.Contains(string.Join("\n", launch.ToArray()), layer);
            Assert.DoesNotContain(string.Join("\n", reentry.ToArray()), layer);
            Assert.Contains(reentry[0], MissionPrompts.BaseLayer);
        }

        [Fact]
        public void TwoMissionsWithTheSameGoalBodiesAndTurnShapeShareTheirPerMissionLayerAndDifferOnlyInTheTimingLayersId()
        {
            const string goal = "Fly the science experiments in orbit";
            var first = MissionPrompts.MissionLayer(goal, "\n\nBodies", 0.35);
            Assert.Equal(first, MissionPrompts.MissionLayer(goal, "\n\nBodies", 0.35));
            Assert.DoesNotContain("m1", first);
            var settings = MissionSettingsValues.Defaults(new PlayerConfig());
            var timing1 = MissionPrompts.TimingLayer("m1", settings);
            var timing2 = MissionPrompts.TimingLayer("m2", settings);
            Assert.StartsWith("Mission m1.\n\n", timing1);
            Assert.Equal(timing1.Substring("Mission m1.".Length), timing2.Substring("Mission m2.".Length));
        }
    }
}
