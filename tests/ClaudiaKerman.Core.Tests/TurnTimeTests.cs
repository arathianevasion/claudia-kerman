// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The turn-time model (spec #314): a conservative 190° turn on the torque a craft has on a Coast, speeding up for the first
    /// half and slowing down for the second, capped at kOS steering's top turn rate (its <c>maxstoppingtime</c>, 2 s by default,
    /// times the craft's angular acceleration), with a settle time on top and a 30 s floor. Expected figures are worked by hand.
    /// </summary>
    public sealed class TurnTimeTests
    {
        /// <summary>
        /// The exec_node reference craft (tests/blocks/exec_node): OKTO2, kOS, two Z-1k batteries, a Small Inline Reaction Wheel
        /// (5 kN·m on each axis), a chute, a decoupler, a full FL-T800 and an LV-T45, about 6.5 t. Its moment of inertia about
        /// pitch and yaw, worked from the parts' masses and the craft file's positions (the tank as a 1.25 m by 3.75 m cylinder,
        /// the rest, but for its three small winglets, as points), is about 13.8 t·m². An estimate: stock's own figure, read
        /// in-game, may differ, though not by the factor of four that would lift the craft off the floor.
        /// </summary>
        private static TurnCraft Reference()
        {
            return new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 13.8, MoiYaw = 13.8 };
        }

        [Fact]
        public void The_reference_craft_turns_in_under_seven_seconds_so_it_gets_the_30_s_floor()
        {
            // alpha = 5 / 13.8 = 0.362 rad/s²; kOS caps the rate at 2 s of it, 0.725 rad/s. Unchecked, half the turn would reach
            // sqrt(0.362 * 3.316) = 1.10 rad/s, so the cap holds: 3.316 / 0.725 cruising plus 2 s speeding up and slowing down.
            var turn = TurnTime.Predict(Reference());
            Assert.Null(turn.Error);
            Assert.InRange(turn.TurnSeconds, 6.5, 6.65);
            Assert.Equal(30, turn.Seconds);
        }

        [Fact]
        public void A_slow_craft_gets_its_turn_at_kOSs_top_rate_plus_the_10_s_settle_time()
        {
            // alpha = 5 / 500 = 0.01 rad/s², capped at 0.02 rad/s: 3.3161 / 0.02 = 165.81 s cruising, 2 s up and down.
            var turn = TurnTime.Predict(new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 });
            Assert.Equal(167.81, turn.TurnSeconds, 2);
            Assert.Equal(177.81, turn.Seconds, 2);
            Assert.Equal(1.146, turn.TopRateDegS, 3);
        }

        [Fact]
        public void A_turn_that_never_reaches_the_cap_speeds_up_for_half_and_slows_down_for_half()
        {
            // With a 100 s stopping time the cap (1 rad/s) is out of reach: half the turn peaks at sqrt(0.01 * 3.3161) = 0.1821
            // rad/s, and the whole takes 2 * sqrt(3.3161 / 0.01) = 36.42 s.
            var turn = TurnTime.Predict(new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 }, 100);
            Assert.Equal(36.42, turn.TurnSeconds, 2);
            Assert.Equal(46.42, turn.Seconds, 2);
            Assert.Equal(10.43, turn.TopRateDegS, 2);
        }

        [Fact]
        public void The_settle_time_alone_never_takes_a_quick_craft_under_the_floor()
        {
            var turn = TurnTime.Predict(new TurnCraft { WheelPitch = 500, WheelYaw = 500, MoiPitch = 1, MoiYaw = 1 });
            Assert.True(turn.TurnSeconds + TurnTime.SettleSeconds < 30);
            Assert.Equal(30, turn.Seconds);
        }

        [Fact]
        public void RCS_adds_its_torque_only_while_it_is_usable()
        {
            var off = new TurnCraft { WheelPitch = 5, WheelYaw = 5, RcsPitch = 5, RcsYaw = 5, MoiPitch = 500, MoiYaw = 500 };
            var on = new TurnCraft { WheelPitch = 5, WheelYaw = 5, RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 };
            Assert.Equal(167.81, TurnTime.Predict(off).TurnSeconds, 2);
            // Twice the torque: alpha 0.02 rad/s². #730: the burst turn TurnPlan plans for 190 degrees (no deadline: half of the
            // wheels' 170.81 s, capped at 120 s), not #678's continuous-RCS turn (44.86 s).
            Assert.Equal(60, TurnTime.Predict(on).TurnSeconds, 2);
        }

        [Fact]
        public void The_turn_is_timed_about_the_slower_axis()
        {
            var turn = TurnTime.Predict(new TurnCraft { WheelPitch = 50, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 });
            Assert.Equal(167.81, turn.TurnSeconds, 2);
        }

        [Theory]
        [InlineData(0, 0, 0, false)]
        [InlineData(0, 0, 5, false)]
        [InlineData(5, 0, 0, false)]
        [InlineData(0, 0, 0, true)]
        public void A_craft_with_no_usable_torque_on_either_axis_cannot_turn(double wheelPitch, double wheelYaw, double rcs, bool rcsUsable)
        {
            var turn = TurnTime.Predict(new TurnCraft
            {
                WheelPitch = wheelPitch, WheelYaw = wheelYaw, RcsPitch = rcs, RcsYaw = rcs, RcsUsable = rcsUsable, MoiPitch = 13.8, MoiYaw = 13.8,
            });
            Assert.Equal("no usable torque", turn.Error);
        }

        [Fact]
        public void A_craft_with_RCS_alone_turns_while_RCS_is_usable()
        {
            var turn = TurnTime.Predict(new TurnCraft { RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 });
            Assert.Null(turn.Error);
            // #730: no wheels to hold the coast or settle, so there's no burst turn; RCS alone is timed on the capped model (alpha 0.01 rad/s²).
            Assert.Equal(167.81, turn.TurnSeconds, 2);
        }

        [Fact]
        public void The_wheels_only_turn_leaves_RCS_out_however_usable()
        {
            var craft = new TurnCraft { WheelPitch = 5, WheelYaw = 5, RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 };
            Assert.Equal(167.81, TurnTime.PredictWheels(craft).TurnSeconds, 2);
            Assert.Equal(60, TurnTime.Predict(craft).TurnSeconds, 2);
        }

        // ---- the burst turn (#692, #730) ----

        [Theory]
        [InlineData(5, 5, 500)]
        [InlineData(5, 5, 100)]
        [InlineData(5, 20, 300)]
        public void With_RCS_the_time_is_TURNPLANs_turn_plus_the_settle(double wheel, double rcs, double moi)
        {
            var craft = new TurnCraft { WheelPitch = wheel, WheelYaw = wheel, RcsPitch = rcs, RcsYaw = rcs, RcsUsable = true, MoiPitch = moi, MoiYaw = moi };
            var plan = TurnPlan.Plan(craft, TurnTime.TurnDegrees, -1);
            var turn = TurnTime.Predict(craft);
            Assert.True(plan.TurnS > 0);
            Assert.Equal(plan.TurnS, turn.TurnSeconds, 6);
            Assert.Equal(System.Math.Max(TurnTime.FloorSeconds, plan.TurnS + TurnTime.SettleSeconds), turn.Seconds, 6);
            Assert.Equal(plan.CoastRateDegS, turn.TopRateDegS, 6);
            Assert.Equal(plan.AlphaDegS2, turn.AlphaDegS2, 6);
        }

        [Fact]
        public void RCS_that_adds_little_to_strong_wheels_plans_no_burst_turn_and_the_capped_model_stands()
        {
            // #754: wheels 50 against RCS 8 on 300: a burst turn can't beat the wheels meaningfully, so none is planned.
            var craft = new TurnCraft { WheelPitch = 50, WheelYaw = 50, RcsPitch = 8, RcsYaw = 8, RcsUsable = true, MoiPitch = 300, MoiYaw = 300 };
            Assert.Equal(0, TurnPlan.Plan(craft, TurnTime.TurnDegrees, -1).CoastRateDegS);
            var turn = TurnTime.Predict(craft);
            Assert.Null(turn.Error);
            Assert.True(turn.TurnSeconds > 0);
            Assert.True(turn.TurnSeconds <= TurnTime.PredictWheels(craft).TurnSeconds);
        }

        [Fact]
        public void The_wheels_only_figures_stay_at_a_2_s_stopping_time()
        {
            var craft = new TurnCraft { WheelPitch = 5, WheelYaw = 5, RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 };
            var wheels = TurnTime.PredictWheels(craft);
            Assert.Equal(167.81, wheels.TurnSeconds, 2);
            Assert.Equal(1.146, wheels.TopRateDegS, 3);
            Assert.Equal(0.573, wheels.AlphaDegS2, 3);
        }

        [Fact]
        public void A_craft_with_RCS_alone_has_no_wheels_only_turn()
        {
            var turn = TurnTime.PredictWheels(new TurnCraft { RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 });
            Assert.Equal("no usable torque", turn.Error);
        }

        // ---- the block-internal call: ADDONS:CLAUDIA:TURNTIME ----

        [Fact]
        public void The_block_call_gives_the_wheels_only_turn_beside_the_one_with_RCS()
        {
            var craft = new TurnCraft { WheelPitch = 5, WheelYaw = 5, RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 };
            var result = BlockCalls.TurnTime(new Dictionary<string, object>(), craft);
            Assert.Equal("", result["error"]);
            // Wheels and RCS (the burst turn, #730): 60 s turning, 10 s settling. Wheels alone, at T = 2: 167.81 s and 10 s.
            Assert.Equal(70, (double)result["turn_time"], 2);
            Assert.Equal(60, (double)result["turn_s"], 2);
            Assert.Equal(1.146, (double)result["alpha"], 3);
            Assert.Equal(177.81, (double)result["wheels_turn_time"], 2);
            Assert.Equal(167.81, (double)result["wheels_turn_s"], 2);
        }

        [Theory]
        [InlineData(5, false)] // thrusters, but no MonoPropellant
        [InlineData(0, true)]  // MonoPropellant, but no enabled thrusters
        public void Without_MonoPropellant_or_enabled_thrusters_both_figures_are_the_same(double rcs, bool usable)
        {
            var craft = new TurnCraft { WheelPitch = 5, WheelYaw = 5, RcsPitch = rcs, RcsYaw = rcs, RcsUsable = usable, MoiPitch = 500, MoiYaw = 500 };
            var result = BlockCalls.TurnTime(new Dictionary<string, object>(), craft);
            Assert.Equal(177.81, (double)result["turn_time"], 2);
            Assert.Equal(result["turn_time"], result["wheels_turn_time"]);
            Assert.Equal(result["turn_s"], result["wheels_turn_s"]);
        }

        [Fact]
        public void The_block_call_gives_minus_one_for_the_wheels_only_turn_of_a_craft_with_no_wheel_torque()
        {
            // Wheel torque about pitch only: the wheels alone can't turn it, RCS can.
            var craft = new TurnCraft { WheelPitch = 5, RcsPitch = 5, RcsYaw = 5, RcsUsable = true, MoiPitch = 500, MoiYaw = 500 };
            var result = BlockCalls.TurnTime(new Dictionary<string, object>(), craft);
            Assert.Equal("", result["error"]);
            Assert.True((double)result["turn_time"] > 0);
            Assert.Equal(-1.0, result["wheels_turn_time"]);
            Assert.Equal(-1.0, result["wheels_turn_s"]);
        }

        // ---- #941: reentry holds attitude only once the air is a turn time plus a margin away ----

        [Fact]
        public void Reentry_starts_holding_a_turn_time_plus_the_margin_before_the_air()
        {
            var slow = new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 }; // turn_time 177.81 s
            var result = BlockCalls.TurnTime(new Dictionary<string, object> { { "to_air", 1000.0 } }, slow);
            Assert.Equal(1000 - 177.81 - TurnTime.HoldMarginSeconds, (double)result["hold_in"], 2);
        }

        [Theory]
        [InlineData(0.0)]    // already in the air
        [InlineData(100.0)]  // too close to be turned in time
        [InlineData(177.81 + 30.0)] // exactly the turn time plus the margin: now
        public void A_craft_in_the_air_or_too_close_holds_at_once(double toAir)
        {
            var slow = new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 };
            var result = BlockCalls.TurnTime(new Dictionary<string, object> { { "to_air", toAir } }, slow);
            Assert.Equal(0.0, (double)result["hold_in"], 2);
        }

        [Fact]
        public void A_craft_with_no_turn_time_holds_at_once()
        {
            var result = BlockCalls.TurnTime(new Dictionary<string, object> { { "to_air", 5000.0 } }, new TurnCraft());
            Assert.Equal("no usable torque", result["error"]);
            Assert.Equal(0.0, (double)result["hold_in"], 2);
        }

        [Fact]
        public void The_block_call_gives_the_time_to_leave_and_the_turn_alone()
        {
            var slow = new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 };
            var result = BlockCalls.TurnTime(new Dictionary<string, object>(), slow);
            Assert.Equal("", result["error"]);
            Assert.Equal(177.81, (double)result["turn_time"], 2);
            Assert.Equal(167.81, (double)result["turn_s"], 2);
            Assert.Equal(1.146, (double)result["top_rate"], 3);
        }

        [Fact]
        public void The_block_call_takes_the_steering_managers_stopping_time()
        {
            var slow = new TurnCraft { WheelPitch = 5, WheelYaw = 5, MoiPitch = 500, MoiYaw = 500 };
            var result = BlockCalls.TurnTime(new Dictionary<string, object> { { "MAX_STOPPING_TIME", 100.0 } }, slow);
            Assert.Equal(36.42, (double)result["turn_s"], 2);
        }

        [Fact]
        public void The_block_call_gives_only_the_reason_for_a_craft_that_cannot_turn()
        {
            var result = BlockCalls.TurnTime(null, new TurnCraft { MoiPitch = 13.8, MoiYaw = 13.8 });
            Assert.Equal("no usable torque", result["error"]);
            Assert.False(result.ContainsKey("turn_time"));
        }
    }
}
