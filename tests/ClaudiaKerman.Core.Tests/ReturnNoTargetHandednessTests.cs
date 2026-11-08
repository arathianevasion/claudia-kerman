// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #888: a <c>return</c> with no target took the landing's turn direction as 1 (#802's handedness). It now reads it from the planet's frame, as the
    /// aimed return does; with a target the landing's own frame is still the one used.
    /// </summary>
    public sealed class ReturnNoTargetHandednessTests
    {
        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
            RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };

        private static GameSnapshot Orbiting(double phase, double lan)
        {
            var mun = new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = phase };
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = mun.Radius + 30000, Eccentricity = 0, Inclination = 66.43, Lan = lan, Body = "Mun", Periapsis = 30000, Apoapsis = 30000, Epoch = 1000,
            };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, mun });
            return snapshot;
        }

        private sealed class FrameBody : PlanningBodyBase
        {
            private readonly double _handed;
            public FrameBody(double handed) { _handed = handed; }
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(_handed * Math.Atan2(y, x) * 180 / Math.PI);
                return true;
            }
        }

        private static IDictionary<string, object> NoTarget(GameSnapshot snapshot, IPlanningBody body)
        {
            var input = new Dictionary<string, object> { { "target_pe", 35000.0 }, { "earliest_ut", snapshot.Ut }, { "cheap_window", true } };
            return BlockCalls.ReturnBurn(input, snapshot, body);
        }

        private static ReturnPlan Direct(GameSnapshot snapshot, double handed)
        {
            return ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, CheapWindow = true, Handed = handed });
        }

        private static double Ut(IDictionary<string, object> result)
        {
            return Convert.ToDouble(result["ut"], CultureInfo.InvariantCulture);
        }

        private static ReturnAim.Search Start(GameSnapshot snapshot, ReturnAsk ask)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            Assert.NotNull(setup);
            return ReturnAim.Search.Start(setup.System, setup.Body, setup.Start, setup.Now, ask);
        }

        /// <summary>
        /// A no-target search with the planet's frame at handed -1 spans the turn a target with that handedness does (5.2 h at the Mun, not 7.1 h), and the
        /// block's no-target answer is the search's with the handedness it read.
        /// </summary>
        [Fact]
        public void A_no_target_return_solves_with_the_planets_frame_handedness()
        {
            var snapshot = Orbiting(1.7, 90);
            string problem;
            var mirroredLanding = ReturnAim.LandingOn(snapshot, -0.0972, -74.5577, 0, 0, new FrameBody(-1), new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), out problem);
            var viaTarget = Start(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, Landing = mirroredLanding });
            var noTarget = Start(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, CheapWindow = true, Handed = ReturnAim.HandedOn(Kerbin, snapshot.Ut, new FrameBody(-1)) });
            var before = Start(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, CheapWindow = true });
            Assert.Equal(viaTarget.Turn, noTarget.Turn, 6);
            Assert.InRange(noTarget.Turn / 3600, 5.1, 5.3);
            Assert.InRange(before.Turn / 3600, 7.0, 7.2);
            foreach (var handed in new[] { 1.0, -1 })
            {
                var direct = Direct(snapshot, handed);
                Assert.Equal(direct.Ut, Ut(NoTarget(snapshot, new FrameBody(handed))), 6);
            }
        }

        [Fact]
        public void The_handedness_of_the_planets_frame_is_read_without_a_landing()
        {
            var snapshot = Orbiting(1.7, 0);
            Assert.Equal(-1, ReturnAim.HandedOn(Kerbin, snapshot.Ut, new FrameBody(-1)));
            Assert.Equal(1, ReturnAim.HandedOn(Kerbin, snapshot.Ut, new FrameBody(1)));
            Assert.Equal(1, ReturnAim.HandedOn(Kerbin, snapshot.Ut, null));
        }

        /// <summary>With a target the landing's frame decides; the ask's Handed is not read.</summary>
        [Fact]
        public void A_return_with_a_target_ignores_the_asks_handedness()
        {
            var snapshot = Orbiting(1.7, 90);
            foreach (var handed in new[] { 1.0, -1 })
            {
                string problem;
                var landing = ReturnAim.LandingOn(snapshot, -0.0972, -74.5577, 0, 0, new FrameBody(handed), new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), out problem);
                Assert.NotNull(landing);
                var a = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 30000, EarliestUt = snapshot.Ut, Landing = landing, Handed = 1 });
                var b = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 30000, EarliestUt = snapshot.Ut, Landing = landing, Handed = -1 });
                Assert.Equal(a.Ut, b.Ut, 6);
                Assert.Equal(a.DeltaV, b.DeltaV, 6);
            }
        }
    }
}
