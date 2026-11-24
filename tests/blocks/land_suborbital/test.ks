// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, a start from a suborbital arc (#1309): the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). The reference lander arrives in a 30 km circular
// equatorial Mun orbit with no stage fired; exec_node flies a 59 m/s retrograde burn a minute on, which drops
// periapsis to about 50 km under the surface, so the craft is on a suborbital arc. land, given "any", must aim where
// that arc meets the ground and fly its powered descent from the arc, with no deorbit or correction burn.

@lazyglobal off.

wait until ship:unpacked.
add node(time:seconds + 60, 0, 0, -59).
local burned is exec_node(lexicon("stage_limit", 2)).
print "land_suborbital test: burn " + burned:success + ", " + status + ", periapsis " + round(ship:orbit:periapsis) + " m".
local was is status.
local result is land(lexicon("target", "any", "daylight", false)).
set result["started_suborbital"] to burned:success and was = "SUB_ORBITAL".
print "land_suborbital test: success " + result:success + ", " + result:distance_to_target + " m off, " + result:touchdown_speed_actual + " m/s, dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
