// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #828: <c>find_landing_site</c> on bodies with air, through the Planning Tools entry point: a sun fixed in space over a turning
    /// body, so a site's sun depends on the arrival, and a craft in an 80 km orbit whose ground track is worked out by hand.
    /// </summary>
    public sealed class FindLandingSiteAirTests
    {
        private const double Now = 1000;
        private const double DunaTurn = 65517.859;

        private class World : PlanningBodyBase
        {
            public double Turn = DunaTurn;
            /// <summary>The longitude under the sun at <see cref="Now"/>.</summary>
            public double SunLongitude;
            public Func<double, double, double> Terrain = (lat, lon) => 100;
            public Func<double, double, string> BiomeAt = (lat, lon) => "Lowlands";
            public int SunReads;

            public override double TerrainAltitude(string body, double latitude, double longitude) { return Terrain(latitude, longitude); }
            public override string Biome(string body, double latitude, double longitude) { return BiomeAt(latitude, longitude); }
            public override IList<string> BiomeNames(string body) { return new string[0]; }

            public override double SunElevation(string body, double latitude, double longitude, double ut)
            {
                SunReads++;
                var hour = (longitude + 360 * (ut - Now) / Turn - SunLongitude) * Math.PI / 180;
                return Math.Asin(Math.Cos(latitude * Math.PI / 180) * Math.Cos(hour)) * 180 / Math.PI;
            }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private readonly PlanningTools _tools = PlanningTools.Default();

        private static SnapshotBody Duna()
        {
            return new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0666, RotationPeriod = DunaTurn };
        }

        private static SnapshotBody Kerbin()
        {
            return new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, RotationPeriod = 21549.425, Ocean = true };
        }

        private static SnapshotBody Laythe()
        {
            return new SnapshotBody { Name = "Laythe", Parent = "Jool", Gm = 1.962e12, Radius = 500000, AtmosphereHeight = 50000, SeaLevelAtm = 0.8, RotationPeriod = 52980.879, Ocean = true };
        }

        private static GameSnapshot Orbiting(SnapshotBody body, double inclination = 0, double altitude = 80000)
        {
            var snapshot = new GameSnapshot { Ut = Now, Situation = "ORBITING", Body = body.Name };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = body.Radius + altitude, Eccentricity = 0, Inclination = inclination, Lan = 0, ArgumentOfPeriapsis = 0, MeanAnomalyAtEpoch = 0, Epoch = Now,
                Body = body.Name, Periapsis = altitude, Apoapsis = altitude,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(body);
            return snapshot;
        }

        private JsonElement Run(GameSnapshot snapshot, string input, World world)
        {
            var outcome = _tools.Run("find_landing_site", input, snapshot, world);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private static List<JsonElement> Sites(JsonElement result)
        {
            return result.GetProperty("sites").EnumerateArray().ToList();
        }

        private static double Num(JsonElement site, string name)
        {
            return site.GetProperty(name).GetDouble();
        }

        [Fact]
        public void Duna_returns_a_daylight_site_near_the_equator_at_a_high_sun()
        {
            // The equatorial track crosses every longitude in one orbit: the sun is over 0 E, and only a site close under it is high.
            var result = Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"near\":{\"latitude\":0,\"longitude\":0},\"daylight\":true}", new World());
            var sites = Sites(result);
            Assert.NotEmpty(sites);
            Assert.All(sites, s => Assert.InRange(Num(s, "latitude"), -1, 1));
            Assert.True(Num(sites[0], "sun_elevation_deg") > 60, sites[0].ToString());
        }

        [Fact]
        public void Among_lit_sites_it_prefers_the_highest_sun_not_merely_one_over_20_degrees()
        {
            // With no near point the whole lit half of the equator matches, and the soonest pass isn't at noon: the sun stands over 120 E.
            var sites = Sites(Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"daylight\":true}", new World { SunLongitude = 120 }));
            Assert.True(Num(sites[0], "sun_elevation_deg") > 80, sites[0].ToString());
            Assert.True(Num(sites[0], "sun_elevation_deg") >= Num(sites[sites.Count - 1], "sun_elevation_deg"));
        }

        [Fact]
        public void The_sun_is_read_at_the_arrival_deorbit_takes_not_at_a_later_time_over_the_same_ground()
        {
            // On an equatorial orbit deorbit burns for the soonest pass, so every site's landing is within one orbit and a fall of now, whatever its place in the window.
            var world = new World();
            var period = 2 * Math.PI * Math.Sqrt(Math.Pow(400000, 3) / Duna().Gm);
            foreach (var site in Sites(Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"daylight\":true}", world)))
            {
                var ut = Num(site, "landing_ut");
                Assert.InRange(ut, Now, Now + 2 * period);
                Assert.Equal(world.SunElevation("Duna", Num(site, "latitude"), Num(site, "longitude"), ut), Num(site, "sun_elevation_deg"), 0);
            }
        }

        [Fact]
        public void On_an_inclined_orbit_a_site_at_a_high_sun_lands_at_that_sun_with_no_wait()
        {
            // Only the passes that cross the site cost nothing: the site's landing is one deorbit can take, and the sun at it is the one reported.
            var world = new World { SunLongitude = 90 };
            var sites = Sites(Run(Orbiting(Duna(), 6), "{\"body\":\"Duna\",\"daylight\":true}", world));
            Assert.NotEmpty(sites);
            Assert.True(Num(sites[0], "sun_elevation_deg") > 60, sites[0].ToString());
            Assert.All(sites, s =>
            {
                Assert.False(s.TryGetProperty("wait_until_ut", out _));
                Assert.Equal(world.SunElevation("Duna", Num(s, "latitude"), Num(s, "longitude"), Num(s, "landing_ut")), Num(s, "sun_elevation_deg"), 0);
            });
        }

        private sealed class SlowWorld : World, IPlanningClock
        {
            public double ElapsedMs { get; private set; }

            public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
            {
                ElapsedMs += 2000;
                base.Survey(body, samples, facts);
            }
        }

        [Fact]
        public void A_search_out_of_time_says_so_with_what_it_tried()
        {
            var result = Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"biome\":\"Nowhere\"}", new SlowWorld());
            Assert.Empty(Sites(result));
            Assert.Contains("partial: out of time", result.GetProperty("note").GetString());
        }

        [Fact]
        public void Slope_is_not_applied_and_the_result_says_so()
        {
            // Every metre of this ground is a cliff: the airless search would find nothing gentle.
            var world = new World { Terrain = (lat, lon) => 100 + 1000 * lat + 1000 * lon };
            var result = Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"max_slope_deg\":1}", world);
            Assert.NotEmpty(Sites(result));
            Assert.All(Sites(result), s => Assert.False(s.TryGetProperty("slope_deg", out _)));
            Assert.Contains("not applied", result.GetProperty("slope").GetString());
        }

        [Fact]
        public void A_biome_keeps_only_its_sites_and_a_daylight_false_search_keeps_the_dark_ones()
        {
            var world = new World { BiomeAt = (lat, lon) => lon > 0 ? "Highlands" : "Lowlands" };
            var inBiome = Sites(Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"biome\":\"Lowlands\",\"edge_margin_m\":0}", world));
            Assert.NotEmpty(inBiome);
            Assert.All(inBiome, s => Assert.Equal("Lowlands", s.GetProperty("biome").GetString()));
            var dark = new World { SunLongitude = 180 };
            var sites = Sites(Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"daylight\":false,\"near\":{\"latitude\":0,\"longitude\":0}}", dark));
            Assert.NotEmpty(sites);
            Assert.All(sites, s => Assert.True(Num(s, "sun_elevation_deg") < 20));
        }

        [Fact]
        public void An_exact_place_dark_at_its_next_pass_comes_back_lit_at_a_later_pass_with_the_wait_it_needs()
        {
            // The near point is at midnight for this orbit's next pass; half a turn on, it has come round to the sun.
            var result = Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"daylight\":true,\"near\":{\"latitude\":0,\"longitude\":180}}", new World());
            var sites = Sites(result);
            Assert.NotEmpty(sites);
            Assert.All(sites, s =>
            {
                Assert.True(Num(s, "sun_elevation_deg") > 20, s.ToString());
                Assert.True(Num(s, "wait_until_ut") > Now + 3600, s.ToString());
                Assert.True(Num(s, "landing_ut") > Num(s, "wait_until_ut"), s.ToString());
            });
            Assert.Contains("wait_until_ut", result.GetProperty("note").GetString());
        }

        [Fact]
        public void A_lit_next_pass_needs_no_wait()
        {
            var sites = Sites(Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"daylight\":true,\"near\":{\"latitude\":0,\"longitude\":0}}", new World()));
            Assert.All(sites, s => Assert.False(s.TryGetProperty("wait_until_ut", out _)));
        }

        /// <summary>The sun over the sea, the land east of it: only Water can be near noon.</summary>
        private static World Coast()
        {
            return new World { Turn = 21549.425, SunLongitude = -30, Terrain = (lat, lon) => lon < 0 ? -50 : 100, BiomeAt = (lat, lon) => lon < 0 ? "Water" : "Shores" };
        }

        public static IEnumerable<object[]> OceanBodies()
        {
            yield return new object[] { "Kerbin" };
            yield return new object[] { "Laythe" };
        }

        private static SnapshotBody OceanBody(string name)
        {
            return name == "Kerbin" ? Kerbin() : Laythe();
        }

        [Theory]
        [MemberData(nameof(OceanBodies))]
        public void An_entry_that_is_not_the_Last_Entry_excludes_water(string name)
        {
            var snapshot = Orbiting(OceanBody(name));
            snapshot.LastEntryPlanned = false;
            var result = Run(snapshot, "{\"body\":\"" + name + "\"}", Coast());
            Assert.NotEmpty(Sites(result));
            Assert.All(Sites(result), s =>
            {
                Assert.True(Num(s, "terrain_altitude") > 0, s.ToString());
                Assert.False(s.TryGetProperty("water", out _));
            });
            Assert.Contains("excluded", result.GetProperty("water").GetString());
        }

        [Theory]
        [MemberData(nameof(OceanBodies))]
        public void A_Last_Entry_allows_water_alongside_land_with_no_preference(string name)
        {
            var snapshot = Orbiting(OceanBody(name));
            snapshot.LastEntryPlanned = true;
            var result = Run(snapshot, "{\"body\":\"" + name + "\"}", Coast());
            Assert.True(Sites(result)[0].GetProperty("water").GetBoolean(), Sites(result)[0].ToString());
            Assert.Contains("allowed", result.GetProperty("water").GetString());
            // Land near noon is taken just the same.
            var land = Coast();
            land.SunLongitude = 60;
            Assert.All(Sites(Run(snapshot, "{\"body\":\"" + name + "\"}", land)), s => Assert.True(Num(s, "terrain_altitude") > 0, s.ToString()));
        }

        [Fact]
        public void Low_ground_on_a_body_with_no_ocean_is_land()
        {
            var world = new World { Terrain = (lat, lon) => -300 };
            var result = Run(Orbiting(Duna()), "{\"body\":\"Duna\"}", world);
            Assert.NotEmpty(Sites(result));
            Assert.False(result.TryGetProperty("water", out _));
        }

        [Fact]
        public void A_point_of_interest_stays_with_land()
        {
            var outcome = _tools.Run("find_landing_site", "{\"body\":\"Duna\",\"poi\":{\"latitude\":0,\"longitude\":0}}", Orbiting(Duna()), new World());
            Assert.True(outcome.IsError);
            Assert.Contains("airless", outcome.Json);
        }

        [Fact]
        public void With_no_site_lit_or_in_reach_the_note_says_how_many_got_how_far()
        {
            var world = new World { BiomeAt = (lat, lon) => "Highlands" };
            var result = Run(Orbiting(Duna()), "{\"body\":\"Duna\",\"biome\":\"Lowlands\"}", world);
            Assert.Empty(Sites(result));
            Assert.Contains(" 0 were in the biome", result.GetProperty("note").GetString());
        }

        [Fact]
        public void Lands_block_call_still_refuses_a_body_with_air()
        {
            var answer = BlockCalls.LandingSite(new Dictionary<string, object> { { "biome", "Lowlands" } }, Orbiting(Duna()), new World());
            Assert.Contains("airless", (string)answer["error"]);
        }
    }
}
