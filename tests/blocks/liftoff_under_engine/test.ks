// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch with a decoupler and an Entry Shield under the lit engine (#945): the Mission Script the debug block test runner starts.
// The test lights the LV-T45 first, as a powered descent leaves it (the throttle is 0, so it gives thrust but does not burn), then
// launch has to clear the TD-12 and the shield under it before its throttle comes up. A trigger reads the craft the moment the throttle
// first rises and reports whether they were already gone.

@lazyglobal off.

wait until ship:unpacked.

local engs is list().
list engines in engs.
for e in engs {
  e:activate().
}
wait 1.
local cleared is false.
local seen is false.
when ship:control:mainthrottle > 0 then {
  set seen to true.
  local left is 0.
  for p in ship:parts {
    if p:name = "HeatShield1" {
      set left to left + 1.
    }
  }
  set cleared to left = 0.
}
local result is launch(lexicon("target_ap", 8000, "turn_start_speed", 100000, "stage_limit", 2)).
local shields is 0.
for p in ship:parts {
  if p:name = "HeatShield1" {
    set shields to shields + 1.
  }
}
set result["test_shield_left"] to shields.
set result["test_cleared_before_climb"] to seen and cleared.
set result["test_top_alt"] to round(altitude).
print "liftoff under engine test: success " + result:success + ", shield left " + shields + ", cleared first " + result:test_cleared_before_climb + ", " + result:reason.
addons:claudia:report(result).
