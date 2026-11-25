// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#1358: a landed block test starts only on flat ground, and only once the craft has settled on it.</summary>
    public class BlockTestLandedStartTests
    {
        private const double Radius = 200000;
        private const double MetresPerDegree = Radius * Math.PI / 180;

        private static Terrain.HeightAt RisingEast(double degrees)
        {
            return (lat, lng) => 1000 + Math.Tan(degrees * Math.PI / 180) * lng * MetresPerDegree * Math.Cos(lat * Math.PI / 180);
        }

        [Fact]
        public void FlatGroundIsNotRefused()
        {
            Assert.Null(BlockTestLandedStart.RefuseSite(RisingEast(3), Radius, 2.46, 77.2));
        }

        [Fact]
        public void GroundSteeperThanFiveDegreesIsRefusedWithItsSlope()
        {
            var why = BlockTestLandedStart.RefuseSite(RisingEast(8), Radius, 2.46, 77.2);
            Assert.NotNull(why);
            Assert.Contains("slopes 8.0", why);
            Assert.Contains("more than 5", why);
        }

        [Fact]
        public void ItStartsAfterThreeSecondsLandedAndStill()
        {
            var settle = new BlockTestLandedStart.Settle();
            Assert.Equal(BlockTestLandedStart.SettleState.Waiting, settle.Step(0.0, true, 0.05));
            Assert.Equal(BlockTestLandedStart.SettleState.Waiting, settle.Step(2.9, true, 0.05));
            Assert.Equal(BlockTestLandedStart.SettleState.Settled, settle.Step(3.0, true, 0.05));
        }

        [Fact]
        public void MovingOrNotLandedStartsTheThreeSecondsAgain()
        {
            var settle = new BlockTestLandedStart.Settle();
            settle.Step(0, true, 0.05);
            settle.Step(2, true, 0.3);   // rocking on its legs
            Assert.Equal(BlockTestLandedStart.SettleState.Waiting, settle.Step(4.5, true, 0.05));
            settle.Step(5, false, 0.05); // bounced: not LANDED
            Assert.Equal(BlockTestLandedStart.SettleState.Waiting, settle.Step(7.9, true, 0.05));
            Assert.Equal(BlockTestLandedStart.SettleState.Settled, settle.Step(10.9, true, 0.05));
        }

        [Fact]
        public void ItGivesUpAfterThirtySeconds()
        {
            var settle = new BlockTestLandedStart.Settle();
            Assert.Equal(BlockTestLandedStart.SettleState.Waiting, settle.Step(0, false, 2));
            Assert.Equal(BlockTestLandedStart.SettleState.Waiting, settle.Step(29.9, false, 2));
            Assert.Equal(BlockTestLandedStart.SettleState.NeverSettled, settle.Step(30, false, 2));
        }

        [Fact]
        public void NeverSettledSaysTheLeanAndTheSlope()
        {
            var why = BlockTestLandedStart.NeverSettled(84.26, 6.04);
            Assert.Equal("never settled: 30 s without 3 s LANDED under 0.1 m/s; the craft leans 84.3 degrees on ground sloping 6.0 degrees", why);
        }

        [Fact]
        public void TheSpawnSetsTheCraftDownAFifthOfAMetreUp()
        {
            Assert.Equal(0.2, BlockTestLandedStart.SpawnClearance);
        }
    }
}
