// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// deorbit block test, Kerbin to KSC with defaults (#666): the Mission Script the debug block test runner
// starts (bundled with the Core Library, as for a real Mission). The craft arrives in an 80 km circular
// equatorial orbit. deorbit(target ksc) and reentry() are called with no options, as Haiku did in Mission
// efe0: deorbit's separate is "none", so it aims for the whole stack, then reentry's "auto" drops the tank
// and engine below the heat shield, so the lighter capsule lands short. The Script Result is deorbit's plus
// miss_km (actual landing against deorbit's prediction), target_km (against KSC) and parts_dropped.

@lazyglobal off.

wait until ship:unpacked.

// Great-circle km between two lat/lng lexicons on the body the craft is on.
function test_km {
  parameter from_geo, to_geo.
  local h is sin((to_geo:lat - from_geo:lat) / 2) ^ 2 + cos(from_geo:lat) * cos(to_geo:lat) * sin((to_geo:lng - from_geo:lng) / 2) ^ 2.
  return 2 * arcsin(min(1, sqrt(h))) * constant:pi / 180 * body:radius / 1000.
}

local aim is deorbit_ksc().
local result is deorbit(lexicon("target", "ksc")).
print "deorbit defaults test: success " + result:success + ", burn at " + result:burn_ut + ", dv " + result:dv_used + ", predicted " + result:predicted_geo + " (" + result:predicted_error_km + " km off), " + result:reason.
// The checker compares a field with a literal only, so the test reports the comparisons it needs; a
// distance it never measured stays far over its check.
set result["landed_or_splashed"] to false.
set result["miss_km"] to 9999.
set result["target_km"] to 9999.
set result["parts_dropped"] to 0.
set result["actual_geo"] to lexicon().
if result:burn_ut > 0 {
  local before is ship:parts:length.
  local down is reentry().
  set result["parts_dropped"] to before - ship:parts:length.
  set result["reentry_success"] to down:success.
  set result["reentry_reason"] to down:reason.
  set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
  if down:haskey("geo") {
    set result["actual_geo"] to down:geo.
    set result["target_km"] to round(test_km(aim, down:geo), 2).
    if result:predicted_geo:haskey("lat") {
      set result["miss_km"] to round(test_km(result:predicted_geo, down:geo), 2).
    }
  }
  print "deorbit defaults test: came down at " + result:actual_geo + ", " + result:miss_km + " km from the prediction, " + result:target_km + " km from the target, " + result:parts_dropped + " parts dropped".
}
addons:claudia:report(result).
