// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// #713 (ADR 0014): land's keep-clear decision in Core, moved from land_clear_of and land_clear_shift (their cases, #427) and
    /// land_clear_site, with a Point of Interest kept poi_min_m clear.
    /// </summary>
    public sealed class KeepClearTests
    {
        private const double MunR = 200000;

        [Theory]
        // Already clear: stays put, at 50 m too.
        [InlineData(80, 300, 0)]
        [InlineData(50, 300, 0)]
        // Moves the rest of the 50 m.
        [InlineData(49.9, 300, 0.1)]
        [InlineData(20, 300, 30)]
        [InlineData(0, 300, 50)]
        // Never more than max_divert.
        [InlineData(0, 10, 10)]
        public void TheTouchdownPointMovesTo50MClearWithinMaxDivert(double dist, double maxDivert, double expected)
        {
            Assert.Equal(expected, KeepClear.Shift(dist, maxDivert), 9);
        }

        private static double M(IDictionary<string, object> at, double lat, double lng)
        {
            return LandingSiteSearch.ArcDegrees((double)at["lat"], (double)at["lng"], lat, lng) * Math.PI / 180 * MunR;
        }

        private static IDictionary<string, object> Ask(double lat, double lng, double keepLat, double keepLng, double clearM, double maxDivert)
        {
            return new Dictionary<string, object>
            {
                { "lat", lat }, { "lng", lng }, { "keep_lat", keepLat }, { "keep_lng", keepLng }, { "ahead_lat", lat }, { "ahead_lng", lng + 0.01 },
                { "clear_m", clearM }, { "max_divert", maxDivert }, { "radius", MunR },
            };
        }

        [Fact]
        public void APointOfInterestIsKeptPoiMinMClearStraightAwayFromIt()
        {
            // A divert left the site 60 m north of the point: it goes back to 150 m, still north.
            var north = 60 / (Math.PI / 180 * MunR);
            var moved = KeepClear.Call(Ask(north, 30, 0, 30, LandingSiteSearch.PoiMinM, 300));
            Assert.Equal(60, (double)moved["dist"], 3);
            Assert.Equal(90, (double)moved["moved"], 3);
            Assert.Equal(150, M(moved, 0, 30), 1);
            Assert.True((double)moved["lat"] > north);
            Assert.Equal(30, (double)moved["lng"], 6);
        }

        [Fact]
        public void EveryDivertWithinMaxDivertEndsAtLeastClearMFromThePoint()
        {
            // From any spot within 150 m of the point, a max_divert of 150 always brings it back to 150 m.
            for (var bearing = 0; bearing < 360; bearing += 30)
                for (var d = 0.0; d < 150; d += 25)
                {
                    double lat, lng;
                    LandingSiteSearch.Destination(0, 30, bearing, d / (Math.PI / 180 * MunR), out lat, out lng);
                    var moved = KeepClear.Call(Ask(lat, lng, 0, 30, 150, 150));
                    Assert.True(M(moved, 0, 30) >= 150 - 0.01, bearing + " deg, " + d + " m: " + M(moved, 0, 30));
                }
        }

        [Fact]
        public void OnTheSameSpotItMovesAlongTheTrackAndAClearSiteStays()
        {
            var moved = KeepClear.Call(Ask(0, 30, 0, 30, 50, 300));
            Assert.Equal(50, M(moved, 0, 30), 1);
            Assert.True((double)moved["lng"] > 30);
            var stays = KeepClear.Call(Ask(1, 30, 0, 30, 150, 300));
            Assert.Equal(0.0, (double)stays["moved"]);
            Assert.Equal(1.0, (double)stays["lat"]);
        }
    }
}
