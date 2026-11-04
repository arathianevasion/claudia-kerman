// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>#692: the burst turn's plan (<see cref="TurnPlan"/>): spin up on RCS, coast with RCS off, stop on RCS, settle on the wheels.</summary>
    public sealed class TurnPlanTests
    {
        private const double Lever = 4;

        /// <summary>
        /// e17d's craft (#673): 11.4 t, a moment of inertia of about 95,000 kg·m² (95 t·m²), RCS of 1 kN on a 4 m lever (4 kN·m)
        /// and a small reaction wheel (1 kN·m), with monopropellant.
        /// </summary>
        private static TurnCraft E17d()
        {
            return new TurnCraft { WheelPitch = 1, WheelYaw = 1, RcsPitch = Lever, RcsYaw = Lever, RcsUsable = true, MoiPitch = 95, MoiYaw = 95 };
        }

        [Fact]
        public void A_142_degree_turn_on_e17d_spends_within_twice_the_ideal_impulse_and_finishes_before_turn_by()
        {
            var plan = TurnPlan.Plan(E17d(), 142, 60);
            Assert.Null(plan.Error);
            Assert.True(plan.Rcs);
            Assert.True(plan.CoastS > 0);
            // The bursts' impulse: the RCS force (torque over the lever, kN) for both bursts, against 2·I·ω / r.
            var impulse = E17d().RcsPitch / Lever * (plan.SpinS + plan.StopS);
            var ideal = 2 * 95 * plan.CoastRateDegS * Math.PI / 180 / Lever;
            Assert.InRange(impulse / ideal, 0.5, 2);
            Assert.True(plan.TurnS < 60, plan.TurnS + " s");
            // The bursts and coast cover the turn but for the degrees the wheels settle.
            var rad = plan.CoastRateDegS;
            Assert.Equal(142 - plan.WheelsDeg, rad * plan.CoastS + rad * rad / plan.AlphaDegS2, 6);
            Assert.Equal(TurnPlan.HandOverDeg, plan.WheelsDeg);
        }

        [Fact]
        public void A_turn_with_more_time_coasts_slower_and_spends_less()
        {
            var hurried = TurnPlan.Plan(E17d(), 142, 40);
            var easy = TurnPlan.Plan(E17d(), 142, 100);
            Assert.True(easy.CoastRateDegS < hurried.CoastRateDegS);
            Assert.True(easy.SpinS + easy.StopS < hurried.SpinS + hurried.StopS);
            Assert.True(easy.TurnS < 100 && hurried.TurnS < 40);
        }

        [Fact]
        public void A_turn_the_wheels_can_make_in_time_plans_no_RCS()
        {
            // The wheels alone turn 142 degrees on e17d in about 125 s; with 300 s left they make it.
            var plan = TurnPlan.Plan(E17d(), 142, 300);
            Assert.True(plan.WheelsS < 300);
            Assert.False(plan.Rcs);
        }

        [Fact]
        public void A_2_degree_error_with_10_s_left_stays_on_the_wheels()
        {
            // #408 (the amendment): wheels that close 2 degrees in about 3 s (alpha 8/9 deg/s²), not TURNTIME's 190-degree figure.
            var alpha = 8.0 / 9 * Math.PI / 180;
            var craft = new TurnCraft { WheelPitch = alpha * 10, WheelYaw = alpha * 10, RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 10, MoiYaw = 10 };
            var plan = TurnPlan.Plan(craft, 2, 10);
            Assert.InRange(plan.WheelsS - TurnPlan.SettleSeconds, 2.9, 3.1);
            Assert.False(plan.Rcs);
            Assert.True(TurnTime.PredictWheels(craft).Seconds > 10);
        }

        [Fact]
        public void A_turn_too_short_for_a_coast_flies_the_fastest_bursts_and_says_it_finishes_late()
        {
            var plan = TurnPlan.Plan(E17d(), 142, 5);
            Assert.True(plan.Rcs);
            Assert.Equal(0, plan.CoastS, 6);
            Assert.Equal(Math.Sqrt(plan.AlphaDegS2 * (142 - plan.WheelsDeg)), plan.CoastRateDegS, 6);
            Assert.True(plan.TurnS > 5);
        }

        [Fact]
        public void No_monoprop_no_rcs_torque_or_no_wheels_plans_no_burst()
        {
            var dry = E17d();
            dry.RcsUsable = false;
            Assert.Equal(0, TurnPlan.Plan(dry, 142, 30).CoastRateDegS);
            Assert.False(TurnPlan.Plan(dry, 142, 30).Rcs);
            var noRcs = new TurnCraft { WheelPitch = 1, WheelYaw = 1, RcsUsable = true, MoiPitch = 95, MoiYaw = 95 };
            Assert.Equal(0, TurnPlan.Plan(noRcs, 142, 30).SpinS);
            var noWheels = new TurnCraft { RcsPitch = 4, RcsYaw = 4, RcsUsable = true, MoiPitch = 95, MoiYaw = 95 };
            var plan = TurnPlan.Plan(noWheels, 142, 30);
            Assert.Equal(-1, plan.WheelsS);
            Assert.Equal(0, plan.SpinS);
            Assert.False(plan.Rcs);
            Assert.Equal(TurnTime.NoTorque, TurnPlan.Plan(new TurnCraft { MoiPitch = 95, MoiYaw = 95 }, 142, 30).Error);
        }

        [Fact]
        public void Each_phase_caps_kOS_at_the_coast_rate_on_the_torque_it_has_and_the_stop_brakes()
        {
            var plan = TurnPlan.Plan(E17d(), 142, 60);
            var wheels = 1 / 95.0 * 180 / Math.PI;
            Assert.Equal(plan.CoastRateDegS, plan.Spin.T * plan.AlphaDegS2, 6);
            Assert.Equal(plan.CoastRateDegS, plan.Coast.T * wheels, 6);
            Assert.Equal(TurnPlan.StopStoppingTime, plan.Stop.T);
            // kp x error stays over the coast rate until the stop burst starts; no integral to wind up.
            Assert.True(plan.Coast.Kp * plan.StopDeg > plan.CoastRateDegS);
            Assert.Equal(0, plan.Spin.Ki + plan.Coast.Ki + plan.Stop.Ki);
        }

        [Theory]
        // After a stop burst: a leftover over 10 degrees the wheels can't close in time takes a burst pair, at most twice.
        [InlineData(40, 20, 0, true)]
        [InlineData(40, 20, 1, true)]
        [InlineData(40, 20, 2, false)]
        // Under the threshold, or the wheels close it in time, or no deadline: the wheels settle it.
        [InlineData(9, 2, 0, false)]
        [InlineData(40, 300, 0, false)]
        [InlineData(40, -1, 0, false)]
        public void The_correction_rule_takes_a_burst_pair_for_a_big_leftover_and_never_more_than_two(double left, double leftS, int taken, bool expected)
        {
            Assert.Equal(expected, TurnPlan.Plan(E17d(), left, leftS, taken).Rcs);
        }

        [Fact]
        public void With_no_deadline_a_turn_takes_half_the_wheels_time()
        {
            var plan = TurnPlan.Plan(E17d(), 142, -1);
            Assert.False(plan.Rcs);
            Assert.True(plan.CoastRateDegS > 0);
            Assert.True(plan.TurnS <= Math.Min(plan.WheelsS, TurnPlan.MaxSeconds) * TurnPlan.NoDeadlineShare + 1e-6, plan.TurnS + " s");
        }

        [Fact]
        public void The_call_gives_every_key_and_phase_tuning()
        {
            var answer = TurnPlan.Call(new Dictionary<string, object> { { "angle", 142.0 }, { "left_s", 60.0 } }, E17d());
            Assert.Equal("", answer["error"]);
            Assert.Equal(true, answer["rcs"]);
            foreach (var key in new[] { "wheels_s", "coast_rate", "spin_s", "coast_s", "stop_s", "stop_deg", "wheels_deg", "turn_s", "alpha" })
                Assert.IsType<double>(answer[key]);
            foreach (var phase in new[] { "spin", "coast", "stop" })
            {
                var tuning = (IDictionary<string, object>)answer[phase];
                Assert.IsType<double>(tuning["t"]);
                Assert.IsType<double>(tuning["kp"]);
                Assert.IsType<double>(tuning["ki"]);
            }
            var correction = TurnPlan.Call(new Dictionary<string, object> { { "angle", 40.0 }, { "left_s", 20.0 }, { "corrections", 2.0 } }, E17d());
            Assert.Equal(false, correction["rcs"]);
            var none = TurnPlan.Call(null, new TurnCraft());
            Assert.Equal(TurnTime.NoTorque, none["error"]);
            Assert.Equal(false, none["rcs"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_plans_take_under_5_ms()
        {
            // ADR 0014: TURNPLAN is called once per turn, at the on-gate (at most once a second) and after each stop burst.
            var ask = new Dictionary<string, object> { { "angle", 142.0 }, { "left_s", 60.0 } };
            var craft = E17d();
            Timing.Within(5, () =>
            {
                for (var i = 0; i < 1000; i++) TurnPlan.Call(ask, craft);
            });
        }
    }
}
