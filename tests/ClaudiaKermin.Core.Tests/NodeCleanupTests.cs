// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class NodeCleanupTests
    {
        private sealed class Node { }

        private static RunOutcome Ended(RunOutcomeKind kind, string reason = "")
        {
            return new RunOutcome(kind, null, "", reason, kind == RunOutcomeKind.Aborted);
        }

        [Theory]
        [InlineData(RunOutcomeKind.NoReport, "", true)]
        [InlineData(RunOutcomeKind.Interrupted, "the game was loaded", true)]
        [InlineData(RunOutcomeKind.Aborted, "blocked: SHUTDOWN", true)]
        [InlineData(RunOutcomeKind.Aborted, "the player took Manual Control", false)]
        [InlineData(RunOutcomeKind.Finished, "", false)]
        [InlineData(RunOutcomeKind.StoppedByPlayer, "", false)]
        public void NodesTheRunAddedComeOffOnlyForTheListedEnds(RunOutcomeKind kind, string reason, bool removed)
        {
            var added = new Node();

            var gone = NodeCleanup.ToRemove(new Node[0], new[] { added }, Ended(kind, reason));

            Assert.Equal(removed ? new[] { added } : new Node[0], gone);
        }

        [Theory]
        [InlineData(RunOutcomeKind.Finished)]
        [InlineData(RunOutcomeKind.NoReport)]
        [InlineData(RunOutcomeKind.Aborted)]
        [InlineData(RunOutcomeKind.StoppedByPlayer)]
        [InlineData(RunOutcomeKind.Interrupted)]
        public void NodesFromBeforeTheRunNeverComeOff(RunOutcomeKind kind)
        {
            var players = new Node();
            var added = new Node();

            var gone = NodeCleanup.ToRemove(new[] { players }, new[] { players, added }, Ended(kind));

            Assert.DoesNotContain(players, gone);
        }

        [Fact]
        public void ANodeAlreadyRemovedIsNotListed()
        {
            var added = new Node();
            Assert.Empty(NodeCleanup.ToRemove(new[] { added }, new Node[0], Ended(RunOutcomeKind.NoReport)));
        }

        [Fact]
        public void TheSnapshotTextListsEachPlannedNodeAndLeavesTheSectionOutWithNone()
        {
            var snapshot = new GameSnapshot();
            Assert.DoesNotContain("\"nodes\"", snapshot.ToJson(false));

            snapshot.Nodes.Add(new SnapshotNode { Ut = 1234.5, DeltaV = 900, Prograde = 880, Normal = 20, Radial = -5, AddedBy = "Mun transfer" });
            snapshot.Nodes.Add(new SnapshotNode { Ut = 5000, DeltaV = 10 });
            var text = snapshot.ToJson(false);
            var parsed = GameSnapshot.Parse(text);

            Assert.Contains("\"nodes\"", text);
            Assert.Equal(2, parsed.Nodes.Count);
            Assert.Equal(1234.5, parsed.Nodes[0].Ut);
            Assert.Equal(900, parsed.Nodes[0].DeltaV);
            Assert.Equal(880, parsed.Nodes[0].Prograde);
            Assert.Equal(20, parsed.Nodes[0].Normal);
            Assert.Equal(-5, parsed.Nodes[0].Radial);
            Assert.Equal("Mun transfer", parsed.Nodes[0].AddedBy);
            Assert.Equal("", parsed.Nodes[1].AddedBy);
        }
    }
}
