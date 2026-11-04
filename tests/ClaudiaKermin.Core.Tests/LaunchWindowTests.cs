// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Launch-to-plane timing (#464): the port of MechJeb's <c>TimeToPlane</c> and <c>MinimumTimeToPlane</c>, <see cref="LaunchWindow"/>
    /// on it and <c>ADDONS:CLAUDIA:LAUNCHWINDOW</c> through <see cref="BlockCalls"/>. Each window is checked by the plane's normal:
    /// the pad's position at launch, turned with the body, lies in the plane (its dot product with the normal is zero).
    /// </summary>
    public sealed class LaunchWindowTests
    {
        private const double Period = 21549.425;
        private const double KscLatitude = -0.0972;

        private static SnapshotBody Kerbin()
        {
            return new SnapshotBody { Name = "Kerbin", Gm = 3.5316e12, Radius = 600000, RotationPeriod = Period, AtmosphereHeight = 70000, SeaLevelAtm = 1 };
        }

        /// <summary>The plane's unit normal for inclination <paramref name="inc"/> and ascending node <paramref name="lan"/> (degrees).</summary>
        private static double[] Normal(double inc, double lan)
        {
            var i = inc * Math.PI / 180;
            var o = lan * Math.PI / 180;
            return new[] { Math.Sin(i) * Math.Sin(o), -Math.Sin(i) * Math.Cos(o), Math.Cos(i) };
        }

        /// <summary>The pad's unit position wait seconds on, its celestial longitude carried east by the body's turning.</summary>
        private static double[] Pad(double lat, double celestialLongitude, double wait)
        {
            var phi = lat * Math.PI / 180;
            var lambda = (celestialLongitude + 360 * wait / Period) * Math.PI / 180;
            return new[] { Math.Cos(phi) * Math.Cos(lambda), Math.Cos(phi) * Math.Sin(lambda), Math.Sin(phi) };
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static double OffPlane(double lat, double celestialLongitude, double wait, double inc, double lan)
        {
            return Dot(Pad(lat, celestialLongitude, wait), Normal(inc, lan));
        }

        private static GameSnapshot Snapshot(double lat, double celestialLongitude)
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Ut = 1000, Latitude = lat, CelestialLongitude = celestialLongitude };
            snapshot.Bodies.Add(Kerbin());
            return snapshot;
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(90, 100)]
        [InlineData(200, 250)]
        public void KSC_to_a_polar_plane_has_two_windows_half_a_day_apart(double celestialLongitude, double lan)
        {
            double north = PlaneTiming.TimeToPlane(Period, KscLatitude, celestialLongitude, lan, 90);
            double south = PlaneTiming.TimeToPlane(Period, KscLatitude, celestialLongitude, lan, -90);
            var apart = Math.Abs(north - south);
            Assert.InRange(apart, Period / 2 - 1, Period / 2 + 1);
            // Each lies in the plane: the pad is within its latitude's sine of it, because the pad is not on the equator.
            Assert.InRange(OffPlane(KscLatitude, celestialLongitude, north, 90, lan), -1e-9, 1e-9);
            Assert.InRange(OffPlane(KscLatitude, celestialLongitude, south, 90, lan), -1e-9, 1e-9);
        }

        [Theory]
        [InlineData(6, 78, 0)]
        [InlineData(6, 78, 123.4)]
        [InlineData(6, 78, 300)]
        [InlineData(28.5, 10, 45)]
        [InlineData(97.4, 200, 300)]
        public void Minmus_and_other_planes_pass_over_the_pad_in_the_plane_at_the_sooner_window(double inc, double lan, double celestialLongitude)
        {
            var lat = KscLatitude;
            double wait, signed;
            PlaneTiming.MinimumTimeToPlane(Period, lat, celestialLongitude, lan, inc, out wait, out signed);
            Assert.InRange(wait, 0, Period);
            Assert.Equal(Math.Abs(signed), inc, 9);
            Assert.InRange(OffPlane(lat, celestialLongitude, wait, inc, lan), -1e-9, 1e-9);
            // The other window is no sooner.
            var other = PlaneTiming.TimeToPlane(Period, lat, celestialLongitude, lan, -signed);
            Assert.True(other >= wait);
        }

        [Fact]
        public void The_Muns_plane_is_the_equator_so_the_pad_is_under_it_at_once()
        {
            // The Mun's inclination is 0 in stock: any pad's wait is 0 and the plane's normal is the pole.
            var window = LaunchWindow.Find(Kerbin(), 1000, KscLatitude, 33, 0, 0, 80000);
            Assert.Null(window.Error);
            Assert.Equal(0, window.WaitSeconds);
            Assert.Equal(1000, window.Ut);
            Assert.Equal(0, Dot(new[] { 0.0, 0, 1 }, Normal(0, 0)) - 1, 12);
            // Due east at the equator, less the pad's own speed.
            Assert.InRange(window.Heading, 88, 92);
        }

        [Fact]
        public void A_pad_north_of_the_equator_to_a_polar_plane_launches_into_the_soonest_pass()
        {
            var window = LaunchWindow.Find(Kerbin(), 1000, 28.5, 40, 90, 100, 80000);
            Assert.Null(window.Error);
            Assert.InRange(OffPlane(28.5, 40, window.WaitSeconds, 90, 100), -1e-9, 1e-9);
            Assert.Equal(window.North ? 90 : -90, window.Inclination);
            Assert.Equal(1000 + window.WaitSeconds, window.Ut, 6);
            // A polar plane from a pad on the turning body leans east into the pad's speed, north or south.
            Assert.True(window.North ? window.Heading < 20 || window.Heading > 340 : window.Heading > 160 && window.Heading < 200);
        }

        // #727: a site past a prograde plane launches due east (its latitude) when its orbit's node is the plane's: the least
        // relative inclination, the latitude less the plane's, which the transfer fixes on the way.
        [Theory]
        [InlineData(28.5, 0.0)]
        [InlineData(28.5, 200.0)]
        [InlineData(-28.5, 33.0)]
        public void A_site_whose_latitude_exceeds_the_inclination_launches_due_east_at_the_plane_s_node(double lat, double celestialLongitude)
        {
            var window = LaunchWindow.Find(Kerbin(), 0, lat, celestialLongitude, 6, 78, 80000);
            Assert.Null(window.Error);
            Assert.Equal(22.5, window.Residual, 9);
            Assert.Equal(28.5, window.Inclination, 9);
            Assert.True(window.North);
            Assert.Equal(78, window.Lan);
            Assert.InRange(window.WaitSeconds, 0, Period);
            Assert.InRange(window.Heading, 89.9, 90.1);
            // Under the due-east orbit at the window: the plane of inclination 28.5 and Minmus's node.
            Assert.InRange(OffPlane(lat, celestialLongitude, window.WaitSeconds, 28.5, 78), -1e-9, 1e-9);
            // Into the plane, no residual; due east loses nothing to the body's turning, into 6° from 0° about 1 m/s.
            Assert.Equal(0, LaunchWindow.Find(Kerbin(), 0, 28.5, 0, 28.5, 78, 80000).Residual);
            Assert.Equal(0, LaunchWindow.AscentLoss(Kerbin(), 28.5, 28.5), 9);
            Assert.InRange(LaunchWindow.AscentLoss(Kerbin(), 0, 6), 0.9, 1.0);
        }

        [Fact]
        public void A_site_past_a_retrograde_plane_is_an_error_with_a_reason()
        {
            // A retrograde plane counts from the other side: 180 - 6 is as far from the equator, and due east is no way into it.
            var window = LaunchWindow.Find(Kerbin(), 0, 28.5, 0, 174, 78, 80000);
            Assert.Contains("never passes under a plane of inclination 174", window.Error);
            Assert.Contains("28.5", window.Error);
        }

        [Theory]
        [InlineData(double.NaN, 0, 0)]
        [InlineData(0, double.PositiveInfinity, 0)]
        [InlineData(0, 0, double.NaN)]
        [InlineData(0, 200, 0)]
        public void Bad_inputs_are_refused_in_every_build(double lat, double inc, double lan)
        {
            Assert.NotNull(LaunchWindow.Find(Kerbin(), 0, lat, 0, inc, lan, 80000).Error);
            Assert.Throws<ArgumentOutOfRangeException>(() => PlaneTiming.TimeToPlane(Period, lat, 0, lan, inc));
        }

        [Fact]
        public void A_body_that_does_not_turn_has_no_window()
        {
            var still = Kerbin();
            still.RotationPeriod = 0;
            Assert.Contains("rotation period", LaunchWindow.Find(still, 0, 0, 0, 90, 0, 80000).Error);
            Assert.Throws<ArgumentOutOfRangeException>(() => PlaneTiming.TimeToPlane(0, 0, 0, 0, 90));
        }

        [Fact]
        public void A_body_that_turns_backwards_is_timed_the_other_way()
        {
            var forward = PlaneTiming.TimeToPlane(Period, 10, 0, 90, 45);
            var back = PlaneTiming.TimeToPlane(-Period, 10, 0, 90, 45);
            // Same plane, opposite spin: the waits add to a whole turn.
            Assert.Equal(Period, forward + back, 6);
        }

        [Fact]
        public void The_block_call_reads_the_pad_from_the_snapshot()
        {
            var snapshot = Snapshot(KscLatitude, 123.4);
            var result = BlockCalls.LaunchWindow(new Dictionary<string, object> { { "inc", 6.0 }, { "lan", 78.0 }, { "target_ap", 80000.0 } }, snapshot);
            Assert.Equal("", result["error"]);
            var wait = (double)result["wait"];
            var signed = (double)result["inc"];
            Assert.Equal(1000 + wait, (double)result["ut"], 6);
            Assert.Equal(signed >= 0, (bool)result["north"]);
            Assert.InRange(OffPlane(KscLatitude, 123.4, wait, 6, 78), -1e-9, 1e-9);
            Assert.InRange((double)result["heading"], 0, 360);
        }

        [Fact]
        public void The_block_call_gives_a_reason_not_a_window_when_it_cannot()
        {
            var unreachable = BlockCalls.LaunchWindow(new Dictionary<string, object> { { "inc", 174.0 }, { "lan", 0.0 } }, Snapshot(28.5, 0));
            Assert.Single(unreachable);
            Assert.Contains("never passes under", (string)unreachable["error"]);
            // Past a prograde plane (#727): due east, and the residual says how far off.
            var east = BlockCalls.LaunchWindow(new Dictionary<string, object> { { "inc", 6.0 }, { "lan", 0.0 } }, Snapshot(28.5, 0));
            Assert.Equal("", east["error"]);
            Assert.Equal(22.5, (double)east["residual"], 9);
            Assert.Equal(28.5, (double)east["inc"], 9);
            Assert.Contains("required", (string)BlockCalls.LaunchWindow(new Dictionary<string, object>(), Snapshot(0, 0))["error"]);
            Assert.Contains("body table", (string)BlockCalls.LaunchWindow(new Dictionary<string, object> { { "inc", 6.0 }, { "lan", 0.0 } }, new GameSnapshot { Body = "Kerbin" })["error"]);
        }

        private static Dictionary<string, object> LaunchHeadingAsk(double inc, double lat)
        {
            return new Dictionary<string, object>
            {
                { "inc", inc }, { "lat", lat }, { "target_ap", 80000.0 },
                { "gm", 3.5316e12 }, { "radius", 600000.0 }, { "rotation", Period },
            };
        }

        // #475: launch flies this heading, so for the same inputs it must be the one plan_ascent reports (the window's).
        [Theory]
        [InlineData(6.0, 78.0, 123.4)]
        [InlineData(-6.0, 78.0, 123.4)]
        [InlineData(-45.0, 0.0, 10.0)]
        [InlineData(90.0, 100.0, 40.0)]
        public void The_launch_heading_is_the_heading_plan_ascent_reports_for_the_same_window(double inc, double lan, double celestialLongitude)
        {
            var snapshot = Snapshot(KscLatitude, celestialLongitude);
            var window = BlockCalls.LaunchWindow(new Dictionary<string, object> { { "inc", inc }, { "lan", lan }, { "target_ap", 80000.0 } }, snapshot);
            Assert.Equal("", window["error"]);
            var ask = LaunchHeadingAsk((double)window["inc"], KscLatitude);
            var heading = BlockCalls.LaunchHeading(ask);
            Assert.Equal("", heading["error"]);
            Assert.Equal((double)window["heading"], (double)heading["heading"], 9);
        }

        // The block's heading is the signed inclination's: a southbound plane flies southeast, a northbound one northeast.
        [Fact]
        public void A_southbound_plane_flies_southward_and_a_northbound_one_northward()
        {
            var north = (double)BlockCalls.LaunchHeading(LaunchHeadingAsk(45, 28.5))["heading"];
            var south = (double)BlockCalls.LaunchHeading(LaunchHeadingAsk(-45, 28.5))["heading"];
            Assert.InRange(north, 0, 90);
            Assert.InRange(south, 90, 180);
        }

        [Fact]
        public void A_block_call_without_its_inputs_or_a_body_that_turns_gives_a_reason()
        {
            Assert.Contains("inc is required", (string)BlockCalls.LaunchHeading(new Dictionary<string, object>())["error"]);
            var still = LaunchHeadingAsk(45, 28.5);
            still["rotation"] = 0.0;
            Assert.Contains("above zero", (string)BlockCalls.LaunchHeading(still)["error"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void The_launch_heading_runs_in_microseconds_every_tick()
        {
            var ask = LaunchHeadingAsk(6, 28.5);
            Timing.Within(2, () =>
            {
                for (var i = 0; i < 100; i++) BlockCalls.LaunchHeading(ask);
            });
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void It_runs_in_milliseconds()
        {
            var snapshot = Snapshot(KscLatitude, 10);
            var ask = new Dictionary<string, object> { { "inc", 6.0 }, { "lan", 78.0 } };
            Timing.Within(5, () =>
            {
                for (var i = 0; i < 100; i++) BlockCalls.LaunchWindow(ask, snapshot);
            });
        }
    }
}
