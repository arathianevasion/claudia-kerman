// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// A Course Correction aimed at a landing (#793): the landing's miss is an equality beside target_pe, through <see cref="BlockCalls.CorrectionBurn"/>. Each
    /// answer's landing is flown afresh through the patches and the predictor, not read from the solve.
    /// </summary>
    public sealed class AimedCorrectionTests
    {
        private readonly ITestOutputHelper _out;

        public AimedCorrectionTests(ITestOutputHelper output)
        {
            _out = output;
        }

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

        private static IDictionary<string, object> Aim(GameSnapshot snapshot, double lat, double lng, double burnIn = 300, double dvLeft = 0, double pe = Pe)
        {
            return BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", pe }, { "earliest_ut", snapshot.Ut + burnIn }, { "latest_ut", snapshot.Ut + burnIn }, { "lat", lat }, { "lng", lng }, { "dv_left", dvLeft },
            }, snapshot, new FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
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

        /// <summary>km of longitude on the equator.</summary>
        private const double KmPerDeg = 600 * Math.PI / 180;

        [Theory]
        [InlineData(-5)]
        [InlineData(10)]
        [InlineData(-30)]
        [InlineData(60)]
        public void The_landing_moves_to_the_target_with_the_periapsis_held(double eastKm)
        {
            var snapshot = Falling();
            double lat, lng;
            Natural(snapshot, out lat, out lng);
            var target = lng + eastKm / KmPerDeg;
            var node = Aim(snapshot, lat, target);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            _out.WriteLine("east " + eastKm + ": dv " + D(node, "dv") + " error_km " + D(node, "error_km") + " flown km " + Km(flown.Latitude, flown.Longitude, lat, target) + " pe " + pe + " lat " + flown.Latitude);
            Assert.InRange(pe, Pe - 1000, Pe + 1000);
            Assert.InRange(Km(flown.Latitude, flown.Longitude, lat, target), 0, 1);
            Assert.InRange(D(node, "error_km"), 0, 1);
        }
    }
}
