// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// transfer_to block test, Duna to Kerbin with the landing at KSC (#918): the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). The reference craft arrives in a 100 km circular Duna orbit with no
// stage fired. transfer_to Kerbin ejects at the window, corrects on the Sun's patch (target_pe only), and inside Kerbin's sphere of
// influence flies the correction aimed at KSC (#915); reentry then starts with #880's Late Trim and brings the craft down. The Script
// Result is transfer_to's, plus where the craft came down: target_km against KSC. KSP.log has the lines the doc names:
// `block call TRANSFERSTEP` ("landing aim: ...") and `block call ENTRYTRIM`.

@lazyglobal off.

wait until ship:unpacked.

// Great-circle km between two lat/lng lexicons on the body the craft is on.
function test_km {
  parameter from_geo, to_geo.
  local h is sin((to_geo:lat - from_geo:lat) / 2) ^ 2 + cos(from_geo:lat) * cos(to_geo:lat) * sin((to_geo:lng - from_geo:lng) / 2) ^ 2.
  return 2 * arcsin(min(1, sqrt(h))) * constant:pi / 180 * body:radius / 1000.
}

// The Mission names no place, so a trip home aims at KSC by default (#880); this is where the craft is measured against.
local aim is claudia_ksc().
local result is transfer_to(lexicon("target", "Kerbin", "target_pe", 30000, "stage_limit", 2)).
print "transfer_to kerbin from duna test: success " + result:success + ", " + result:reason.
// The checker compares a field with a literal only, so the test reports the comparisons it needs; a distance it never
// measured stays far over its check.
set result["landed_or_splashed"] to false.
set result["target_km"] to 9999.
set result["actual_geo"] to lexicon().
if result:success {
  local down is reentry().
  set result["reentry_success"] to down:success.
  set result["reentry_reason"] to down:reason.
  set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
  if down:geo:haskey("lat") {
    set result["actual_geo"] to down:geo.
    set result["target_km"] to round(test_km(aim, down:geo), 2).
  }
  print "transfer_to kerbin from duna test: came down at " + result:actual_geo + ", " + result:target_km + " km from KSC, correction_dv " + result:correction_dv.
}
addons:claudia:report(result).
