// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>land</c>'s braking tick (#865, ADR 0014), as <c>ADDONS:CLAUDIA:LANDSTEER</c> answers it (<see cref="LandSteer.Tick"/>):
    /// the final descent's lean cap, its throttle while off target and its settle before touchdown, replaying the terminal states
    /// of m20261107-055424-908b on Ike; and the steering rules that moved from LandAscendTests with land_steer.
    /// </summary>
    public sealed class LandSteerTests
    {
        /// <summary>908b's lander: about 180 kN on about 11.5 t.</summary>
        private const double IkeAMax = 180000 / 11500.0;
        /// <summary>Ike's surface gravity, m/s².</summary>
        private const double IkeG = 1.10;
        /// <summary>TURNTIME's figures for 908b's land: up to 48.34°/s, 11.9 s for its turn.</summary>
        private const double TopRate = 48.34;
        /// <summary>Its wheels, kN m, and its moment of inertia, t m² (the same TURNTIME lines: 23.5-30.6): about 10°/s² of turn.</summary>
        private const double Wheels = 5;
        private const double Moi = 28;

        private static Dictionary<string, object> Lex(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        /// <summary>A nose <paramref name="lean"/> degrees from upright towards +al.</summary>
        private static Dictionary<string, object> Nose(double lean)
        {
            return Lex("al", Math.Sin(lean * Math.PI / 180), "cr", 0.0, "up", Math.Cos(lean * Math.PI / 180));
        }

        /// <summary>A final-descent tick on Ike: the demand (al, up) h m up, the nose leaning lean degrees, turning at rate °/s.</summary>
        private static Dictionary<string, object> Terminal(double al, double up, double h, double lean, double rate, IDictionary<string, object> last = null,
            double ut = 100, double dvLeft = 500, double vUp = -2)
        {
            return Lex("want", Lex("al", al, "cr", 0.0, "up", up, "h", h, "h_ref", 0.0), "last", last ?? Lex("al", 0.0, "cr", 0.0, "up", 1.0),
                "v_al", 0.5, "v_cr", 0.0, "v_up", vUp, "a_max", IkeAMax, "g", IkeG, "facing", Nose(lean), "rate", rate, "top_rate", TopRate,
                "dv_left", dvLeft, "ut", ut);
        }

        private static double D(IDictionary<string, object> map, string key)
        {
            return Convert.ToDouble(map[key]);
        }

        private static double LeanOf(IDictionary<string, object> dir)
        {
            return Math.Atan2(Math.Sqrt(D(dir, "al") * D(dir, "al") + D(dir, "cr") * D(dir, "cr")), D(dir, "up")) * 180 / Math.PI;
        }

        // ---- The lean cap ----

        [Fact]
        public void The_final_descent_leans_no_more_than_20_degrees_and_tightens_near_the_ground_as_before()
        {
            Assert.Equal(20, LandSteer.MaxLean(300, true), 9);
            Assert.Equal(20, LandSteer.MaxLean(7.5, true), 9);
            Assert.Equal(15, LandSteer.MaxLean(5, true), 9);
            Assert.Equal(5, LandSteer.MaxLean(0, true), 9);
            Assert.Equal(90, LandSteer.MaxLean(300, false), 9);
            Assert.Equal(25, LandSteer.MaxLean(10, false), 9);
            // A 1 m/s² push sideways against Ike's 1.1 m/s² up: 42 degrees before, 20 now.
            var tick = LandSteer.Tick(Terminal(1, 1.1, 300, 0, 0));
            Assert.Equal(20, LeanOf(tick), 6);
            // The last direction held for a tiny demand is capped too.
            var held = LandSteer.Tick(Terminal(0.1, 0.1, 300, 0, 0, Lex("al", -1.0, "cr", 0.0, "up", 0.2)));
            Assert.Equal(20, LeanOf(held), 6);
            Assert.True(D(held, "al") < 0);
        }

        [Fact]
        public void Capped_at_20_degrees_it_adds_lift_to_keep_braking_but_never_more_than_holds_its_height()
        {
            var tan20 = Math.Tan(20 * Math.PI / 180);
            // Falling 20 m/s with 2 m/s² wanted sideways and nothing up: lift for the 20 degree lean (2 / tan 20 = 5.5), under the
            // 1.1 + 10 that would hold its height, so all 2 m/s² brake.
            double al = 2, cr = 0, up = 0;
            LandSteer.Lean(ref al, ref cr, ref up, 300, true, IkeAMax, IkeG, -20);
            Assert.Equal(2 / tan20, up, 9);
            Assert.Equal(2, al, 9);
            // Not falling: no more lift than gravity, so the sideways part is cut to fit 20 degrees and it never climbs.
            al = 2;
            up = 0;
            LandSteer.Lean(ref al, ref cr, ref up, 300, true, IkeAMax, IkeG, 0);
            Assert.Equal(IkeG, up, 9);
            Assert.Equal(IkeG * tan20, al, 9);
            // In the last 7.5 m and above the final descent: the sideways part cut as before, no lift added.
            al = 2;
            up = 1;
            LandSteer.Lean(ref al, ref cr, ref up, 3, true, IkeAMax, IkeG, -20);
            Assert.Equal(1, up, 9);
            Assert.Equal(Math.Tan(11 * Math.PI / 180), al, 9);
            al = 2;
            up = 0.5;
            LandSteer.Lean(ref al, ref cr, ref up, 300, false, IkeAMax, IkeG, -20);
            Assert.Equal(0.5, up, 9);
            Assert.Equal(2, al, 9);
        }

        [Fact]
        public void In_the_final_descent_the_cap_holds_only_below_50_m_or_under_2_m_s_across_the_ground()
        {
            // The player's call (2026-11-07): 300 m up at 12 m/s across, the lean is free as before #865, with no lift added.
            var fast = Terminal(1, 1.1, 300, 0, 0);
            fast["v_al"] = 12.0;
            Assert.Equal(Math.Atan2(1, 1.1) * 180 / Math.PI, LeanOf(LandSteer.Tick(fast)), 6);
            Assert.Equal(Math.Sqrt(1 + 1.1 * 1.1), D(LandSteer.Tick(fast), "mag"), 9);
            // Below 50 m at that speed, or 300 m up under 2 m/s across (across and cross together): capped.
            var low = Terminal(1, 1.1, 49, 0, 0);
            low["v_al"] = 12.0;
            Assert.Equal(20, LeanOf(LandSteer.Tick(low)), 6);
            var slow = Terminal(1, 1.1, 300, 0, 0);
            slow["v_al"] = 1.2;
            slow["v_cr"] = 1.5;
            Assert.Equal(20, LeanOf(LandSteer.Tick(slow)), 6);
            slow["v_cr"] = 1.7;
            Assert.Equal(Math.Atan2(1, 1.1) * 180 / Math.PI, LeanOf(LandSteer.Tick(slow)), 6);
        }

        [Fact]
        public void Above_the_final_descent_the_lean_is_free_as_before()
        {
            var input = Terminal(1, 1.1, 300, 0, 0);
            ((IDictionary<string, object>)input["want"])["h_ref"] = 250.0;
            Assert.Equal(Math.Atan2(1, 1.1) * 180 / Math.PI, LeanOf(LandSteer.Tick(input)), 6);
        }

        // ---- The throttle while off target ----

        [Fact]
        public void At_30_degrees_off_the_throttle_gives_only_the_vertical_need_and_at_10_degrees_the_scaling_of_before()
        {
            // Upright demand of 2 m/s² up: 30 degrees over, it gives 2 / cos 30 of the 15.7 m/s², nothing more for the sideways.
            var off = LandSteer.Tick(Terminal(0, 2, 200, 30, 0));
            Assert.Equal(30, D(off, "error"), 6);
            Assert.Equal(2 / Math.Cos(Math.PI / 6) / IkeAMax, D(off, "throttle"), 9);
            // A demand with a sideways part, the nose 30 degrees past it on the other side: still only the vertical need.
            var side = LandSteer.Tick(Terminal(0.4, 2, 200, -19, 0));
            Assert.True(D(side, "error") > 15);
            Assert.Equal(2 / Math.Cos(19 * Math.PI / 180) / IkeAMax, D(side, "throttle"), 9);
            // 10 degrees off: the demand's size scaled by cos(error), as before.
            var near = LandSteer.Tick(Terminal(0, 2, 200, 10, 0));
            Assert.Equal(2 / IkeAMax * Math.Cos(10 * Math.PI / 180), D(near, "throttle"), 9);
            // Leaning over: never more than full, and none past 90 degrees.
            Assert.Equal(1, D(LandSteer.Tick(Terminal(0, 15, 200, 80, 0)), "throttle"), 9);
            Assert.Equal(0, D(LandSteer.Tick(Terminal(0, 2, 200, 120, 0)), "throttle"), 9);
        }

        [Fact]
        public void Off_target_with_no_thrust_or_a_need_not_finite_the_throttle_is_0()
        {
            // #595: a stage run dry (a_max 0), or a vertical need infinite or not a number, gives none rather than 1 or NaN.
            foreach (var need in new[] { double.PositiveInfinity, double.NaN })
                Assert.Equal(0, D(LandSteer.Tick(Terminal(0, need, 200, 30, 0)), "throttle"));
            var dry = Terminal(0, 2, 200, 30, 0);
            dry["a_max"] = 0.0;
            Assert.Equal(0, D(LandSteer.Tick(dry), "throttle"));
        }

        [Fact]
        public void Above_the_final_descent_30_degrees_off_keeps_the_scaling_of_before()
        {
            var input = Terminal(0, 2, 200, 30, 0);
            ((IDictionary<string, object>)input["want"])["h_ref"] = 150.0;
            Assert.Equal(2 / IkeAMax * Math.Cos(Math.PI / 6), D(LandSteer.Tick(input), "throttle"), 9);
        }

        // ---- The settle ----

        [Fact]
        public void Below_10_m_swinging_it_holds_its_height_and_stops_its_drift()
        {
            foreach (var swinging in new[] { Terminal(0.2, 1.5, 8, 25, 0), Terminal(0.2, 1.5, 8, 3, 20) })
            {
                var tick = LandSteer.Tick(swinging);
                Assert.True((bool)tick["settling"]);
                // Holding: gravity plus half the 2 m/s descent to stop it; half the 0.5 m/s drift back, not the site's demand.
                Assert.Equal(Math.Sqrt(0.25 * 0.25 + (IkeG + 1) * (IkeG + 1)), D(tick, "mag"), 9);
                Assert.Equal(-0.25 / D(tick, "mag"), D(tick, "al"), 9);
            }
            // A fast drift is stopped within the lean limit (5 + 2 degrees a metre, 21 at 8 m, so 20).
            var drifting = Terminal(0, 1.5, 8, 25, 0);
            drifting["v_al"] = 5.0;
            Assert.Equal(20, LeanOf(LandSteer.Tick(drifting)), 6);
        }

        [Fact]
        public void Settled_or_above_10_m_or_before_the_final_descent_it_does_not_hold()
        {
            Assert.False((bool)LandSteer.Tick(Terminal(0, 1.5, 8, 3, 1))["settling"]);
            Assert.False((bool)LandSteer.Tick(Terminal(0, 1.5, 12, 30, 20))["settling"]);
            var approach = Terminal(0, 1.5, 8, 30, 20);
            ((IDictionary<string, object>)approach["want"])["h_ref"] = 8.0;
            Assert.False((bool)LandSteer.Tick(approach)["settling"]);
            Assert.Equal(0, D(LandSteer.Tick(Terminal(0, 1.5, 8, 3, 1)), "settle_s"), 9);
        }

        [Fact]
        public void The_settle_stops_after_10_s()
        {
            IDictionary<string, object> last = null;
            var ut = 100.0;
            var held = 0;
            for (var i = 0; i < 1000; i++, ut += 0.02)
            {
                last = LandSteer.Tick(Terminal(0, 1.5, 8, 25, 10, last, ut, 500));
                if ((bool)last["settling"]) held++;
                else if (held > 0) break;
            }
            Assert.True((bool)last["settle_done"]);
            Assert.InRange(D(last, "settle_s"), 10, 10.03);
            Assert.InRange(held * 0.02, 9.98, 10.03);
            // Done: swinging on, it lands as before.
            Assert.False((bool)LandSteer.Tick(Terminal(0, 1.5, 5, 25, 10, last, ut + 0.02, 500))["settling"]);
        }

        [Fact]
        public void The_settle_stops_once_it_has_spent_2_percent_of_its_delta_v()
        {
            IDictionary<string, object> last = null;
            var ut = 100.0;
            var dv = 500.0;
            for (var i = 0; i < 1000 && (last == null || (bool)last["settling"]); i++, ut += 0.02, dv -= 0.5)
                last = LandSteer.Tick(Terminal(0, 1.5, 8, 25, 10, last, ut, dv));
            Assert.True((bool)last["settle_done"]);
            // 10 m/s of the 500 at 0.5 m/s a tick: the 21st tick finds it spent, 0.4 s in.
            Assert.InRange(D(last, "settle_s"), 0.39, 0.41);
        }

        [Fact]
        public void Once_upright_and_still_the_hold_ends_and_the_descent_goes_on()
        {
            var first = LandSteer.Tick(Terminal(0, 1.5, 8, 25, 10, null, 100));
            Assert.True((bool)first["settling"]);
            var swinging = LandSteer.Tick(Terminal(0, 1.5, 8, 12, 2, first, 101));
            Assert.True((bool)swinging["settling"]);
            // Within 10 degrees of the hold's own direction (leaning back against the 0.5 m/s drift) and still.
            var settled = LandSteer.Tick(Terminal(0, 1.5, 8, -4, 1, swinging, 102));
            Assert.False((bool)settled["settling"]);
            Assert.Equal(1, D(settled, "settle_s"), 9);
        }

        // ---- 908b's final descent, flown ----

        private sealed class Flight
        {
            public double MostLean, MostTurnRate, Error, Rate, Lean, Down, Sideways, Settle;
            /// <summary>Seconds from first coming below 50 m (always in the cap's zone) to the command first within the cap; -1 never.</summary>
            public double IntoCapS = -1;
            /// <summary>The most the command leaned above 50 m, slow across the ground (the zone's other half) and not slow or high.</summary>
            public double MostLeanSlowHigh, MostLeanFastHigh;
        }

        /// <summary>
        /// The final descent on Ike in the plane of the swing, every 0.02 s: LandGuard's straight-down guidance (land's terminal
        /// descent; stopping across the ground over the site) through <see cref="LandSteer.Tick"/>, the craft turning towards its
        /// command no faster than <see cref="TopRate"/> and its wheels' angular acceleration allow, braking in time, and
        /// thrusting along its nose. From <paramref name="h"/> m, <paramref name="vDown"/> m/s down,
        /// <paramref name="vH"/> across, leaning <paramref name="lean"/>° and turning <paramref name="rate"/>°/s, as 908b was.
        /// </summary>
        private static Flight Fly(double h, double vDown, double vH, double lean, double rate)
        {
            const double dt = 0.02;
            var alpha = Wheels / Moi * 180 / Math.PI;
            double x = -5, y = h, vx = vH, vy = -vDown, theta = lean, omega = rate, dv = 600, ut = 1000;
            IDictionary<string, object> last = null;
            var lastCmd = double.NaN;
            var zoneUt = -1.0;
            var flight = new Flight();
            while (y > 0 && ut < 1300)
            {
                var want = LandGuard.Vertical(-x, 0, vx, 0, vy, y, IkeAMax, IkeG, 130000 + y, 2, 1.1, 2, false);
                want["h_ref"] = 0.0;
                last = LandSteer.Tick(Lex("want", want, "last", last ?? Lex("al", 0.0, "cr", 0.0, "up", 1.0), "v_al", vx, "v_cr", 0.0, "v_up", vy,
                    "a_max", IkeAMax, "g", IkeG, "facing", Nose(theta), "rate", Math.Abs(omega), "top_rate", TopRate, "dv_left", dv, "ut", ut));
                var cmd = Math.Atan2(D(last, "al"), D(last, "up")) * 180 / Math.PI;
                // The lean is checked below 50 m, once the command has turned onto the cap (it turns no faster than the craft). Above
                // it the craft can speed past 2 m/s across and leave the zone, as the player asked, so it is only recorded there.
                if (zoneUt < 0 && y < LandSteer.CapBelowM) zoneUt = ut;
                if (zoneUt < 0 && Math.Abs(vx) < LandSteer.CapSlowerMs) flight.MostLeanSlowHigh = Math.Max(flight.MostLeanSlowHigh, Math.Abs(cmd));
                if (zoneUt < 0 && Math.Abs(vx) >= LandSteer.CapSlowerMs) flight.MostLeanFastHigh = Math.Max(flight.MostLeanFastHigh, Math.Abs(cmd));
                if (zoneUt >= 0 && flight.IntoCapS < 0 && Math.Abs(cmd) <= LandSteer.TerminalLeanDeg + 1e-6) flight.IntoCapS = ut - zoneUt;
                if (flight.IntoCapS >= 0) flight.MostLean = Math.Max(flight.MostLean, Math.Abs(cmd));
                if (!double.IsNaN(lastCmd)) flight.MostTurnRate = Math.Max(flight.MostTurnRate, Math.Abs(cmd - lastCmd) / dt);
                lastCmd = cmd;
                // The craft: a rate towards the command, braked in time, within the top rate and the torque's angular acceleration.
                var wanted = Math.Sign(cmd - theta) * Math.Min(TopRate, Math.Sqrt(2 * alpha * Math.Abs(cmd - theta)));
                omega += Math.Max(-alpha * dt, Math.Min(alpha * dt, wanted - omega));
                theta += omega * dt;
                var a = D(last, "throttle") * IkeAMax;
                vx += a * Math.Sin(theta * Math.PI / 180) * dt;
                vy += (a * Math.Cos(theta * Math.PI / 180) - IkeG) * dt;
                x += vx * dt;
                y += vy * dt;
                dv -= a * dt;
                ut += dt;
                flight.Error = D(last, "error");
            }
            flight.Rate = Math.Abs(omega);
            flight.Lean = Math.Abs(theta);
            flight.Down = -vy;
            flight.Sideways = Math.Abs(vx);
            flight.Settle = D(last, "settle_s");
            return flight;
        }

        [Theory]
        // 908b's trace: 368 m at 30 m/s down, 42-85 degrees off and turning 28-34°/s; 165 m; 37 m at 54 degrees and 11°/s; 8 m at 43 and 21.
        // From 8 m it can't be saved in the second it has (the settle rights it but drifts it), so only the command is checked there.
        [InlineData(368, 30.2, 1.9, 60, 33, true)]
        [InlineData(165, 30.6, 5.3, -44, 27, true)]
        [InlineData(37, 6.2, 0.4, 54, 11, true)]
        [InlineData(8, 2.8, 1.3, 43, 21, false)]
        public void From_908bs_final_descent_the_command_leans_at_most_20_degrees_where_capped_and_turns_no_faster_than_the_craft(double h, double vDown, double vH,
            double lean, double rate, bool landsUpright)
        {
            // The cap's zone is below 50 m or under 2 m/s across (the player's call): from coming below 50 m the command turns onto
            // the cap within 2 s and stays there to touchdown. Its turn rate is checked over the whole final descent.
            var flight = Fly(h, vDown, vH, lean, rate);
            Assert.InRange(flight.IntoCapS, 0, 2);
            Assert.InRange(flight.MostLean, 0, 20 + 1e-6);
            Assert.InRange(flight.MostTurnRate, 0, TopRate + 1e-6);
            if (!landsUpright) return;
            // #865's pass, where 908b came in 43 degrees off at 21°/s and 1.7 m/s sideways: no more than 5 degrees off and 2°/s, leaning
            // under 10, not drifting.
            Assert.InRange(flight.Error, 0, 5);
            Assert.InRange(flight.Rate, 0, 2);
            Assert.InRange(flight.Lean, 0, 10);
            Assert.InRange(flight.Down, 0, 2.5);
            Assert.InRange(flight.Sideways, 0, 0.5);
        }

        // ---- The steering (land_steer's rules, #511, #530, #595) ----

        /// <summary>Steer for a demand (al, up) at a velocity (v_al, v_up), the last direction straight back, a_max 10.</summary>
        private static double[] Steer(double al, double up, double vAl, double vUp, out double mag)
        {
            return LandSteer.Steer(new[] { al, 0, up }, new[] { -1.0, 0, 0 }, new[] { vAl, 0, vUp }, 10, out mag);
        }

        private static double OffRetrograde(double[] d, double vAl, double vUp)
        {
            return Math.Acos(Math.Max(-1, Math.Min(1, -(d[0] * vAl + d[2] * vUp) / Math.Sqrt(vAl * vAl + vUp * vUp)))) * 180 / Math.PI;
        }

        [Fact]
        public void A_demand_under_5_percent_of_the_maximum_holds_the_last_direction_and_the_steering_stays_within_45_degrees_of_retrograde()
        {
            // #511: the tiny demand that flipped every tick on Twin Craters (attitude errors of 40-106 degrees) moves nothing.
            double mag;
            foreach (var tiny in new[] { Steer(0.3, 0.2, 567, 13, out mag), Steer(-0.3, -0.2, 567, 13, out mag), Steer(0, 0.49, 567, -5, out mag) })
            {
                Assert.Equal(-1, tiny[0], 9);
                Assert.Equal(0, tiny[2], 9);
            }
            // Straight up at 567 m/s across the ground: laid 45 degrees off retrograde, upward.
            var up = Steer(0, 5, 567, 0, out mag);
            Assert.Equal(45, OffRetrograde(up, 567, 0), 3);
            Assert.True(up[2] > 0);
            // Ahead along the track: never.
            Assert.InRange(OffRetrograde(Steer(5, 0, 567, 13, out mag), 567, 13), 0, 45.0001);
            Assert.InRange(OffRetrograde(Steer(3, 4, 300, -40, out mag), 300, -40), 0, 45.0001);
            Assert.InRange(OffRetrograde(Steer(-1, 9, 100, 0, out mag), 100, 0), 0, 45.0001);
            Assert.InRange(OffRetrograde(Steer(0, -5, 567, 13, out mag), 567, 13), 0, 45.0001);
            // Within the cone: as asked.
            Assert.Equal(-8 / Math.Sqrt(73), Steer(-8, 3, 567, -10, out mag)[0], 6);
            // Slow (the approach, 50 m/s or under): as asked, straight up if need be.
            Assert.Equal(1, Steer(0, 5, 40, -5, out mag)[2], 9);
        }

        [Fact]
        public void A_demand_ahead_over_50_m_s_thrusts_nothing_rather_than_being_turned_up_and_back()
        {
            // #530: on Minmus Flats a push ahead at 50 m/s was laid 45 degrees off retrograde at its full 12 m/s² and climbed the craft.
            double mag;
            Steer(12, 0, 52, 0.5, out mag);
            Assert.Equal(0, mag, 9);
            Steer(12, 0, 180, -2, out mag);
            Assert.Equal(0, mag, 9);
            // An upward demand keeps its upward part, as before (#511): 5 m/s² up at 45 degrees takes 7.07.
            Steer(0, 5, 567, 0, out mag);
            Assert.Equal(5 / Math.Sin(Math.PI / 4), mag, 6);
            // Within the cone, as asked.
            Steer(-8, 3, 567, -10, out mag);
            Assert.Equal(Math.Sqrt(73), mag, 6);
        }

        [Fact]
        public void With_no_thrust_or_a_demand_that_is_zero_or_not_finite_it_holds_the_last_direction()
        {
            // #595: a_max 0 divided by zero in a stage that ran dry; a zero or non-finite demand did the same by its magnitude.
            var last = new[] { -1.0, 0, 0 };
            var vel = new[] { 567.0, 0, 13 };
            double mag;
            foreach (var c in new[]
            {
                Tuple.Create(0.0, new[] { 0.0, 0, 0 }), Tuple.Create(0.0, new[] { 3.0, 0, 4 }), Tuple.Create(10.0, new[] { 0.0, 0, 0 }),
                Tuple.Create(10.0, new[] { double.PositiveInfinity, 0, 0 }), Tuple.Create(10.0, new[] { double.NaN, 0, 0 }),
            })
            {
                Assert.Equal(last, LandSteer.Steer(c.Item2, last, vel, c.Item1, out mag));
                Assert.Equal(0, mag);
            }
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_ticks_take_under_2_ms()
        {
            // ADR 0014 guardrail 6: LANDSTEER is called every braking tick; 1,000 ticks (about 20 s of descent at 50 Hz) in 2 ms.
            var braking = Lex("want", Lex("al", -8.0, "cr", 0.5, "up", 3.0, "h", 900.0, "h_ref", 600.0), "last", Lex("al", -1.0, "cr", 0.0, "up", 0.0),
                "v_al", 300.0, "v_cr", 2.0, "v_up", -20.0, "a_max", IkeAMax, "g", IkeG, "facing", Nose(-60), "rate", 3.0, "top_rate", TopRate,
                "dv_left", 600.0, "ut", 100.0);
            var settling = Terminal(0.2, 1.5, 8, 25, 10);
            Timing.Within(2, () =>
            {
                for (var i = 0; i < 500; i++)
                {
                    LandSteer.Tick(braking);
                    LandSteer.Tick(settling);
                }
            });
        }
    }
}
