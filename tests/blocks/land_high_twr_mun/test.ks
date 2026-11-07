// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test with far more thrust than it needs (#865): the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). The high TWR lander (about 15:1 at Ike, as m20261107-055424-908b's was, whose
// final descent swung to touchdown 43 degrees off and tipped to 70) arrives in a 30 km circular equatorial orbit with no
// stage fired; land must stage into its engine and land on the equator three quarters of an orbit ahead, ignoring daylight.
// The same script flies tests/blocks/land_high_twr_mun.

@lazyglobal off.

wait until ship:unpacked.
local site is latlng(0, land_wrap_lng(ship:geoposition:lng + 270)).
print "land test: aiming for " + round(site:lat, 3) + ", " + round(site:lng, 3) + " on " + body:name.
local result is land(lexicon("target", site, "daylight", false)).
print "land test: success " + result:success + ", " + result:distance_to_target + " m off, " + result:touchdown_speed_actual + " m/s (" + result:touchdown_h_speed + " sideways), lean " + result:lean + ", settle_s " + result:settle_s + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
