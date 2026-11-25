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

        /// <summary>
        /// #1363: a made-up slope field, a plain rising 20 degrees to the east everywhere except flat round discs
        /// (centre lat, lng, radius m) at the plain's height there.
        /// </summary>
        private static Terrain.HeightAt SteepWithFlats(params double[][] flats)
        {
            Terrain.HeightAt steep = RisingEast(20);
            return (lat, lng) =>
            {
                foreach (var flat in flats)
                    if (Terrain.Distance(flat[0], flat[1], lat, lng, Radius) < flat[2]) return steep(flat[0], flat[1]);
                return steep(lat, lng);
            };
        }

        /// <summary>Degrees of longitude for <paramref name="metres"/> east at <paramref name="lat"/>.</summary>
        private static double East(double lat, double metres)
        {
            return metres / (MetresPerDegree * Math.Cos(lat * Math.PI / 180));
        }

        [Fact]
        public void AFlatStartStaysWhereItIs()
        {
            var place = BlockTestLandedStart.Place(RisingEast(3), Radius, 2.46, 77.2, null, null);
            Assert.Null(place.Failure);
            Assert.Equal(2.46, place.Start.Lat, 9);
            Assert.Equal(77.2, place.Start.Lng, 9);
            Assert.Equal(0, place.Start.Distance, 6);
        }

        [Fact]
        public void ASteepStartMovesToTheFlatGroundAFewKilometresAway()
        {
            // Steep at the site; a flat disc of 300 m radius centred 3 km east.
            var flatLng = 77.2 + East(2.46, 3000);
            var place = BlockTestLandedStart.Place(SteepWithFlats(new[] { 2.46, flatLng, 300.0 }), Radius, 2.46, 77.2, null, null);
            Assert.Null(place.Failure);
            Assert.True(place.Start.Slope < 5, "slope " + place.Start.Slope);
            // The nearest flat ground is the disc's near edge, about 2.7 km away.
            Assert.InRange(place.Start.Distance, 2600, 2800);
            Assert.InRange(Terrain.Distance(2.46, flatLng, place.Start.Lat, place.Start.Lng, Radius), 0, 300);
        }

        [Fact]
        public void TheMoveIsLoggedWithWhereItWentHowFarAndItsSlope()
        {
            var flatLng = 77.2 + East(2.46, 3000);
            var place = BlockTestLandedStart.Place(SteepWithFlats(new[] { 2.46, flatLng, 300.0 }), Radius, 2.46, 77.2, null, null);
            var line = Assert.Single(place.Notes);
            Assert.StartsWith("moved the landed start from 2.46, 77.2 to ", line);
            Assert.EndsWith(", 2.8 km away, where the ground slopes 0.0 degrees", line);
        }

        [Fact]
        public void AHopTargetMovesToFlatGroundKeepingTheHopWithinTwoKilometres()
        {
            // The start is flat; the target, 15 km east, is steep. The nearest flat ground to it, 2.8 km further east, would
            // make the hop 17.8 km, so it is passed over for the flat ground 3.7 km south, a 15.5 km hop.
            var targetLng = 77.2 + East(2.46, 15000);
            var startFlat = new[] { 2.46, 77.2, 300.0 };
            var eastFlat = new[] { 2.46, targetLng + East(2.46, 3000), 200.0 };
            var southFlat = new[] { 2.46 - 4000 / MetresPerDegree, targetLng, 300.0 };
            var place = BlockTestLandedStart.Place(SteepWithFlats(startFlat, eastFlat, southFlat), Radius, 2.46, 77.2, 2.46, targetLng);
            Assert.Null(place.Failure);
            Assert.True(place.Target.Slope < 5, "slope " + place.Target.Slope);
            var hop = Terrain.Distance(place.Start.Lat, place.Start.Lng, place.Target.Lat, place.Target.Lng, Radius);
            Assert.InRange(hop, 13000, 17000);
            Assert.InRange(Terrain.Distance(southFlat[0], southFlat[1], place.Target.Lat, place.Target.Lng, Radius), 0, 300);
            Assert.Equal(3, place.Notes.Count);
            Assert.StartsWith("moved the hop target from 2.46, ", place.Notes[1]);
            Assert.Contains(" km asked for", place.Notes[2]);
        }

        [Fact]
        public void WithNoFlatGroundWithinTenKilometresItFailsNamingTheFlattestSlope()
        {
            var place = BlockTestLandedStart.Place(RisingEast(8), Radius, 2.46, 77.2, null, null);
            Assert.Null(place.Start);
            Assert.Equal("no ground under 5 degrees within 10.0 km of the landed start at 2.46, 77.2: the flattest found slopes 8.0 degrees", place.Failure);
        }

        [Fact]
        public void FlatGroundJustOverTenKilometresAwayIsOutOfReach()
        {
            var place = BlockTestLandedStart.Place(SteepWithFlats(new[] { 2.46, 77.2 + East(2.46, 10400), 300.0 }), Radius, 2.46, 77.2, null, null);
            Assert.NotNull(place.Failure);
        }

        [Fact]
        public void ASteepHopTargetWithNoFlatGroundFailsTheTestToo()
        {
            var targetLng = 77.2 + East(2.46, 15000);
            var place = BlockTestLandedStart.Place(SteepWithFlats(new[] { 2.46, 77.2, 300.0 }), Radius, 2.46, 77.2, 2.46, targetLng);
            Assert.StartsWith("no ground under 5 degrees within 10.0 km of the hop target at 2.46, ", place.Failure);
            Assert.EndsWith(": the flattest found slopes 20.0 degrees", place.Failure);
        }

        [Fact]
        public void FlatGroundThatWouldStretchTheHopPastTwoKilometresDoesNotCount()
        {
            // The only flat ground near the target is 3 km beyond it: an 18 km hop for the 15 asked.
            var targetLng = 77.2 + East(2.46, 15000);
            var place = BlockTestLandedStart.Place(SteepWithFlats(new[] { 2.46, 77.2, 300.0 }, new[] { 2.46, targetLng + East(2.46, 3000), 200.0 }),
                Radius, 2.46, 77.2, 2.46, targetLng);
            Assert.StartsWith("no ground under 5 degrees within 10.0 km of the hop target at 2.46, ", place.Failure);
            Assert.EndsWith(" that keeps the hop within 2.0 km of the 15.0 km asked for: the flattest found slopes 20.0 degrees", place.Failure);
        }

        [Fact]
        public void TheTestScriptReadsTheMovedHopTargetAsAGlobalDeclaredAfterItsDirective()
        {
            var target = new Spot(1.25, 80.5, 2, 1000, 1500, true);
            var source = BlockTestLandedStart.WithHopTarget("// a comment\n@lazyglobal off.\nprint test_hop_target.\n", target);
            Assert.Equal("// a comment\n@lazyglobal off.\nglobal test_hop_target is latlng(1.25, 80.5).\nprint test_hop_target.\n", source);
            Assert.Equal("global test_hop_target is latlng(1.25, 80.5).\nprint 1.\n", BlockTestLandedStart.WithHopTarget("print 1.\n", target));
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
