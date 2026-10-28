// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// change_orbit block test (resonant): the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The reference craft arrives in a 100 km circular equatorial Kerbin orbit;
// change_orbit must make a 2:3 resonant orbit, its period within 1% of 1.5 times the one it started on.

@lazyglobal off.

wait until ship:unpacked.
local before is ship:orbit:period.
local result is change_orbit(lexicon("goal", "resonant", "value", 2, "of", 3, "at", "in 300")).
set result:test_period_before to round(before, 1).
// period_actual is missing when the block failed before it read the orbit (an option or the solve).
set result:test_period_ratio to -1.
if result:haskey("period_actual") {
  set result:test_period_ratio to round(result:period_actual / before, 4).
}
print "change_orbit test: success " + result:success + ", period ratio " + result:test_period_ratio + " from " + round(before, 1) + " s, burn at " + result:burn_ut + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
