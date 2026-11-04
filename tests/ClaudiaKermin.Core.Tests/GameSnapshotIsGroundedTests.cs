// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The one grounded rule (#728): PRELAUNCH, LANDED or SPLASHED, for a snapshot and for a situation string.</summary>
    public sealed class GameSnapshotIsGroundedTests
    {
        [Theory]
        [InlineData("PRELAUNCH", true)]
        [InlineData("LANDED", true)]
        [InlineData("SPLASHED", true)]
        [InlineData("FLYING", false)]
        [InlineData("SUB_ORBITAL", false)]
        [InlineData("ORBITING", false)]
        [InlineData("ESCAPING", false)]
        [InlineData("DOCKED", false)]
        [InlineData("", false)]
        public void IsGroundedForEachSituation(string situation, bool grounded)
        {
            var snapshot = new GameSnapshot { Situation = situation };
            Assert.Equal(grounded, snapshot.IsGrounded);
            Assert.Equal(grounded, GameSnapshot.IsGroundedSituation(situation));
        }

        [Fact]
        public void UnreadSituationIsNotGrounded()
        {
            Assert.False(new GameSnapshot().IsGrounded);
            Assert.False(GameSnapshot.IsGroundedSituation(null));
        }
    }
}
