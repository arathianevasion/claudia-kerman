// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// launch's turn law in Core (#978, ADR 0014; <c>ADDONS:CLAUDIA:LAUNCHPITCH</c>): the pitch program and the thin-air rule, which were
    /// launch.ks's launch_pitch and launch_thin_pitch (#236) and now let a craft with thrust to spare turn below the prograde.
    /// </summary>
    public sealed class LaunchTurnTests
    {
        [Theory]
        [InlineData(500, 90)]
        [InlineData(1000, 90)]
        [InlineData(18250, 41.77)]
        [InlineData(70000, 0)]
        [InlineData(75000, 0)]
        public void The_pitch_falls_from_vertical_at_the_turn_start_to_flat_at_the_turn_end(double altitude, double pitch)
        {
            Assert.Equal(pitch, Math.Round(LaunchTurn.Program(altitude, 1000, 70000, 0.45), 2));
        }

        [Fact]
        public void Before_the_turn_starts_the_pitch_is_vertical()
        {
            Assert.Equal(90, LaunchTurn.Program(5000, -1, 70000, 0.45));
        }

        // #236: a craft with no thrust to spare (here 9 m/s² against 9.8) is held in thin air no lower than 3 degrees under the prograde.
        [Theory]
        [InlineData(30, 60, 100, 57)]
        [InlineData(58, 60, 100, 58)]
        [InlineData(70, 60, 100, 70)]
        [InlineData(30, 60, 500, 57)]
        [InlineData(30, 60, 501, 30)]
        [InlineData(30, 60, 20000, 30)]
        public void In_thin_air_a_craft_with_no_thrust_to_spare_is_held_within_3_degrees_of_the_prograde(double pitch, double prograde, double q, double held)
        {
            bool thin;
            Assert.Equal(held, LaunchTurn.Pitch(pitch, prograde, 100, q, 0.5, 9, 9.8, out thin), 9);
            Assert.Equal(held > pitch, thin);
        }

        [Fact]
        public void Under_30_m_s_the_thin_air_rule_waits()
        {
            bool thin;
            Assert.Equal(30, LaunchTurn.Pitch(30, 89, 30, 100, 0.5, 20, 9.8, out thin));
            Assert.False(thin);
        }

        // #978: Duna's craft (thrust-to-weight 3.5) turns under 500 Pa, once the prograde has come down far enough, to where its thrust still
        // holds its vertical speed: asin(1 / 3.5), 16.6 degrees.
        [Theory]
        [InlineData(80, 88, 80)]
        [InlineData(10, 25, 16.6)]
        [InlineData(10, 15, 12)]
        public void In_thin_air_a_craft_with_thrust_to_spare_may_turn_below_the_prograde_down_to_where_it_holds_its_climb(double program, double prograde, double pitch)
        {
            bool thin;
            Assert.Equal(pitch, Math.Round(LaunchTurn.Pitch(program, prograde, 80, 300, 0.7, 3.5 * 2.94, 2.94, out thin), 1));
            Assert.Equal(pitch > program, thin);
        }

        // #978 review: at Duna's turn start (60 m/s, prograde 87 degrees, q 175 Pa) shape 0.10's program says 40 degrees and the hold pitch is
        // 16.6; the nose may go only 20 degrees below the prograde (LaunchTurn.ThickLead), a kick kOS steering follows. As the prograde falls,
        // the floor follows it down to the hold pitch.
        [Theory]
        [InlineData(40, 87, 67)]
        [InlineData(10, 60, 40)]
        [InlineData(10, 30, 16.6)]
        public void At_the_turn_start_the_nose_goes_no_more_than_20_degrees_below_the_prograde(double program, double prograde, double pitch)
        {
            bool thin;
            Assert.Equal(pitch, Math.Round(LaunchTurn.Pitch(program, prograde, 60, 175, 0.7, 3.5 * 2.94, 2.94, out thin), 1));
            Assert.True(prograde - pitch <= LaunchTurn.ThickLead);
            Assert.True(thin);
        }

        // Up high (pressure under a tenth of the surface's) even a strong craft keeps #236's 3 degrees, so Kerbin's upper turn doesn't change.
        [Fact]
        public void High_in_the_air_a_craft_with_thrust_to_spare_still_keeps_within_3_degrees_of_the_prograde()
        {
            bool thin;
            Assert.Equal(42, LaunchTurn.Pitch(18, 45, 1400, 450, 0.05, 20, 5, out thin), 9);
            Assert.True(thin);
        }

        [Fact]
        public void The_hold_pitch_is_vertical_when_the_thrust_cant_hold_the_climb_and_flat_once_the_orbit_holds_it()
        {
            Assert.Equal(90, LaunchTurn.HoldPitch(5, 9.8));
            Assert.Equal(90, LaunchTurn.HoldPitch(0, 9.8));
            Assert.Equal(30, LaunchTurn.HoldPitch(19.6, 9.8), 9);
            Assert.Equal(0, LaunchTurn.HoldPitch(19.6, -1));
        }

        private static Dictionary<string, object> Ask()
        {
            return new Dictionary<string, object>
            {
                { "alt", 3200.0 }, { "start_alt", 3100.0 }, { "end_alt", 50000.0 }, { "shape", 0.1 }, { "speed", 80.0 }, { "prograde", 88.0 },
                { "q", 300.0 }, { "ratio", 0.7 }, { "accel", 10.3 }, { "g_net", 2.94 },
            };
        }

        [Fact]
        public void The_block_call_gives_the_pitch_and_whether_the_thin_air_rule_raised_it()
        {
            var turn = LaunchTurn.Call(Ask());
            Assert.Equal("", turn["error"]);
            bool thin;
            Assert.Equal(LaunchTurn.Pitch(LaunchTurn.Program(3200, 3100, 50000, 0.1), 88, 80, 300, 0.7, 10.3, 2.94, out thin), (double)turn["pitch"]);
            Assert.Equal(thin, turn["thin"]);
        }

        [Fact]
        public void The_block_call_names_a_missing_input_and_holds_the_nose_up()
        {
            var ask = Ask();
            ask.Remove("g_net");
            var turn = LaunchTurn.Call(ask);
            Assert.Equal("g_net is required, a number", turn["error"]);
            Assert.Equal(90.0, turn["pitch"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void The_block_call_runs_in_microseconds_every_tick()
        {
            var ask = Ask();
            Timing.Within(LaunchTurn.BudgetMs, () =>
            {
                for (var i = 0; i < 100; i++) LaunchTurn.Call(ask);
            });
        }
    }
}
