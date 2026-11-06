// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// #693: claudia_tick credits each elapsed interval with the throttle in effect during it (ctx:last_tset, the tick
    /// before), not with the throttle just commanded. The step is claudia_dv_step, a pure helper. The scripts below run
    /// claudia_tick's timing on ticks at i * 0.02 s (the first tick has no earlier interval), with thrust 20 kN over
    /// 10 t (2 m/s^2), so the counted delta-v can be compared with the true integral and with the old formula.
    /// </summary>
    public class DvUsedTests
    {
        private static readonly Regex Printed = new Regex(@"^DV (\S+) OLD (\S+) TRUE (\S+)$");

        private static double[] Parse(string line)
        {
            var m = Printed.Match(line);
            Assert.True(m.Success, "unexpected output: " + line);
            return new[]
            {
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            };
        }

        [Fact]
        public void A_ramp_down_is_counted_within_5_percent_of_the_true_integral()
        {
            // Throttle 1, 0.6, 0.3, 0.1, 0 on ticks at 0.02 s. Each level is held until the next tick, so the true
            // delta-v over the five ticks is 2 * 0.02 * (1 + 0.6 + 0.3 + 0.1) = 0.08 m/s. The old formula credits
            // each interval with the level commanded at its end, which gives 2 * 0.02 * (0.6 + 0.3 + 0.1) = 0.04 m/s.
            var script = @"
local thrust_kn is 20.
local mass_t is 10.
local levels is list(1, 0.6, 0.3, 0.1, 0).
local last_ut is 0.
local last_tset is 0.
local dv_used is 0.
local old_used is 0.
for i in range(levels:length) {
  local now is i * 0.02.
  local tset is levels[i].
  set dv_used to dv_used + claudia_dv_step(last_tset, thrust_kn, mass_t, now - last_ut).
  set old_used to old_used + tset * thrust_kn / mass_t * (now - last_ut).
  set last_tset to tset.
  set last_ut to now.
}
local truth is thrust_kn / mass_t * 0.02 * (1 + 0.6 + 0.3 + 0.1).
print ""DV "" + round(dv_used, 6) + "" OLD "" + round(old_used, 6) + "" TRUE "" + round(truth, 6).
";
            var v = Parse(KosHarness.Run(CoreLibrary.Bundle(script)).Single());
            Assert.InRange(v[0], v[2] * 0.95, v[2] * 1.05);
            // The old formula misses this ramp by about half, so the test would fail on the old accounting.
            Assert.False(v[1] >= v[2] * 0.95 && v[1] <= v[2] * 1.05, "the old formula should miss the ramp: " + v[1]);
        }

        [Fact]
        public void A_steady_burn_is_counted_within_1_percent_of_the_old_count()
        {
            // Throttle 1 for 500 ticks (10 s). Both formulas credit the same 499 intervals of 0.04 m/s (the first
            // tick has no interval), so the count matches the old one to rounding.
            var script = @"
local thrust_kn is 20.
local mass_t is 10.
local last_ut is 0.
local last_tset is 0.
local dv_used is 0.
local old_used is 0.
for i in range(500) {
  local now is i * 0.02.
  set dv_used to dv_used + claudia_dv_step(last_tset, thrust_kn, mass_t, now - last_ut).
  set old_used to old_used + 1 * thrust_kn / mass_t * (now - last_ut).
  set last_tset to 1.
  set last_ut to now.
}
local truth is thrust_kn / mass_t * 0.02 * 499.
print ""DV "" + round(dv_used, 6) + "" OLD "" + round(old_used, 6) + "" TRUE "" + round(truth, 6).
";
            var v = Parse(KosHarness.Run(CoreLibrary.Bundle(script)).Single());
            Assert.InRange(v[0], v[1] * 0.99, v[1] * 1.01);
            Assert.InRange(v[0], v[2] * 0.99, v[2] * 1.01);
        }
    }
}
