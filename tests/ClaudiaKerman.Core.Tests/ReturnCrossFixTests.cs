// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #790's amendment (#797): off Minmus's node a timed return lands with the moon's latitude still in it, 20 to 60 km across the track. With delta-v over the reserve the Course
    /// Correction takes that out and predicts within a few km of KSC; without, the miss stands and is reported. The ejection's own time is never moved for it, so no wait for the node.
    /// Both burns go through <see cref="BlockCalls"/>, as the block flies them.
    /// </summary>
    public sealed class ReturnCrossFixTests
    {
        private const double KscLat = -0.0972;
        private const double KscLng = -74.5577;

        private readonly ITestOutputHelper _out;

        public ReturnCrossFixTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48, RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };

        private static SnapshotBody Minmus(double phase)
        {
            return new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = phase,
            };
        }

        private static GameSnapshot AtMinmus(double phase, double ut = 1000)
        {
            var moon = Minmus(phase);
            var snapshot = new GameSnapshot { Ut = ut, Situation = "ORBITING", Body = moon.Name };
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = moon.Radius + 20000, Eccentricity = 0, Body = moon.Name, Periapsis = 20000, Apoapsis = 20000, Epoch = ut };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, moon });
            return snapshot;
        }

        private sealed class FrameBody : PlanningBodyBase
        {
            private readonly double _turned;

            public FrameBody(double ut)
            {
                _turned = 360 * ut / Kerbin.RotationPeriod;
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

        /// <summary>A capsule with one chute, for a craft that needn't land on its engines.</summary>
        private sealed class ChutedCapsule : IReentryCraft
        {
            private readonly ReentrySim.Capsule _capsule = new ReentrySim.Capsule();
            public double MassKg { get { return _capsule.MassKg; } }
            public double DragArea(double mach, double densitySpeed) { return _capsule.DragArea(mach, densitySpeed); }
            public double LiftArea(double mach, double angleOfAttack) { return _capsule.LiftArea(mach, angleOfAttack); }
            public IList<ReentryChute> Chutes { get { return new[] { new ReentryChute() }; } }
            public IReentryCraft AfterShield { get { return null; } }
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        /// <summary>#792's timed ejection, as the block asks it.</summary>
        private static IDictionary<string, object> Ejection(GameSnapshot snapshot)
        {
            var input = new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut }, { "lat", KscLat }, { "lng", KscLng } };
            return BlockCalls.ReturnBurn(input, snapshot, new FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
        }

        /// <summary>The Game Snapshot on the ejection's path round Kerbin, halfway from the moon's edge to periapsis.</summary>
        private static GameSnapshot Halfway(GameSnapshot snapshot, IDictionary<string, object> ejection)
        {
            var system = new BodySystem(snapshot.Bodies);
            var moon = system.Find("Minmus");
            var orbit = Conic.FromElements(moon.Gm, snapshot.Orbit.SemiMajorAxis, 0, 0, 0, 0, 0, snapshot.Orbit.Epoch);
            var ut = D(ejection, "ut");
            var arrival = SingleShooting.Arrive(system, moon, orbit, ut, SingleShooting.FromNodeFrame(orbit, ut, D(ejection, "prograde"), D(ejection, "normal"), D(ejection, "radial")), system.Find("Kerbin"), 2);
            Assert.NotNull(arrival);
            var mid = (arrival.StartUt + arrival.Orbit.TimeAtTrue(0, arrival.StartUt).Value) / 2;
            var path = arrival.Orbit;
            Vec3 r, v;
            path.State(mid, out r, out v);
            var h = Vec3.Cross(r, v);
            var node = Math.Abs(h.X) + Math.Abs(h.Y) > 1e-6 ? new Vec3(-h.Y, h.X, 0) : new Vec3(1, 0, 0);
            var anomaly = path.TrueAnomaly(mid);
            var eccentric = 2 * Math.Atan2(Math.Sqrt(1 - path.E) * Math.Sin(anomaly / 2), Math.Sqrt(1 + path.E) * Math.Cos(anomaly / 2));
            var coasting = snapshot.At(mid);
            coasting.Body = "Kerbin";
            coasting.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = path.A, Eccentricity = path.E, Inclination = Math.Acos(h.Z / h.Length) * 180 / Math.PI, Lan = Math.Atan2(node.Y, node.X) * 180 / Math.PI,
                ArgumentOfPeriapsis = Math.Atan2(Vec3.Dot(path.P, Vec3.Cross(h.Normalized, node.Normalized)), Vec3.Dot(path.P, node.Normalized)) * 180 / Math.PI,
                MeanAnomalyAtEpoch = eccentric - path.E * Math.Sin(eccentric), Epoch = mid, Body = "Kerbin", Periapsis = path.PeR - Kerbin.Radius, Apoapsis = 2 * path.A - path.PeR - Kerbin.Radius,
            };
            return coasting;
        }

        /// <summary>The Course Correction 300 s on, as the block asks it.</summary>
        private static IDictionary<string, object> Correction(GameSnapshot coasting, double dvLeft, IReentryCraft craft = null)
        {
            return BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", 30000.0 }, { "earliest_ut", coasting.Ut + 300 }, { "latest_ut", coasting.Ut + 300 }, { "lat", KscLat }, { "lng", KscLng }, { "dv_left", dvLeft },
            }, coasting, new FrameBody(coasting.Ut), new ReentrySim.Atmosphere(), craft ?? new ChutedCapsule());
        }

        [Theory]
        [InlineData(0.9)]
        [InlineData(1.7)]
        [InlineData(3.0)]
        [InlineData(4.5)]
        [InlineData(5.2)]
        [InlineData(6.1)]
        public void A_minmus_return_with_spare_delta_v_flies_the_fix_and_predicts_within_a_few_km_of_ksc(double phase)
        {
            var snapshot = AtMinmus(phase);
            var ejection = Ejection(snapshot);
            Assert.InRange(D(ejection, "error_km"), 20, 70);
            var fix = Correction(Halfway(snapshot, ejection), 1000);
            _out.WriteLine("phase {0}: {1:0.0} km; {2}", phase, D(ejection, "error_km"), fix["aim_note"]);
            Assert.Equal("", fix["error"]);
            Assert.Equal(CorrectionBurn.FixFlown, fix["cross_fix"]);
            Assert.InRange(D(fix, "error_km"), 0, 3);
            Assert.InRange(D(fix, "pe_predicted"), 29000, 31000);
            Assert.InRange(D(fix, "dv"), 5, 60);
            Assert.Equal(D(fix, "dv"), D(fix, "cross_dv"));
            Assert.Contains("fix flown", (string)fix["aim_note"]);
        }

        /// <summary>The ejection's window stands: no wait for the node, whether the fix flies or not. The Due Time the Mission works out for it stays right.</summary>
        [Fact]
        public void The_fix_does_not_touch_the_ejection()
        {
            var snapshot = AtMinmus(0.9);
            var ejection = Ejection(snapshot);
            Assert.InRange(D(ejection, "ut"), snapshot.Ut, snapshot.Ut + 6.2 * 3600 + 200);
            Assert.False(ejection.ContainsKey("cross_fix"));
        }

        [Theory]
        [InlineData(0.9)]
        [InlineData(3.0)]
        [InlineData(4.5)]
        public void A_minmus_return_without_spare_delta_v_accepts_the_miss_and_reports_it(double phase)
        {
            var snapshot = AtMinmus(phase);
            var coasting = Halfway(snapshot, Ejection(snapshot));
            var fixDv = D(Correction(coasting, 1000), "dv");
            var miss = Correction(coasting, fixDv + 49);
            _out.WriteLine("{0}", miss["aim_note"]);
            Assert.Equal("", miss["error"]);
            Assert.Equal(CorrectionBurn.MissAccepted, miss["cross_fix"]);
            Assert.InRange(D(miss, "error_km"), 15, 70);
            Assert.Equal(fixDv, D(miss, "cross_dv"), 6);
            Assert.Contains("miss accepted", (string)miss["aim_note"]);
            Assert.Contains("reserve", (string)miss["aim_note"]);
        }

        [Fact]
        public void Stock_saying_nothing_of_the_delta_v_left_accepts_the_miss()
        {
            var snapshot = AtMinmus(3.0);
            var miss = Correction(Halfway(snapshot, Ejection(snapshot)), -1);
            Assert.Equal(CorrectionBurn.MissAccepted, miss["cross_fix"]);
            Assert.Contains("can't say", (string)miss["aim_note"]);
        }

        /// <summary>The reserve binds: a fix the delta-v left covers, but not with the larger of 50 m/s and 10% kept back, stays unflown.</summary>
        [Fact]
        public void The_reserve_is_not_spent_on_the_fix()
        {
            var snapshot = AtMinmus(3.0);
            var coasting = Halfway(snapshot, Ejection(snapshot));
            var dv = D(Correction(coasting, 1000), "dv");
            Assert.Equal(CorrectionBurn.MissAccepted, Correction(coasting, dv + 49)["cross_fix"]);
            Assert.Equal(CorrectionBurn.FixFlown, Correction(coasting, dv + 51)["cross_fix"]);
            Assert.Equal(450.0, DeorbitBurn.AutoMaxDv(500, 0));
        }

        /// <summary>The along-track burn after an accepted miss doesn't spend the reserve either.</summary>
        [Fact]
        public void An_accepted_miss_keeps_the_reserve_on_the_along_track_burn_too()
        {
            var snapshot = AtMinmus(3.0);
            var coasting = Halfway(snapshot, Ejection(snapshot));
            foreach (var left in new[] { 52.0, 60.0, 70.0 })
            {
                var miss = Correction(coasting, left);
                Assert.InRange(D(miss, "dv"), 0, Math.Max(left - 50, 3));
            }
        }

        /// <summary>A craft with no chutes keeps its powered landing's need too: what is spare for the chuted one isn't for it.</summary>
        [Fact]
        public void A_chuteless_craft_keeps_what_its_powered_landing_needs()
        {
            var snapshot = AtMinmus(3.0);
            var coasting = Halfway(snapshot, Ejection(snapshot));
            var left = D(Correction(coasting, 1000), "dv") + 51;
            Assert.Equal(CorrectionBurn.FixFlown, Correction(coasting, left)["cross_fix"]);
            Assert.Equal(CorrectionBurn.MissAccepted, Correction(coasting, left, new ReentrySim.Capsule())["cross_fix"]);
        }

        /// <summary>A path already on the target's latitude has nothing across the track: the correction is #793's, and says nothing.</summary>
        [Fact]
        public void A_return_with_nothing_across_the_track_says_nothing()
        {
            var snapshot = AtMinmus(5.5);
            snapshot.Bodies[2].Inclination = 0;
            var fix = Correction(Halfway(snapshot, Ejection(snapshot)), 1000);
            Assert.Equal("", fix["cross_fix"]);
            Assert.Equal("", fix["aim_note"]);
        }
    }
}
