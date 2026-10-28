// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// change_orbit block test (match_plane): the Mission Script the debug block test runner starts (bundled with the
// Core Library, as for a real Mission). The reference craft arrives in a 100 km circular equatorial Kerbin orbit;
// change_orbit must put it in Minmus's orbit plane, within 0.2 degrees.

@lazyglobal off.

wait until ship:unpacked.
local result is change_orbit(lexicon("goal", "match_plane", "target", "Minmus", "tolerance", 0.2)).
// The whole result: rel_inc_actual is missing when the block failed before it read the orbit.
print "change_orbit test: " + result.
addons:claudia:report(result).
