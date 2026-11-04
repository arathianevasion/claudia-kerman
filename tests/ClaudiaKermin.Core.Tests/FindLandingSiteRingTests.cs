// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// #713: <c>find_landing_site</c>'s <c>poi</c>, the ring search beside a Point of Interest (<see cref="LandingSiteSearch.Ring"/>),
    /// on a sphere the size of the Mun under a 50 km equatorial orbit, with the point on the equator ahead.
    /// </summary>
    public sealed class FindLandingSiteRingTests
    {
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double PoiLat = 0.5;
        private const double PoiLon = 30;

        private sealed class FakeBody : PlanningBodyBase
        {
            public Func<double, double, double> Terrain = (lat, lon) => 100;
            public readonly List<SurfaceObject> Objects = new List<SurfaceObject>();

            public override double TerrainAltitude(string body, double latitude, double longitude) { return Terrain(latitude, longitude); }
            public override string Biome(string body, double latitude, double longitude) { return "Midlands"; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 45; }
            public override IList<SurfaceObject> SurfaceObjects(string body) { return Objects; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private readonly PlanningTools _tools = PlanningTools.Default();

        private static GameSnapshot Snapshot()
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = MunR + 50000, Eccentricity = 0, Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = 0, Epoch = 1000, Body = "Mun", Periapsis = 50000, Apoapsis = 50000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256,
            });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000 });
            return snapshot;
        }

        private ToolOutcome Call(string input, FakeBody body)
        {
            return _tools.Run("find_landing_site", input, Snapshot(), body);
        }

        private JsonElement Run(string input, FakeBody body)
        {
            var outcome = Call(input, body);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private static string AtPoi(string extra = "")
        {
            return "{\"body\":\"Mun\",\"daylight\":false,\"poi\":{\"latitude\":" + PoiLat + ",\"longitude\":" + PoiLon + "}" + extra + "}";
        }

        private static double FromPoiM(JsonElement site)
        {
            return LandingSiteSearch.ArcDegrees(site.GetProperty("latitude").GetDouble(), site.GetProperty("longitude").GetDouble(), PoiLat, PoiLon) * Math.PI / 180 * MunR;
        }

        private static double FromPoiM(double lat, double lon)
        {
            return LandingSiteSearch.ArcDegrees(lat, lon, PoiLat, PoiLon) * Math.PI / 180 * MunR;
        }

        [Fact]
        public void EverySiteIsInTheRingAndNeverThePointItself()
        {
            var result = Run(AtPoi(), new FakeBody());
            var sites = result.GetProperty("sites").EnumerateArray().ToList();
            Assert.NotEmpty(sites);
            foreach (var site in sites)
            {
                // The rounding of the coordinates moves a site a few metres at most.
                Assert.InRange(FromPoiM(site), LandingSiteSearch.PoiMinM - 10, LandingSiteSearch.PoiMaxM + 10);
                Assert.InRange(site.GetProperty("from_poi_m").GetDouble(), LandingSiteSearch.PoiMinM, LandingSiteSearch.PoiMaxM);
                Assert.False(site.GetProperty("latitude").GetDouble() == PoiLat && site.GetProperty("longitude").GetDouble() == PoiLon);
                Assert.True(site.TryGetProperty("landing_ut", out _));
            }
        }

        [Fact]
        public void OnFlatGroundTheNearestTheInnerEdgeComeFirstAndSitesStandApart()
        {
            var sites = Run(AtPoi(), new FakeBody()).GetProperty("sites").EnumerateArray().ToList();
            var distances = sites.Select(s => s.GetProperty("from_poi_m").GetDouble()).ToList();
            Assert.Equal(distances.OrderBy(d => d), distances);
            // Across the track of the equatorial orbit, by at least 0.7 x poi_min_m.
            Assert.All(sites, s => Assert.True(Math.Abs(s.GetProperty("latitude").GetDouble() - PoiLat) * Math.PI / 180 * MunR >= 0.7 * 150 - 5));
            for (var i = 0; i < sites.Count; i++)
                for (var j = i + 1; j < sites.Count; j++)
                    Assert.True(LandingSiteSearch.ArcDegrees(sites[i].GetProperty("latitude").GetDouble(), sites[i].GetProperty("longitude").GetDouble(),
                        sites[j].GetProperty("latitude").GetDouble(), sites[j].GetProperty("longitude").GetDouble()) * Math.PI / 180 * MunR >= 145);
        }

        [Fact]
        public void TheFlattestComeFirst()
        {
            // A hill on the point: steep near it, flat far out.
            var hill = new FakeBody { Terrain = (lat, lon) => 2000 * Math.Exp(-Math.Pow(FromPoiM(lat, lon) / 500, 2)) };
            var sites = Run(AtPoi(",\"max_slope_deg\":30"), hill).GetProperty("sites").EnumerateArray().ToList();
            Assert.NotEmpty(sites);
            var slopes = sites.Select(s => s.GetProperty("slope_deg").GetDouble()).ToList();
            Assert.Equal(slopes.OrderBy(x => Math.Floor(x)), slopes);
            Assert.True(sites[0].GetProperty("from_poi_m").GetDouble() > 1000, sites[0].ToString());
        }

        [Fact]
        public void WithNoFlatSpotInTheRingItSaysSoAndNeverOffersThePoint()
        {
            var steep = new FakeBody { Terrain = (lat, lon) => 10000 * lat };
            var result = Run(AtPoi(), steep);
            Assert.Empty(result.GetProperty("sites").EnumerateArray());
            var note = result.GetProperty("note").GetString();
            Assert.Contains("no site in the ring 150 to 1500 m from", note);
            Assert.Contains("never a site", note);
        }

        [Fact]
        public void ANameIsLookedUpAndTheBuriedMonolithIsRefused()
        {
            var game = new FakeBody();
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "RockArch00", Latitude = PoiLat, Longitude = PoiLon });
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "Monolith02", Latitude = -82.2, Longitude = 102.9 });
            var result = Run("{\"body\":\"Mun\",\"daylight\":false,\"poi\":\"Mun Rock Arch\"}", game);
            Assert.Equal("Mun Rock Arch", result.GetProperty("poi").GetString());
            Assert.NotEmpty(result.GetProperty("sites").EnumerateArray());
            var refused = Call("{\"body\":\"Mun\",\"daylight\":false,\"poi\":\"TMA-7\"}", game);
            Assert.True(refused.IsError);
            Assert.Contains("Monolith TMA-7 is not a landable Point of Interest", refused.Json);
            var unknown = Call("{\"body\":\"Mun\",\"daylight\":false,\"poi\":\"Duna Face\"}", game);
            Assert.True(unknown.IsError);
            Assert.Contains("Mun Rock Arch, Monolith TMA-7", unknown.Json);
        }

        [Fact]
        public void LandsBlockCallGivesTheRingSiteWithThePointToKeepClearAndNeverThePoint()
        {
            var game = new FakeBody();
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "RockArch00", Latitude = PoiLat, Longitude = PoiLon });
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "Monolith02", Latitude = -82.2, Longitude = 102.9 });
            var site = BlockCalls.LandingSite(new Dictionary<string, object>
            {
                { "poi", "rockarch00" }, { "daylight", false }, { "max_slope_deg", 5.0 }, { "max_correction_dv", 30.0 },
            }, Snapshot(), game);
            Assert.Equal("", site["error"]);
            Assert.Equal("Mun Rock Arch", site["poi"]);
            Assert.Equal(PoiLat, (double)site["poi_lat"], 3);
            Assert.Equal(PoiLon, (double)site["poi_lng"], 3);
            Assert.Equal(LandingSiteSearch.PoiMinM, (double)site["clear_m"]);
            Assert.InRange(FromPoiM((double)site["lat"], (double)site["lng"]), LandingSiteSearch.PoiMinM - 10, LandingSiteSearch.PoiMaxM + 10);
            // A divert keeps the site 50 m across the track from the point: the site is at least 0.7 x 150 m across it.
            Assert.InRange((double)site["max_divert"], 0.7 * 150 - 50 - 1, 1500);
            Assert.StartsWith("beside Mun Rock Arch: ", (string)site["note"]);
            Assert.Contains(" m from it, bearing ", (string)site["note"]);

            Assert.Equal("", BlockCalls.LandingSite(new Dictionary<string, object> { { "poi", "Mun Rock Arch" }, { "check_only", true } }, Snapshot(), game)["error"]);
            Assert.Contains("not a landable Point of Interest",
                (string)BlockCalls.LandingSite(new Dictionary<string, object> { { "poi", "TMA-7" }, { "check_only", true } }, Snapshot(), game)["error"]);
            Assert.Contains("names no one Point of Interest on Mun",
                (string)BlockCalls.LandingSite(new Dictionary<string, object> { { "poi", "Duna Face" }, { "check_only", true } }, Snapshot(), game)["error"]);

            var steep = new FakeBody { Terrain = (lat, lon) => 10000 * lat };
            steep.Objects.AddRange(game.Objects);
            var none = BlockCalls.LandingSite(new Dictionary<string, object> { { "poi", "Mun Rock Arch" }, { "daylight", false } }, Snapshot(), steep);
            Assert.StartsWith("no site beside Mun Rock Arch on Mun: no site in the ring", (string)none["error"]);
            Assert.False(none.ContainsKey("lat"));
        }

        [Fact]
        public void ThePoiTakesNoBiomeOrNearAndItsRingIsChecked()
        {
            Assert.True(Call(AtPoi(",\"biome\":\"Midlands\""), new FakeBody()).IsError);
            Assert.True(Call(AtPoi(",\"near\":{\"latitude\":0,\"longitude\":0}"), new FakeBody()).IsError);
            Assert.Contains("poi_min_m", Call(AtPoi(",\"poi_min_m\":10"), new FakeBody()).Json);
            Assert.Contains("poi_max_m", Call(AtPoi(",\"poi_min_m\":500,\"poi_max_m\":400"), new FakeBody()).Json);
            var wide = Run(AtPoi(",\"poi_min_m\":400,\"poi_max_m\":900"), new FakeBody()).GetProperty("sites").EnumerateArray().ToList();
            Assert.All(wide, s => Assert.InRange(s.GetProperty("from_poi_m").GetDouble(), 400, 900));
        }
    }
}
