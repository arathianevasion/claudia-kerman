// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, chutes first, engines last (#954): reentry_duna_powered's reference craft (the Mk16 and an LV-T45, which has a gimbal), started at the top of a
// near-vertical 8 km hop (test.cfg has why) with no stage fired. reentry opens the Chute Group ("auto"), waits until the chute is fully open, then burns only the last stretch. The Script Result is reentry's, plus whether it came down and whether the gimbal was given back.

@lazyglobal off.

wait until ship:unpacked.

local result is reentry().
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
