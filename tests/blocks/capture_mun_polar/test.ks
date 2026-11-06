// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// capture block test (#788): the Mission Script the debug block test runner starts. From a 100 km Kerbin orbit, transfer_to to the Mun with plane 90
// sets a polar arrival on the approach; capture then reads the recorded plane and circularizes in it.

@lazyglobal off.

wait until ship:unpacked.
local flown is transfer_to(lexicon("target", "Mun", "target_pe", 30000, "plane", 90, "stage_limit", 2)).
print "capture test: transfer_to " + flown:success + ", plane_dv " + flown:plane_dv + " m/s, correction_dv " + flown:correction_dv + " m/s, " + flown:reason.
if not flown:success {
  addons:claudia:report(flown).
} else {
  if body:name <> "Mun" {
    coast(lexicon("until", flown:encounter_ut + 60)).
  }
  local result is capture(lexicon("stage_limit", 2)).
  print "capture test: " + result:success + ", " + result:capture_method + ", capture " + result:capture_dv + " m/s, plane " + result:plane_dv + " m/s, leftover " + result:plane_leftover_deg + " deg, inc " + result:inc + ", ecc " + result:ecc + ", " + result:reason.
  set result:body to body:name.
  addons:claudia:report(result).
}
