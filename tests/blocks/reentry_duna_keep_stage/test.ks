// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, a Duna entry that keeps its stage (#823): the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). reentry_duna_powered's reference craft, in an 80 km circular
// equatorial orbit with no stage fired. deorbit stages into the LV-T45 and burns for 0 N, 0 E; reentry, with its default
// separate and jettison_stage, has no Entry Shield on this craft, so nothing drops, and it brakes to the ground on the
// engine. The Script Result is reentry's, plus how many parts left during the entry.

@lazyglobal off.

wait until ship:unpacked.

local burn is deorbit(lexicon("target", lexicon("lat", 0, "lng", 0), "stage_limit", 2)).
print "reentry keep stage test: deorbit " + burn:success + ", " + burn:reason.
local result is burn.
local parts_before is ship:parts:length.
if burn:success {
  set result to reentry(lexicon("chutes", "none")).
  set result["deorbit_dv"] to burn:dv_used.
}
// The checker compares a field with a literal only, so the test reports the comparisons it needs.
set result["parts_dropped"] to parts_before - ship:parts:length.
set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
if not result:haskey("touchdown_speed") {
  set result["touchdown_speed"] to -1.
  set result["max_temp_pct"] to 0.
  set result["shield_dropped"] to false.
}
print "reentry keep stage test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, max temp " + result:max_temp_pct + "%, " + result:reason.
addons:claudia:report(result).
