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
    /// <c>land</c>'s pass search over a fixed site (#510), alone, through its block call and through <c>find_landing_site</c>, against
    /// a fake body interface: a smooth sphere whose frame is the orbit's, with the sun a function of latitude, longitude and UT.
    /// </summary>
    public sealed class LandingPassTests
    {
        private const double MinmusGm = 1.7658e9;
        private const double MinmusR = 60000;
        private const double MinmusTurn = 40400;
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double MunTurn = 138984.38;

        private sealed class FakeBody : PlanningBodyBase
        {
            public Func<double, double, double, double> Sun = (lat, lon, ut) => 45;
            public Func<double, double, string> BiomeAt = (lat, lon) => "Flats";
            public string[] Biomes = new string[0];

            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return BiomeAt(latitude, longitude); }
            public override System.Collections.Generic.IList<string> BiomeNames(string body) { return Biomes; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return Sun(latitude, longitude, ut); }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private static GameSnapshot Snapshot(string name, double gm, double radius, double turn, double sma, double inclination)
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = name };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = sma, Eccentricity = 0, Inclination = inclination, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = 0, Epoch = 1000, Body = name, Periapsis = sma - radius, Apoapsis = sma - radius,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = name, Parent = "Kerbin", Gm = gm, Radius = radius, RotationPeriod = turn, SoiRadius = 2e6 });
            return snapshot;
        }

        /// <summary>Minmus with a low orbit of about 3,700 s, as in the ticket's spikes.</summary>
        private static GameSnapshot Minmus(double inclination)
        {
            return Snapshot("Minmus", MinmusGm, MinmusR, MinmusTurn, Math.Pow(MinmusGm * Math.Pow(3700 / (2 * Math.PI), 2), 1.0 / 3), inclination);
        }

        /// <summary>The Mun with a 30 km orbit.</summary>
        private static GameSnapshot Mun(double inclination)
        {
            return Snapshot("Mun", MunGm, MunR, MunTurn, MunR + 30000, inclination);
        }

        private static LandingPassResult Find(GameSnapshot snapshot, double lat, double lng, bool daylight, double maxDv, FakeBody fake = null, double? withinHours = null)
        {
            var body = snapshot.Bodies[0];
            var o = snapshot.Orbit;
            var orbit = Conic.FromElements(body.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
            var window = LandingPass.WindowHours(withinHours, snapshot.TimeCapHours, body.RotationPeriod);
            return LandingPass.Find(body, orbit, snapshot.Ut, new LandingPassAsk
            {
                Latitude = lat, Longitude = lng, PeRadius = body.Radius + LandingPass.ApproachM, EarliestUt = snapshot.Ut + LandingPass.LeadS,
                LatestUt = snapshot.Ut + LandingPass.ReachSeconds(window, daylight, body.RotationPeriod), Daylight = daylight, MaxCorrectionDv = maxDv,
            }, fake ?? new FakeBody());
        }

        private static Dictionary<string, object> Ask(params object[] keysAndValues)
        {
            var input = new Dictionary<string, object>();
            for (var i = 0; i < keysAndValues.Length; i += 2) input[(string)keysAndValues[i]] = keysAndValues[i + 1];
            return input;
        }

        // ---- The window and the reach ----

        [Fact]
        public void The_window_is_the_larger_of_the_time_cap_and_a_turn_of_the_body_up_to_48_h_unless_within_hours_is_given()
        {
            Assert.Equal(11.22, LandingPass.WindowHours(null, 6, MinmusTurn), 2);
            Assert.Equal(38.61, LandingPass.WindowHours(null, 6, MunTurn), 2);
            Assert.Equal(24, LandingPass.WindowHours(null, 24, MinmusTurn));
            Assert.Equal(48, LandingPass.WindowHours(null, 6, 3 * 86400));
            Assert.Equal(3, LandingPass.WindowHours(3, 6, MunTurn));
            Assert.Equal(48, LandingPass.WindowHours(60, 6, MunTurn));
        }

        [Fact]
        public void Daylight_keeps_its_own_day_of_the_body_reach_where_that_is_longer()
        {
            Assert.Equal(1.2 * MunTurn, LandingPass.ReachSeconds(38.61, true, MunTurn), 0);
            Assert.Equal(38.61 * 3600, LandingPass.ReachSeconds(38.61, false, MunTurn), 0);
            Assert.Equal(48 * 3600, LandingPass.ReachSeconds(48, true, MinmusTurn), 0);
        }

        [Theory]
        [InlineData(1000, 30)]
        [InlineData(400, 20)]
        [InlineData(0, 30)]
        [InlineData(-1, 30)]
        public void The_default_max_correction_dv_is_the_smaller_of_30_m_s_and_5_percent_of_the_landers_delta_v(double landerDv, double expected)
        {
            Assert.Equal(expected, LandingPass.DefaultMaxCorrectionDv(landerDv), 6);
        }

        [Fact]
        public void A_site_on_the_ground_track_needs_no_correction_and_a_site_off_it_costs_about_2_6_m_s_a_km_on_the_Mun_and_Minmus_alike()
        {
            var minmusSma = Minmus(0).Orbit.SemiMajorAxis;
            Assert.Equal(0, LandingPass.CorrectionDv(MinmusGm, minmusSma, MinmusR + 3000, 0, MinmusR), 6);
            // The two bodies have about the same density, so a low orbit's speed over the radius, and the cost a km, match.
            Assert.InRange(LandingPass.CorrectionDv(MinmusGm, minmusSma, MinmusR + 3000, 10000, MinmusR), 24, 28);
            Assert.InRange(LandingPass.CorrectionDv(MunGm, MunR + 30000, MunR + 3000, 10000, MunR), 25, 29);
        }

        [Fact]
        public void The_reach_rule_takes_a_Minmus_site_over_20_km_off_the_track_within_max_correction_dv_and_refuses_a_Mun_site_dearer_than_it()
        {
            // Equatorial orbits, so a site's latitude is its distance off the track. Both are over the old 20 km rule.
            var minmus = Find(Minmus(0), 22000 / MinmusR * 180 / Math.PI, 30, false, 60);
            Assert.Null(minmus.Error);
            Assert.InRange(minmus.CrossM, 21900, 22100);
            Assert.InRange(minmus.CorrectionDv, 55, 59);
            var mun = Find(Mun(0), 25000 / MunR * 180 / Math.PI, 30, false, 60);
            Assert.NotNull(mun.Error);
            Assert.Contains("25 km off the ground track", mun.Error);
            Assert.Contains("a 69 m/s correction, over max_correction_dv 60 m/s", mun.Error);
            Assert.All(mun.Passes, p => Assert.StartsWith("a 69 m/s correction, over max_correction_dv 60 (25 km off the track)", p.Verdict));
            // At the default 30 m/s the Minmus site is out of reach too: the rule is delta-v, not distance.
            Assert.NotNull(Find(Minmus(0), 22000 / MinmusR * 180 / Math.PI, 30, false, 30).Error);
        }

        // ---- The whole window, not 3 orbits ----

        [Fact]
        public void A_fixed_Minmus_site_whose_first_pass_in_reach_is_six_orbits_on_is_found_where_3_orbits_found_none()
        {
            // A 60 degree orbit of about 3,700 s against Minmus's 40,400 s turn: the site at (-40, 0) is 40 to 84 km off the track
            // at each pass in the first 3 orbits, over the old 20 km rule, so the old search failed it. The window, a turn of Minmus
            // (11.2 h), holds a pass 6.5 orbits on with a 10.6 m/s correction.
            var snapshot = Minmus(60);
            var found = Find(snapshot, -40, 0, false, 30);
            Assert.Null(found.Error);
            Assert.InRange((found.ArriveUt - 1000) / 3700, 6, 7);
            Assert.InRange(found.CorrectionDv, 9, 12);
            var early = found.Passes.Where(p => p.ArriveUt < 1000 + 3 * 3700).ToList();
            Assert.NotEmpty(early);
            Assert.All(early, p => Assert.True(p.CrossM > 20000, "a pass " + p.CrossM + " m off the track in the first 3 orbits"));
            Assert.Equal("chosen", found.Passes.Single(p => p.ArriveUt == found.ArriveUt).Verdict);

            // land asks the block call the same, with its periapsis and the window find_landing_site would take.
            var call = BlockCalls.LandingPass(Ask("lat", -40.0, "lng", 0.0, "r_pe", MinmusR + 3000.0, "daylight", false, "max_correction_dv", 30.0), snapshot, new FakeBody());
            Assert.Equal("", call["error"]);
            Assert.Equal(found.ArriveUt, (double)call["arrive_ut"], 3);
            Assert.Equal(found.BurnUt, (double)call["burn_ut"], 3);
            Assert.Equal(11.22, (double)call["window_hours"], 2);
        }

        [Fact]
        public void The_burn_is_pinned_to_the_pass_and_the_next_pass_comes_a_synodic_orbit_later()
        {
            // An equatorial Mun orbit over a site on the equator: a pass each time the craft laps the turning site.
            var snapshot = Mun(0);
            var found = Find(snapshot, 0, 120, false, 30);
            Assert.Null(found.Error);
            var period = 2 * Math.PI * Math.Sqrt(Math.Pow(MunR + 30000, 3) / MunGm);
            var synodic = 1 / (1 / period - 1 / MunTurn);
            Assert.Equal(synodic, found.NextPassUt - found.ArriveUt, 0);
            Assert.Equal(new[] { "chosen", "next" }, found.Passes.Select(p => p.Verdict).ToArray());
            // In the frame fixed to the stars, the craft at the burn is opposite where the turning site is at the touchdown, to the
            // search's half second (about 0.06 degrees of this orbit).
            var craftAtBurn = (found.BurnUt - 1000) * 360 / period;
            var siteAtTouchdown = 120 + (found.ArriveUt - 1000) * 360 / MunTurn;
            Assert.InRange(LandingSiteSearch.Wrap180(siteAtTouchdown - 180 - craftAtBurn), -0.06, 0.06);
        }

        [Fact]
        public void A_dark_pass_is_passed_over_for_a_lit_one_and_none_lit_says_so()
        {
            var snapshot = Mun(0);
            var first = Find(snapshot, 0, 120, false, 30);
            var sunUp = new FakeBody { Sun = (lat, lon, ut) => ut < first.ArriveUt + 100 ? -5 : 40 };
            var found = Find(snapshot, 0, 120, true, 30, sunUp);
            Assert.Null(found.Error);
            Assert.Equal(first.NextPassUt, found.ArriveUt, 3);
            Assert.Equal("dark: the sun -5 degrees up, not over 20", found.Passes[0].Verdict);
            var never = Find(snapshot, 0, 120, true, 30, new FakeBody { Sun = (lat, lon, ut) => 10 });
            Assert.StartsWith("no daylight pass over the site in the next 46.3 h on this orbit", never.Error);
        }

        [Fact]
        public void No_pass_at_all_in_a_short_window_says_so()
        {
            Assert.Equal("no pass over the site in the next 0.1 h", Find(Mun(0), 0, 120, false, 30, withinHours: 0.1).Error);
        }

        [Fact]
        public void An_orbit_too_low_for_the_descent_is_an_error()
        {
            var snapshot = Mun(0);
            snapshot.Orbit.SemiMajorAxis = MunR + 3500;
            Assert.StartsWith("orbit too low to land from: it must be at least 4000 m up", Find(snapshot, 0, 120, false, 30).Error);
        }

        [Fact]
        public void The_block_call_says_what_it_needs_and_passes_on_the_searchs_failure_with_its_passes()
        {
            Assert.Equal("r_pe is required", BlockCalls.LandingPass(Ask("lat", 0.0, "lng", 0.0), Mun(0), new FakeBody())["error"]);
            Assert.Equal("daylight must be true or false", BlockCalls.LandingPass(Ask("lat", 0.0, "lng", 0.0, "r_pe", 203000.0, "daylight", "yes"), Mun(0), new FakeBody())["error"]);
            var landed = Mun(0);
            landed.Orbit = null;
            Assert.Contains("not in orbit", (string)BlockCalls.LandingPass(Ask("lat", 0.0, "lng", 0.0, "r_pe", 203000.0), landed, new FakeBody())["error"]);
            var far = BlockCalls.LandingPass(Ask("lat", 7.2, "lng", 30.0, "r_pe", 203000.0, "daylight", false), Mun(0), new FakeBody());
            Assert.Contains("over max_correction_dv 30 m/s", (string)far["error"]);
            Assert.Equal(-1.0, far["next_pass_ut"]);
            Assert.NotEmpty((List<object>)far["passes"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void It_runs_in_milliseconds()
        {
            // The longest search land makes on the Mun: daylight's 46 h reach, about 80 passes with the sun asked at each. Measured
            // at about 1 ms alone in a Debug build; the budget is about 20 times that.
            var snapshot = Mun(10);
            Timing.Within(20, () => Find(snapshot, 3, 77, true, 30));
        }

        [Fact]
        public void Land_given_a_biome_the_body_does_not_have_gets_the_tools_error_before_any_search()
        {
            var fake = new FakeBody { Biomes = new[] { "East Crater", "Farside Crater", "Midlands" } };
            var site = BlockCalls.LandingSite(Ask("biome", "Crater", "daylight", false), Mun(10), fake);
            Assert.Equal("\"Crater\" is not a biome on Mun; closest: East Crater, Farside Crater; its biomes are: East Crater, Farside Crater, Midlands", site["error"]);
            Assert.False(site.ContainsKey("lat"));
        }

        // ---- find_landing_site and land choose the same pass ----

        [Fact]
        public void Land_and_find_landing_site_given_the_same_orbit_site_rule_and_within_hours_choose_the_same_pass()
        {
            // An inclined Mun orbit, the sun overhead at longitude 90 at UT 1000 and drifting west, and a biome east of longitude 60.
            var fake = new FakeBody
            {
                Sun = (lat, lon, ut) => 90 - LandingSiteSearch.ArcDegrees(lat, lon, 0, 90 - 360 * (ut - 1000) / MunTurn),
                BiomeAt = (lat, lon) => lon > 60 && lon < 120 ? "EastCrater" : "Highlands",
            };
            var snapshot = Mun(10);
            var outcome = PlanningTools.Default().Run("find_landing_site",
                "{\"body\":\"Mun\",\"biome\":\"EastCrater\",\"daylight\":true,\"within_hours\":20}", snapshot, fake);
            Assert.False(outcome.IsError, outcome.Json);
            JsonElement shown;
            using (var doc = JsonDocument.Parse(outcome.Json)) shown = doc.RootElement.GetProperty("sites")[0].Clone();
            var lat = shown.GetProperty("latitude").GetDouble();
            var lng = shown.GetProperty("longitude").GetDouble();
            var landingUt = shown.GetProperty("landing_ut").GetDouble();

            // land with the biome and the tool's site as near: its site search, then its pass search over that site.
            var site = BlockCalls.LandingSite(Ask("biome", "EastCrater", "daylight", true, "max_slope_deg", 8.0, "within_hours", 20.0,
                "near", Ask("latitude", lat, "longitude", lng)), snapshot, fake);
            Assert.Equal("", site["error"]);
            Assert.Equal(lat, (double)site["lat"], 3);
            Assert.Equal(lng, (double)site["lng"], 3);
            var pass = BlockCalls.LandingPass(Ask("lat", site["lat"], "lng", site["lng"], "r_pe", MunR + LandingPass.ApproachM,
                "daylight", true, "within_hours", 20.0), snapshot, fake);
            Assert.Equal("", pass["error"]);
            Assert.Equal(landingUt, (double)pass["arrive_ut"], 0);

            // land with the tool's site alone as its target: the same pass.
            var alone = BlockCalls.LandingPass(Ask("lat", lat, "lng", lng, "r_pe", MunR + LandingPass.ApproachM, "daylight", true, "within_hours", 20.0), snapshot, fake);
            Assert.Equal(landingUt, (double)alone["arrive_ut"], 0);
        }

        [Fact]
        public void Find_landing_site_shows_only_sites_land_can_fly()
        {
            // The sun up over the ground-track points only in the first minute, dark at every touchdown land could plan (from 120 s on): the one
            // site under the craft now has the sun up only now, too soon for land.
            var fake = new FakeBody { Sun = (lat, lon, ut) => ut < 1060 ? 45 : -10 };
            var outcome = PlanningTools.Default().Run("find_landing_site", "{\"body\":\"Mun\",\"daylight\":true,\"within_hours\":2}", Mun(0), fake);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json))
            {
                Assert.Empty(doc.RootElement.GetProperty("sites").EnumerateArray());
                Assert.Contains("with no pass land can fly in the window", doc.RootElement.GetProperty("note").GetString());
            }
        }
    }
}
