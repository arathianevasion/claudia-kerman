// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// site_hop block test from the ground (#1355): the Mission Script the debug block test runner starts (bundled with the
// Core Library). The runner lands the lander on the Mun at 1.153, 79.031 (#1358: East Crater, where f623 touched down) and
// starts this once it has settled; site_hop plans and flies a suborbital hop to 1.153, 83.33, about 15 km east, and
// land's powered descent takes over on the arc.

@lazyglobal off.

wait until ship:unpacked.
// The runner starts this only once the craft has settled on its legs (#1358).
local started is status.
local start_slope is round(addons:claudia:slope(ship:geoposition), 1).
print "site_hop_landed test: start " + started + " at " + round(ship:geoposition:lat, 3) + ", " + round(ship:geoposition:lng, 3).
local site is latlng(1.153, 83.33).
local site_slope is round(addons:claudia:slope(site), 1).
print "site_hop_landed test: slope " + start_slope + " at the start, " + site_slope + " at the site".
local result is site_hop(lexicon("target", site)).
set result["test_start"] to started.
set result["test_start_slope"] to start_slope.
set result["test_site_slope"] to site_slope.
set result["test_hop_m"] to round((site:position - ship:position):mag).
print "site_hop_landed test: success " + result:success + ", " + result:test_hop_m + " m off, hop dv " + result:hop_burn_dv + ", " + result:reason.
addons:claudia:report(result).
