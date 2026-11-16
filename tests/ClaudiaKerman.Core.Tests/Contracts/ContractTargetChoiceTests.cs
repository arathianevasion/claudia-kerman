// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1033, ADR 0013's amendment: the start form's one Target Vessel choice when a ticked Contract brings a vessel. The Contract's
    /// vessel is the target whatever the goal says, unless the goal or the player's pick gives another, which wins; Clear unticks it.
    /// </summary>
    public sealed class ContractTargetChoiceTests
    {
        /// <summary>The rescue's pod: a Kerbal on EVA as the game spawned it, a type the goal-name search and the Choose list skip.</summary>
        private static readonly SnapshotVessel Pod = new SnapshotVessel { Id = 4401, Name = "Bill Kerman", Body = "Kerbin", Situation = "ORBITING", ContractGuid = "c-rescue" };

        private static readonly SnapshotVessel Station = new SnapshotVessel { Id = 5000, Name = "Kerbin Station", Body = "Kerbin", Situation = "ORBITING" };

        private static TargetFacts Facts(bool withContract = true)
        {
            return new TargetFacts
            {
                CraftName = "Rescue One", CraftBody = "Kerbin", CraftId = 1,
                Vessels = new List<SnapshotVessel> { Station },
                Contract = withContract ? Pod : null,
            };
        }

        [Fact]
        public void TheContractsVesselIsTheTargetWhateverTheGoalSaysAndTheLineEndsFromTheContract()
        {
            var choice = TargetVessel.Choose("", TargetPick.Auto, Facts());

            Assert.True(choice.Needed);
            Assert.Equal((uint)4401, choice.Target.Id);
            Assert.Equal("Target Vessel: Bill Kerman · Kerbin · orbiting · from the Contract", choice.Line);
            Assert.Equal("", choice.Displaces);
        }

        [Fact]
        public void ATargetTheGoalNamesOrThePlayerPicksWinsAndTheContractIsDisplaced()
        {
            var named = TargetVessel.Choose("dock with Kerbin Station", TargetPick.Auto, Facts());
            var picked = TargetVessel.Choose("", TargetPick.Of(Station), Facts());

            Assert.Equal((uint)5000, named.Target.Id);
            Assert.Equal("c-rescue", named.Displaces);
            Assert.Equal((uint)5000, picked.Target.Id);
            Assert.Equal("c-rescue", picked.Displaces);
        }

        [Fact]
        public void PickingTheContractsOwnVesselChangesNothing()
        {
            var choice = TargetVessel.Choose("", TargetPick.Of(Pod), Facts());

            Assert.Equal((uint)4401, choice.Target.Id);
            Assert.Equal("", choice.Displaces);
        }

        [Fact]
        public void ClearUnticksTheContractRatherThanLeavingItWithoutItsVessel()
        {
            var choice = TargetVessel.Choose("", TargetPick.Cleared, Facts());

            Assert.Null(choice.Target);
            Assert.Equal("c-rescue", choice.Unticks);
        }

        [Fact]
        public void WithoutAContractNothingChanges()
        {
            var choice = TargetVessel.Choose("", TargetPick.Auto, Facts(false));

            Assert.False(choice.Needed);
            Assert.Equal("", choice.Unticks);
        }
    }
}
