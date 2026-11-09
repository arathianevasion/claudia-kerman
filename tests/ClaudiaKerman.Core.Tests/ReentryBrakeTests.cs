// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #954: when a powered finish in <c>reentry</c> may start braking (<c>BRAKEHEIGHTS</c> and <c>BRAKETICK</c>): chutes first, engines last.
    /// Flight m20261109-140705-ae3a braked at 1.7 km and 150 m/s under chutes still opening and tumbled the lander.
    /// </summary>
    public sealed class ReentryBrakeTests
    {
        private const double AUp = 12, G = 2.94, Soft = 2, End = 10;

        private static List<object> NoDrag()
        {
            return Enumerable.Repeat((object)0.0, 20).ToList();
        }

        private static IDictionary<string, object> Heights(double vDown, double radar, double aUp = AUp, List<object> ks = null)
        {
            return BlockCalls.BrakeHeights(new Dictionary<string, object>
            {
                { "v_down", vDown }, { "a_up", aUp }, { "g", G }, { "ks", ks ?? NoDrag() }, { "radar", radar }, { "soft", Soft }, { "end", End },
            });
        }

        private static List<object> Chutes(params string[] states)
        {
            return states.Select((s, i) => (object)new Dictionary<string, object> { { "title", "Chute " + i }, { "state", s } }).ToList();
        }

        /// <summary>Chutes as the adapter reads them, all drogues or all mains.</summary>
        private static List<object> Set(bool drogue, params string[] states)
        {
            return states.Select((s, i) => (object)new Dictionary<string, object> { { "title", "Chute " + i }, { "state", s }, { "drogue", drogue } }).ToList();
        }

        /// <summary>One tick. <paramref name="openS"/>: seconds since the chutes all read deployed; <paramref name="decel"/>: the sampled slowing, m/s²; <paramref name="calledS"/>: seconds since both sets were called, -1 for not called (the addon carries both from tick to tick); <paramref name="rate"/>: deg/s.</summary>
        private static IDictionary<string, object> Tick(double radar, double speed, double brakeH, double lastH, List<object> chutes, double throttle = 0, double off = 0, double openS = 20, double decel = 0, List<object> uids = null, double calledS = -1, double rate = 0)
        {
            var ask = new Dictionary<string, object>
            {
                { "radar", radar }, { "speed", speed }, { "brake_h", brakeH }, { "last_h", lastH },
                { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "chutes", chutes },
                { "throttle", throttle }, { "off_deg", off }, { "rate_deg", rate },
                { "now", 100.0 }, { "open_ut", 100.0 - openS }, { "decel", decel },
            };
            if (calledS >= 0)
            {
                ask["drogues_called"] = true;
                ask["mains_called"] = true;
                ask["drogue_call_ut"] = 100.0 - calledS;
                ask["main_call_ut"] = 100.0 - calledS;
            }
            if (uids != null) ask["uids"] = uids;
            return BlockCalls.BrakeTick(ask);
        }

        private static double D(IDictionary<string, object> answer, string key)
        {
            return Convert.ToDouble(answer[key]);
        }

        // ---- The stopping distance, as reentry_stop_distance had it ----

        [Fact]
        public void With_no_drag_the_stopping_distance_is_the_kinematic_one()
        {
            // (100^2 - 2^2) / (2 x 10) = 499.8 m, over twenty 50 m steps.
            Assert.Equal(499.8, ReentryBrake.StopDistance(100, 10, new double[20], 50, 2), 2);
        }

        [Fact]
        public void With_even_drag_the_stopping_distance_is_the_closed_form_one()
        {
            // net 10 m/s^2, k 1e-3 /m from 100 m/s to 0: ln(1 + k v^2 / net) / (2 k) = 500 ln 2 = 346.57 m.
            Assert.Equal(346.57, ReentryBrake.StopDistance(100, 10, Enumerable.Repeat(1e-3, 10).ToArray(), 100, 0), 2);
        }

        [Fact]
        public void A_fall_the_engines_cant_stop_within_the_steps_is_minus_one()
        {
            Assert.Equal(-1, ReentryBrake.StopDistance(100, -1, new double[4], 50, 2));
            Assert.Equal(-1, D(Heights(300, 1000), "brake_h"));
        }

        // ---- The two heights ----

        [Fact]
        public void The_brake_height_holds_a_sixth_back_and_adds_the_end_height_and_a_second_of_fall()
        {
            // (12^2 - 2^2) / (2 x (12/1.2 - 2.94)) = 140 / 14.12 = 9.915, + 10 + 12 = 31.915.
            Assert.Equal(31.915, D(Heights(12, 1000), "brake_h"), 3);
        }

        [Fact]
        public void A_craft_that_only_stops_on_all_its_thrust_gets_that_figure()
        {
            // Net at 10 m/s^2 of a sixth held back is short of 2.94 only at a_up under 3.53: 3.4 gives -0.107 net, so no stop, then all of it.
            var heights = Heights(5, 1000, 3.4);
            Assert.Equal((25 - 4) / (2 * (3.4 - G)) + End + 5, D(heights, "brake_h"), 6);
        }

        [Fact]
        public void The_last_resort_height_is_full_thrust_with_no_margin()
        {
            // (150^2 - 2^2) / (2 x 9.06) = 1241.5 m: no sixth, no end height, no second of fall; half a second of the fall for the 0.25 s between samples.
            Assert.Equal((150.0 * 150 - 4) / (2 * (AUp - G)) + 75, D(Heights(150, 5000), "last_h"), 6);
        }

        [Fact]
        public void Drag_shortens_both_heights()
        {
            var ks = Enumerable.Repeat((object)2e-3, 20).ToList();
            Assert.True(D(Heights(150, 5000, AUp, ks), "last_h") < D(Heights(150, 5000), "last_h"));
            Assert.True(D(Heights(150, 5000, AUp, ks), "brake_h") < D(Heights(150, 5000), "brake_h"));
        }

        [Fact]
        public void No_reading_gives_heights_that_never_start_a_burn_by_themselves()
        {
            var heights = BlockCalls.BrakeHeights(null);
            Assert.Equal("", heights["error"]);
            Assert.Equal(-1.0, heights["brake_h"]);
            Assert.Equal(-1.0, heights["last_h"]);
        }

        // ---- Whether braking may start (the acceptance cases) ----

        [Fact]
        public void Chutes_opening_at_the_flights_1700_m_and_150_m_s_mean_not_yet()
        {
            var heights = Heights(150, 5000);
            var tick = Tick(1700, 150, D(heights, "brake_h"), D(heights, "last_h"), Chutes("semi"));
            Assert.True(D(heights, "brake_h") > 1700, "today's rule started it here");
            Assert.Equal(false, tick["start"]);
            Assert.Equal(false, tick["open"]);
            Assert.Equal("Chute 0 semi", tick["chutes"]);
        }

        [Fact]
        public void A_chute_only_filling_never_fires_the_last_resort_at_any_height()
        {
            var heights = Heights(150, 5000);
            var lastH = D(heights, "last_h");
            Assert.Equal(false, Tick(lastH, 150, D(heights, "brake_h"), lastH, Chutes("semi"), calledS: 5)["start"]);
            Assert.Equal(false, Tick(100, 150, D(heights, "brake_h"), lastH, Chutes("semi"), calledS: 5)["start"]);
        }

        [Fact]
        public void Fully_open_at_12_m_s_and_30_m_braking_starts()
        {
            var heights = Heights(12, 500);
            var tick = Tick(30, 12, D(heights, "brake_h"), D(heights, "last_h"), Chutes("deployed", "deployed"));
            Assert.Equal(true, tick["start"]);
            Assert.Equal(true, tick["open"]);
            Assert.Equal("chutes open", tick["why"]);
        }

        [Fact]
        public void Fully_open_above_the_brake_height_it_waits()
        {
            var heights = Heights(12, 500);
            var tick = Tick(100, 12, D(heights, "brake_h"), D(heights, "last_h"), Chutes("deployed"));
            Assert.Equal(false, tick["start"]);
            Assert.Equal(true, tick["open"]);
        }

        [Theory]
        [InlineData("stowed")]
        [InlineData("armed")]
        public void A_chute_still_packed_past_its_second_fires_the_last_resort_at_its_usual_height(string state)
        {
            var heights = Heights(150, 5000);
            var lastH = D(heights, "last_h");
            var tick = Tick(lastH, 150, D(heights, "brake_h"), lastH, Chutes("deployed", state), calledS: 1.5);
            Assert.Equal(true, tick["start"]);
            Assert.Equal(false, tick["open"]);
            Assert.Equal("last resort", tick["why"]);
            Assert.Equal(false, Tick(lastH + 1, 150, D(heights, "brake_h"), lastH, Chutes("deployed", state), calledS: 1.5)["start"]);
        }

        [Fact]
        public void The_last_resort_height_is_real_even_above_the_craft_so_a_fall_past_it_starts_at_once()
        {
            // 250 m/s: (250^2 - 2^2) / (2 x 9.06) = 3449 m, worked past the ground from 1,000 m of radar, as a sample of a fall already past the height reads.
            var heights = Heights(250, 1000);
            Assert.Equal((250.0 * 250 - 4) / (2 * (AUp - G)) + 125, D(heights, "last_h"), 6);
            var late = Tick(1000, 250, -1, D(heights, "last_h"), Chutes("armed"), calledS: 2);
            Assert.Equal(true, late["start"]);
            Assert.Equal("last resort", late["why"]);
        }

        [Fact]
        public void The_last_resort_height_counts_the_drag_past_the_ground_at_the_last_step()
        {
            // Twenty steps of 50 m at k 1e-3, then on at the same k: the closed form over the whole fall.
            var ks = Enumerable.Repeat((object)1e-3, 20).ToList();
            var net = AUp - G;
            var whole = Math.Log((250.0 * 250 + net / 1e-3) / (4 + net / 1e-3)) / (2 * 1e-3);
            Assert.True(whole > 1000, "the fall must need more than the radar");
            Assert.Equal(whole + 125, D(Heights(250, 1000, AUp, ks), "last_h"), 3);
        }

        [Fact]
        public void A_fall_full_thrust_would_never_stop_is_minus_one_and_waits_for_the_air()
        {
            Assert.Equal(-1, D(Heights(300, 1000, 2), "last_h"));
            Assert.Equal(false, Tick(40000, 1200, -1, -1, Chutes("stowed"), calledS: 5)["start"]);
            Assert.Equal(true, Tick(40000, 390, -1, -1, Chutes("stowed"), calledS: 5)["start"]);
            Assert.Equal(true, Tick(2900, 1200, -1, -1, Chutes("armed"), calledS: 5)["start"]);
        }

        [Fact]
        public void High_on_a_shallow_path_a_height_above_the_craft_still_waits_for_the_air()
        {
            // 1,200 m/s at 40 km: past any height, but the air hasn't done its work.
            Assert.Equal(false, Tick(40000, 1200, -1, 79000, Chutes("stowed"), calledS: 5)["start"]);
        }

        [Fact]
        public void Fully_open_on_a_fall_the_engines_cant_stop_the_air_waits_as_today()
        {
            Assert.Equal(false, Tick(40000, 1200, -1, -1, Chutes("deployed"))["start"]);
            Assert.Equal(true, Tick(2000, 350, -1, -1, Chutes("deployed"))["start"]);
        }

        // ---- Fully open is not yet slowed: a canopy that reads deployed is still filling ----

        [Fact]
        public void Fully_open_at_1000_m_and_140_m_s_it_holds_while_the_canopies_fill()
        {
            // a_up 12, g 2.94, no drag: neither stop fits in 1,000 m, so brake_h is -1 and the radar rule alone would start at once.
            var heights = Heights(140, 1000);
            Assert.Equal(-1, D(heights, "brake_h"));
            Assert.True(D(heights, "last_h") > 1000, "the last-resort height is above the craft");
            var tick = Tick(1000, 140, D(heights, "brake_h"), D(heights, "last_h"), Set(false, "deployed"), openS: 0, decel: 40);
            Assert.Equal(false, tick["start"]);
            Assert.Equal(true, tick["open"]);
        }

        [Theory]
        [InlineData(0, 0, false)] // the first tick fully open
        [InlineData(3, 0.5, false)] // not long enough to call it settled, however steady
        [InlineData(5, 20, false)] // still slowing hard
        [InlineData(5, 0.5, true)] // settled
        [InlineData(5, -0.5, true)] // settled, the noise either way
        [InlineData(7.9, 20, false)]
        [InlineData(8.5, 20, true)] // out of time to wait
        public void Fully_open_the_radar_rule_waits_until_the_speed_settles_or_about_8_seconds(double openS, double decel, bool expected)
        {
            var tick = Tick(30, 12, 32, 13, Chutes("deployed"), openS: openS, decel: decel);
            Assert.Equal(expected, tick["start"]);
            if (expected) Assert.Equal("chutes open", tick["why"]);
        }

        [Fact]
        public void A_fast_fall_balanced_on_a_canopy_just_begun_to_fill_doesnt_read_as_settled()
        {
            // Terminal on the half-open canopy: the slowing reads near nothing while the full canopy has barely started.
            Assert.Equal(false, Tick(1000, 140, -1, 1151, Set(false, "deployed"), openS: 3, decel: 0.2)["start"]);
        }

        [Theory]
        [InlineData(1, 40)]
        [InlineData(3, 0.2)]
        public void While_the_canopies_fill_not_even_the_last_resort_height_starts_the_burn(double openS, double decel)
        {
            var tick = Tick(700, 140, -1, 1151, Set(false, "deployed"), throttle: 1, openS: openS, decel: decel);
            Assert.Equal(false, tick["start"]);
            Assert.Equal(0.0, D(tick, "throttle"));
        }

        [Fact]
        public void The_time_fully_open_starts_on_the_first_tick_and_resets_when_they_are_not()
        {
            var first = BlockCalls.BrakeTick(new Dictionary<string, object> { { "now", 50.0 }, { "open_ut", -1.0 }, { "chutes", Chutes("deployed") } });
            Assert.Equal(50.0, D(first, "open_ut"));
            var later = BlockCalls.BrakeTick(new Dictionary<string, object> { { "now", 58.0 }, { "open_ut", 50.0 }, { "chutes", Chutes("deployed") } });
            Assert.Equal(50.0, D(later, "open_ut"));
            var shut = BlockCalls.BrakeTick(new Dictionary<string, object> { { "now", 60.0 }, { "open_ut", 50.0 }, { "chutes", Chutes("semi") } });
            Assert.Equal(-1.0, D(shut, "open_ut"));
        }

        // ---- The slowing is sampled here, so it is live during a burn too ----

        private static IDictionary<string, object> Sample(double now, double speed, double sampleUt, double sampleSpeed, double decel)
        {
            return BlockCalls.BrakeTick(new Dictionary<string, object> { { "now", now }, { "speed", speed }, { "sample_ut", sampleUt }, { "sample_speed", sampleSpeed }, { "decel", decel }, { "chutes", Chutes("deployed") } });
        }

        [Fact]
        public void The_slowing_is_worked_out_a_quarter_second_apart()
        {
            var tick = Sample(10.25, 40, 10, 50, 0);
            Assert.Equal(40.0, D(tick, "decel"));
            Assert.Equal(10.25, D(tick, "sample_ut"));
            Assert.Equal(40.0, D(tick, "sample_speed"));
        }

        [Fact]
        public void Between_samples_the_slowing_is_held()
        {
            var tick = Sample(10.1, 40, 10, 50, 7);
            Assert.Equal(7.0, D(tick, "decel"));
            Assert.Equal(10.0, D(tick, "sample_ut"));
            Assert.Equal(50.0, D(tick, "sample_speed"));
        }

        [Fact]
        public void The_first_tick_starts_the_sample()
        {
            var tick = BlockCalls.BrakeTick(new Dictionary<string, object> { { "now", 10.0 }, { "speed", 40.0 } });
            Assert.Equal(10.0, D(tick, "sample_ut"));
            Assert.Equal(40.0, D(tick, "sample_speed"));
            Assert.Equal(0.0, D(tick, "decel"));
        }

        [Fact]
        public void A_canopy_still_slowing_the_craft_is_not_settled_on_a_frozen_small_reading()
        {
            // Slowing at 20 m/s^2 from this tick's sample, though the held figure was small: 5 s fully open and not settled.
            var ask = new Dictionary<string, object> { { "now", 100.0 }, { "open_ut", 95.0 }, { "speed", 40.0 }, { "sample_ut", 99.75 }, { "sample_speed", 45.0 }, { "decel", 0.2 }, { "throttle", 1.0 }, { "chutes", Chutes("deployed") } };
            Assert.Equal(0.0, D(BlockCalls.BrakeTick(ask), "throttle"));
        }

        // ---- A cut or missing chute isn't waited for ----

        [Fact]
        public void A_cut_chute_isnt_waited_for_so_the_rest_fully_open_brake_at_the_brake_height()
        {
            var tick = Tick(30, 12, 32, 13, Chutes("deployed", "cut"));
            Assert.Equal(true, tick["open"]);
            Assert.Equal(true, tick["start"]);
            Assert.Equal("chutes open", tick["why"]);
            Assert.Equal("Chute 0 deployed, Chute 1 cut", tick["chutes"]);
        }

        [Fact]
        public void With_only_cut_chutes_it_brakes_as_with_none()
        {
            var tick = Tick(30, 12, 32, 13, Chutes("cut"));
            Assert.Equal(false, tick["open"]);
            Assert.Equal(true, tick["start"]);
            Assert.Equal("radar", tick["why"]);
        }

        // ---- Rule 1: no thrust from a chute's call until it is fully open and settled ----

        private const double FullThrottle = 1;

        private static void AssertNoThrust(IDictionary<string, object> tick, string why)
        {
            Assert.Equal(0.0, D(tick, "throttle"));
            Assert.Equal(why, tick["zero_why"]);
        }

        [Theory]
        [InlineData("armed", 0.1)]
        [InlineData("stowed", 0.1)] // called this tick: the state hasn't moved yet
        [InlineData("semi", 2)]
        public void An_engine_already_burning_stops_the_tick_a_chute_is_called(string state, double calledS)
        {
            AssertNoThrust(Tick(300, 90, 400, 200, Chutes(state), FullThrottle, calledS: calledS), ReentryBrake.WhyChuteOpening);
        }

        [Theory]
        [InlineData(0, 40, 0)] // the first tick fully open
        [InlineData(3, 0.2, 0)] // not long enough to call it settled
        [InlineData(5, 20, 0)] // still slowing hard
        [InlineData(5, 0.5, 1)] // settled: thrust returns
        [InlineData(7.9, 20, 0)]
        [InlineData(8.5, 20, 1)] // out of time to wait: thrust returns
        public void The_throttle_stays_zero_until_the_canopies_settle_or_8_seconds_then_it_returns(double openS, double decel, double expected)
        {
            var tick = Tick(300, 90, 400, 200, Chutes("deployed"), FullThrottle, openS: openS, decel: decel);
            Assert.Equal(expected, D(tick, "throttle"));
            Assert.Equal(expected > 0 ? "" : ReentryBrake.WhyChuteOpening, tick["zero_why"]);
        }

        [Fact]
        public void Drogues_settled_and_then_the_mains_called_zeroes_the_thrust_again()
        {
            var drogues = Set(true, "deployed");
            var settled = Tick(300, 90, 400, 200, drogues.Concat(Set(false, "stowed")).ToList(), FullThrottle, openS: 9);
            Assert.Equal(FullThrottle, D(settled, "throttle"));
            AssertNoThrust(Tick(300, 90, 400, 200, drogues.Concat(Set(false, "armed")).ToList(), FullThrottle, openS: 9), ReentryBrake.WhyChuteOpening);
            AssertNoThrust(Tick(300, 90, 400, 200, drogues.Concat(Set(false, "semi")).ToList(), FullThrottle, openS: 9), ReentryBrake.WhyChuteOpening);
        }

        [Fact]
        public void A_set_called_starts_the_fully_open_time_again_only_once_it_is_really_opening()
        {
            var called = Tick(300, 90, 400, 200, Set(true, "deployed").Concat(Set(false, "armed")).ToList(), openS: 9);
            Assert.Equal(91.0, D(called, "open_ut"));
            var opening = Tick(300, 90, 400, 200, Set(true, "deployed").Concat(Set(false, "semi")).ToList(), openS: 9);
            Assert.Equal(-1.0, D(opening, "open_ut"));
        }

        /// <summary>Flies a tick on from the last, handing back what the addon holds between ticks.</summary>
        private static void CarryOn(Dictionary<string, object> ask, IDictionary<string, object> answer)
        {
            foreach (var key in new[] { "open_ut", "settled", "drogue_call_ut", "main_call_ut", "sample_ut", "sample_speed", "decel" }) ask[key] = answer[key];
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)] // still slowing, over the 1 m/s^2 of a settled speed
        public void Mains_called_at_1500_m_that_stay_stowed_leave_the_last_resort_at_its_usual_height(double slowing)
        {
            // Drogues settled, 140 m/s: stopping takes about 1,080 m, so a settle timer restarted when the mains fail would start the burn too low.
            const double Dt = 0.02, LastH = 1080;
            double radar = 1500, speed = 140, now = 100;
            var ask = new Dictionary<string, object>
            {
                { "brake_h", -1.0 }, { "last_h", LastH }, { "shield_speed", 400.0 }, { "shield_agl", 300.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 },
                { "drogues_called", true }, { "drogue_call_ut", 70.0 }, { "mains_called", true }, { "chutes", Set(true, "deployed").Concat(Set(false, "stowed")).ToList() },
            };
            while (true)
            {
                ask["now"] = now;
                ask["radar"] = radar;
                ask["speed"] = speed;
                var tick = BlockCalls.BrakeTick(ask);
                Assert.Equal(80.0, D(tick, "open_ut"));
                if ((bool)tick["start"])
                {
                    Assert.Equal("last resort", tick["why"]);
                    Assert.InRange(radar, LastH - 140 * Dt - 1, LastH);
                    return;
                }
                Assert.True(radar > LastH, "no start at the last-resort height, radar " + radar);
                CarryOn(ask, tick);
                now += Dt;
                radar -= speed * Dt;
                speed -= slowing * Dt;
            }
        }

        [Fact]
        public void Once_settled_the_burns_own_slowing_doesnt_take_the_throttle_away()
        {
            // Terminal under a full canopy at 40 m/s; the engine then slows the craft at 8 m/s^2, which reads as a canopy still slowing it.
            const double Dt = 0.02;
            double speed = 40, now = 0, onAt = -1;
            var ask = new Dictionary<string, object>
            {
                { "radar", 300.0 }, { "brake_h", 400.0 }, { "last_h", 200.0 }, { "shield_speed", 400.0 }, { "shield_agl", 300.0 }, { "throttle", FullThrottle },
                { "open_ut", -1.0 }, { "chutes", Chutes("deployed") },
            };
            for (var i = 0; i <= 500; i++, now = i * Dt)
            {
                ask["now"] = now;
                ask["speed"] = speed;
                var tick = BlockCalls.BrakeTick(ask);
                var throttle = D(tick, "throttle");
                if (onAt < 0 && throttle > 0) onAt = now;
                if (onAt >= 0)
                {
                    Assert.Equal(FullThrottle, throttle);
                    Assert.Equal("", tick["zero_why"]);
                }
                CarryOn(ask, tick);
                speed = Math.Max(5, speed - (throttle > 0 ? 8 : 0.2) * Dt);
            }
            Assert.InRange(onAt, ReentryBrake.SettleMinSeconds, ReentryBrake.SettleMinSeconds + 0.3);
        }

        [Fact]
        public void Settled_drogues_still_give_way_to_the_mains_in_their_turn()
        {
            var drogues = Set(true, "deployed");
            var ask = new Dictionary<string, object>
            {
                { "now", 100.0 }, { "speed", 40.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 }, { "settled", true }, { "decel", 0.2 },
                { "chutes", drogues.Concat(Set(false, "stowed")).ToList() },
            };
            Assert.Equal(FullThrottle, D(BlockCalls.BrakeTick(ask), "throttle"));

            ask["mains_called"] = true;
            ask["chutes"] = drogues.Concat(Set(false, "armed")).ToList();
            var called = BlockCalls.BrakeTick(ask);
            AssertNoThrust(called, ReentryBrake.WhyChuteOpening);
            Assert.Equal(true, called["settled"]);

            ask["chutes"] = drogues.Concat(Set(false, "semi")).ToList();
            var opening = BlockCalls.BrakeTick(ask);
            AssertNoThrust(opening, ReentryBrake.WhyChuteOpening);
            Assert.Equal(false, opening["settled"]);
            Assert.Equal(-1.0, D(opening, "open_ut"));

            CarryOn(ask, opening);
            ask["chutes"] = drogues.Concat(Set(false, "deployed")).ToList();
            ask["now"] = 110.0;
            var open = BlockCalls.BrakeTick(ask);
            AssertNoThrust(open, ReentryBrake.WhyChuteOpening);
            Assert.Equal(110.0, D(open, "open_ut"));
        }

        [Fact]
        public void Healthy_chutes_at_1000_m_and_140_m_s_give_no_thrust_while_filling_below_the_old_floor_too()
        {
            // The old 80% floor of a 1,000 m deploy height was 800 m; a stop from 140 m/s takes about 1,080 m on Duna's gravity.
            foreach (var radar in new[] { 1000.0, 800.0, 500.0, 100.0 })
            {
                foreach (var state in new[] { "semi", "deployed" })
                {
                    var tick = Tick(radar, 140, -1, 1151, Set(false, state), FullThrottle, openS: 1, decel: 40, calledS: 3);
                    Assert.Equal(false, tick["start"]);
                    AssertNoThrust(tick, ReentryBrake.WhyChuteOpening);
                }
            }
        }

        // ---- Rule 2: a packed chute is failed only a second after its call ----

        [Theory]
        [InlineData("stowed", 0.9, false)]
        [InlineData("armed", 0.9, false)]
        [InlineData("stowed", 1.1, true)]
        [InlineData("armed", 1.1, true)]
        public void A_packed_chute_counts_as_failed_only_a_second_after_it_was_called(string state, double calledS, bool exempt)
        {
            // With the others settled, a chute packed past its second no longer holds the thrust back.
            var tick = Tick(300, 90, 400, 1000, Chutes("deployed", state), FullThrottle, calledS: calledS);
            Assert.Equal(exempt ? FullThrottle : 0.0, D(tick, "throttle"));
            Assert.Equal(exempt ? "" : ReentryBrake.WhyChuteOpening, tick["zero_why"]);
            Assert.Equal(exempt, tick["start"]);
        }

        [Fact]
        public void A_cut_chute_is_exempt_so_the_others_settled_give_thrust()
        {
            Assert.Equal(FullThrottle, D(Tick(300, 90, 400, 200, Chutes("deployed", "cut"), FullThrottle, calledS: 0.2), "throttle"));
        }

        [Fact]
        public void A_chute_missing_from_the_group_is_exempt_but_the_rest_are_still_waited_for()
        {
            var uids = new List<object> { "a", "b" };
            Assert.Equal(FullThrottle, D(Tick(300, 90, 400, 200, Chutes("deployed"), FullThrottle, uids: uids), "throttle"));
            AssertNoThrust(Tick(300, 90, 400, 200, Chutes("semi"), FullThrottle, uids: uids), ReentryBrake.WhyChuteOpening);
        }

        [Fact]
        public void A_cut_chute_gives_a_last_resort_for_the_rest_only_once_nothing_is_filling()
        {
            Assert.Equal(false, Tick(1200, 140, -1, 1300, Set(false, "semi", "cut"))["start"]);
            var tick = Tick(1200, 140, -1, 1300, Set(false, "stowed", "cut"));
            Assert.Equal(true, tick["start"]);
            Assert.Equal("last resort", tick["why"]);
            Assert.Equal(false, Tick(1200, 140, -1, 1100, Set(false, "stowed", "cut"))["start"]);
        }

        [Fact]
        public void Drogues_not_yet_called_on_a_block_starting_under_the_last_resort_height_arent_failed_on_the_first_tick()
        {
            // The block starts at 1.1 km, under the drogues' 3,000 m last call and the last-resort height: the tick before chute_tick's call reads them stowed and not called, and only being failed could start the burn.
            var first = BlockCalls.BrakeTick(new Dictionary<string, object> { { "radar", 1100.0 }, { "speed", 140.0 }, { "brake_h", -1.0 }, { "last_h", 1151.0 }, { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "throttle", FullThrottle }, { "now", 50.0 }, { "chutes", Set(true, "stowed") } });
            Assert.Equal(false, first["start"]);
            Assert.Equal(-1.0, D(first, "drogue_call_ut"));
            Assert.Equal("", first["zero_why"]);
        }

        [Fact]
        public void The_call_time_is_held_per_set_and_resets_when_the_set_is_not_called()
        {
            var ask = new Dictionary<string, object> { { "now", 50.0 }, { "drogues_called", true }, { "chutes", Set(true, "stowed").Concat(Set(false, "stowed")).ToList() } };
            var first = BlockCalls.BrakeTick(ask);
            Assert.Equal(50.0, D(first, "drogue_call_ut"));
            Assert.Equal(-1.0, D(first, "main_call_ut"));
            ask["now"] = 60.0;
            ask["drogue_call_ut"] = 50.0;
            ask["mains_called"] = true;
            var later = BlockCalls.BrakeTick(ask);
            Assert.Equal(50.0, D(later, "drogue_call_ut"));
            Assert.Equal(60.0, D(later, "main_call_ut"));
            ask["drogues_called"] = false;
            ask["mains_called"] = false;
            Assert.Equal(-1.0, D(BlockCalls.BrakeTick(ask), "drogue_call_ut"));
        }

        [Fact]
        public void A_chute_armed_some_other_way_is_called_by_its_state()
        {
            // #942's low charge arms the group without chute_tick's flags.
            Assert.Equal(50.0, D(BlockCalls.BrakeTick(new Dictionary<string, object> { { "now", 50.0 }, { "chutes", Set(true, "armed") } }), "drogue_call_ut"));
        }

        [Fact]
        public void A_chute_not_called_is_neither_waited_on_for_thrust_nor_failed()
        {
            var tick = Tick(5000, 300, 400, 1000, Chutes("stowed"), FullThrottle);
            Assert.Equal(FullThrottle, D(tick, "throttle"));
            Assert.Equal(false, tick["start"]);
        }

        // ---- Rule 3: no thrust while spinning ----

        [Theory]
        [InlineData(19, 0.8, "")]
        [InlineData(20, 0.8, "")]
        [InlineData(21, 0, "spinning")]
        public void Over_20_degrees_a_second_the_engines_stop(double rate, double expected, string why)
        {
            var tick = Tick(1000, 100, 0, 0, Chutes(), 0.8, 10, rate: rate);
            Assert.Equal(expected, D(tick, "throttle"));
            Assert.Equal(why, tick["zero_why"]);
        }

        [Fact]
        public void The_reason_is_the_first_that_holds_the_engine()
        {
            Assert.Equal(ReentryBrake.WhyChuteOpening, Tick(300, 90, 400, 200, Chutes("semi"), 1, 45, calledS: 3, rate: 40)["zero_why"]);
            Assert.Equal(ReentryBrake.WhySpinning, Tick(1000, 100, 0, 0, Chutes(), 1, 45, rate: 40)["zero_why"]);
            Assert.Equal(ReentryBrake.WhyOffAim, Tick(1000, 100, 0, 0, Chutes(), 1, 45)["zero_why"]);
            Assert.Equal("", Tick(1000, 100, 0, 0, Chutes(), 1, 10)["zero_why"]);
        }

        /// <summary>
        /// An unreadable rate (a non-number, absent, NaN or infinity) doesn't hold the engine: a reading that never comes must not stop the landing, and the 30 degree guard still catches a tipped craft.
        /// The answer carries -1 so the log says the rate was unread rather than a number kOS can't round.
        /// </summary>
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void An_unreadable_rate_flies_on_the_off_aim_guard_alone(double rate)
        {
            var tick = Tick(1000, 100, 0, 0, Chutes(), 0.8, 10, rate: rate);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal("", tick["zero_why"]);
            Assert.Equal(-1.0, D(tick, "rate_deg"));
            Assert.Equal(0.0, D(Tick(1000, 100, 0, 0, Chutes(), 0.8, 45, rate: rate), "throttle"));
        }

        [Fact]
        public void A_rate_read_is_handed_back_for_the_log()
        {
            Assert.Equal(12.5, D(Tick(1000, 100, 0, 0, Chutes(), 0.8, 10, rate: 12.5), "rate_deg"));
            Assert.Equal(-1.0, D(BlockCalls.BrakeTick(new Dictionary<string, object> { { "throttle", 1.0 } }), "rate_deg"));
        }

        // Today's rule (reentry_brake_due) with no chutes in the group, or chute_mode none (uids empty).
        [Theory]
        [InlineData(1000, -1, 300, true)]
        [InlineData(40000, -1, 1200, false)]
        [InlineData(40000, -1, 390, true)]
        [InlineData(2500, -1, 1200, true)]
        [InlineData(900, 1000, 1200, true)]
        [InlineData(1100, 1000, 300, false)]
        public void No_chutes_in_the_group_brakes_as_today(double radar, double brakeH, double speed, bool expected)
        {
            var tick = Tick(radar, speed, brakeH, -1, Chutes());
            Assert.Equal(expected, tick["start"]);
            Assert.Equal(false, tick["open"]);
            Assert.Equal("radar", tick["why"]);
            Assert.Equal("", tick["chutes"]);
        }

        // ---- The throttle guard ----

        [Theory]
        [InlineData(45, 0.8, 0)]
        [InlineData(30.01, 1, 0)]
        [InlineData(180, 1, 0)]
        [InlineData(30, 0.6, 0.6)]
        [InlineData(10, 0.8, 0.8)]
        [InlineData(0, 1, 1)]
        public void Over_30_degrees_off_the_aim_the_engines_stop_and_within_it_the_throttle_stands(double offDeg, double throttle, double expected)
        {
            Assert.Equal(expected, D(Tick(1000, 100, 0, 0, Chutes(), throttle, offDeg), "throttle"));
        }

        [Fact]
        public void The_guard_holds_on_every_powered_finish_chutes_or_none()
        {
            Assert.Equal(0.0, D(Tick(1000, 100, 0, 0, Chutes("deployed"), 1, 45), "throttle"));
            Assert.Equal(0.0, D(Tick(1000, 100, 0, 0, Chutes(), 1, 45), "throttle"));
            Assert.Equal(1.0, D(Tick(1000, 100, 0, 0, Chutes(), 1, 10), "throttle"));
        }

        [Fact]
        public void No_reading_starts_nothing_and_cuts_the_engines()
        {
            var tick = BlockCalls.BrakeTick(null);
            Assert.Equal("", tick["error"]);
            Assert.Equal(false, tick["start"]);
            Assert.Equal(false, tick["open"]);
            Assert.Equal(0.0, D(tick, "throttle"));
        }

        /// <summary>ADR 0014 guardrail 6: every powered-finish tick asks; 1,000 ticks (20 s at 50 Hz) in 2 ms.</summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_ticks_take_under_2_ms()
        {
            var ask = new Dictionary<string, object>
            {
                { "radar", 1700.0 }, { "speed", 150.0 }, { "brake_h", 1750.0 }, { "last_h", 1240.0 }, { "shield_speed", 400.0 }, { "shield_agl", 3000.0 },
                { "chutes", Set(false, "deployed", "semi") }, { "uids", new List<object> { "a", "b" } }, { "throttle", 0.7 }, { "off_deg", 12.0 }, { "rate_deg", 8.0 },
                { "now", 100.0 }, { "open_ut", -1.0 }, { "decel", 12.0 }, { "drogues_called", true }, { "mains_called", true }, { "drogue_call_ut", 90.0 }, { "main_call_ut", 99.5 },
            };
            Timing.Within(2, () =>
            {
                for (var i = 0; i < 1000; i++) BlockCalls.BrakeTick(ask);
            });
        }

        /// <summary>ADR 0014 guardrail 6: sampled every 0.25 s; 1,000 calls, each over twenty steps with drag, in 4 ms.</summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_height_calls_take_under_4_ms()
        {
            var ask = new Dictionary<string, object>
            {
                { "v_down", 150.0 }, { "a_up", AUp }, { "g", G }, { "ks", Enumerable.Repeat((object)1e-3, 20).ToList() }, { "radar", 2000.0 }, { "soft", Soft }, { "end", End },
            };
            Timing.Within(4, () =>
            {
                for (var i = 0; i < 1000; i++) BlockCalls.BrakeHeights(ask);
            });
        }
    }
}
