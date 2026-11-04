// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The Bogacki-Shampine 3(2) integrator ported from MechJebLib (#462), on ODEs with answers in closed form: a body falling
    /// onto a floor (the event), exponential decay and a circular orbit (the error control).
    /// </summary>
    public sealed class Bs3Tests
    {
        private const double G = 9.81;

        /// <summary>y = (height, upward speed) under gravity alone.</summary>
        private static void Fall(double[] y, double t, double[] dy)
        {
            dy[0] = y[1];
            dy[1] = -G;
        }

        private static readonly OdeEvent Floor = new OdeEvent((y, t) => y[0], -1);

        [Fact]
        public void A_body_dropped_from_100_m_hits_the_floor_at_the_time_and_speed_of_the_closed_form()
        {
            var result = new Bs3 { Rtol = 1e-8, Atol = 1e-8 }.Solve(Fall, new[] { 100.0, 0 }, 0, 60, new[] { Floor });
            Assert.Equal(OdeStatus.EventTerminated, result.Status);
            Assert.Same(Floor, result.Event);
            // h = 100 - g t^2 / 2: t = sqrt(200 / g) = 4.5152 s, at g t = 44.294 m/s.
            Assert.Equal(Math.Sqrt(200 / G), result.T, 9);
            Assert.Equal(0, result.Y[0], 7);
            Assert.Equal(-Math.Sqrt(200 * G), result.Y[1], 7);
        }

        [Fact]
        public void A_body_thrown_up_passes_the_floor_rising_without_stopping_and_stops_falling_through_it()
        {
            // From 1 m below the floor at 20 m/s up: it rises through 0 (not this event's direction) and falls back through it.
            var result = new Bs3 { Rtol = 1e-8, Atol = 1e-8 }.Solve(Fall, new[] { -1.0, 20 }, 0, 60, new[] { Floor });
            Assert.Equal(OdeStatus.EventTerminated, result.Status);
            // -1 + 20 t - g t^2 / 2 = 0, the later root.
            var later = (20 + Math.Sqrt(400 - 2 * G)) / G;
            Assert.Equal(later, result.T, 8);
        }

        [Fact]
        public void Without_an_event_it_runs_to_the_end_time()
        {
            var result = new Bs3 { Rtol = 1e-8, Atol = 1e-8 }.Solve(Fall, new[] { 100.0, 0 }, 0, 2);
            Assert.Equal(OdeStatus.Finished, result.Status);
            Assert.Null(result.Event);
            Assert.Equal(2, result.T, 12);
            Assert.Equal(100 - G * 2, result.Y[0], 8);
        }

        [Fact]
        public void Exponential_decay_is_within_its_tolerance_and_a_looser_tolerance_takes_fewer_steps()
        {
            OdeFunction decay = (y, t, dy) => dy[0] = -y[0];
            var tight = new Bs3 { Rtol = 1e-9, Atol = 1e-12 }.Solve(decay, new[] { 1.0 }, 0, 5);
            var loose = new Bs3 { Rtol = 1e-5, Atol = 1e-8 }.Solve(decay, new[] { 1.0 }, 0, 5);
            Assert.Equal(Math.Exp(-5), tight.Y[0], 9);
            Assert.Equal(Math.Exp(-5), loose.Y[0], 5);
            Assert.True(loose.Steps < tight.Steps, loose.Steps + " steps at 1e-5, " + tight.Steps + " at 1e-9");
        }

        [Fact]
        public void A_circular_orbit_comes_back_round_to_its_start()
        {
            // Unit radius and gravitational parameter: a period is 2 pi.
            OdeFunction orbit = (y, t, dy) =>
            {
                var r3 = Math.Pow(y[0] * y[0] + y[1] * y[1], 1.5);
                dy[0] = y[2];
                dy[1] = y[3];
                dy[2] = -y[0] / r3;
                dy[3] = -y[1] / r3;
            };
            var result = new Bs3 { Rtol = 1e-10, Atol = 1e-10, MaxSteps = 20000 }.Solve(orbit, new[] { 1.0, 0, 0, 1 }, 0, 2 * Math.PI);
            Assert.Equal(OdeStatus.Finished, result.Status);
            Assert.Equal(1, result.Y[0], 6);
            Assert.Equal(0, result.Y[1], 6);
        }

        [Fact]
        public void The_step_cap_stops_it_and_says_so()
        {
            var result = new Bs3 { Rtol = 1e-8, Atol = 1e-8, Hmax = 0.01, MaxSteps = 10 }.Solve(Fall, new[] { 100.0, 0 }, 0, 60);
            Assert.Equal(OdeStatus.MaxStepsExceeded, result.Status);
            Assert.Contains("10 steps", result.Reason);
        }

        [Fact]
        public void A_derivative_that_goes_non_finite_stops_it_and_says_so()
        {
            OdeFunction blowup = (y, t, dy) => dy[0] = t > 1 ? double.NaN : 1;
            var result = new Bs3().Solve(blowup, new[] { 0.0 }, 0, 5);
            Assert.Equal(OdeStatus.NonFinite, result.Status);
            Assert.NotNull(result.Reason);
        }

        [Theory]
        [InlineData(double.NaN, 1e-9, 1e-9, 0, 2000, "the start state must be finite")]
        [InlineData(1, 0, 1e-9, 0, 2000, "the tolerances must be positive numbers")]
        [InlineData(1, 1e-9, double.NaN, 0, 2000, "the tolerances must be positive numbers")]
        [InlineData(1, 1e-9, 1e-9, -1, 2000, "the step limits must be 0 <= Hmin <= Hmax")]
        [InlineData(1, 1e-9, 1e-9, 0, 0, "the step cap must be at least 1")]
        public void Bad_input_is_refused_in_every_build_without_calling_the_function(double y0, double rtol, double atol, double hmin, int maxSteps, string reason)
        {
            var calls = 0;
            OdeFunction counted = (y, t, dy) => { calls++; dy[0] = 0; };
            var result = new Bs3 { Rtol = rtol, Atol = atol, Hmin = hmin, MaxSteps = maxSteps }.Solve(counted, new[] { y0 }, 0, 1);
            Assert.Equal(OdeStatus.InvalidInput, result.Status);
            Assert.Equal("Bogacki-Shampine: " + reason, result.Reason);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void A_missing_or_malformed_event_is_refused_not_thrown()
        {
            var result = new Bs3().Solve(Fall, new[] { 1.0, 0 }, 0, 1, new[] { Floor, null });
            Assert.Equal(OdeStatus.InvalidInput, result.Status);
            Assert.Equal("Bogacki-Shampine: each event needs a function and a direction of -1, 0 or 1", result.Reason);
            Assert.Equal(OdeStatus.InvalidInput, new Bs3().Solve(Fall, new[] { 1.0, 0 }, 0, 1, new[] { new OdeEvent(null) }).Status);
            Assert.Equal(OdeStatus.InvalidInput, new Bs3().Solve(Fall, new[] { 1.0, 0 }, 0, 1, new[] { new OdeEvent((y, t) => y[0], 2) }).Status);
        }

        [Fact]
        public void A_non_finite_end_time_or_an_empty_state_is_refused()
        {
            Assert.Equal(OdeStatus.InvalidInput, new Bs3().Solve(Fall, new[] { 1.0, 0 }, 0, double.PositiveInfinity).Status);
            Assert.Equal(OdeStatus.InvalidInput, new Bs3().Solve(Fall, new double[0], 0, 1).Status);
        }
    }
}
