// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, a Point of Interest target at Minmus (#713): the reference lander, in a 30 km Minmus orbit inclined 25 degrees
// with no stage fired, lands beside Monolith TMA-8: on the flattest flyable ground 150 m to 1.5 km from it, never on it.

@lazyglobal off.

wait until ship:unpacked.
local result is land(lexicon("target", lexicon("poi", "Monolith TMA-8"), "daylight", true)).
set result["sun_up"] to vang(ship:up:vector, body("Sun"):position) < 90.
print "land_poi test at Minmus: success " + result:success + ", beside " + result:poi + ", " + result:distance_to_poi + " m from it, " + result:distance_to_target + " m off the site, " + result:touchdown_speed_actual + " m/s, lean " + result:lean + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
