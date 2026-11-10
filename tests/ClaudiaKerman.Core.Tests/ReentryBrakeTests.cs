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
            foreach (var key in new[] { "open_ut", "open_count", "settled", "kept_ut", "kept_settled", "kept_drogues", "kept_mains", "drogue_call_ut", "main_call_ut", "sample_ut", "sample_speed", "decel", "last_resort_ut", "unread_ut", "trace_ut", "all_open_ut", "final" }) ask[key] = answer[key];
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
        public void Mains_that_read_semi_and_are_then_cut_leave_the_drogues_settled_and_the_stop_at_its_own_height()
        {
            // Drogues settled, 140 m/s, mains semi at 1,500 m then cut: the stop takes about 1,080 m, not 4 s (560 m) more.
            const double Dt = 0.02, StopH = 1080;
            double radar = 1500, speed = 140, now = 100;
            var drogues = Set(true, "deployed");
            var ask = new Dictionary<string, object>
            {
                { "brake_h", StopH }, { "last_h", StopH }, { "shield_speed", 400.0 }, { "shield_agl", 300.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 }, { "settled", true },
                { "open_count", 1.0 }, { "drogues_called", true }, { "drogue_call_ut", 70.0 }, { "mains_called", true }, { "main_call_ut", 95.0 },
                { "chutes", drogues.Concat(Set(false, "semi")).ToList() },
            };
            var cut = false;
            while (true)
            {
                ask["now"] = now;
                ask["radar"] = radar;
                ask["speed"] = speed;
                if (!cut && radar < 1450)
                {
                    cut = true;
                    ask["chutes"] = drogues.Concat(Set(false, "cut")).ToList();
                }
                var tick = BlockCalls.BrakeTick(ask);
                if (cut) Assert.Equal(80.0, D(tick, "open_ut"));
                if ((bool)tick["start"])
                {
                    Assert.True(cut);
                    Assert.InRange(radar, StopH - 140 * Dt - 1, StopH);
                    return;
                }
                Assert.True(radar > StopH, "no start at the stop height, radar " + radar);
                CarryOn(ask, tick);
                now += Dt;
                radar -= speed * Dt;
            }
        }

        [Fact]
        public void A_chute_opening_normally_holds_the_thrust_until_it_is_open_and_settled()
        {
            var drogues = Set(true, "deployed");
            var ask = new Dictionary<string, object>
            {
                { "now", 100.0 }, { "speed", 40.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 }, { "settled", true }, { "open_count", 1.0 }, { "decel", 0.2 }, { "mains_called", true },
                { "chutes", drogues.Concat(Set(false, "semi")).ToList() },
            };
            var semi = BlockCalls.BrakeTick(ask);
            AssertNoThrust(semi, ReentryBrake.WhyChuteOpening);
            CarryOn(ask, semi);
            ask["now"] = 101.0;
            ask["chutes"] = drogues.Concat(Set(false, "deployed")).ToList();
            var open = BlockCalls.BrakeTick(ask);
            AssertNoThrust(open, ReentryBrake.WhyChuteOpening);
            Assert.Equal(101.0, D(open, "open_ut"));
            CarryOn(ask, open);
            ask["now"] = 101 + ReentryBrake.SettleMinSeconds + 0.5;
            Assert.Equal(FullThrottle, D(BlockCalls.BrakeTick(ask), "throttle"));
        }

        [Fact]
        public void What_was_set_aside_for_a_semi_chute_is_dropped_once_no_chute_is_left()
        {
            var ask = new Dictionary<string, object>
            {
                { "now", 100.0 }, { "speed", 40.0 }, { "open_ut", -1.0 }, { "kept_ut", 80.0 }, { "kept_settled", true }, { "mains_called", true },
                { "chutes", Set(false, "cut") },
            };
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(-1.0, D(tick, "kept_ut"));
            Assert.Equal(false, tick["kept_settled"]);
        }

        [Fact]
        public void A_main_that_reads_deployed_as_the_drogue_is_cut_doesnt_get_the_settled_time_back_under_its_canopy()
        {
            var ask = new Dictionary<string, object>
            {
                { "now", 100.0 }, { "speed", 40.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 }, { "settled", true }, { "open_count", 1.0 }, { "decel", 0.2 }, { "mains_called", true },
                { "chutes", Set(true, "deployed").Concat(Set(false, "semi")).ToList() },
            };
            CarryOn(ask, BlockCalls.BrakeTick(ask));
            ask["now"] = 100.02;
            ask["chutes"] = Set(true, "cut").Concat(Set(false, "deployed")).ToList();
            var tick = BlockCalls.BrakeTick(ask);
            AssertNoThrust(tick, ReentryBrake.WhyChuteOpening);
            Assert.Equal(100.02, D(tick, "open_ut"));
        }

        [Fact]
        public void A_semi_chute_that_reads_deployed_after_a_flicker_is_not_treated_as_cut()
        {
            var drogues = Set(true, "deployed");
            var ask = new Dictionary<string, object>
            {
                { "now", 100.0 }, { "speed", 40.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 }, { "settled", true }, { "open_count", 1.0 }, { "decel", 0.2 }, { "mains_called", true },
                { "chutes", drogues.Concat(Set(false, "semi")).ToList() },
            };
            var semi = BlockCalls.BrakeTick(ask);
            CarryOn(ask, semi);
            ask["now"] = 100.02;
            ask["chutes"] = drogues.Concat(Set(false, "deployed")).ToList();
            var open = BlockCalls.BrakeTick(ask);
            AssertNoThrust(open, ReentryBrake.WhyChuteOpening);
            Assert.Equal(100.02, D(open, "open_ut"));
            Assert.Equal(false, open["settled"]);
            Assert.Equal(-1.0, D(open, "kept_ut"));
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
        public void Mains_going_from_packed_to_deployed_between_ticks_still_have_their_settle_time()
        {
            // Called under their deploy height, with a block tick spanning several physics frames, no tick need read them semi.
            var drogues = Set(true, "deployed");
            var ask = new Dictionary<string, object>
            {
                { "now", 100.0 }, { "speed", 40.0 }, { "throttle", FullThrottle }, { "open_ut", 80.0 }, { "settled", true }, { "decel", 0.2 }, { "mains_called", true },
                { "chutes", drogues.Concat(Set(false, "armed")).ToList() },
            };
            var called = BlockCalls.BrakeTick(ask);
            AssertNoThrust(called, ReentryBrake.WhyChuteOpening);
            Assert.Equal(80.0, D(called, "open_ut"));

            CarryOn(ask, called);
            ask["now"] = 100.1;
            ask["chutes"] = drogues.Concat(Set(false, "deployed")).ToList();
            var open = BlockCalls.BrakeTick(ask);
            AssertNoThrust(open, ReentryBrake.WhyChuteOpening);
            Assert.Equal(100.1, D(open, "open_ut"));
            Assert.Equal(false, open["settled"]);

            CarryOn(ask, open);
            ask["now"] = 104.2;
            Assert.Equal(FullThrottle, D(BlockCalls.BrakeTick(ask), "throttle"));
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

        // ---- A last resort under way (player, 2026-11-09): a later set called doesn't stop it ----

        /// <summary>The last resort burning since UT 90 on drogues called at 80 and failed; the mains called at 95. Now is 100.</summary>
        private static Dictionary<string, object> LastResortBurning(string drogues, string mains, double rate = 5, double off = 5, string why = "last resort", double mainCallUt = 95)
        {
            return new Dictionary<string, object>
            {
                { "radar", 800.0 }, { "speed", 120.0 }, { "brake_h", -1.0 }, { "last_h", 1000.0 }, { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "throttle", 0.8 }, { "off_deg", off }, { "rate_deg", rate },
                { "now", 100.0 }, { "brake_why", why }, { "last_resort_ut", 90.0 }, { "drogues_called", true }, { "drogue_call_ut", 80.0 }, { "mains_called", true }, { "main_call_ut", mainCallUt },
                { "chutes", Set(true, drogues).Concat(Set(false, mains)).ToList() },
            };
        }

        [Theory]
        [InlineData("armed", "armed")] // called a moment ago, still packed
        [InlineData("stowed", "semi")]
        [InlineData("armed", "deployed")]
        public void Failed_drogues_with_the_last_resort_burning_and_healthy_mains_called_keep_the_thrust(string drogues, string mains)
        {
            var tick = BlockCalls.BrakeTick(LastResortBurning(drogues, mains));
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal("", tick["zero_why"]);
        }

        [Theory]
        [InlineData("semi", "semi")]
        [InlineData("semi", "armed")]
        [InlineData("deployed", "armed")] // packed to deployed between two ticks
        public void The_failed_drogues_opening_after_all_zero_the_thrust(string drogues, string mains)
        {
            AssertNoThrust(BlockCalls.BrakeTick(LastResortBurning(drogues, mains)), ReentryBrake.WhyChuteOpening);
        }

        [Fact]
        public void The_failed_drogues_open_settle_and_the_thrust_returns_and_the_mains_opening_doesnt_restart_it()
        {
            var ask = LastResortBurning("deployed", "armed");
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(100.0, D(tick, "open_ut"));
            CarryOn(ask, tick);
            ask["now"] = 108.5;
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(0.8, D(tick, "throttle"));
            CarryOn(ask, tick);
            ask["now"] = 109.0;
            ask["chutes"] = Set(true, "deployed").Concat(Set(false, "deployed")).ToList();
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal(100.0, D(tick, "open_ut"));
        }

        [Fact]
        public void Spinning_or_off_the_aim_during_the_last_resort_still_zeroes_the_thrust()
        {
            AssertNoThrust(BlockCalls.BrakeTick(LastResortBurning("armed", "semi", rate: 25)), ReentryBrake.WhySpinning);
            AssertNoThrust(BlockCalls.BrakeTick(LastResortBurning("armed", "semi", off: 45)), ReentryBrake.WhyOffAim);
        }

        [Theory]
        [InlineData("chutes open", 95)] // not a last resort: every set still counts
        [InlineData("", 95)]
        [InlineData("last resort", 85)] // a set called before the last resort began is no later set
        public void Only_a_set_called_after_a_last_resort_began_is_passed_over(string why, double mainCallUt)
        {
            AssertNoThrust(BlockCalls.BrakeTick(LastResortBurning("deployed", "semi", why: why, mainCallUt: mainCallUt)), ReentryBrake.WhyChuteOpening);
        }

        [Fact]
        public void The_last_resort_time_starts_on_its_first_tick_is_held_and_no_flight_inherits_it()
        {
            var ask = LastResortBurning("armed", "armed");
            ask["last_resort_ut"] = -1.0;
            Assert.Equal(100.0, D(BlockCalls.BrakeTick(ask), "last_resort_ut"));
            ask["last_resort_ut"] = 90.0;
            Assert.Equal(90.0, D(BlockCalls.BrakeTick(ask), "last_resort_ut"));
            ask["brake_why"] = "";
            Assert.Equal(-1.0, D(BlockCalls.BrakeTick(ask), "last_resort_ut"));
        }

        [Fact]
        public void Mains_called_on_the_tick_the_last_resort_began_count_as_later()
        {
            // The block's chute_tick runs after the tick that says start: the mains' call and the last resort's time are both first read on the next tick.
            var ask = LastResortBurning("armed", "armed", mainCallUt: -1);
            ask["last_resort_ut"] = -1.0;
            Assert.Equal(0.8, D(BlockCalls.BrakeTick(ask), "throttle"));
        }

        // ---- Once the chutes have settled, the engine waits for the final burn and the chutes are cut for it (#969) ----
        // Duna, m20261109-200745-96f6: after the last resort the craft burned ~110 s at ~0.1 throttle under a swinging canopy and lost its Mainsail.

        /// <summary>8 m/s down under all chutes fully open since UT 80 (drogues called before the last resort, mains after), upright within <paramref name="up"/> degrees. Now is 100.</summary>
        private static Dictionary<string, object> UnderCanopy(double radar, double up = 5, string why = "last resort", double aUp = AUp)
        {
            var ask = LastResortBurning("deployed", "deployed", why: why);
            ask["radar"] = radar;
            ask["speed"] = 8.0;
            ask["brake_h"] = 12.0; // the sampled height, with the chutes' drag counted
            ask["open_ut"] = 85.0;
            ask["open_count"] = 1.0;
            ask["settled"] = true;
            ask["all_open_ut"] = 80.0;
            foreach (var p in new Dictionary<string, object> { { "v_down", 8.0 }, { "a_up", aUp }, { "g", G }, { "soft", Soft }, { "end", End }, { "up_deg", up } }) ask[p.Key] = p.Value;
            return ask;
        }

        /// <summary>The final height for <see cref="UnderCanopy"/>: 8 m/s to 2 on 12 / 1.2 - 2.94 m/s², 4.25 m, plus the 10 m end and a second of the fall, with no drag.</summary>
        private const double FinalH = 22.25;

        [Fact]
        public void Last_resort_then_chutes_settled_the_thrust_goes_to_zero_and_waits_for_the_final_height()
        {
            var tick = BlockCalls.BrakeTick(UnderCanopy(300));
            AssertNoThrust(tick, ReentryBrake.WhyFinalWait);
            Assert.Equal(true, tick["final"]);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(true, tick["open"]);
        }

        [Fact]
        public void Last_resort_with_the_chutes_not_yet_settled_burns_on()
        {
            var ask = UnderCanopy(300);
            ask["all_open_ut"] = 99.0;
            ask["decel"] = 3.0;
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal(false, tick["final"]);
            Assert.Equal(99.0, D(tick, "all_open_ut"));
        }

        [Fact]
        public void The_time_all_read_fully_open_starts_on_the_first_tick_and_resets_when_one_isnt()
        {
            var ask = UnderCanopy(300);
            ask["all_open_ut"] = -1.0;
            Assert.Equal(100.0, D(BlockCalls.BrakeTick(ask), "all_open_ut"));
            ask["chutes"] = Set(true, "deployed").Concat(Set(false, "semi")).ToList();
            ask["all_open_ut"] = 90.0;
            Assert.Equal(-1.0, D(BlockCalls.BrakeTick(ask), "all_open_ut"));
        }

        [Fact]
        public void The_stop_is_noted_once()
        {
            var ask = UnderCanopy(300);
            var note = (IDictionary<string, object>)BlockCalls.BrakeTick(ask)["note"];
            Assert.Equal("thrust_stopped", note["phase"]);
            Assert.Equal("last resort", note["why"]);
            Assert.Equal(ReentryBrake.WhyFinalWait, note["wait"]);
            Assert.Equal(300.0, Convert.ToDouble(note["agl"]));
            Assert.Equal(22.0, Convert.ToDouble(note["final_h"]));
            ask["final"] = true;
            Assert.Empty((IDictionary<string, object>)BlockCalls.BrakeTick(ask)["note"]);
            Assert.Empty((IDictionary<string, object>)Tick(300, 90, 400, 200, Chutes("deployed"), 1)["note"]);
        }

        [Fact]
        public void Once_waiting_the_wait_holds_whatever_the_slowing_or_a_chute_then_reads()
        {
            var ask = UnderCanopy(300);
            ask["final"] = true;
            ask["all_open_ut"] = 99.0;
            ask["decel"] = 3.0;
            AssertNoThrust(BlockCalls.BrakeTick(ask), ReentryBrake.WhyFinalWait);
            ask["chutes"] = Set(true, "cut").Concat(Set(false, "semi")).ToList();
            AssertNoThrust(BlockCalls.BrakeTick(ask), ReentryBrake.WhyFinalWait);
            // Every chute gone: nothing left to cut, so the radar rule's throttle.
            ask["chutes"] = Set(true, "cut").Concat(Set(false, "cut")).ToList();
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal(false, tick["final"]);
        }

        [Fact]
        public void At_the_final_height_upright_the_chutes_are_cut_and_the_final_burn_flies()
        {
            var tick = BlockCalls.BrakeTick(UnderCanopy(20));
            Assert.Equal(true, tick["cut"]);
            Assert.Equal(ReentryBrake.CutForFinalBurn, tick["cut_why"]);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal("", tick["zero_why"]);
            Assert.Equal(false, tick["open"]); // the gimbals are freed with the cut
            // The block has cut them and sends no uids: the burn flies on the guards alone.
            var ask = UnderCanopy(15);
            ask["chutes"] = new List<object>();
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(0.8, D(tick, "throttle"));
            ask["off_deg"] = 45.0;
            AssertNoThrust(BlockCalls.BrakeTick(ask), ReentryBrake.WhyOffAim);
            ask["off_deg"] = 5.0;
            ask["rate_deg"] = 25.0;
            AssertNoThrust(BlockCalls.BrakeTick(ask), ReentryBrake.WhySpinning);
        }

        [Fact]
        public void The_final_height_counts_no_chute_drag()
        {
            var tick = BlockCalls.BrakeTick(UnderCanopy(300));
            Assert.Equal(FinalH, D(tick, "final_h"), 2);
            Assert.Equal(ReentryBrake.BrakeHeight(8, AUp, G, new double[] { 0 }, 300, Soft, End), D(tick, "final_h"), 6);
            // With the canopy's drag counted it would be lower, too late once the chutes are cut.
            // Half a canopy's drag at its 8 m/s terminal speed: k = 0.5 x 2.94 / 8^2.
            Assert.True(ReentryBrake.BrakeHeight(8, AUp, G, Enumerable.Repeat(0.023, 20).ToList(), 300, Soft, End) < FinalH - 0.3);
            // Under the sampled brake_h (12 m, drag counted) but over the final height: cut and burn, not wait.
            Assert.Equal(true, BlockCalls.BrakeTick(UnderCanopy(FinalH - 0.5))["cut"]);
            Assert.Equal(false, BlockCalls.BrakeTick(UnderCanopy(FinalH + 0.5))["cut"]);
        }

        [Fact]
        public void More_than_30_degrees_off_upright_at_the_final_height_the_cut_is_held()
        {
            var tick = BlockCalls.BrakeTick(UnderCanopy(20, up: 40));
            Assert.Equal(false, tick["cut"]);
            AssertNoThrust(tick, ReentryBrake.WhyCutHeld);
            Assert.Equal(true, BlockCalls.BrakeTick(UnderCanopy(20, up: 30))["cut"]);
        }

        [Fact]
        public void Swinging_through_upright_faster_than_20_degrees_a_second_the_cut_is_held()
        {
            var ask = UnderCanopy(20);
            ask["rate_deg"] = 25.0;
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            AssertNoThrust(tick, ReentryBrake.WhyCutHeld);
            ask["rate_deg"] = -1.0; // unread: no hold, as the spin rule
            Assert.Equal(true, BlockCalls.BrakeTick(ask)["cut"]);
        }

        [Fact]
        public void Tipped_where_waiting_longer_would_leave_the_engine_unable_to_stop_it_burns_with_the_chutes_on()
        {
            // Full thrust with no drag stops 8 m/s in 3.31 m, plus half a second: 7.3 m.
            var tick = BlockCalls.BrakeTick(UnderCanopy(7, up: 40));
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal("", tick["zero_why"]);
            // Upright again down there, the chutes stay on: without them the engine can't stop it.
            Assert.Equal(false, BlockCalls.BrakeTick(UnderCanopy(7))["cut"]);
            // The guards still hold.
            var ask = UnderCanopy(7, up: 40);
            ask["off_deg"] = 45.0;
            AssertNoThrust(BlockCalls.BrakeTick(ask), ReentryBrake.WhyOffAim);
        }

        [Fact]
        public void A_chute_only_landing_never_cuts()
        {
            // Not braking: the fall's own ticks never cut, however low.
            foreach (var radar in new[] { 300.0, 20, 5 })
            {
                var tick = BlockCalls.BrakeTick(UnderCanopy(radar, why: ""));
                Assert.Equal(false, tick["cut"]);
                Assert.Equal(false, tick["final"]);
            }
            // No engine to stop it: the chutes stay, at any height.
            foreach (var radar in new[] { 300.0, 20, 5 }) Assert.Equal(false, BlockCalls.BrakeTick(UnderCanopy(radar, aUp: 0))["cut"]);
            // With no thrust figure at all, as before #969.
            Assert.Equal(false, Tick(5, 8, 32, 8, Chutes("deployed"), 1)["cut"]);
        }

        [Fact]
        public void An_engine_that_cant_hold_the_craft_up_never_cuts_even_already_slow()
        {
            // Duna at TWR 0.5: under the soft speed already, the stop distance is 0, but without the chutes it would fall faster all the way down.
            foreach (var aUp in new[] { G * 0.5, G })
            {
                Assert.Equal(-1, ReentryBrake.FinalHeight(Soft * 0.75, aUp, G, 12, Soft, End));
                Assert.Equal(-1, ReentryBrake.FinalLastHeight(Soft * 0.75, aUp, G, 12, Soft));
                foreach (var radar in new[] { 300.0, 12, 5 })
                {
                    var ask = UnderCanopy(radar, aUp: aUp);
                    ask["v_down"] = Soft * 0.75;
                    var tick = BlockCalls.BrakeTick(ask);
                    Assert.Equal(false, tick["cut"]);
                    Assert.Equal("", tick["zero_why"]); // the pre-#969 burn, chutes kept
                }
            }
        }

        [Fact]
        public void The_radar_brake_under_settled_chutes_starts_at_the_final_height_and_cuts_the_chutes()
        {
            // Not yet braking: the sampled brake_h (12 m, drag counted) is under the final height, which starts it.
            var fall = UnderCanopy(20, why: "");
            fall["main_call_ut"] = 85.0;
            fall["open_ut"] = 85.0;
            fall["open_count"] = 2.0;
            var tick = BlockCalls.BrakeTick(fall);
            Assert.Equal(true, tick["start"]);
            Assert.Equal("chutes open", tick["why"]);
            fall["radar"] = 30.0;
            Assert.Equal(false, BlockCalls.BrakeTick(fall)["start"]);
            // Braking by radar under them: the same wait, then the cut.
            var braking = UnderCanopy(300, why: "chutes open");
            braking["main_call_ut"] = 85.0;
            AssertNoThrust(BlockCalls.BrakeTick(braking), ReentryBrake.WhyFinalWait);
            braking["radar"] = 20.0;
            Assert.Equal(true, BlockCalls.BrakeTick(braking)["cut"]);
        }

        [Fact]
        public void The_final_wait_is_held_through_an_unreadable_tick()
        {
            var ask = UnreadTick(100);
            ask["all_open_ut"] = 80.0;
            ask["final"] = true;
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(80.0, D(tick, "all_open_ut"));
            Assert.Equal(true, tick["final"]);
            Assert.Equal("unreadable", BlockCalls.BrakeTick(UnreadTick(100, 99))["cut_why"]);
        }

        // ---- The final height leaves the time to stop the craft's turn (#969, the player's option c) ----
        // A ±30° swing every ~10 s passes upright at about 19 deg/s; the 41 t stack's wheels give about 7 deg/s², so it turns
        // another 20 to 30° before they stop it, and the 30° guard could zero the thrust near the ground with the chutes gone.

        /// <summary>Wheels of <paramref name="torque"/> kN m on pitch and yaw, 100 t m² each: 10 kN m is 0.1 rad/s², 5.73 deg/s². RCS aboard, which the stop doesn't count (it is off under the chutes).</summary>
        private static TurnCraft Wheels(double torque)
        {
            return new TurnCraft { WheelPitch = torque, WheelYaw = torque, RcsPitch = 50, RcsYaw = 50, RcsUsable = true, MoiPitch = 100, MoiYaw = 100 };
        }

        private static readonly double Alpha10 = 0.1 * 180 / Math.PI;

        /// <summary>The final height for <see cref="UnderCanopy"/> unrounded (<see cref="FinalH"/> is to 2 places).</summary>
        private static readonly double Base = ReentryBrake.FinalHeight(8, AUp, G, 300, Soft, End);

        /// <summary>The final height for <see cref="UnderCanopy"/> leaving <paramref name="s"/> seconds to stop the turn: that long falling with no drag and no thrust from 8 m/s, then the no-drag brake height at the speed it reaches.</summary>
        private static double Raised(double s)
        {
            return 8 * s + G * s * s / 2 + ReentryBrake.BrakeHeight(8 + G * s, AUp, G, new double[] { 0 }, 300, Soft, End);
        }

        private static Dictionary<string, object> Swinging(double radar, double rate, double up = 5)
        {
            var ask = UnderCanopy(radar, up);
            ask["rate_deg"] = rate;
            return ask;
        }

        [Fact]
        public void The_time_to_stop_a_turn_is_its_rate_over_the_wheels_and_the_way_back_inside_30_degrees()
        {
            Assert.Equal(0, ReentryBrake.TurnStopSeconds(0, 25, Alpha10));
            Assert.Equal(0, ReentryBrake.TurnStopSeconds(-1, 25, Alpha10)); // unread: no hold, as the spin rule
            // 10 deg/s at 5° off: stopped in 1.75 s, 8.7° on, still inside 30°.
            Assert.Equal(10 / Alpha10, ReentryBrake.TurnStopSeconds(10, 5, Alpha10), 6);
            // 18 deg/s at 25° off, turning away: it stops 28.3° on, at 53.3°, and the guard zeroes the thrust until it is back inside 30°.
            var over = 25 + 18 * 18 / (2 * Alpha10) - ReentryBrake.MaxOffAimDeg;
            Assert.Equal(18 / Alpha10 + Math.Sqrt(2 * over / Alpha10), ReentryBrake.TurnStopSeconds(18, 25, Alpha10), 6);
            Assert.True(double.IsPositiveInfinity(ReentryBrake.TurnStopSeconds(5, 5, 0))); // no wheels: never stops
        }

        [Fact]
        public void With_no_turn_to_stop_the_final_height_is_as_before()
        {
            var tick = BlockCalls.BrakeTick(Swinging(300, 0), () => Wheels(10));
            Assert.Equal(Base, D(tick, "final_h"), 6);
            Assert.Equal(true, BlockCalls.BrakeTick(Swinging(Base - 0.5, 0), () => Wheels(10))["cut"]);
            Assert.Equal(false, BlockCalls.BrakeTick(Swinging(Base + 0.5, 0), () => Wheels(10))["cut"]);
        }

        [Fact]
        public void A_turn_to_stop_raises_the_final_height_by_the_fall_while_it_stops()
        {
            // 10 deg/s on 0.1 rad/s² of wheels: 1.75 s of free fall from 8 m/s, then the brake at 13.1 m/s: 53.5 m, not 22.2.
            var raised = Raised(10 / Alpha10);
            Assert.InRange(raised, 53, 54);
            Assert.Equal(raised, D(BlockCalls.BrakeTick(Swinging(60, 10), () => Wheels(10)), "final_h"), 6);
            Assert.Equal(true, BlockCalls.BrakeTick(Swinging(raised - 0.5, 10), () => Wheels(10))["cut"]);
            var above = BlockCalls.BrakeTick(Swinging(raised + 0.5, 10), () => Wheels(10));
            Assert.Equal(false, above["cut"]);
            AssertNoThrust(above, ReentryBrake.WhyFinalWait);
            // Without a reading of the craft, the height is as before #969's follow-up.
            Assert.Equal(Base, D(BlockCalls.BrakeTick(Swinging(60, 10), () => null), "final_h"), 6);
            // Higher than the cap could raise it, the craft isn't read: it waits either way.
            var read = false;
            Assert.Equal(Base, D(BlockCalls.BrakeTick(Swinging(300, 10), () => { read = true; return Wheels(10); }), "final_h"), 6);
            Assert.False(read);
        }

        [Fact]
        public void A_weak_craft_gets_no_more_than_the_cap_and_the_cut_waits_while_its_turn_would_take_longer()
        {
            // 1 kN m on 100 t m²: 0.57 deg/s², 17 s to stop 10 deg/s.
            var capped = Raised(ReentryBrake.MaxTurnStopSeconds);
            Assert.Equal(capped, D(BlockCalls.BrakeTick(Swinging(capped - 0.5, 10), () => Wheels(1)), "final_h"), 6);
            var held = BlockCalls.BrakeTick(Swinging(capped - 0.5, 10), () => Wheels(1));
            Assert.Equal(false, held["cut"]);
            AssertNoThrust(held, ReentryBrake.WhyCutHeld);
            // The swing slows enough: the cut goes.
            Assert.Equal(true, BlockCalls.BrakeTick(Swinging(Raised(1 / (Alpha10 / 10)) - 0.5, 1), () => Wheels(1))["cut"]);
            // Too low for the capped fall and the brake after it, or under the height where full thrust only just stops it: it burns with the chutes on, on the guards.
            foreach (var radar in new[] { 60.0, 7 })
            {
                var low = BlockCalls.BrakeTick(Swinging(radar, 10), () => Wheels(1));
                Assert.Equal(false, low["cut"]);
                Assert.Equal(0.8, D(low, "throttle"));
            }
        }

        /// <summary>Kerbin under a Mk16 (7 m/s), an engine of TWR 1.5, tipping 10 deg/s at 5° off.</summary>
        private static Dictionary<string, object> KerbinSwinging(double radar)
        {
            var ask = Swinging(radar, 10);
            ask["g"] = 9.81;
            ask["a_up"] = 9.81 * 1.5;
            ask["v_down"] = 7.0;
            return ask;
        }

        [Fact]
        public void A_turn_that_would_raise_the_final_height_over_the_metres_cap_gets_the_cap_and_the_cut_is_held()
        {
            // 5 kN m on 100 t m²: 2.86 deg/s², 3.5 s to stop 10 deg/s, inside the 6 s cap. But 3.5 s of free fall on Kerbin, then
            // the brake on a sixth less than TWR 1.5 at the 41 m/s it reaches, would cut about 480 m up: about 460 m over the 26 m with no turn.
            var noTurn = ReentryBrake.FinalHeight(7, 9.81 * 1.5, 9.81, double.PositiveInfinity, Soft, End);
            var withTurn = ReentryBrake.FinalHeight(7, 9.81 * 1.5, 9.81, double.PositiveInfinity, Soft, End, 10 / (0.05 * 180 / Math.PI));
            Assert.InRange(noTurn, 25, 27);
            Assert.InRange(withTurn, 226, 227); // the metres cap, not the ~480 m the turn would ask
            var capped = noTurn + ReentryBrake.MaxTurnStopMetres;
            Assert.Equal(capped, withTurn, 6);
            Assert.False(ReentryBrake.TurnFits(7, 9.81 * 1.5, 9.81, Soft, End, 10 / (0.05 * 180 / Math.PI)));
            // Above the capped height it waits; at it, the cut is held, the thrust zero, though the turn is inside the 6 s cap.
            AssertNoThrust(BlockCalls.BrakeTick(KerbinSwinging(300), () => Wheels(5)), ReentryBrake.WhyFinalWait);
            var held = BlockCalls.BrakeTick(KerbinSwinging(capped - 0.5), () => Wheels(5));
            Assert.Equal(capped, D(held, "final_h"), 6);
            Assert.Equal(false, held["cut"]);
            AssertNoThrust(held, ReentryBrake.WhyCutHeld);
            // Where the fall and the brake no longer fit, it burns with the chutes on, on the guards, as over the time cap.
            var low = BlockCalls.BrakeTick(KerbinSwinging(60), () => Wheels(5));
            Assert.Equal(false, low["cut"]);
            Assert.Equal(0.8, D(low, "throttle"));
            // Stronger wheels (0.35 s), a turn that fits the metres: the cut goes at its raised height.
            var strong = ReentryBrake.FinalHeight(7, 9.81 * 1.5, 9.81, double.PositiveInfinity, Soft, End, 10 / (0.5 * 180 / Math.PI));
            Assert.InRange(strong, noTurn + 1, capped - 100);
            Assert.Equal(true, BlockCalls.BrakeTick(KerbinSwinging(strong - 0.5), () => Wheels(50))["cut"]);
        }

        [Fact]
        public void A_craft_with_no_wheel_torque_never_cuts_it_waits_down_to_where_the_turn_no_longer_fits_then_burns_under_its_chutes()
        {
            // No wheels, or wheels with no electric charge (the reader gives them no torque): any turn takes forever, so the capped height, the cut held, then the burn with the chutes on.
            // Pinned as it is; whether such a craft should cut anyway is the player's call.
            var capped = Raised(ReentryBrake.MaxTurnStopSeconds);
            AssertNoThrust(BlockCalls.BrakeTick(Swinging(capped + 0.5, 10), () => Wheels(0)), ReentryBrake.WhyFinalWait);
            foreach (var rate in new[] { 10.0, 0.1 })
            {
                var held = BlockCalls.BrakeTick(Swinging(capped - 0.5, rate), () => Wheels(0));
                Assert.Equal(false, held["cut"]);
                AssertNoThrust(held, ReentryBrake.WhyCutHeld);
                var low = BlockCalls.BrakeTick(Swinging(60, rate), () => Wheels(0));
                Assert.Equal(false, low["cut"]);
                Assert.Equal(0.8, D(low, "throttle"));
            }
        }

        [Fact]
        public void The_cut_is_noted_with_the_rate_the_angle_and_the_extra_seconds()
        {
            var raised = Raised(10 / Alpha10);
            var tick = BlockCalls.BrakeTick(Swinging(raised - 0.5, 10, 12), () => Wheels(10));
            Assert.Equal(true, tick["cut"]);
            var note = (IDictionary<string, object>)tick["note"];
            Assert.Equal("final_cut", note["phase"]);
            Assert.Equal(Math.Round(10 / Alpha10, 1), Convert.ToDouble(note["turn_s"]));
            Assert.Equal(10.0, Convert.ToDouble(note["rate_deg"]));
            Assert.Equal(12.0, Convert.ToDouble(note["up_deg"]));
            Assert.Equal(Math.Round(raised), Convert.ToDouble(note["final_h"]));
            Assert.Equal(Math.Round(raised - 0.5), Convert.ToDouble(note["agl"]));
        }

        // ---- States the addon can't read (player, 2026-11-09): cut what is out and land on the engine ----

        private const string Unread = "chutes_unread";

        private static Dictionary<string, object> UnreadTick(double now, double unreadUt = -1)
        {
            return new Dictionary<string, object>
            {
                { "radar", 500.0 }, { "speed", 40.0 }, { "brake_h", 600.0 }, { "last_h", 300.0 }, { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "throttle", 0.8 }, { "off_deg", 5.0 }, { "rate_deg", 2.0 },
                { "now", now }, { "uids", new List<object> { "a", "b" } }, { Unread, true }, { "unread_ut", unreadUt }, { "sample_ut", now - 0.25 }, { "sample_speed", 40.0 },
            };
        }

        private static void AssertWaiting(IDictionary<string, object> tick)
        {
            Assert.Equal(false, tick["start"]);
            AssertNoThrust(tick, ReentryBrake.WhyUnread);
            Assert.Equal("unreadable", tick["chutes"]);
        }

        [Fact]
        public void One_bad_tick_cuts_nothing_starts_no_burn_and_holds_the_settled_chutes()
        {
            var ask = UnreadTick(100);
            ask["open_ut"] = 80.0;
            ask["open_count"] = 2.0;
            ask["settled"] = true;
            ask["drogue_call_ut"] = 70.0;
            ask["main_call_ut"] = 75.0;
            var bad = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, bad["cut"]);
            AssertWaiting(bad);
            Assert.Equal(100.0, D(bad, "unread_ut"));
            foreach (var held in new[] { "open_ut", "open_count", "settled", "drogue_call_ut", "main_call_ut" }) Assert.Equal(ask[held], bad[held]);
        }

        [Fact]
        public void Failing_for_under_a_second_then_reading_again_cuts_nothing_and_the_thrust_returns()
        {
            var ask = UnreadTick(100);
            ask["open_ut"] = 80.0;
            ask["open_count"] = 1.0;
            ask["settled"] = true;
            var tick = BlockCalls.BrakeTick(ask);
            CarryOn(ask, tick);
            ask["now"] = 100.9;
            ask["sample_ut"] = 100.75;
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(100.0, D(tick, "unread_ut"));
            CarryOn(ask, tick);
            ask["now"] = 101.5;
            ask["sample_ut"] = 101.25;
            ask.Remove(Unread);
            ask["uids"] = new List<object> { "a" };
            ask["chutes"] = Chutes("deployed");
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(-1.0, D(tick, "unread_ut"));
            Assert.Equal(80.0, D(tick, "open_ut"));
            Assert.Equal(0.8, D(tick, "throttle"));
        }

        [Fact]
        public void Failing_for_a_second_asks_for_the_cut_once_and_the_engine_then_proceeds_on_the_radar_rule()
        {
            var ask = UnreadTick(100, 99);
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(true, tick["cut"]);
            AssertNoThrust(tick, ReentryBrake.WhyUnread);
            // The block has cut them and set chute_mode "none", so it sends no uids from here: even a read that still fails asks for nothing more.
            CarryOn(ask, tick);
            ask["uids"] = new List<object>();
            ask["now"] = 100.02;
            ask["sample_ut"] = 100.0;
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(true, tick["start"]);
            Assert.Equal("radar", tick["why"]);
            Assert.Equal(0.8, D(tick, "throttle"));
            Assert.Equal("", tick["zero_why"]);
            Assert.Equal(-1.0, D(tick, "unread_ut"));
        }

        [Fact]
        public void A_failed_read_from_an_earlier_flight_doesnt_count_towards_the_second()
        {
            var ask = UnreadTick(5000, 100);
            ask["sample_ut"] = 101.0;
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(5000.0, D(tick, "unread_ut"));
        }

        [Fact]
        public void Nothing_is_cut_when_the_read_fails_with_no_chutes_to_read()
        {
            var ask = UnreadTick(100, 95);
            ask["uids"] = new List<object>();
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["cut"]);
            Assert.Equal(true, tick["start"]);
        }

        [Fact]
        public void With_the_states_read_nothing_is_ever_cut()
        {
            Assert.Equal(false, Tick(30, 12, 32, 8, Chutes("deployed"), 1)["cut"]);
            Assert.Equal(false, BlockCalls.BrakeTick(null)["cut"]);
        }

        // ---- The trace's second ----

        [Fact]
        public void The_trace_is_due_once_a_second_only_while_braking()
        {
            var ask = new Dictionary<string, object> { { "now", 100.0 }, { "brake_why", "chutes open" } };
            var tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(true, tick["trace"]);
            CarryOn(ask, tick);
            ask["now"] = 100.9;
            tick = BlockCalls.BrakeTick(ask);
            Assert.Equal(false, tick["trace"]);
            CarryOn(ask, tick);
            ask["now"] = 101.0;
            Assert.Equal(true, BlockCalls.BrakeTick(ask)["trace"]);
            ask["brake_why"] = "";
            Assert.Equal(false, BlockCalls.BrakeTick(ask)["trace"]);
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
                { "now", 100.0 }, { "open_ut", -1.0 }, { "decel", 12.0 }, { "drogues_called", true }, { "mains_called", true }, { "drogue_call_ut", 90.0 }, { "main_call_ut", 99.5 }, { "brake_why", "last resort" }, { "last_resort_ut", 95.0 }, { "trace_ut", 99.0 },
                { "v_down", 150.0 }, { "a_up", AUp }, { "g", G }, { "soft", Soft }, { "end", End }, { "up_deg", 12.0 }, // #969: the final height, every tick
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
