// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
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
            // The stop asks for no rate at all, so kOS brakes whatever the cap: never kp x error, which could speed a light craft up.
            Assert.Equal(0, plan.Stop.Kp);
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

        private static Dictionary<string, object> Now(double ut, double error, double rate, double leftS, bool aligned = false, double monoprop = 10)
        {
            return new Dictionary<string, object>
            {
                { "ut", ut }, { "error", error }, { "rate", rate }, { "left_s", leftS }, { "aligned", aligned }, { "monoprop", monoprop },
                { "energy", -2.5e6 }, { "speed", 2200.0 },
            };
        }

        /// <summary>
        /// Flies e17d's 142° turn tick by tick (50 Hz) against a craft that turns as commanded: RCS on adds alpha towards the coast
        /// rate in the spin and takes it away in the stop; RCS off holds the rate in the coast; the wheels settle the rest.
        /// </summary>
        private static List<IDictionary<string, object>> Fly(double angle, double leftS, out IDictionary<string, object> report)
        {
            var craft = E17d();
            IDictionary<string, object> turn = TurnPlan.Call(new Dictionary<string, object> { { "angle", angle }, { "left_s", leftS } }, craft);
            var alphaW = 1 / 95.0 * 180 / Math.PI;
            double ut = 1000, error = angle, rate = 0, mono = 10;
            var ticks = new List<IDictionary<string, object>>();
            report = null;
            for (var i = 0; i < 50 * 200; i++)
            {
                var aligned = error < 2 && rate < 0.5;
                turn = TurnPlan.Step(turn, Now(ut, error, rate, leftS - (ut - 1000), aligned, mono), () => craft);
                ticks.Add(turn);
                if (turn.ContainsKey("report")) report = (IDictionary<string, object>)turn["report"];
                if ((bool)turn["done"]) break;
                var phase = (string)turn["phase"];
                var alpha = (double)turn["alpha"];
                const double dt = 0.02;
                if ((bool)turn["rcs_on"]) mono -= 0.001;
                if (phase == "spin") rate = Math.Min((double)turn["coast_rate"], rate + alpha * dt);
                else if (phase == "stop") rate = Math.Max(0, rate - alpha * dt);
                else if (phase == "settle") rate = Math.Min(rate + alphaW * dt, Math.Sqrt(2 * alphaW * Math.Max(0, error)));
                error = Math.Max(0, error - rate * dt);
                ut += dt;
            }
            return ticks;
        }

        [Fact]
        public void RCS_is_commanded_only_in_the_two_bursts_never_in_the_coast_or_the_wheels_settle()
        {
            IDictionary<string, object> report;
            var ticks = Fly(142, 60, out report);
            Assert.True((bool)ticks[ticks.Count - 1]["done"]);
            var phases = new List<string>();
            foreach (var tick in ticks)
            {
                var phase = (string)tick["phase"];
                Assert.Equal(phase == "spin" || phase == "stop", (bool)tick["rcs_on"]);
                if (phases.Count == 0 || phases[phases.Count - 1] != phase) phases.Add(phase);
            }
            Assert.Equal(new[] { "spin", "coast", "stop", "settle" }, phases);
            // Each phase hands kOS its tuning as it begins, and only then.
            Assert.Equal(3, ticks.FindAll(t => t.ContainsKey("tuning")).Count);
            // Done within the minute it had, with one report.
            Assert.True((double)ticks[ticks.Count - 1]["ut"] - 1000 < 60);
            Assert.NotNull(report);
            Assert.Single(ticks.FindAll(t => t.ContainsKey("report")));
        }

        [Fact]
        public void The_turns_one_line_gives_the_planned_and_flown_rate_the_bursts_the_monoprop_the_leftover_and_the_push()
        {
            IDictionary<string, object> report;
            Fly(142, 60, out report);
            var plan = TurnPlan.Plan(E17d(), 142, 60);
            Assert.Equal(plan.CoastRateDegS, (double)report["coast_dps"], 6);
            Assert.InRange((double)report["flown_dps"], plan.CoastRateDegS * 0.99, plan.CoastRateDegS * 1.01);
            Assert.InRange((double)report["spin_s"], plan.SpinS - 0.05, plan.SpinS + 0.05);
            // The stop ends on its time or once the craft has stopped (under 5% of the coast rate), whichever comes first.
            Assert.InRange((double)report["stop_s"], plan.StopS * (1 - TurnPlan.StoppedShare) - 0.05, plan.StopS + 0.05);
            Assert.True((double)report["monoprop_used"] > 0);
            Assert.InRange((double)report["left_deg"], 0, plan.WheelsDeg + 0.5);
            Assert.Equal(0.0, report["corrections"]);
            Assert.Equal(0.0, report["dv_mps"]);
        }

        [Fact]
        public void A_stop_burst_that_leaves_a_big_error_late_takes_a_burst_pair_and_the_third_reports()
        {
            var craft = E17d();
            IDictionary<string, object> turn = TurnPlan.Call(new Dictionary<string, object> { { "angle", 142.0 }, { "left_s", 60.0 } }, craft);
            turn = TurnPlan.Step(turn, Now(0, 142, 0, 60), () => craft);
            for (var pair = 1; pair <= 3; pair++)
            {
                // Put the turn at the end of a stop burst with 40 degrees still to go and 20 s left.
                var at = (Dictionary<string, object>)turn;
                at["phase"] = "stop";
                at["ut"] = 0.0;
                turn = TurnPlan.Step(turn, Now(100, 40, 0, 20), () => craft);
                if (pair < 3)
                {
                    Assert.Equal("spin", turn["phase"]);
                    Assert.Equal(true, turn["rcs_on"]);
                    Assert.Equal((double)pair, turn["corrections"]);
                    Assert.True(turn.ContainsKey("tuning"));
                    Assert.Equal(TurnPlan.Plan(craft, 40, 20).CoastRateDegS, (double)turn["coast_rate"], 6);
                }
                else
                {
                    Assert.Equal("settle", turn["phase"]);
                    Assert.Equal(false, turn["rcs_on"]);
                    Assert.Equal(2.0, ((IDictionary<string, object>)turn["report"])["corrections"]);
                }
            }
            // The first plan's rate stays the one reported.
            Assert.Equal(TurnPlan.Plan(craft, 142, 60).CoastRateDegS, (double)((IDictionary<string, object>)turn["report"])["coast_dps"], 6);
        }

        [Fact]
        public void The_push_is_the_change_in_orbital_energy_over_the_speed_and_the_settle_ends_when_Aligned_or_after_30_s()
        {
            var craft = E17d();
            IDictionary<string, object> turn = TurnPlan.Call(new Dictionary<string, object> { { "angle", 142.0 }, { "left_s", 60.0 } }, craft);
            turn = TurnPlan.Step(turn, Now(0, 142, 0, 60), () => craft);
            ((Dictionary<string, object>)turn)["phase"] = "stop";
            var now = Now(30, 3, 0, 30);
            now["energy"] = -2.5e6 + 110;
            turn = TurnPlan.Step(turn, now, () => craft);
            Assert.Equal(0.05, (double)((IDictionary<string, object>)turn["report"])["dv_mps"], 6);
            Assert.False((bool)TurnPlan.Step(turn, Now(40, 2.5, 0.2, 20), () => craft)["done"]);
            Assert.True((bool)TurnPlan.Step(turn, Now(40, 1.5, 0.2, 20, true), () => craft)["done"]);
            Assert.True((bool)TurnPlan.Step(turn, Now(60.1, 2.5, 0.2, 0), () => craft)["done"]);
            Assert.False(TurnPlan.Step(turn, Now(40, 1.5, 0.2, 20, true), () => craft).ContainsKey("report"));
        }

        [Fact]
        public void A_coast_that_runs_well_past_its_plan_stops_wherever_the_craft_points()
        {
            var craft = E17d();
            IDictionary<string, object> turn = TurnPlan.Call(new Dictionary<string, object> { { "angle", 142.0 }, { "left_s", 60.0 } }, craft);
            turn = TurnPlan.Step(turn, Now(0, 142, 0, 60), () => craft);
            ((Dictionary<string, object>)turn)["phase"] = "coast";
            var coast = (double)turn["coast_s"];
            Assert.Equal("coast", TurnPlan.Step(turn, Now(2 * coast + TurnPlan.CoastSlackSeconds - 0.1, 90, 0, 10), () => craft)["phase"]);
            Assert.Equal("stop", TurnPlan.Step(turn, Now(2 * coast + TurnPlan.CoastSlackSeconds + 0.1, 90, 0, 10), () => craft)["phase"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_turn_ticks_take_under_5_ms()
        {
            // ADR 0014 guardrail 6: BURSTTURN is called every tick of a turn; 1,000 ticks (20 s at 50 Hz) in 5 ms.
            var craft = E17d();
            var start = TurnPlan.Call(new Dictionary<string, object> { { "angle", 142.0 }, { "left_s", 60.0 } }, craft);
            var coasting = TurnPlan.Step(start, Now(0, 142, 0, 60), () => craft);
            ((Dictionary<string, object>)coasting)["phase"] = "coast";
            var now = Now(1, 100, 3, 59);
            Timing.Within(5, () =>
            {
                for (var i = 0; i < 1000; i++) TurnPlan.Step(coasting, now, () => craft);
            });
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
