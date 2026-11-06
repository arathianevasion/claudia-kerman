// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>#713: the curated table merged with the game's surface objects.</summary>
    public sealed class PointsOfInterestTests
    {
        private static SurfaceObject Obj(string body, string name, double lat, double lon, bool facility = false)
        {
            return new SurfaceObject { Body = body, Name = name, Latitude = lat, Longitude = lon, Altitude = 100, Facility = facility };
        }

        [Fact]
        public void TheGamesObjectsGetTheTablesNamesAndTheGamesPositionsAndAnUnknownOneItsInternalName()
        {
            var game = new List<SurfaceObject>
            {
                Obj("Mun", "RockArch00", -12.47, -140.95),
                Obj("Mun", "RockArch02", 5.5, 60.25),
                Obj("Mun", "Boulder42", 1, 2),
            };
            var pois = PointsOfInterest.Merge("Mun", game);
            Assert.Equal(3, pois.Count);
            var arch = pois.Single(p => p.Internal == "RockArch00");
            Assert.Equal("Mun Rock Arch", arch.Name);
            Assert.Equal(-12.47, arch.Latitude);
            Assert.Equal(-140.95, arch.Longitude);
            Assert.NotEqual("", arch.Note);
            var second = pois.Single(p => p.Internal == "RockArch02");
            Assert.Equal("Mun Rock Arch 2", second.Name);
            Assert.Equal(5.5, second.Latitude);
            var unknown = pois.Single(p => p.Internal == "Boulder42");
            Assert.Equal("Boulder42", unknown.Name);
            Assert.Equal("", unknown.Note);
            Assert.True(unknown.Landable);
        }

        [Fact]
        public void AnObjectWhoseNameTheTableDoesntKnowMatchesByPosition()
        {
            var poi = Assert.Single(PointsOfInterest.Merge("Minmus", new[] { Obj("Minmus", "Monolith00", 23.78, 60.05) }));
            Assert.Equal("Monolith TMA-8", poi.Name);
            Assert.Equal(23.78, poi.Latitude);
        }

        [Fact]
        public void TheKscAndLaunchSitesAreLeftOut()
        {
            var game = new List<SurfaceObject>
            {
                Obj("Kerbin", "KSC", -0.097, -74.557),
                Obj("Kerbin", "KSC2", 20, -146),
                Obj("Kerbin", "Desert_Launch_Site", -6.56, -143.95),
                Obj("Kerbin", "Woomerang_Launch_Site", 45.29, 136.11),
                Obj("Kerbin", "Something", 10, 10, facility: true),
                Obj("Kerbin", "Monolith00", 0.102, -74.568),
            };
            var poi = Assert.Single(PointsOfInterest.Merge("Kerbin", game));
            Assert.Equal("KSC Monolith", poi.Name);
        }

        [Fact]
        public void TMA7IsListedAsNotLandableAndLast()
        {
            var game = new List<SurfaceObject> { Obj("Mun", "Monolith02", -82.2, 102.9), Obj("Mun", "Monolith00", -9.83, 25.92) };
            var pois = PointsOfInterest.Merge("Mun", game);
            Assert.Equal(new[] { "Monolith TMA-5", "Monolith TMA-7" }, pois.Select(p => p.Name).ToArray());
            Assert.False(pois[1].Landable);
            Assert.True(pois[0].Landable);
        }

        [Fact]
        public void ATableEntryTheGameDoesntHaveIsLeftOutButAnUnreadBodyFallsBackToTheTable()
        {
            Assert.Empty(PointsOfInterest.Merge("Duna", new[] { Obj("Mun", "RockArch00", -12.47, -140.95) }.Concat(new[] { Obj("Duna", "KSC", 0, 0) })));
            var table = PointsOfInterest.Merge("Duna", new SurfaceObject[0]);
            Assert.Equal(new[] { "Duna Face", "Duna MSL Rover" }, table.Select(p => p.Name).ToArray());
            Assert.Equal(17.060, table[0].Latitude);
        }

        [Fact]
        public void AnUnreadablePositionFallsBackToTheTablesAndOneWithNoneIsDropped()
        {
            var game = new List<SurfaceObject> { Obj("Mun", "RockArch00", double.NaN, double.NaN), Obj("Mun", "RockArch02", double.NaN, double.NaN) };
            var poi = Assert.Single(PointsOfInterest.Merge("Mun", game));
            Assert.Equal(-12.465, poi.Latitude);
        }

        [Fact]
        public void FindTakesTheFriendlyOrInternalNameOrAUniqueAlias()
        {
            var pois = PointsOfInterest.Merge("Minmus", new SurfaceObject[0]);
            Assert.Equal("Monolith TMA-8", PointsOfInterest.Find(pois, "tma 8").Name);
            Assert.Equal("Monolith TMA-8", PointsOfInterest.Find(pois, "monolith").Name);
            var mun = PointsOfInterest.Merge("Mun", new SurfaceObject[0]);
            Assert.Null(PointsOfInterest.Find(mun, "monolith"));
            Assert.Equal("Mun Rock Arch", PointsOfInterest.Find(mun, "RockArch00").Name);
            Assert.Null(PointsOfInterest.Find(mun, "Duna Face"));
        }
    }
}
