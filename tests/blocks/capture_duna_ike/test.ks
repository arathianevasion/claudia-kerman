// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// capture block test arriving at Duna with an Ike leg next (#866): the Mission Script the debug block test runner starts. From a 100 km
// Kerbin orbit, transfer_to Duna at a 60 km periapsis; capture, with the test's plan naming Ike next, keeps a high ellipse timed to Ike;
// transfer_to Ike leaves from it at a periapsis; capture at Ike. The result is Ike's capture, with the kept line and the delta-v from
// Duna's arrival to low Ike orbit.

@lazyglobal off.

wait until ship:unpacked.
local inbound is transfer_to(lexicon("target", "Duna", "target_pe", 60000, "stage_limit", 2)).
print "capture duna ike test: transfer_to Duna " + inbound:success + ", pe " + inbound:target_pe_actual + " m, " + inbound:reason.
if not inbound:success {
  addons:claudia:report(inbound).
} else {
  if body:name <> "Duna" {
    coast(lexicon("until", inbound:encounter_ut + 60)).
  }
  local kept is capture(lexicon("alt", 60000, "stage_limit", 2)).
  print "capture duna ike test: Duna capture " + kept:success + ", " + kept:capture_dv + " + plane " + kept:plane_dv + " m/s; " + kept:kept + "; " + kept:reason.
  if not kept:success {
    addons:claudia:report(kept).
  } else {
    local hop is transfer_to(lexicon("target", "Ike", "target_pe", 10000, "stage_limit", 2)).
    print "capture duna ike test: transfer_to Ike " + hop:success + ", ejection " + hop:ejection_dv_used + " at UT " + hop:departure_ut + ", correction " + hop:correction_dv + " m/s; " + hop:reason.
    if not hop:success {
      addons:claudia:report(hop).
    } else {
      if body:name <> "Ike" {
        coast(lexicon("until", hop:encounter_ut + 60)).
      }
      local result is capture(lexicon("stage_limit", 2)).
      set result:body to body:name.
      set result:kept_for_ike to kept:kept:startswith("kept") and kept:kept:contains("for Ike").
      set result:arrival_to_ike_dv to round(kept:capture_dv + kept:plane_dv + max(0, hop:ejection_dv_used) + hop:correction_dv + hop:plane_dv + result:capture_dv + result:plane_dv, 1).
      print "capture duna ike test: Ike capture " + result:success + ", " + result:capture_dv + " m/s; Duna arrival to low Ike orbit " + result:arrival_to_ike_dv + " m/s; " + result:reason.
      addons:claudia:report(result).
    }
  }
}
