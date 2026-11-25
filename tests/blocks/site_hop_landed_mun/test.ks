// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// site_hop block test from the ground (#1355): the Mission Script the debug block test runner starts (bundled with the
// Core Library). The runner lands the lander on the Mun at 2.46, 77.2; site_hop plans and flies a suborbital hop to
// 2.46, 81.5, about 15 km east, and land's powered descent takes over on the arc.

@lazyglobal off.

wait until ship:unpacked.
// Let KSP settle the spawned craft on its legs before reading where it stands.
wait 5.
local started is status.
print "site_hop_landed test: start " + started + " at " + round(ship:geoposition:lat, 3) + ", " + round(ship:geoposition:lng, 3).
local site is latlng(2.46, 81.5).
local result is site_hop(lexicon("target", site)).
set result["test_start"] to started.
set result["test_hop_m"] to round((site:position - ship:position):mag).
print "site_hop_landed test: success " + result:success + ", " + result:test_hop_m + " m off, hop dv " + result:hop_burn_dv + ", " + result:reason.
addons:claudia:report(result).
