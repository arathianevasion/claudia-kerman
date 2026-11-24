// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// site_hop block test, a Site Hop between two Mun sites (#1310): the Mission Script the debug block test runner starts
// (bundled with the Core Library). The lander comes down from a suborbital arc as in land_suborbital, then site_hop
// refuses a site 0.5 km away before liftoff, then hops to a site 20 km (5.73 degrees of longitude) east of where it landed.

@lazyglobal off.

wait until ship:unpacked.
add node(time:seconds + 60, 0, 0, -59).
exec_node(lexicon("stage_limit", 2)).
local first is land(lexicon("target", "any", "daylight", false)).
print "site_hop test: first landing " + first:success + ", " + status.
local here is ship:geoposition.
local near is site_hop(lexicon("target", latlng(here:lat, here:lng + 0.14))).
print "site_hop test: near hop " + near:success + ", " + near:reason.
local stayed is status = "LANDED" and (ship:geoposition:position - here:position):mag < 50.
local site is latlng(here:lat, here:lng + 5.73).
local result is site_hop(lexicon("target", site)).
set result["test_first_landed"] to first:success.
set result["test_near_refused"] to not near:success and near:reason:startswith("the target is 0.").
set result["test_near_stayed"] to stayed.
set result["test_hop_m"] to round((site:position - ship:position):mag).
print "site_hop test: success " + result:success + ", " + result:test_hop_m + " m off, " + result:touchdown_speed_actual + " m/s, hop dv " + result:hop_burn_dv + ", " + result:reason.
addons:claudia:report(result).
