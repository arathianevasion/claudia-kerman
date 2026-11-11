// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// A Course Correction aimed at a landing (#793): the landing's miss is an equality beside target_pe, through <see cref="BlockCalls.CorrectionBurn"/>. Each
    /// answer's landing is flown afresh through the patches and the predictor, not read from the solve.
    /// </summary>
    public sealed class AimedCorrectionTests
    {
        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
            RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };
        private const double Pe = 30000;
        private const double Ut = 100000;

        /// <summary>The body interface as the frame reads it: the ground turned with Kerbin since UT 0, as the game has it at <paramref name="ut"/>.</summary>
        internal sealed class FrameBody : PlanningBodyBase
        {
            private readonly double _turned;

            public FrameBody(double ut)
            {
                _turned = 360 / 21549.425 * ut;
            }

            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(Math.Atan2(y, x) * 180 / Math.PI - _turned);
                return true;
            }
        }

        /// <summary>A craft falling home from the Mun's distance, inclined by <paramref name="inc"/>: periapsis <paramref name="pe"/> m, reentry about <paramref name="toPeriapsis"/> s after the Game Snapshot's UT.</summary>
        internal static GameSnapshot Falling(double pe = Pe, double inc = 0, double toPeriapsis = 30000)
        {
            const double apR = 12600000;
            var peR = Kerbin.Radius + pe;
            var sma = (apR + peR) / 2;
            var ecc = (apR - peR) / (apR + peR);
            var meanAnomaly = 2 * Math.PI - toPeriapsis * Math.Sqrt(Kerbin.Gm / (sma * sma * sma));
            var snapshot = new GameSnapshot { Ut = Ut, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = sma, Eccentricity = ecc, Inclination = inc, Body = "Kerbin", Periapsis = pe, Apoapsis = apR - Kerbin.Radius, MeanAnomalyAtEpoch = meanAnomaly, Epoch = Ut,
            };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin });
            return snapshot;
        }

        private static Conic OrbitOf(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            return Conic.FromElements(Kerbin.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        private static ReentryPrediction Predict(GameSnapshot snapshot, Conic orbit, double from)
        {
            string problem;
            var frame = BodyFrame.Read(Kerbin, new FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            Assert.Null(problem);
            var craft = new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(Kerbin, orbit, from, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
            Assert.Null(landed.Error);
            return landed;
        }

        /// <summary>Where the craft comes down with no burn.</summary>
        internal static void Natural(GameSnapshot snapshot, out double lat, out double lng)
        {
            var landed = Predict(snapshot, OrbitOf(snapshot), snapshot.Ut);
            lat = landed.Latitude;
            lng = landed.Longitude;
        }

        private static IDictionary<string, object> Aim(GameSnapshot snapshot, double lat, double lng, double burnIn = 300, double dvLeft = -1, double pe = Pe, IDictionary<string, object> craft = null)
        {
            var ask = new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", pe }, { "earliest_ut", snapshot.Ut + burnIn }, { "latest_ut", snapshot.Ut + burnIn }, { "lat", lat }, { "lng", lng }, { "dv_left", dvLeft },
            };
            if (craft != null) ask["craft"] = craft;
            return BlockCalls.CorrectionBurn(ask, snapshot, new FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
        }

        private static double D(IDictionary<string, object> node, string key)
        {
            return Convert.ToDouble(node[key]);
        }

        /// <summary>The node flown afresh: its periapsis (m above sea level) and landing.</summary>
        private static ReentryPrediction Flown(GameSnapshot snapshot, IDictionary<string, object> node, out double pe)
        {
            var orbit = OrbitOf(snapshot);
            var ut = D(node, "ut");
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            var after = Conic.FromState(Kerbin.Gm, r, v + SingleShooting.FromNodeFrame(orbit, ut, D(node, "prograde"), D(node, "normal"), D(node, "radial")), ut);
            pe = after.PeR - Kerbin.Radius;
            return Predict(snapshot, after, ut);
        }

        private static double Km(double fromLat, double fromLng, double toLat, double toLng)
        {
            return LandingSiteSearch.ArcDegrees(fromLat, fromLng, toLat, toLng) * Math.PI / 180 * 600;
        }

        /// <summary>At Kerbin the aimed solve may move the periapsis within the corridor (#817), then holds it to the solver's tolerance.</summary>
        private static void InCorridor(double pe)
        {
            Assert.InRange(pe, Pe - CorrectionBurn.PeCorridor - 1000, Pe + CorrectionBurn.PeCorridor + 1000);
        }

        /// <summary>km of longitude on the equator.</summary>
        private const double KmPerDeg = 600 * Math.PI / 180;

        [Theory]
        [InlineData(-5)]
        [InlineData(10)]
        [InlineData(-30)]
        [InlineData(60)]
        public void The_landing_moves_to_the_target_with_the_periapsis_in_the_corridor(double eastKm)
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var target = lng + eastKm / KmPerDeg;
            var node = Aim(snapshot, lat, target);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            InCorridor(pe);
            Assert.InRange(Km(flown.Latitude, flown.Longitude, lat, target), 0, 1);
            Assert.InRange(D(node, "error_km"), 0, 1);
        }

        /// <summary>The miss is more than the burn may reach: it burns its most, the periapsis stays in the corridor, the landing moves that far, and the rest is reported, no failure.</summary>
        [Theory]
        [InlineData(1500)]
        [InlineData(-1500)]
        public void What_it_cannot_reach_is_reported_and_is_no_failure(double eastKm)
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var target = lng + eastKm / KmPerDeg;
            var node = Aim(snapshot, lat, target);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            var left = Km(flown.Latitude, flown.Longitude, lat, target);
            InCorridor(pe);
            // Never over its most by more than exec_node's residual (#817): an answer over it is brought back to it.
            Assert.InRange(D(node, "dv"), 40, CorrectionBurn.MostLandingDv + 0.5);
            Assert.InRange(left, 100, Math.Abs(eastKm) - 100);
            Assert.InRange(D(node, "error_km"), left - 0.01, left + 0.01);
        }

        /// <summary>The craft's delta-v left limits the burn below its most: with no more than the reserve (#790), nothing is spent on the landing.</summary>
        [Fact]
        public void The_burn_keeps_the_reserve()
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng + 500 / KmPerDeg, 300, 10);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 0, 3);
            Assert.True(D(node, "error_km") > 300);
        }

        /// <summary>#907: the delta-v a stage below stage_limit holds (ENTRYCRAFT's held_dv) isn't the craft's to spend: the correction spends by the kept delta-v.</summary>
        [Fact]
        public void The_burn_spends_only_the_kept_delta_v()
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var whole = Aim(snapshot, lat, lng + 500 / KmPerDeg, 300, 450);
            var kept = Aim(snapshot, lat, lng + 500 / KmPerDeg, 300, 450, craft: new Dictionary<string, object> { { "whole", 10.0 }, { "held_dv", 170.0 } });
            Assert.Equal("", kept["error"]);
            // A capsule with no chutes lands on its engines (about 230 m/s). 450 left: whole spends what the miss takes (about 54); 170 held leaves 280, less the 50 reserve and the landing: about nothing.
            Assert.True(D(whole, "dv") > 40, D(whole, "dv") + " " + whole["aim_note"]);
            Assert.InRange(D(kept, "dv"), 0, 20);
            Assert.True(D(kept, "dv") < D(whole, "dv") - 20, D(kept, "dv") + " " + kept["aim_note"]);
        }

        /// <summary>A craft with nothing left spends nothing on the landing: 0 is a reading, only a negative figure is unknown.</summary>
        [Fact]
        public void A_craft_with_no_delta_v_left_burns_nothing_for_the_landing()
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng + 50 / KmPerDeg, 300, 0);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 0, 0.1);
            Assert.InRange(D(node, "error_km"), 49, 51);
        }

        /// <summary>A path off target_pe comes back to it, the entry steep again, and still lands on the target.</summary>
        [Theory]
        [InlineData(38900)]
        [InlineData(26000)]
        public void A_path_off_target_pe_is_brought_to_it_and_to_the_target(double arrivalPe)
        {
            var snapshot = Falling(arrivalPe);
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var target = lng + 20 / KmPerDeg;
            var node = Aim(snapshot, lat, target);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            var left = Km(flown.Latitude, flown.Longitude, lat, target);
            InCorridor(pe);
            Assert.InRange(left, 0, 1);
        }

        /// <summary>So late the track hardly moves for any burn (0.04 km per m/s): the landing isn't burned for, and what is left is reported.</summary>
        [Fact]
        public void Too_late_to_move_the_landing_it_burns_nothing_for_it()
        {
            var snapshot = Falling(Pe, 0, 3000);
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var target = lng + 200 / KmPerDeg;
            var node = Aim(snapshot, lat, target, 120);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            var left = Km(flown.Latitude, flown.Longitude, lat, target);
            InCorridor(pe);
            Assert.InRange(D(node, "dv"), 0, 0.1);
            Assert.InRange(D(node, "error_km"), left - 0.01, left + 0.01);
        }

        /// <summary>A track inclined to the equator: the landing moves along the way it can, and what is across stays in error_km, no burn spent on it.</summary>
        [Fact]
        public void An_inclined_arrival_keeps_what_is_across_and_says_so()
        {
            var snapshot = Falling(Pe, 30);
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var target = lng + 20 / KmPerDeg;
            var node = Aim(snapshot, lat + 10 / KmPerDeg, target);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            var left = Km(flown.Latitude, flown.Longitude, lat + 10 / KmPerDeg, target);
            InCorridor(pe);
            Assert.True(left < Km(lat, lng, lat + 10 / KmPerDeg, target), "the landing moves toward the target");
            Assert.InRange(D(node, "error_km"), left - 0.01, left + 0.01);
            Assert.InRange(D(node, "dv"), 0, 10);
        }

        /// <summary>A target_pe above the air has no landing to read: the burn that holds it stands, as without a target, with no landing given.</summary>
        [Fact]
        public void With_no_landing_to_read_the_periapsis_burn_stands()
        {
            var snapshot = Falling(90000);
            var node = Aim(snapshot, 0, 0, 300, 0, 90000);
            Assert.Equal("", node["error"]);
            Assert.False(node.ContainsKey("error_km"));
            Assert.InRange(D(node, "dv"), 0, 0.1);
        }

        /// <summary>Nothing to move: an arrival already there burns about nothing, which the block doesn't fly.</summary>
        [Fact]
        public void An_arrival_already_there_burns_nothing()
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 0, 0.1);
            Assert.InRange(D(node, "error_km"), 0, 0.3);
        }

        /// <summary>#795: the plan-vs-flown line carries the landing target, so it no longer says none.</summary>
        [Fact]
        public void The_plan_log_names_the_landing_target()
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var line = (string)Aim(snapshot, 1.25, lng + 5 / KmPerDeg)["plan_log"];
            Assert.Contains("CORRECTIONBURN planned arrival", line);
            Assert.Contains("landing target 1.2500", line);
        }

        [Theory]
        [InlineData("lng")]
        [InlineData("lat")]
        public void A_landing_needs_both_coordinates(string missing)
        {
            var snapshot = Falling();
            var ask = new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", Pe }, { "earliest_ut", snapshot.Ut + 300 }, { "lat", 0.0 }, { "lng", 0.0 },
            };
            ask.Remove(missing);
            var node = BlockCalls.CorrectionBurn(ask, snapshot, new FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("found no correction: lat and lng must both be numbers", node["error"]);
        }

        [Fact]
        public void A_landing_needs_the_air_and_the_craft_and_takes_no_arrival_inclination()
        {
            var snapshot = Falling();
            var ask = new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", Pe }, { "earliest_ut", snapshot.Ut + 300 }, { "lat", 0.0 }, { "lng", 0.0 },
            };
            Assert.Equal("found no correction: the landing needs the planet's air and the craft", BlockCalls.CorrectionBurn(ask, snapshot)["error"]);
            ask["target_inc"] = 5.0;
            Assert.Equal("found no correction: a landing target takes no arrival inclination or plane",
                BlockCalls.CorrectionBurn(ask, snapshot, new FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule())["error"]);
        }

        /// <summary>A target that is no body is the solve's to say, as without a landing, though the adapter has no air to give for it.</summary>
        [Fact]
        public void A_landing_on_no_such_body_says_so()
        {
            var node = BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Nowhere" }, { "target_pe", Pe }, { "earliest_ut", Ut + 300 }, { "lat", 0.0 }, { "lng", 0.0 },
            }, Falling());
            Assert.Equal("found no correction: there is no body named \"Nowhere\"; the bodies are Sun, Kerbin", node["error"]);
        }

        /// <summary>Without a landing the ask and its answer are as before: no landing keys, the log says none.</summary>
        [Fact]
        public void Without_a_landing_nothing_changes()
        {
            var snapshot = Falling(33000);
            var node = BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", Pe }, { "earliest_ut", snapshot.Ut + 300 },
            }, snapshot);
            Assert.Equal("", node["error"]);
            Assert.False(node.ContainsKey("error_km"));
            Assert.Contains("landing target none", (string)node["plan_log"]);
            Assert.InRange(D(node, "pe_predicted"), Pe - 1000, Pe + 1000);
        }

        // ---- a return from another planet: the same landing mode from far out and near Kerbin ----

        private static ReturnLanding LandingOn(double lat, double lng, double ut)
        {
            string problem;
            var frame = BodyFrame.Read(Kerbin, new FrameBody(ut), ut, out problem);
            Assert.Null(problem);
            return new ReturnLanding { Latitude = lat, Longitude = lng, Air = new ReentrySim.Atmosphere(), Craft = new ReentrySim.Capsule(), Frame = frame };
        }

        /// <summary>An arrival at Kerbin from another planet: round the star until <paramref name="daysOut"/> days after <paramref name="now"/>, coming in with about 1.2 km/s to spare.</summary>
        private static Conic InboundRoundTheSun(BodySystem system, double now, double daysOut)
        {
            var enter = now + daysOut * 21600;
            var kerbin = system.OrbitOf(Kerbin);
            Vec3 r, v;
            kerbin.State(enter, out r, out v);
            var up = r.Normalized;
            var ahead = v.Normalized;
            var soi = Kerbin.SoiRadius * 0.98;
            // At the sphere's edge, off to the side so the path passes the planet rather than through it, and falling toward it.
            var at = r + up * (-soi) + ahead * (soi * 0.03);
            var arrive = v + up * 1200;
            return Conic.FromState(Sun.Gm, at, arrive, enter);
        }

        [Fact]
        public void A_far_out_correction_round_the_star_aims_a_landing_with_the_periapsis_in_the_corridor()
        {
            var system = new BodySystem(new[] { Sun, Kerbin });
            const double now = 1000000;
            var orbit = InboundRoundTheSun(system, now, 10);
            var sun = system.Find("Sun");
            var ask = new CorrectionAsk { Target = "Kerbin", TargetPe = Pe, EarliestUt = now + 300, LatestUt = now + 300 };
            var plain = CorrectionBurn.Find(system, sun, orbit, now, ask);
            Assert.Null(plain.Error);
            var landed = LandingOn(0, 0, now);
            var first = Natural(system, sun, orbit, plain, now, landed);
            landed.Latitude = first.Latitude;
            landed.Longitude = LandingSiteSearch.Wrap180(first.Longitude + 100 / KmPerDeg);
            ask.Landing = landed;
            var plan = CorrectionBurn.Find(system, sun, orbit, now, ask);
            Assert.Null(plan.Error);
            InCorridor(plan.PePredicted);
            Assert.NotNull(plan.Landing);
            Assert.InRange(plan.ErrorKm, 0, 1);
            Assert.InRange(plan.DeltaV, 0, CorrectionBurn.MostLandingDv * 1.1);
        }

        [Fact]
        public void A_near_kerbin_correction_on_a_hyperbola_aims_a_landing_with_the_periapsis_in_the_corridor()
        {
            var system = new BodySystem(new[] { Sun, Kerbin });
            const double now = 1000000;
            // Coming in with 1.2 km/s to spare, an hour and a bit from periapsis.
            var vInf = 1200.0;
            var peR = Kerbin.Radius + Pe;
            var sma = -Kerbin.Gm / (vInf * vInf);
            var ecc = 1 - peR / sma;
            var orbit = Conic.FromElements(Kerbin.Gm, sma, ecc, 0, 0, 0, -4000 * Math.Sqrt(Kerbin.Gm / -(sma * sma * sma)), now);
            var kerbin = system.Find("Kerbin");
            var ask = new CorrectionAsk { Target = "Kerbin", TargetPe = Pe, EarliestUt = now + 120, LatestUt = now + 120 };
            var plain = CorrectionBurn.Find(system, kerbin, orbit, now, ask);
            Assert.Null(plain.Error);
            var landed = LandingOn(0, 0, now);
            var first = Natural(system, kerbin, orbit, plain, now, landed);
            landed.Latitude = first.Latitude;
            landed.Longitude = LandingSiteSearch.Wrap180(first.Longitude + 10 / KmPerDeg);
            ask.Landing = landed;
            var plan = CorrectionBurn.Find(system, kerbin, orbit, now, ask);
            Assert.Null(plan.Error);
            InCorridor(plan.PePredicted);
            Assert.NotNull(plan.Landing);
            Assert.True(plan.ErrorKm < 10, "the landing moves toward the target");
        }

        /// <summary>Where <paramref name="plain"/>'s burn comes down, by the predictor.</summary>
        private static ReentryPrediction Natural(BodySystem system, SnapshotBody from, Conic orbit, CorrectionPlan plain, double now, ReturnLanding landing)
        {
            var burn = SingleShooting.FromNodeFrame(orbit, plain.Ut, plain.Prograde, plain.Normal, plain.Radial);
            for (var patches = 1; patches <= 4; patches++)
            {
                var arrival = SingleShooting.Arrive(system, from, orbit, plain.Ut, burn, Kerbin, patches);
                if (arrival == null) continue;
                var landed = ReentryPredictor.Predict(Kerbin, arrival.Orbit, arrival.StartUt, 0, landing.Craft.MassKg, landing.Air, landing.Craft, landing.Frame);
                Assert.Null(landed.Error);
                return landed;
            }
            throw new InvalidOperationException("the burn does not reach Kerbin");
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void The_whole_aimed_solve_takes_under_100_ms()
        {
            var snapshot = Falling(33000);
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            Timing.Within(100, () => Assert.Equal("", Aim(snapshot, lat, lng + 40 / KmPerDeg)["error"]));
        }
    }
}
