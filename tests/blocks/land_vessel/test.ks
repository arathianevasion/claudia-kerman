// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, beside a landed Target Vessel (#662): the Mission Script the debug block test runner starts (bundled with
// the Core Library, as for a real Mission). The reference lander arrives in a 30 km circular equatorial Mun orbit with no
// stage fired and "Block Test Target" landed on the equator ahead; land's target "vessel" must read where it is from the
// Game Snapshot (through its block-internal call, never the script's) and come to rest beside it, not on it. The script
// only reads the target vessel, by name, to judge the landing. The vessel is read only here, never passed to a function:
// the Script Check follows another vessel into every function it is passed to (ADR 0003), so lv_target is a name no other
// code uses, and its figures reach land_surface_distance as plain numbers through round().

@lazyglobal off.

wait until ship:unpacked.
local lv_target is vessel("Block Test Target").
local target_landed is lv_target:status = "LANDED".
local start_lat is round(lv_target:geoposition:lat, 7).
local start_lng is round(lv_target:geoposition:lng, 7).
print "land_vessel test: the target is " + lv_target:status + " at " + round(start_lat, 4) + ", " + round(start_lng, 4).
local result is land(lexicon("target", "vessel", "daylight", false)).
local end_lat is round(lv_target:geoposition:lat, 7).
local end_lng is round(lv_target:geoposition:lng, 7).
// The checker compares a field with a literal only, so the script reports the comparisons.
set result["test_read_target"] to target_landed and result:success.
set result["test_vessel_m"] to round(land_surface_distance(ship:geoposition:lat, ship:geoposition:lng, end_lat, end_lng, body:radius), 1).
set result["test_target_moved_m"] to round(land_surface_distance(start_lat, start_lng, end_lat, end_lng, body:radius), 1).
print "land_vessel test: success " + result:success + ", " + result["test_vessel_m"] + " m from the target (moved " + result["test_target_moved_m"] + " m), "
  + result:distance_to_target + " m off the site, " + result:touchdown_speed_actual + " m/s, lean " + result:lean + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
