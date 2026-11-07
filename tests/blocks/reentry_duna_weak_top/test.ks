// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, a weak engine above a strong one (#842): the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). reentry_duna_powered's craft with a 2 kN Spider in stage 3 above the LV-T45
// in stage 2, arriving in an 80 km circular equatorial orbit with no stage fired. deorbit burns for 0 N, 0 E, keeping the tank
// and engines; reentry then falls through Duna's thin air with no chute armed (chutes "none") and decides for itself (#824) to
// brake on the highest stage that can stop the fall: the LV-T45's, not the Spider's (#842). The Script Result is reentry's, plus
// whether it came down and what became of the chute.

@lazyglobal off.

wait until ship:unpacked.

local burn is deorbit(lexicon("target", lexicon("lat", 0, "lng", 0), "stage_limit", 2)).
print "reentry weak top test: deorbit " + burn:success + ", " + burn:reason.
local result is burn.
if burn:success {
  set result to reentry(lexicon("chutes", "none")).
  set result["deorbit_dv"] to burn:dv_used.
}
// The checker compares a field with a literal only, so the test reports the comparisons it needs.
set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
if not result:haskey("chutes_used") {
  set result["chutes_used"] to list().
  set result["chutes_kept"] to list().
  set result["powered_dv"] to 0.
  set result["touchdown_speed"] to -1.
}
set result["chutes_opened"] to result:chutes_used:length.
set result["chute_stowed"] to result:chutes_kept:length = 1.
print "reentry weak top test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, powered dv " + result:powered_dv + ", " + result:reason.
addons:claudia:report(result).
