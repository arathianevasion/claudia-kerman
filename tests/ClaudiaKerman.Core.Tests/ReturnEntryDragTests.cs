// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #924, step 1: which input put <c>return</c>'s aimed landing 115° off (Mission m20261108-180219-f40a). The two REENTRYIMPACT lines rebuilt: Pe 29,912 m, Ap
    /// 10,762,541 m, Pe at UT 147485117.3, inclination 3.2°. <c>return</c> read 0.11 m² on 1.455 t and landed it at -73.8417 (8 km from KSC); <c>reentry</c> read 2.24 m²
    /// on 1.415 t and landed it at 170.9142; the capsule splashed down at 169.9957. A frame or rotation error is ruled out, and the Mun's place is not an input: same
    /// orbit, same frame, only the drag differs.
    /// </summary>
    public sealed class ReturnEntryDragTests
    {
        internal const double PeUt = 147485117.3;
        private const double ReentryLanding = 170.9142;
        private const double ReturnLanding = -73.8417;
        /// <summary>The logged latitude of the 2.24 m² landing puts the periapsis here on the 3.2° orbit (the node's place goes into the frame's offset).</summary>
        private const double ArgPe = -141.5;

        private readonly ITestOutputHelper _out;

        public ReturnEntryDragTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>A drag area that is the same at every Mach, the other end of the shapes the real occluded craft's curve can have.</summary>
        private sealed class Flat : IReentryCraft
        {
            private readonly double _area;

            public Flat(double area)
            {
                _area = area;
            }

            public double MassKg { get { return 1455; } }
            public double DragArea(double mach, double densitySpeed) { return _area; }
            public double LiftArea(double mach, double angleOfAttack) { return 0; }
            public IList<ReentryChute> Chutes { get; } = new List<ReentryChute> { ReentrySim.Mk16() };
            public IReentryCraft AfterShield { get { return null; } }
        }

        internal static Conic LoggedArrival()
        {
            const double rp = ReentrySim.R + 29912, ra = ReentrySim.R + 10762541;
            return Conic.FromElements(ReentrySim.Mu, (rp + ra) / 2, (ra - rp) / (ra + rp), 3.2, 0, ArgPe, 0, PeUt);
        }

        internal static BodyFrame LoggedFrame(double lonOffset = 0, double ut = PeUt)
        {
            return new BodyFrame { LonOffset = lonOffset, Handed = 1, Ut = ut, RotationPeriod = ReentrySim.RotationPeriod };
        }

        internal static ReentryPrediction Fly(IReentryCraft craft, double massKg, BodyFrame frame)
        {
            return ReentryPredictor.Predict(ReentrySim.Kerbin(), LoggedArrival(), PeUt - 1500, 0, massKg, new ReentrySim.Atmosphere(), craft, frame);
        }

        /// <summary>The research's capsule scaled to read <paramref name="area"/> at Mach 2 as CraftLine logs it (ρv 1000).</summary>
        internal static ReentrySim.Capsule Capsule(double area)
        {
            var craft = new ReentrySim.Capsule();
            craft.Scale = area / craft.DragArea(2, 1000);
            craft.Chutes.Add(ReentrySim.Mk16());
            return craft;
        }

        /// <summary>The frame's offset that puts the 2.24 m² landing where <c>reentry</c> logged it: the orbit's longitude is not in the log's table.</summary>
        internal static double Offset()
        {
            return LandingSiteSearch.Wrap180(ReentryLanding - Fly(Capsule(2.24), 1415, LoggedFrame()).Longitude);
        }

        /// <summary>Over drag areas round 0.11 m², the one that lands nearest the logged -73.84: its area, and how far off.</summary>
        private static double[] Nearest(Func<double, IReentryCraft> craft)
        {
            var frame = LoggedFrame(Offset());
            var best = new[] { 0.0, 360.0 };
            Action<double> tryArea = area =>
            {
                var landed = Fly(craft(area), 1455, frame);
                if (landed.Error != null) return;
                var off = Math.Abs(LandingSiteSearch.Wrap180(landed.Longitude - ReturnLanding));
                if (off < best[1]) best = new[] { area, off };
            };
            for (var area = 0.075; area <= 0.16; area += 0.0005) tryArea(area);
            // The landing sweeps fast here (about 13° per 0.001 m²): look closer round the best.
            var centre = best[0];
            for (var area = centre - 0.0005; area <= centre + 0.0005; area += 0.00002) tryArea(area);
            return best;
        }

        [Fact]
        public void A_drag_area_round_return_s_0_11_m2_lands_at_the_logged_minus_73_8_on_the_same_orbit_and_frame()
        {
            var capsule = Nearest(a => Capsule(a));
            var flat = Nearest(a => new Flat(a));
            _out.WriteLine("capsule {0:0.0000} m² off {1:0.00}°; flat {2:0.0000} m² off {3:0.00}°", capsule[0], capsule[1], flat[0], flat[1]);
            Assert.True(capsule[1] < 3, "capsule shape: " + capsule[1]);
            Assert.True(flat[1] < 3, "flat shape: " + flat[1]);
            // The two ends of the shapes the real occluded craft's curve can have bracket the logged figure, and the same craft nearby in area lands round the planet.
            Assert.InRange(flat[0], 0.08, 0.11);
            Assert.InRange(capsule[0], 0.11, 0.16);
        }

        [Fact]
        public void The_capsule_at_reentry_s_2_24_m2_comes_down_just_past_periapsis_where_0_11_m2_flies_on_round_the_planet()
        {
            var frame = LoggedFrame(Offset());
            var big = Fly(Capsule(2.24), 1415, frame);
            var small = Fly(Capsule(0.11), 1455, frame);
            _out.WriteLine("2.24 m²: {0:0.00}, {1:0.00} at Pe + {2:0} s; 0.11 m²: {3:0.00}, {4:0.00} at Pe + {5:0} s", big.Latitude, big.Longitude, big.LandingUt - PeUt, small.Latitude, small.Longitude, small.LandingUt - PeUt);
            Assert.Null(big.Error);
            Assert.Null(small.Error);
            Assert.InRange(big.LandingUt - PeUt, 100, 400);
            Assert.True(small.LandingUt - PeUt > 1800, "0.11 m² should skim on for many minutes: " + (small.LandingUt - PeUt));
            // Anything like the real capsule's drag stays near 170.9: the miss is not in the orbit.
            foreach (var area in new[] { 1.5, 3.0 })
                Assert.True(Math.Abs(LandingSiteSearch.Wrap180(Fly(Capsule(area), 1415, frame).Longitude - ReentryLanding)) < 10, area + " m²");
        }

        [Fact]
        public void The_rotation_is_ruled_out_the_call_11700_s_earlier_lands_the_same_and_an_error_of_that_turn_would_be_195_not_115()
        {
            const double between = 11700;
            var turn = 360 * between / ReentrySim.RotationPeriod;
            var craft = Capsule(2.24);
            var early = Fly(craft, 1415, LoggedFrame(Offset()));
            var late = Fly(craft, 1415, LoggedFrame(Offset() + turn, PeUt - between));
            Assert.Equal(early.Longitude, late.Longitude, 6);
            Assert.Equal(early.Latitude, late.Latitude, 6);
            // The ticket's LonOffsets differ by 195.3° over the same time, Kerbin's own turn (195.4 here), and the miss is 115.2: not that turn, or the turn left out.
            Assert.InRange(turn, 195.2, 195.6);
            Assert.InRange(Math.Abs(LandingSiteSearch.Wrap180(ReturnLanding - ReentryLanding)), 115, 115.5);
        }
    }
}
