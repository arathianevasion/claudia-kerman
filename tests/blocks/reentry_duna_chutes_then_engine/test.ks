// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, chutes first, engines last (#954): reentry_duna_powered's reference craft (the Mk16 and an LV-T45, which has a gimbal), in an 80 km circular
// equatorial orbit with no stage fired. deorbit stages into the LV-T45 and burns for 0 N, 0 E; reentry opens the Chute Group ("auto"), waits until the chute is fully open,
// then burns only the last stretch. The Script Result is reentry's, plus whether it came down and whether the gimbal was given back.

@lazyglobal off.

wait until ship:unpacked.

local burn is deorbit(lexicon("target", lexicon("lat", 0, "lng", 0), "stage_limit", 2)).
print "reentry chutes then engine test: deorbit " + burn:success + ", " + burn:reason.
local result is burn.
if burn:success {
  set result to reentry().
  set result["deorbit_dv"] to burn:dv_used.
}
// The checker compares a field with a literal only, so the test reports the comparisons it needs.
set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
if not result:haskey("chutes_used") {
  set result["chutes_used"] to list().
  set result["powered_dv"] to 0.
  set result["touchdown_speed"] to -1.
}
set result["chutes_opened"] to result:chutes_used:length.
local gimbal_locked is false.
for e in ship:engines {
  if e:hasgimbal and e:gimbal:lock { set gimbal_locked to true. }
}
set result["gimbal_locked"] to gimbal_locked.
print "reentry chutes then engine test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, powered dv " + result:powered_dv + ", chutes " + result:chutes_opened + ", gimbal locked " + gimbal_locked + ", " + result:reason.
addons:claudia:report(result).
