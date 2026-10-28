// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// change_orbit block test (set_inc): the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The reference craft arrives in a 100 km circular equatorial Kerbin orbit with no
// stage fired; change_orbit must turn the orbit to 90 degrees in one burn.

@lazyglobal off.

wait until ship:unpacked.
local result is change_orbit(lexicon("goal", "set_inc", "value", 90, "at", "in 300")).
// The whole result: inc_actual is missing when the block failed before it read the orbit.
print "change_orbit test: " + result.
addons:claudia:report(result).
