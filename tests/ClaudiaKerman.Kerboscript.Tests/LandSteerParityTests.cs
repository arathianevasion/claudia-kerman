// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// ADR 0014 guardrail 4 for #865: land_descend's braking tick on kOS (land_guide's lean limit, land_steer and the throttle
    /// scaled by the cosine of the error) against Core's <see cref="LandSteer.Tick"/> on the same inputs, above the final descent
    /// and on the Mun terminal states of m20261107-051747-7bc7. Deleted with the Kerboscript it compares.
    /// </summary>
    public class LandSteerParityTests
    {
        static LandSteerParityTests()
        {
            KosHarness.Register(typeof(LandSteerRigFunctions));
        }

        // One tick: land_guide's demand for the state (s, c, v_al, v_cr, v_up, h; a_max, g), land_steer from straight back, today's
        // throttle with the nose off the direction by (dx, dy, dz) before normalising; then the same through test_land_steer.
        // Prints kOS's al, cr, up, mag, throttle, then Core's.
        private static double[] Tick(double s, double c, double vAl, double vCr, double vUp, double h, double aMax, double g, string last = "-1, \"cr\", 0, \"up\", 0")
        {
            var source = string.Format(CultureInfo.InvariantCulture, @"
local a_max is {6}.
local w is land_guide(lexicon(""s"", {0}, ""c"", {1}, ""v_al"", {2}, ""v_cr"", {3}, ""v_up"", {4}, ""h"", {5}, ""h_start"", 3000, ""v0"", 575,
  ""shape"", 2, ""v_top"", 575, ""a_max"", a_max, ""g"", {7}, ""radius"", 200000 + {5}), 2, 1.1).
local last is lexicon(""al"", {8}).
local d is land_steer(w, last, lexicon(""al"", {2}, ""cr"", {3}, ""up"", {4}), a_max).
local p is list(d:al, d:cr, d:up).
if sqrt(d:al ^ 2 + d:cr ^ 2 + d:up ^ 2) <= 0.001 {{ set p to list(0, 0, 1). }}
local f is list(d:al + 0.05, d:cr - 0.03, d:up + 0.02).
local fn is sqrt(f[0] ^ 2 + f[1] ^ 2 + f[2] ^ 2).
local pn is sqrt(p[0] ^ 2 + p[1] ^ 2 + p[2] ^ 2).
local err is arccos(max(-1, min(1, (f[0] * p[0] + f[1] * p[1] + f[2] * p[2]) / (fn * pn)))).
local tset is min(1, d:mag / max(0.001, a_max)) * max(0, cos(err)).
print d:al. print d:cr. print d:up. print d:mag. print tset.
local k is test_land_steer(lexicon(""want"", w, ""last"", last, ""v_al"", {2}, ""v_cr"", {3}, ""v_up"", {4}, ""a_max"", a_max, ""g"", {7},
  ""facing"", lexicon(""al"", f[0], ""cr"", f[1], ""up"", f[2]), ""rate"", 1, ""top_rate"", 0, ""dv_left"", 900, ""ut"", 100)).
print k:al. print k:cr. print k:up. print k:mag. print k:throttle.
", s, c, vAl, vCr, vUp, h, aMax, g, last);
            return KosHarness.Run(CoreLibrary.Bundle(source)).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        private static void Same(double[] t)
        {
            Assert.Equal(10, t.Length);
            for (var i = 0; i < 5; i++) Assert.Equal(t[i], t[i + 5], 9);
        }

        private static double Lean(double al, double cr, double up)
        {
            return Math.Atan2(Math.Sqrt(al * al + cr * cr), up) * 180 / Math.PI;
        }

        [Fact]
        public void Above_the_final_descent_Core_steers_and_throttles_as_land_did()
        {
            // The reference lander (60 kN, 5.4 t) at the Mun: braking fast inside and outside the 45 degree cone, a demand ahead,
            // the approach, and a low hop still over its shape.
            var a = 60000 / 5400.0;
            Same(Tick(30000, 0, 575, 0, -5, 3000, a, 1.63));
            Same(Tick(12000, 400, 300, -8, -40, 1800, a, 1.63));
            Same(Tick(2000, -150, 180, 3, 2, 900, a, 1.63));
            Same(Tick(-300, 20, 52, 0, 0.5, 600, a, 1.63));
            Same(Tick(224, 10, 49.5, 0, -22, 488, a, 1.63));
            Same(Tick(78, -5, 24.3, 1, -19.1, 405, a, 1.63));
            Same(Tick(40, 0, 14.5, 0, -17.4, 368, a, 1.63));
        }

        [Fact]
        public void On_the_Mun_landing_of_7bc7_the_final_descent_steers_and_throttles_as_land_did_unless_land_leaned_over_20_degrees()
        {
            // m20261107-051747-7bc7's terminal trace (Mun Shot v2: 60 kN, about 5.2 t): h, v_h, v_down, dist, every 2 s to 8 m.
            var a = 60000 / 5200.0;
            var changed = 0;
            foreach (var st in new[]
            {
                new[] { 357, 11.2, 16.7, 30 }, new[] { 322, 5.9, 17.3, 13 }, new[] { 284, 2.3, 20.4, 5 }, new[] { 240, 0.9, 23.7, 2 },
                new[] { 192, 1.1, 25.3, 2 }, new[] { 147, 0.3, 19.8, 4 }, new[] { 112, 1.4, 15.3, 2 }, new[] { 85, 0.3, 11.7, 0 },
                new[] { 65, 0.2, 8.9, 0 }, new[] { 49, 0.1, 6.8, 0 }, new[] { 38, 0.1, 5.1, 0 }, new double[] { 29, 0, 3.9, 0 },
                new double[] { 22, 0, 3, 0 }, new double[] { 17, 0, 2.3, 0 }, new double[] { 12, 0, 2.1, 0 }, new double[] { 8, 0, 2, 0 },
            })
            {
                // The last direction a few degrees off upright, as it is by then (a demand under 5% holds it).
                var t = Tick(st[3], 0.5, st[1], 0.1, -st[2], st[0], a, 1.63, "0.06, \"cr\", -0.02, \"up\", 0.998");
                // The first (30 m to go) is still the approach, leaning 57 degrees as before.
                if (st[3] >= 30 || Lean(t[0], t[1], t[2]) <= LandSteer.TerminalLeanDeg)
                {
                    Same(t);
                    continue;
                }
                // At 322 and 284 m land asked for sideways thrust only (falling slower than it wanted), so it laid the craft
                // flat at 21% and 9% throttle, as 7bc7's trace shows; the cap leaves it upright and coasting instead.
                changed++;
                Assert.InRange(Lean(t[5], t[6], t[7]), 0, LandSteer.TerminalLeanDeg + 1e-9);
                Assert.Equal(0, t[9], 9);
            }
            Assert.Equal(2, changed);
        }

        [Fact]
        public void The_lean_limit_above_the_final_descent_is_land_max_tilt()
        {
            foreach (var h in new[] { -3.0, 0, 2.5, 7.5, 10, 20, 42.4, 42.5, 50, 900 })
                Assert.Equal(double.Parse(KosHarness.Run(CoreLibrary.Bundle("print land_max_tilt(" + h.ToString(CultureInfo.InvariantCulture) + ").")).Single(),
                    CultureInfo.InvariantCulture), LandSteer.MaxLean(h, false), 9);
        }
    }
}
