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

        /// <summary>Chutes as the adapter reads them, with their deploy height (m) and whether each is a drogue.</summary>
        private static List<object> ChutesAt(double deployM, bool drogue, params string[] states)
        {
            return states.Select((s, i) => (object)new Dictionary<string, object> { { "title", "Chute " + i }, { "state", s }, { "deploy_m", deployM }, { "drogue", drogue } }).ToList();
        }

        /// <summary>One tick. <paramref name="openS"/>: seconds since the chutes all read deployed (the addon carries it from tick to tick); <paramref name="decel"/>: the sampled slowing, m/s².</summary>
        private static IDictionary<string, object> Tick(double radar, double speed, double brakeH, double lastH, List<object> chutes, double throttle = 0, double off = 0, double openS = 20, double decel = 0, List<object> uids = null)
        {
            var ask = new Dictionary<string, object>
            {
                { "radar", radar }, { "speed", speed }, { "brake_h", brakeH }, { "last_h", lastH },
                { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "chutes", chutes },
                { "throttle", throttle }, { "off_deg", off },
                { "now", 100.0 }, { "open_ut", 100.0 - openS }, { "decel", decel },
                { "chute_last_agl", 1500.0 }, { "drogue_last_agl", 3000.0 },
            };
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
        [InlineData("semi")]
        public void Never_open_at_the_last_resort_height_braking_starts_anyway(string state)
        {
            var heights = Heights(150, 5000);
            var lastH = D(heights, "last_h");
            var tick = Tick(lastH, 150, D(heights, "brake_h"), lastH, Chutes("deployed", state));
            Assert.Equal(true, tick["start"]);
            Assert.Equal(false, tick["open"]);
            Assert.Equal("last resort", tick["why"]);
            Assert.Equal(false, Tick(lastH + 1, 150, D(heights, "brake_h"), lastH, Chutes("deployed", state))["start"]);
        }

        [Fact]
        public void The_last_resort_height_is_real_even_above_the_craft_so_a_fall_past_it_starts_at_once()
        {
            // 250 m/s: (250^2 - 2^2) / (2 x 9.06) = 3449 m, worked past the ground from 1,000 m of radar, as a sample of a fall already past the height reads.
            var heights = Heights(250, 1000);
            Assert.Equal((250.0 * 250 - 4) / (2 * (AUp - G)) + 125, D(heights, "last_h"), 6);
            var late = Tick(1000, 250, -1, D(heights, "last_h"), Chutes("semi"));
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
            Assert.Equal(false, Tick(40000, 1200, -1, -1, Chutes("stowed"))["start"]);
            Assert.Equal(true, Tick(40000, 390, -1, -1, Chutes("stowed"))["start"]);
            Assert.Equal(true, Tick(2900, 1200, -1, -1, Chutes("semi"))["start"]);
        }

        [Fact]
        public void High_on_a_shallow_path_a_height_above_the_craft_still_waits_for_the_air()
        {
            // 1,200 m/s at 40 km: past any height, but the air hasn't done its work.
            Assert.Equal(false, Tick(40000, 1200, -1, 79000, Chutes("stowed"))["start"]);
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
            var tick = Tick(1000, 140, D(heights, "brake_h"), D(heights, "last_h"), ChutesAt(1000, false, "deployed"), openS: 0, decel: 40);
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
            Assert.Equal(false, Tick(1000, 140, -1, 1151, ChutesAt(1000, false, "deployed"), openS: 3, decel: 0.2)["start"]);
        }

        [Fact]
        public void While_the_canopies_fill_the_last_resort_height_still_overrides()
        {
            var tick = Tick(700, 140, -1, 1151, ChutesAt(1000, false, "deployed"), openS: 1, decel: 40);
            Assert.Equal(true, tick["start"]);
            Assert.Equal("last resort", tick["why"]);
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

        // ---- The player's option (b): the last resort can't fire above the height the chutes should be fully open, unless one has failed or is missing ----

        [Theory]
        [InlineData("semi", 1500)] // half-open counts as healthy
        [InlineData("deployed", 1500)]
        [InlineData("semi", 801)] // just above the height with its margin
        public void A_fast_fall_with_healthy_chutes_above_the_deploy_height_doesnt_fire(string state, double radar)
        {
            var tick = Tick(radar, 140, -1, 1600, ChutesAt(1000, false, state), openS: 1, decel: 40);
            Assert.Equal(false, tick["start"]);
        }

        [Fact]
        public void At_or_below_the_height_with_its_margin_the_last_resort_may_fire()
        {
            var tick = Tick(800, 140, -1, 1600, ChutesAt(1000, false, "semi"));
            Assert.Equal(true, tick["start"]);
            Assert.Equal("last resort", tick["why"]);
        }

        [Fact]
        public void The_lowest_deploy_height_of_the_group_sets_the_floor()
        {
            var chutes = ChutesAt(2500, true, "deployed").Concat(ChutesAt(1000, false, "semi")).ToList();
            Assert.Equal(false, Tick(900, 140, -1, 1600, chutes)["start"]);
            Assert.Equal(true, Tick(800, 140, -1, 1600, chutes)["start"]);
        }

        [Theory]
        [InlineData("stowed")]
        [InlineData("armed")]
        public void A_chute_still_packed_at_its_last_call_height_allows_the_usual_last_resort(string state)
        {
            var tick = Tick(1500, 140, -1, 1600, ChutesAt(1000, false, state));
            Assert.Equal(true, tick["start"]);
            Assert.Equal("last resort", tick["why"]);
            Assert.Equal(false, Tick(1501, 140, -1, 1600, ChutesAt(1000, false, state))["start"]);
        }

        [Fact]
        public void A_drogue_counts_as_failed_at_the_drogues_own_last_call_height()
        {
            var tick = Tick(3000, 240, -1, 3200, ChutesAt(2500, true, "stowed"));
            Assert.Equal(true, tick["start"]);
            Assert.Equal(false, Tick(3001, 240, -1, 3200, ChutesAt(2500, true, "stowed"))["start"]);
        }

        [Fact]
        public void A_cut_chute_lifts_the_floor_for_the_others()
        {
            var tick = Tick(1200, 140, -1, 1300, ChutesAt(1000, false, "semi", "cut"));
            Assert.Equal(true, tick["start"]);
            Assert.Equal("last resort", tick["why"]);
        }

        [Fact]
        public void A_chute_missing_from_the_group_lifts_the_floor_for_the_others()
        {
            var uids = new List<object> { "a", "b" };
            var tick = Tick(1200, 140, -1, 1300, ChutesAt(1000, false, "semi"), uids: uids);
            Assert.Equal(true, tick["start"]);
            Assert.Equal("last resort", tick["why"]);
            Assert.Equal(false, Tick(1200, 140, -1, 1300, ChutesAt(1000, false, "semi", "semi"), uids: uids)["start"]);
        }

        [Fact]
        public void With_the_floor_lifted_the_last_resort_height_still_decides()
        {
            Assert.Equal(false, Tick(1200, 140, -1, 1100, ChutesAt(1000, false, "semi", "cut"))["start"]);
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
                { "chutes", ChutesAt(1000, false, "deployed", "semi") }, { "uids", new List<object> { "a", "b" } }, { "throttle", 0.7 }, { "off_deg", 12.0 },
                { "now", 100.0 }, { "open_ut", -1.0 }, { "decel", 12.0 }, { "chute_last_agl", 1500.0 }, { "drogue_last_agl", 3000.0 },
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
