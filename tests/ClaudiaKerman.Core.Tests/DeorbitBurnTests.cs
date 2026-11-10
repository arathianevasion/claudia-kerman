// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The deorbit burn search with the research's capsule and atmosphere (<see cref="ReentrySim"/>), from an 80 km circular
    /// equatorial Kerbin orbit that crosses longitude 0 at UT 0. A burn it chooses is flown again through the predictor alone,
    /// so what it reports is checked against an independent prediction.
    /// </summary>
    public sealed class DeorbitBurnTests
    {
        private const double KscLat = -0.0972;
        private const double KscLng = -74.5577;

        private static Conic Circular(double alt = 80000, double inclinationDeg = 0)
        {
            var r = ReentrySim.R + alt;
            var v = Math.Sqrt(ReentrySim.Mu / r);
            var i = inclinationDeg * Math.PI / 180;
            return Conic.FromState(ReentrySim.Mu, new Vec3(r, 0, 0), new Vec3(0, v * Math.Cos(i), v * Math.Sin(i)), 0);
        }

        private static DeorbitPlan Find(double lat, double lng, Conic orbit = null, double maxWait = ReentrySim.RotationPeriod, double atmosphere = ReentrySim.AtmTop, double accuracyKm = 5, double earliest = 120, bool daylight = false, Func<double, double> sunAt = null, SitePass site = null, double maxDv = 0)
        {
            var ask = new DeorbitAsk { TargetLatitude = lat, TargetLongitude = lng, EarliestUt = earliest, MaxWait = maxWait, AccuracyKm = accuracyKm, DeltaVLeft = 5000, Daylight = daylight, SunAt = sunAt, Site = site, MaxDv = maxDv };
            return DeorbitBurn.Find(ReentrySim.Kerbin(atmosphere), orbit ?? Circular(), ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame());
        }

        /// <summary>The plan's burn applied to the orbit and flown by the predictor alone.</summary>
        private static ReentryPrediction FlyAgain(Conic orbit, DeorbitPlan plan, out Conic after)
        {
            Vec3 r, v;
            orbit.State(plan.BurnUt, out r, out v);
            var up = Vec3.Cross(r, v).Normalized;
            after = Conic.FromState(ReentrySim.Mu, r, v + v.Normalized * plan.Prograde + up * plan.Normal, plan.BurnUt);
            var craft = new ReentrySim.Capsule();
            return ReentryPredictor.Predict(ReentrySim.Kerbin(), after, plan.BurnUt, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
        }

        private static double Km(double lat1, double lng1, double lat2, double lng2)
        {
            return LandingSiteSearch.ArcDegrees(lat1, lng1, lat2, lng2) * Math.PI / 180 * ReentrySim.R / 1000;
        }

        [Fact]
        public void It_finds_a_retrograde_burn_that_lands_on_KSC_within_a_rotation()
        {
            var orbit = Circular();
            var plan = Find(KscLat, KscLng);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            Assert.InRange(plan.BurnUt, 120, 120 + ReentrySim.RotationPeriod);
            Assert.True(plan.Prograde < 0);
            Assert.InRange(Math.Abs(plan.Normal), 0, 5);
            Assert.Equal(Math.Sqrt(plan.Prograde * plan.Prograde + plan.Normal * plan.Normal), plan.DeltaV, 6);

            Conic after;
            var again = FlyAgain(orbit, plan, out after);
            Assert.InRange(Km(again.Latitude, again.Longitude, plan.Landing.Latitude, plan.Landing.Longitude), 0, 0.01);
            Assert.InRange(Km(again.Latitude, again.Longitude, KscLat, KscLng), 0, 0.5);
            // The steep entry the research recommends: periapsis at 30% of the atmosphere, 21 km at Kerbin.
            Assert.InRange(after.PeR - ReentrySim.R, 20900, 21100);
        }

        [Fact]
        public void A_target_beside_the_ground_track_is_reached_with_a_normal_part()
        {
            // A tight band: in a wider one the search spends its width on less normal part (#794).
            var plan = Find(1, KscLng, accuracyKm: 0.5);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            // North of a prograde equatorial orbit: along its angular momentum.
            Assert.InRange(plan.Normal, 1, DeorbitBurn.MaxNormal);
        }

        [Fact]
        public void In_a_mirrored_frame_the_normal_part_keeps_its_real_sign()
        {
            // The same orbit and target seen in a frame with y flipped: the node's normal is the real angular momentum, so its sign holds.
            var plain = Find(1, KscLng, accuracyKm: 0.5);
            Vec3 r, v;
            Circular().State(0, out r, out v);
            var flipped = Conic.FromState(ReentrySim.Mu, new Vec3(r.X, -r.Y, r.Z), new Vec3(v.X, -v.Y, v.Z), 0);
            var mirrored = new BodyFrame { LonOffset = 0, Handed = -1, Ut = 0, RotationPeriod = ReentrySim.RotationPeriod };
            var ask = new DeorbitAsk { TargetLatitude = 1, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, AccuracyKm = 0.5, DeltaVLeft = 5000 };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), flipped, ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), mirrored);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            Assert.InRange(plan.Normal, plain.Normal - 0.5, plain.Normal + 0.5);
            Assert.InRange(plan.BurnUt, plain.BurnUt - 1, plain.BurnUt + 1);
        }

        [Fact]
        public void An_inclined_orbit_lands_on_its_target_too()
        {
            var plan = Find(KscLat, KscLng, Circular(inclinationDeg: 6), accuracyKm: 0.5);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
        }

        [Fact]
        public void A_target_too_far_off_the_ground_track_fails_rather_than_burns_and_says_where_the_best_lands()
        {
            var plan = Find(20, KscLng);
            Assert.Contains("over accuracy_km 5.0", plan.Error);
            Assert.Contains("too far off the ground track", plan.Error);
            Assert.InRange(plan.ErrorKm, 100, 2500);
            Assert.NotNull(plan.Landing);
            Assert.Null(plan.Landing.Error);
        }

        [Fact]
        public void A_short_max_wait_fails_when_no_burn_in_it_reaches_the_target()
        {
            // Burns in the first 10 minutes land between about 125 and 230 degrees east; 270 (-90) is out of reach.
            var plan = Find(0, -90, maxWait: 600);
            Assert.Contains("the best burn in the next 0.2 h lands", plan.Error);
            Assert.Contains("allow a longer max_wait", plan.Error);
            Assert.InRange(plan.BurnUt, 120, 720);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void It_runs_in_milliseconds()
        {
            // The deorbit block's search to KSC from the 80 km orbit, and one too far off the track (every pass refined). Measured
            // at about 280 ms for the two in a Debug build since #794 scans every pass (about 55 ms in Release).
            Timing.Within(500, () =>
            {
                Find(KscLat, KscLng);
                Find(20, KscLng);
            });
        }

        [Fact]
        public void An_airless_body_fails_with_body_has_no_atmosphere()
        {
            Assert.Equal("body has no atmosphere", Find(KscLat, KscLng, atmosphere: 0).Error);
        }

        [Fact]
        public void An_escape_orbit_fails()
        {
            var r = ReentrySim.R + 80000;
            var escape = Conic.FromState(ReentrySim.Mu, new Vec3(r, 0, 0), new Vec3(0, 1.5 * Math.Sqrt(2 * ReentrySim.Mu / r), 0), 0);
            Assert.Equal("the orbit escapes Kerbin: deorbit burns from a closed orbit, so capture first", Find(KscLat, KscLng, escape).Error);
        }

        [Fact]
        public void A_periapsis_already_in_the_atmosphere_fails_and_says_to_fly_reentry()
        {
            Assert.Equal("the periapsis (30000 m) is already inside Kerbin's atmosphere: fly reentry", Find(KscLat, KscLng, ReentrySim.AfterBurn()).Error);
        }

        // ---- the block-internal calls: ADDONS:CLAUDIA:DEORBITBURN and REENTRYIMPACT ----

        /// <summary>A sphere whose surface frame is the orbit frame at the call (x under longitude 0, z north).</summary>
        private sealed class Sphere : PlanningBodyBase
        {
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            /// <summary>What the sun reader was asked, and what it answers (0 unless set).</summary>
            public readonly System.Collections.Generic.List<double[]> SunAsks = new System.Collections.Generic.List<double[]>();
            public readonly System.Collections.Generic.List<string> SunBodies = new System.Collections.Generic.List<string>();
            public double Sun;
            public override double SunElevation(string body, double latitude, double longitude, double ut)
            {
                SunBodies.Add(body);
                SunAsks.Add(new[] { latitude, longitude, ut });
                return Sun;
            }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        /// <summary>The 80 km circular equatorial orbit as a Game Snapshot at UT 0, round Kerbin (or the airless Mun).</summary>
        private static Missions.GameSnapshot Snapshot(string body = "Kerbin", double pe = 80000)
        {
            var kerbin = ReentrySim.Kerbin();
            var radius = body == "Kerbin" ? ReentrySim.R : 200000;
            var snapshot = new Missions.GameSnapshot { Ut = 0, Situation = "ORBITING", Body = body, DaylightLandingDefault = false };
            var sma = (2 * radius + 80000 + pe) / 2;
            snapshot.Orbit = new Missions.SnapshotOrbit
            {
                SemiMajorAxis = sma, Eccentricity = (radius + 80000 - (radius + pe)) / (2 * sma), Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 180,
                MeanAnomalyAtEpoch = Math.PI, Epoch = 0, Body = body, Periapsis = pe, Apoapsis = 80000,
            };
            snapshot.Bodies.Add(new Missions.SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(kerbin);
            snapshot.Bodies.Add(new Missions.SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000 });
            return snapshot;
        }

        private static System.Collections.Generic.Dictionary<string, object> Ask(params object[] pairs)
        {
            var ask = new System.Collections.Generic.Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) ask[(string)pairs[i]] = pairs[i + 1];
            return ask;
        }

        [Fact]
        public void The_block_call_reads_the_daylight_setting_and_the_planning_bodys_sun()
        {
            var ask = Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0);
            var snapshot = Snapshot();
            snapshot.DaylightLandingDefault = true;
            var dark = BlockCalls.DeorbitBurn(ask, snapshot, new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Contains("daylight", (string)dark["error"]);
            snapshot.DaylightLandingDefault = false;
            Assert.Equal("", BlockCalls.DeorbitBurn(ask, snapshot, new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule())["error"]);
        }

        [Fact]
        public void The_block_call_asks_the_planning_body_for_the_sun_at_the_target_when_the_burn_lands()
        {
            var ask = Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0);
            var snapshot = Snapshot();
            snapshot.DaylightLandingDefault = true;
            var sphere = new Sphere { Sun = 40 };
            var result = BlockCalls.DeorbitBurn(ask, snapshot, sphere, new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", result["error"]);
            Assert.NotEmpty(sphere.SunAsks);
            Assert.All(sphere.SunBodies, body => Assert.Equal("Kerbin", body));
            Assert.All(sphere.SunAsks, a => { Assert.Equal(KscLat, a[0]); Assert.Equal(KscLng, a[1]); });
            Assert.Contains(sphere.SunAsks, a => a[2] == (double)result["landing_ut"]);
            Assert.All(sphere.SunAsks, a => Assert.True(a[2] > (double)result["burn_ut"] || a[2] > 120, "asked at " + a[2]));
        }

        [Fact]
        public void The_block_call_gives_the_burn_as_node_components_and_its_landing()
        {
            var found = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "ground_alt", 0.0, "accuracy_km", 5.0, "dv_left", 5000.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", found["error"]);
            Assert.True((double)found["prograde"] < 0);
            Assert.Equal(0.0, found["radial"]);
            Assert.InRange((double)found["burn_ut"], 120, 120 + 21549.425);
            Assert.InRange((double)found["error_km"], 0, 0.5);
            Assert.InRange((double)found["lat"], KscLat - 0.01, KscLat + 0.01);
            Assert.InRange((double)found["lng"], KscLng - 0.01, KscLng + 0.01);
            Assert.True((double)found["landing_ut"] > (double)found["burn_ut"]);
            Assert.InRange((double)found["dv"], 30, 150);
            // Every value is a double or a string, as KosValues turns into kOS values.
            foreach (var value in found.Values) Assert.True(value is double || value is string, value.GetType().Name);
        }

        [Fact]
        public void The_deorbit_call_logs_the_planned_orbit_after_the_burn_and_the_frame()
        {
            var found = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "ground_alt", 70.0, "dv_left", 5000.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            var line = (string)found["plan_log"];
            Assert.StartsWith("[plan-vs-flown] DEORBITBURN planned orbit: Pe ", line);
            // The entry periapsis is 30 % of the way up the atmosphere; the burn is from an 80 km circle, so the apoapsis stays 80 km.
            Assert.Contains("Pe " + Math.Round(0.3 * ReentrySim.AtmTop).ToString(System.Globalization.CultureInfo.InvariantCulture) + " m, Ap 80000 m", line);
            Assert.Contains(", period ", line);
            Assert.Contains(", Pe at UT ", line);
            Assert.Contains("frame LonOffset 0.0000 deg, handed 1, ground_alt 70 m", line);
        }

        [Fact]
        public void The_impact_call_logs_the_orbit_it_predicted_and_the_frame()
        {
            var flown = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            var line = (string)flown["plan_log"];
            Assert.StartsWith("[plan-vs-flown] REENTRYIMPACT planned orbit: Pe 30000 m, Ap 80000 m, period ", line);
            Assert.Contains("frame LonOffset 0.0000 deg, handed 1, ground_alt 0 m", line);
        }

        [Fact]
        public void The_block_call_over_accuracy_gives_the_reason_and_the_best_landing()
        {
            var found = BlockCalls.DeorbitBurn(Ask("lat", 20.0, "lng", KscLng, "earliest_ut", 120.0, "dv_left", 5000.0), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            // The default accuracy, 5 km since #462 (10 before).
            Assert.Contains("over accuracy_km 5.0", (string)found["error"]);
            Assert.True((double)found["error_km"] > 100);
            Assert.True(found.ContainsKey("lat"));
        }

        [Fact]
        public void The_block_calls_on_an_airless_body_say_body_has_no_atmosphere()
        {
            var deorbit = BlockCalls.DeorbitBurn(Ask("lat", 0.0, "lng", 0.0), Snapshot("Mun"), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("body has no atmosphere", deorbit["error"]);
            Assert.Single(deorbit);
            var impact = BlockCalls.ReentryImpact(Ask(), Snapshot("Mun"), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("body has no atmosphere", impact["error"]);
        }

        [Fact]
        public void The_impact_call_predicts_the_orbit_as_it_is_and_the_distance_to_a_target()
        {
            // The research's burn, already flown: 80 km by 30 km, from the apoapsis on the x axis.
            var flown = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", flown["error"]);
            Assert.InRange(ReentrySim.Km((double)flown["lng"]), 1095, 1099);
            Assert.InRange((double)flown["error_km"], 1090, 1100);
            var noTarget = BlockCalls.ReentryImpact(Ask(), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal(-1.0, noTarget["error_km"]);
        }

        [Fact]
        public void The_impact_call_given_an_orbit_predicts_that_one_not_the_crafts()
        {
            // The craft sits in a safe 80 km orbit (#283): return asks for the path it has planned, kOS's elements in degrees.
            var sma = (2 * ReentrySim.R + 80000 + 30000) / 2;
            var ecc = (ReentrySim.R + 80000 - (ReentrySim.R + 30000)) / (2 * sma);
            var planned = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0, "orbit_sma", sma, "orbit_ecc", ecc, "orbit_inc", 0.0,
                "orbit_lan", 0.0, "orbit_argpe", 180.0, "orbit_mna", 180.0, "orbit_epoch", 0.0, "orbit_body", "Kerbin"),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            var asFlown = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", planned["error"]);
            Assert.Equal((double)asFlown["lng"], (double)planned["lng"], 6);
            Assert.Equal((double)asFlown["error_km"], (double)planned["error_km"], 6);
        }

        [Fact]
        public void The_impact_call_falls_from_from_ut_not_from_an_earlier_pass_of_the_same_orbit()
        {
            // The 80 km by 30 km orbit is at apoapsis at UT 0 and 1800ish: a path that begins then falls on its next pass.
            var now = BlockCalls.ReentryImpact(Ask("ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            var later = BlockCalls.ReentryImpact(Ask("ground_alt", 0.0, "from_ut", 2500.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", later["error"]);
            Assert.True((double)later["landing_ut"] > 2500);
            Assert.True((double)now["landing_ut"] < 2500);
        }

        [Fact]
        public void The_impact_call_given_an_orbit_round_a_body_with_no_air_says_so()
        {
            var mun = BlockCalls.ReentryImpact(Ask("orbit_sma", 230000.0, "orbit_ecc", 0.0, "orbit_body", "Mun"), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("body has no atmosphere", mun["error"]);
        }

        [Fact]
        public void The_impact_call_on_an_orbit_above_the_air_says_so()
        {
            var high = BlockCalls.ReentryImpact(Ask(), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("the orbit never reaches Kerbin's atmosphere (periapsis 80000 m)", high["error"]);
        }

        [Fact]
        public void The_chutes_input_reaches_the_impact_and_the_deorbit_calls_and_a_bad_value_is_named()
        {
            // With chutes false the stowed chute stays stowed: the craft comes down later and farther, as the predictor alone says.
            var craft = new ReentrySim.Capsule { Chutes = new System.Collections.Generic.List<ReentryChute> { ReentrySim.Mk16() } };
            var chuted = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), craft);
            var kept = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0, "chutes", false), Snapshot(pe: 30000),
                new Sphere(), new ReentrySim.Atmosphere(), craft);
            Assert.Equal("", kept["error"]);
            Assert.True((double)kept["lng"] > (double)chuted["lng"]);
            Assert.True((double)kept["landing_ut"] < (double)chuted["landing_ut"]);

            var deorbitChuted = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), craft);
            var deorbitKept = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "chutes", false, "dv_left", 5000.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), craft);
            Assert.Equal("", deorbitKept["error"]);
            Assert.NotEqual((double)deorbitChuted["dv"], (double)deorbitKept["dv"]);

            var badDeorbit = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "chutes", "no"), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), craft);
            Assert.Equal("chutes must be true or false", badDeorbit["error"]);
            Assert.Single(badDeorbit);
            var badImpact = BlockCalls.ReentryImpact(Ask("chutes", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), craft);
            Assert.Equal("chutes must be true or false", badImpact["error"]);
            Assert.Single(badImpact);
        }

        [Fact]
        public void The_parts_to_leave_out_are_the_uids_given_as_leave_out()
        {
            // kOS gives a part's UID as a string; a list of them, as KosValues turns it, or one bare.
            Assert.Equal(new[] { "4294512936", "17" }, BlockCalls.LeftOut(Ask("leave_out", new System.Collections.Generic.List<object> { "4294512936", 17.0 })));
            Assert.Equal(new[] { "99" }, BlockCalls.LeftOut(Ask("leave_out", "99")));
            Assert.Empty(BlockCalls.LeftOut(Ask("lat", 0.0)));
            Assert.Empty(BlockCalls.LeftOut(null));
        }

        [Fact]
        public void The_parts_the_heat_shield_takes_are_the_uids_given_as_shield_drop_and_none_by_default()
        {
            Assert.Equal(new[] { "4294512000" }, BlockCalls.ShieldDrop(Ask("shield_drop", new System.Collections.Generic.List<object> { "4294512000" })));
            Assert.Empty(BlockCalls.ShieldDrop(Ask("leave_out", "99")));
        }

        [Fact]
        public void The_engines_isp_lightens_the_craft_after_the_burn_and_the_search_allows_for_it()
        {
            var orbit = Circular();
            var ask = new DeorbitAsk { TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, Isp = 300, DeltaVLeft = 5000 };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), orbit, ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame());
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            // Flown at the full mass, the same burn goes long: a lighter craft slows sooner.
            Conic after;
            var heavy = FlyAgain(orbit, plan, out after);
            Assert.InRange(Km(heavy.Latitude, heavy.Longitude, KscLat, KscLng), 0.5, 20);
        }

        private static DeorbitPlan Burn(double ut, double dv, double errorKm)
        {
            return new DeorbitPlan { BurnUt = ut, DeltaV = dv, ErrorKm = errorKm };
        }

        [Fact]
        public void Of_burns_within_accuracy_the_cheapest_is_chosen_not_the_earliest_or_the_most_accurate()
        {
            Assert.True(DeorbitBurn.Better(Burn(9000, 60, 4.5), Burn(300, 400, 0.01), 5));
            Assert.False(DeorbitBurn.Better(Burn(300, 400, 0.01), Burn(9000, 60, 4.5), 5));
            // A near tie in cost (under 1 m/s) goes to the earlier burn.
            Assert.True(DeorbitBurn.Better(Burn(300, 60.5, 1), Burn(9000, 60, 1), 5));
            Assert.False(DeorbitBurn.Better(Burn(9000, 60, 1), Burn(300, 60.5, 1), 5));
        }

        private static string Under(DeorbitPlan plan, double limit)
        {
            return plan.DeltaV <= limit ? null : "needs " + plan.DeltaV + " at " + plan.BurnUt;
        }

        [Fact]
        public void A_chain_of_near_ties_does_not_walk_the_choice_away_from_the_cheapest()
        {
            var burns = new System.Collections.Generic.List<DeorbitPlan> { Burn(900, 100, 1), Burn(500, 100.9, 1), Burn(300, 101.8, 1), Burn(100, 102.7, 1) };
            Assert.Equal(500, DeorbitBurn.Choose(burns, 5, plan => null).BurnUt);
        }

        [Fact]
        public void When_the_earlier_of_a_near_tie_is_over_budget_the_cheapest_flies_or_is_the_one_quoted()
        {
            var burns = new System.Collections.Generic.List<DeorbitPlan> { Burn(500, 100.4, 1), Burn(3000, 99.8, 1) };
            Assert.Equal(500, DeorbitBurn.Choose(burns, 5, plan => Under(plan, 200)).BurnUt);
            var fits = DeorbitBurn.Choose(burns, 5, plan => Under(plan, 100));
            Assert.Equal(3000, fits.BurnUt);
            Assert.Null(fits.Error);
            var over = DeorbitBurn.Choose(burns, 5, plan => Under(plan, 90));
            Assert.Equal(3000, over.BurnUt);
            Assert.Equal("needs 99.8 at 3000", over.Error);
        }

        [Fact]
        public void A_pass_that_reaches_the_target_is_not_dropped_when_the_budget_runs_short()
        {
            // An inclined orbit and a target at the edge of its track: over two turns of the body most passes can't reach it, and
            // their refining runs the full count; those that can must still get their turn.
            var plan = Find(5.9, KscLng, Circular(inclinationDeg: 6), maxWait: 2 * ReentrySim.RotationPeriod);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 5);
        }

        [Fact]
        public void A_burn_within_accuracy_beats_any_outside_it_however_cheap()
        {
            Assert.True(DeorbitBurn.Better(Burn(9000, 800, 4.9), Burn(300, 20, 5.1), 5));
            Assert.False(DeorbitBurn.Better(Burn(300, 20, 5.1), Burn(9000, 800, 4.9), 5));
        }

        [Fact]
        public void Outside_accuracy_the_smallest_error_is_still_the_best_with_the_earlier_on_a_near_tie()
        {
            Assert.True(DeorbitBurn.Better(Burn(9000, 800, 8), Burn(300, 20, 30), 5));
            Assert.True(DeorbitBurn.Better(Burn(300, 800, 8.2), Burn(9000, 20, 8), 5));
            Assert.False(DeorbitBurn.Better(Burn(9000, 20, 8), Burn(300, 800, 8.2), 5));
        }

        [Fact]
        public void The_cheapest_burn_within_accuracy_wins_over_the_most_accurate()
        {
            // From this orbit a pass near UT 5,300 costs about 325 m/s on the target; refining the four passes one prediction
            // favoured took a 528 m/s burn at UT 16,689 instead.
            var orbit = Eccentric();
            var onTarget = Find(KscLat, KscLng, orbit, accuracyKm: 0.05);
            Assert.InRange(onTarget.BurnUt, 5200, 5400);
            Assert.InRange(onTarget.DeltaV, 320, 330);

            // With the band to spend, it lands off the target for less delta-v, and an independent flight of it stays in the band.
            var plan = Find(KscLat, KscLng, orbit);
            Assert.Null(plan.Error);
            Assert.InRange(plan.BurnUt, 5200, 5400);
            Assert.InRange(plan.DeltaV, 300, onTarget.DeltaV - DeorbitBurnTieDv);
            Assert.InRange(plan.ErrorKm, 1, 5);
            Conic after;
            var again = FlyAgain(orbit, plan, out after);
            Assert.InRange(Km(again.Latitude, again.Longitude, KscLat, KscLng), 0, 5);

            // A wider band buys more.
            Assert.True(Find(KscLat, KscLng, orbit, accuracyKm: 20).DeltaV < plan.DeltaV - DeorbitBurnTieDv);
        }

        [Theory]
        [InlineData(1500000)]
        [InlineData(2000000)]
        [InlineData(300000)]
        public void No_narrow_window_finds_a_burn_cheaper_than_the_whole_window_does(double apoapsis)
        {
            var orbit = Eccentric(apoapsis);
            var whole = Find(KscLat, KscLng, orbit);
            Assert.Null(whole.Error);
            Assert.InRange(whole.ErrorKm, 0, 5);
            var slide = Math.Min(orbit.Period / 16, 600);
            var hits = 0;
            for (var start = 120.0; start < 120 + ReentrySim.RotationPeriod; start += slide)
            {
                var narrow = Find(KscLat, KscLng, orbit, maxWait: slide, earliest: start);
                if (narrow.ErrorKm > 5) continue;
                hits++;
                Assert.True(whole.DeltaV <= narrow.DeltaV + DeorbitBurnTieDv, "UT " + narrow.BurnUt + " costs " + narrow.DeltaV + ", the whole window took " + whole.DeltaV);
            }
            Assert.True(hits > 0);
        }

        [Fact]
        public void A_pass_one_prediction_would_overlook_is_found_rather_than_failing()
        {
            // From this orbit the first burn's landing, rotated round the orbit, says nothing near UT 7,000 is worth a look.
            var plan = Find(KscLat, KscLng, Eccentric(2000000));
            Assert.Null(plan.Error);
            Assert.InRange(plan.BurnUt, 6900, 7100);
        }

        [Fact]
        public void Burns_that_cost_the_same_take_the_first_pass_and_stay_on_the_target()
        {
            var plan = Find(KscLat, KscLng);
            Assert.InRange(plan.BurnUt, 120, 120 + Circular().Period);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            // A wide band with nothing to save in it: not a burn 18 km off, a few seconds earlier.
            var wide = Find(KscLat, KscLng, accuracyKm: 20);
            Assert.InRange(wide.ErrorKm, 0, 0.5);
        }

        // #957: the Daylight landing setting. A near-circular, near-equatorial orbit passes the site every turn at about the same cost.
        [Fact]
        public void With_daylight_on_a_dark_pass_is_skipped_for_a_lit_one()
        {
            var orbit = Circular(inclinationDeg: 2);
            // The pass the search would fly with the sun ignored is dark; only the pass after it (about 2,000 s on) is lit.
            var first = Find(KscLat, KscLng, orbit);
            Assert.Null(first.Error);
            var land = first.Landing.LandingUt;
            Func<double, double> sun = ut => ut > land + 1000 && ut < land + 3200 ? 40 : -30;
            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: sun);
            Assert.Null(plan.Error);
            Assert.InRange(plan.Landing.LandingUt, land + 1000, land + 3200);
            Assert.InRange(plan.ErrorKm, 0, 5);
        }

        /// <summary>
        /// #970, flight m20261109-200745-96f6's figures with the sun as it moves (the hour angle turning with the body): find_landing_site's pass
        /// lands at 73 degrees, the pass deorbit flew about 84 degrees of turn before it. At the target that pass landed at dawn, about 22.8 degrees
        /// up: lit by the 20 degree rule, so it is flown; the craft then landed 17 degrees of longitude west, where the sun was 6.2. Had the sun
        /// there been under the horizon (the other side of noon, -10.6), the pass is skipped for the next lit one.
        /// </summary>
        [Theory]
        [InlineData(-67.1, true)]
        [InlineData(-100.6, false)]
        public void This_flights_dawn_pass_is_lit_at_the_target_and_a_dark_one_is_skipped(double hourAngleAtFirstLanding, bool firstIsLit)
        {
            var orbit = Circular(inclinationDeg: 2);
            var first = Find(KscLat, KscLng, orbit);
            var land = first.Landing.LandingUt;
            Func<double, double> sun = ut =>
            {
                var hour = (hourAngleAtFirstLanding + 360 * (ut - land) / ReentrySim.RotationPeriod) * Math.PI / 180;
                return Math.Asin(Math.Cos(KscLat * Math.PI / 180) * Math.Cos(hour)) * 180 / Math.PI;
            };
            Assert.Equal(firstIsLit, sun(land) >= LandingSiteSearch.MinSunElevationDeg);
            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: sun);
            Assert.Null(plan.Error);
            Assert.True(sun(plan.Landing.LandingUt) >= LandingSiteSearch.MinSunElevationDeg);
            if (firstIsLit) Assert.Equal(first.BurnUt, plan.BurnUt, 3);
            else Assert.True(plan.Landing.LandingUt > land + 1000, "a later, lit pass");
        }

        /// <summary>
        /// #974: the landings of three passes over KSC from the 2 degree orbit, each the cheapest from the last: the one deorbit's own rule flies
        /// (about 3,800 s), one about 12,300 s later in the same turn of the body, and one past that turn. A dearer pass (about 1,700 s) lands too.
        /// </summary>
        private static double[] ThreeLandings(Conic orbit)
        {
            var first = Find(KscLat, KscLng, orbit).Landing.LandingUt;
            var second = Find(KscLat, KscLng, orbit, earliest: first).Landing.LandingUt;
            var third = Find(KscLat, KscLng, orbit, earliest: second).Landing.LandingUt;
            var turnEnds = 120 + ReentrySim.RotationPeriod;
            Assert.True(second > first + 1000 && second < turnEnds - 1000 && third > turnEnds + 1000, first + " " + second + " " + third);
            return new[] { first, second, third };
        }

        /// <summary>A sun that is <paramref name="suns"/>[i] within 300 s of landing i, and <paramref name="elsewhere"/> between them.</summary>
        private static Func<double, double> SunAtPasses(double[] landings, double[] suns, double elsewhere)
        {
            return ut =>
            {
                for (var i = 0; i < landings.Length; i++)
                    if (Math.Abs(ut - landings[i]) < 300) return suns[i];
                return elsewhere;
            };
        }

        /// <summary>
        /// #974, flight m20261109-200745-96f6: find_landing_site's pass lands with the sun at 73 degrees, but an earlier pass at dawn, about 22.8 degrees,
        /// is lit too and costs the same. Given the site's pass, deorbit flies it, not the earliest lit one.
        /// </summary>
        [Fact]
        public void Given_find_landing_sites_pass_it_flies_that_pass_not_an_earlier_dawn_one()
        {
            var orbit = Circular(inclinationDeg: 2);
            var lands = ThreeLandings(orbit);
            var sun = SunAtPasses(lands, new[] { 22.8, 73, 50 }, 50);
            var earliestLit = Find(KscLat, KscLng, orbit, daylight: true, sunAt: sun);
            Assert.InRange(earliestLit.Landing.LandingUt, lands[0] - 300, lands[0] + 300);

            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: sun, site: new SitePass { LandingUt = lands[1], SunDeg = 73 });
            Assert.Null(plan.Error);
            Assert.True(plan.Note == null, plan.Note);
            Assert.InRange(plan.Landing.LandingUt, lands[1] - 300, lands[1] + 300);
            Assert.Equal(73, sun(plan.Landing.LandingUt));
            Assert.InRange(plan.ErrorKm, 0, 5);
        }

        /// <summary>
        /// #974: the site's pass can't be flown (here it lands past max_wait): the lit pass whose sun is nearest the one find_landing_site gave
        /// is flown, never one under the 20 degree bar, and the note says so.
        /// </summary>
        [Fact]
        public void When_the_sites_pass_cannot_be_flown_the_lit_pass_nearest_its_sun_is_flown_with_a_note()
        {
            var orbit = Circular(inclinationDeg: 2);
            var lands = ThreeLandings(orbit);
            var site = new SitePass { LandingUt = lands[2], SunDeg = 73 };

            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: SunAtPasses(lands, new[] { 22.8, 45, 73 }, -30), site: site);
            Assert.Null(plan.Error);
            Assert.InRange(plan.Landing.LandingUt, lands[1] - 300, lands[1] + 300);
            Assert.NotNull(plan.Note);
            Assert.Contains("find_landing_site", plan.Note);
            Assert.Contains("73", plan.Note);
            Assert.Contains("45", plan.Note);

            // With daylight off, a pass under the bar is not taken though its sun is nearer the site's.
            var low = new SitePass { LandingUt = lands[2], SunDeg = 21 };
            var barred = Find(KscLat, KscLng, orbit, daylight: false, sunAt: SunAtPasses(lands, new[] { 60, 19.5, 21 }, -30), site: low);
            Assert.Null(barred.Error);
            Assert.InRange(barred.Landing.LandingUt, lands[0] - 300, lands[0] + 300);
            Assert.NotNull(barred.Note);
        }

        /// <summary>
        /// #974's fix round: the site's pass can't be flown, and the lit pass whose sun is nearest its sun is over max_dv, while another lit pass fits.
        /// The fallback flies the one that fits, with the note, rather than refusing with the dearer one's error.
        /// </summary>
        [Fact]
        public void The_nearest_sun_fallback_skips_a_pass_over_budget_for_a_lit_one_that_fits()
        {
            // Half a degree north of the equator, off the 2 degree orbit's track at some passes: the second pass costs about 0.2 m/s more than the first.
            const double lat = 0.5;
            var orbit = Circular(inclinationDeg: 2);
            var first = Find(lat, KscLng, orbit);
            var second = Find(lat, KscLng, orbit, earliest: first.Landing.LandingUt);
            var third = Find(lat, KscLng, orbit, earliest: second.Landing.LandingUt);
            var lands = new[] { first.Landing.LandingUt, second.Landing.LandingUt, third.Landing.LandingUt };
            Assert.True(second.DeltaV > first.DeltaV + 0.1 && lands[1] > lands[0] + 1000 && lands[2] > 120 + ReentrySim.RotationPeriod, first.DeltaV + " " + second.DeltaV + " " + string.Join(" ", lands));
            // The site's pass lands past max_wait. The second pass's sun (70) is nearest the site's 73, the first's (40) is lit too; max_dv lets only the first fly.
            var site = new SitePass { LandingUt = lands[2], SunDeg = 73 };
            var plan = Find(lat, KscLng, orbit, daylight: true, sunAt: SunAtPasses(lands, new[] { 40.0, 70, 73 }, -30), site: site, maxDv: (first.DeltaV + second.DeltaV) / 2);
            Assert.True(plan.Error == null, plan.Error);
            Assert.InRange(plan.Landing.LandingUt, lands[0] - 300, lands[0] + 300);
            Assert.NotNull(plan.Note);
            Assert.Contains("40 degrees", plan.Note);
        }

        /// <summary>
        /// #974: with no site pass to follow, the earliest lit pass is flown as before. Through the block call: find_landing_site's last sites are
        /// followed only for a target that is one of them and a max_wait given as a number (its max_wait_s); "auto" or another target is today's rule.
        /// </summary>
        [Fact]
        public void With_no_site_pass_to_follow_the_earliest_lit_pass_is_flown()
        {
            var orbit = Circular(inclinationDeg: 2);
            var lands = ThreeLandings(orbit);
            var none = Find(KscLat, KscLng, orbit, daylight: true, sunAt: SunAtPasses(lands, new[] { 22.8, 73, 50 }, 50), site: null);
            Assert.Null(none.Note);
            Assert.InRange(none.Landing.LandingUt, lands[0] - 300, lands[0] + 300);

            // The block call, from the equatorial snapshot orbit, with find_landing_site's sites recorded for it.
            var snapshot = Snapshot();
            snapshot.DaylightLandingDefault = true;
            Func<System.Collections.Generic.Dictionary<string, object>, System.Collections.Generic.IDictionary<string, object>> call = ask =>
                BlockCalls.DeorbitBurn(ask, snapshot, new Sphere { Sun = 40 }, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), Recorded);
            var first = call(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", "auto", "dv_left", 5000.0));
            Assert.Equal("", first["error"]);
            var later = call(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", (double)first["landing_ut"], "max_wait", 21549.425, "dv_left", 5000.0));
            var siteLanding = (double)later["landing_ut"];
            var sites = new SitePasses();
            // Recorded for another vessel: the craft flying deorbit is a lander decoupled from the one the tool ran for (#974's fix round).
            sites.Record("mothership", "Kerbin", new[] { new LandingSite { Latitude = KscLat + 0.0004, Longitude = KscLng - 0.0004, PassUt = (double)later["burn_ut"], LandingUt = siteLanding, SunElevationDeg = 40 } });
            Assert.NotEqual("mothership", snapshot.Craft.VesselId);
            Recorded = sites;

            var followed = call(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0));
            Assert.Equal("", followed["error"]);
            Assert.InRange((double)followed["landing_ut"], siteLanding - 300, siteLanding + 300);
            Assert.False(followed.ContainsKey("pass_note"));
            // The site typed to 2 places (about 50 m off on Kerbin) is still that site.
            var twoPlaces = call(Ask("lat", Math.Round(KscLat, 2), "lng", Math.Round(KscLng, 2), "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0));
            Assert.InRange((double)twoPlaces["landing_ut"], siteLanding - 300, siteLanding + 300);
            Assert.False(twoPlaces.ContainsKey("pass_note"));
            var auto = call(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", "auto", "dv_left", 5000.0));
            Assert.Equal((double)first["burn_ut"], (double)auto["burn_ut"], 3);
            Assert.False(auto.ContainsKey("pass_note"));

            // No site within accuracy_km of the target: today's earliest pass, and with Daylight landing on and max_wait a number, a note saying so.
            var far = new SitePasses();
            far.Record("", "Kerbin", new[] { new LandingSite { Latitude = KscLat + 1, Longitude = KscLng, PassUt = (double)later["burn_ut"], LandingUt = siteLanding, SunElevationDeg = 40 } });
            Recorded = far;
            var elsewhere = call(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0));
            Assert.True((double)elsewhere["landing_ut"] < siteLanding - 1000, "another target: today's earliest pass");
            Assert.Contains("not one of find_landing_site's sites", (string)elsewhere["pass_note"]);
            snapshot.DaylightLandingDefault = false;
            Assert.False(call(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "dv_left", 5000.0)).ContainsKey("pass_note"));
        }

        private SitePasses Recorded = new SitePasses();

        [Fact]
        public void A_max_wait_of_auto_is_one_turn_of_the_body()
        {
            var snapshot = Snapshot();
            snapshot.DaylightLandingDefault = true;
            var dark = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", "auto", "dv_left", 5000.0), snapshot, new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Contains("in the next 6 h", (string)dark["error"]);
        }

        [Fact]
        public void With_daylight_on_the_sun_is_read_when_the_burn_lands_not_when_it_burns()
        {
            var orbit = Circular(inclinationDeg: 2);
            var first = Find(KscLat, KscLng, orbit);
            var land = first.Landing.LandingUt;
            Assert.True(land - first.BurnUt > 400, "the burn must be well before the landing for this to tell them apart");
            // Lit only about the first landing, not about its burn nor about any other pass.
            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: ut => Math.Abs(ut - land) < 300 ? 40 : -30);
            Assert.Null(plan.Error);
            Assert.Equal(first.BurnUt, plan.BurnUt, 0);
        }

        [Fact]
        public void With_daylight_on_a_walked_burn_that_lands_in_the_dark_is_not_flown()
        {
            var orbit = Circular(inclinationDeg: 2);
            var best = Find(KscLat, KscLng, orbit);
            var landed = best.Landing.LandingUt;
            // Everything is lit but the very landing the sun-blind search ends on, which is a walked burn (a refined one lands a hair off it).
            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: ut => ut == landed ? -30 : 40);
            Assert.Null(plan.Error);
            Assert.NotEqual(landed, plan.Landing.LandingUt);
        }

        [Fact]
        public void With_daylight_off_the_first_pass_is_flown_whatever_the_sun()
        {
            var orbit = Circular(inclinationDeg: 2);
            var first = Find(KscLat, KscLng, orbit);
            var plan = Find(KscLat, KscLng, orbit, daylight: false, sunAt: ut => -30);
            Assert.Null(plan.Error);
            Assert.Equal(first.BurnUt, plan.BurnUt, 3);
        }

        [Fact]
        public void With_daylight_on_and_no_lit_pass_in_the_window_it_fails_naming_the_best_sun_found()
        {
            var orbit = Circular(inclinationDeg: 2);
            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: ut => -12.34);
            Assert.NotNull(plan.Error);
            Assert.Contains("sun", plan.Error);
            Assert.Contains("-12.3", plan.Error);
            Assert.Contains("max_wait", plan.Error);
            Assert.Contains("site", plan.Error);
            Assert.Equal(-1, plan.BurnUt);
        }

        [Fact]
        public void With_daylight_on_a_sun_at_the_minimum_counts_as_lit()
        {
            var plan = Find(KscLat, KscLng, Circular(inclinationDeg: 2), daylight: true, sunAt: ut => LandingSiteSearch.MinSunElevationDeg);
            Assert.Null(plan.Error);
        }

        [Fact]
        public void With_daylight_on_a_sun_that_cannot_be_read_behaves_as_today()
        {
            var orbit = Circular(inclinationDeg: 2);
            var first = Find(KscLat, KscLng, orbit);
            var plan = Find(KscLat, KscLng, orbit, daylight: true, sunAt: ut => double.NaN);
            Assert.Null(plan.Error);
            Assert.Equal(first.BurnUt, plan.BurnUt, 3);
            var none = Find(KscLat, KscLng, orbit, daylight: true);
            Assert.Equal(first.BurnUt, none.BurnUt, 3);
        }

        [Fact]
        public void With_daylight_on_a_lit_pass_beyond_the_scan_budget_fails_rather_than_flying_an_earlier_dark_one()
        {
            var orbit = Circular(inclinationDeg: 2);
            var window = 20 * ReentrySim.RotationPeriod;
            var far = Find(KscLat, KscLng, orbit, maxWait: window);
            var searched = 120 + 12 * ReentrySim.RotationPeriod;
            var plan = Find(KscLat, KscLng, orbit, maxWait: window, daylight: true, sunAt: ut => ut < searched ? -30 : 40);
            Assert.NotNull(plan.Error);
            Assert.Equal(-1, plan.BurnUt);
        }

        [Fact]
        public void A_window_too_long_to_scan_is_searched_as_far_as_the_budget_and_the_failure_says_how_far()
        {
            // Twenty turns of the body, at 80 km: every pass would be over 200 refinements.
            var plan = Find(20, KscLng, maxWait: 20 * ReentrySim.RotationPeriod);
            var hours = Assert.Single(System.Text.RegularExpressions.Regex.Matches(plan.Error, @"in the next ([\d.]+) h")).Groups[1].Value;
            Assert.InRange(double.Parse(hours, CultureInfo.InvariantCulture), 6, 12);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_long_window_is_bounded_too()
        {
            // At the prediction budget: measured at about 250 ms in a Debug build (about 50 ms in Release).
            Timing.Within(500, () => Find(20, KscLng, maxWait: 20 * ReentrySim.RotationPeriod));
        }

        [Fact]
        public void A_burn_over_max_dv_fails_before_burning_with_its_cost_and_when()
        {
            var cheapest = Find(KscLat, KscLng, Eccentric());
            // The cheapest burn within accuracy, not the most accurate one's cost.
            Assert.InRange(cheapest.DeltaV, 300, 324);
            var ask = new DeorbitAsk
            {
                TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, AccuracyKm = 5,
                MaxDv = cheapest.DeltaV - 1, DeltaVLeft = 5000,
            };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), Eccentric(), ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame());
            Assert.StartsWith("the cheapest burn within accuracy_km needs " + Math.Round(cheapest.DeltaV, 1).ToString("0.#", CultureInfo.InvariantCulture) + " m/s (at UT "
                + Math.Round(cheapest.BurnUt).ToString("0", CultureInfo.InvariantCulture) + "), over max_dv ", plan.Error);
            Assert.NotNull(plan.Landing);
            ask.MaxDv = cheapest.DeltaV + 1;
            Assert.Null(DeorbitBurn.Find(ReentrySim.Kerbin(), Eccentric(), ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame()).Error);
        }

        [Fact]
        public void Auto_max_dv_is_the_delta_v_left_less_the_larger_of_50_m_s_or_10_percent_and_a_powered_landing_s_need()
        {
            Assert.Equal(450, DeorbitBurn.AutoMaxDv(500, 0), 9);
            Assert.Equal(450 - 120, DeorbitBurn.AutoMaxDv(500, 120), 9);
            Assert.Equal(1800, DeorbitBurn.AutoMaxDv(2000, 0), 9);
            Assert.Equal(-20, DeorbitBurn.AutoMaxDv(130, 100), 9);
        }

        [Fact]
        public void The_auto_max_dv_refuses_a_burn_that_eats_the_reserve()
        {
            var cheapest = Find(KscLat, KscLng, Eccentric());
            // Left = the burn plus 40 m/s: under the 50 m/s reserve, so the auto budget is short of the burn; plus 60 clears it.
            Assert.Contains("over max_dv", Auto(cheapest.DeltaV + 40, withChutes: true).Error);
            Assert.Null(Auto(cheapest.DeltaV + 60, withChutes: true).Error);
        }

        [Fact]
        public void A_craft_without_chutes_also_keeps_back_what_a_powered_landing_needs()
        {
            var cheapest = Find(KscLat, KscLng, Eccentric());
            var left = cheapest.DeltaV + 60;
            Assert.Null(Auto(left, withChutes: true).Error);
            var bare = Auto(left, withChutes: false);
            Assert.Contains("over max_dv", bare.Error);
            Assert.Contains("a powered landing", bare.Error);
        }

        [Fact]
        public void Stock_reporting_no_delta_v_left_requires_max_dv_rather_than_passing()
        {
            var ask = new DeorbitAsk { TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, AccuracyKm = 5 };
            Assert.Equal("stock can't say the delta-v left: give max_dv", DeorbitBurn.Find(ReentrySim.Kerbin(), Circular(), ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame()).Error);
            ask.MaxDv = 500;
            Assert.Null(DeorbitBurn.Find(ReentrySim.Kerbin(), Circular(), ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame()).Error);
        }

        [Theory]
        [InlineData(80, 500, "")]
        [InlineData(80, 80, "")]
        [InlineData(80, 0, "")]
        [InlineData(80, 50, "not enough delta-v: the burn needs 80 m/s, 50 m/s left")]
        [InlineData(402.44, 120.06, "not enough delta-v: the burn needs 402.4 m/s, 120.1 m/s left")]
        public void A_burn_over_the_delta_v_left_fails_before_burning(double dv, double left, string reason)
        {
            Assert.Equal(reason, DeorbitBurn.ShortOfDv(dv, left));
        }

        [Fact]
        public void The_block_call_takes_max_dv_and_dv_left()
        {
            var cheapest = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_dv", "auto", "dv_left", 5000.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", cheapest["error"]);
            var tight = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_dv", 10.0, "dv_left", 5000.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Contains("over max_dv 10", (string)tight["error"]);
            Assert.True(tight.ContainsKey("lat"));
            var unknown = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "max_dv", "auto", "dv_left", 0.0), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("stock can't say the delta-v left: give max_dv", unknown["error"]);
        }

        /// <summary>
        /// #832 in flight: the block call's snapshot is stamped by the Mission's view (<see cref="Missions.MissionView.Stamp"/>), which sets the later legs' delta-v from the legs after the one in hand,
        /// a Child Vessel's left out; the auto max_dv keeps it back. 5,000 m/s left clears the burn with the reserve and a powered landing, not with a 4,500 m/s leg after.
        /// </summary>
        [Fact]
        public void In_flight_the_block_call_keeps_back_the_later_legs_from_the_Mission_s_view()
        {
            Func<Missions.GameSnapshot, System.Collections.Generic.IDictionary<string, object>> deorbit = snapshot => BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_dv", "auto", "dv_left", 5000.0),
                snapshot, new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", deorbit(Snapshot())["error"]);
            var view = new Missions.MissionView
            {
                Stage = Missions.MissionStage.Running, CurrentSequence = 1, CraftName = "Ship",
                PlanLegs = new System.Collections.Generic.List<Missions.PlannedSequence>
                {
                    new Missions.PlannedSequence { Name = "Launch", DeltaV = 3400 }, new Missions.PlannedSequence { Name = "Deorbit", DeltaV = 100 },
                    new Missions.PlannedSequence { Name = "Hop", DeltaV = 4500, Vessel = "craft" }, new Missions.PlannedSequence { Name = "Probe", DeltaV = 900, Vessel = "Probe" },
                },
            };
            view.Settings.DaylightLanding = false;
            var stamped = Missions.MissionView.Stamp(Snapshot(), view);
            Assert.Equal(4500, stamped.LaterLegsDv);
            var held = deorbit(stamped);
            Assert.Contains("over max_dv", (string)held["error"]);
            Assert.Contains("and 4500 m/s for the later legs", (string)held["error"]);
        }

        /// <summary>The cost two burns must differ by not to be a tie, m/s.</summary>
        private const double DeorbitBurnTieDv = 1;

        /// <summary>A 100 km periapsis, by default 1,500 km apoapsis, equatorial Kerbin orbit that crosses longitude 0 at UT 0: burns cost very differently round it.</summary>
        private static Conic Eccentric(double apoapsis = 1500000)
        {
            var rp = ReentrySim.R + 100000;
            var ra = ReentrySim.R + apoapsis;
            var a = (rp + ra) / 2;
            var v = Math.Sqrt(ReentrySim.Mu * (2 / rp - 1 / a));
            return Conic.FromState(ReentrySim.Mu, new Vec3(rp, 0, 0), new Vec3(0, v, 0), 0);
        }

        [Fact]
        public void A_craft_with_no_chutes_that_keeps_no_engine_is_charged_no_powered_landing()
        {
            var left = Find(KscLat, KscLng, Eccentric()).DeltaV + 60;
            Assert.Contains("a powered landing", Auto(left, withChutes: false).Error);
            Assert.Null(Auto(left, withChutes: false, payer: new ClaudiaKerman.Core.Missions.DropBurn { KeepsEngine = false }).Error);
        }

        [Fact]
        public void Stages_below_stage_limit_can_t_be_spent_by_the_burn()
        {
            var cheapest = Find(KscLat, KscLng, Eccentric()).DeltaV;
            Assert.Null(Auto(cheapest + 100, withChutes: true).Error);
            Assert.Contains("not enough delta-v", Auto(cheapest + 100, withChutes: true, payer: new ClaudiaKerman.Core.Missions.DropBurn { HeldDv = 2000 }).Error);
        }

        [Fact]
        public void The_one_spend_rule_counts_the_kept_delta_v_the_dropped_share_and_the_engine()
        {
            Assert.Equal(DeorbitBurn.AutoMaxDv(900, 10, 20), DeorbitBurn.SpareDv(900, 10, 20, null), 9);
            Assert.Equal(DeorbitBurn.AutoMaxDv(900, 10, 20), DeorbitBurn.SpareDv(900, 10, 20, new ClaudiaKerman.Core.Missions.DropBurn()), 9);
            Assert.Equal(DeorbitBurn.AutoMaxDv(700, 10, 20), DeorbitBurn.SpareDv(900, 10, 20, new ClaudiaKerman.Core.Missions.DropBurn { HeldDv = 200 }), 9);
            Assert.Equal(DeorbitBurn.AutoMaxDv(900, 10, 20, 0.5), DeorbitBurn.SpareDv(900, 10, 20, new ClaudiaKerman.Core.Missions.DropBurn { KeptShare = 0.5 }), 9);
            Assert.Equal(DeorbitBurn.AutoMaxDv(900, 0, 20), DeorbitBurn.SpareDv(900, 10, 20, new ClaudiaKerman.Core.Missions.DropBurn { KeepsEngine = false }), 9);
            Assert.Equal(-1, DeorbitBurn.KeptDv(-1, new ClaudiaKerman.Core.Missions.DropBurn { HeldDv = 50 }), 9);
        }

        [Fact]
        public void The_over_budget_message_prints_the_kept_delta_v_not_stock_s()
        {
            var cheapest = Find(KscLat, KscLng, Eccentric()).DeltaV;
            var left = cheapest + 20 + 30;
            var plan = Auto(left, withChutes: true, payer: new ClaudiaKerman.Core.Missions.DropBurn { HeldDv = 30 });
            Assert.Contains("over max_dv", plan.Error);
            Assert.Contains("(auto: " + Math.Round(left - 30, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m/s left, less the reserve", plan.Error);
        }

        private static DeorbitPlan Auto(double dvLeft, bool withChutes, ClaudiaKerman.Core.Missions.DropBurn payer = null)
        {
            var ask = new DeorbitAsk { TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, AccuracyKm = 5, DeltaVLeft = dvLeft };
            var craft = new ReentrySim.Capsule();
            if (withChutes) craft.Chutes = new System.Collections.Generic.List<ReentryChute> { ReentrySim.Mk16() };
            ask.Payer = payer;
            return DeorbitBurn.Find(ReentrySim.Kerbin(), Eccentric(), ask, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
        }
    }
}
