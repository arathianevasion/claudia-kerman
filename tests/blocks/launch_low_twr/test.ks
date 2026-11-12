// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch's burn-through (#932): an upper stage too weak to coast to apoapsis and circularize there. The craft flies on past
// target_ap until the periapsis is above the atmosphere, then circularize trims at apoapsis. The Script Result is circularize's,
// with launch's result under launch_ names; stage_limit 2 keeps the probe's decoupler and chute.

@lazyglobal off.

wait until ship:unpacked.
local ascent is launch(lexicon("target_ap", 80000, "stage_limit", 2)).
print "launch: success " + ascent:success + ", ap_error " + ascent:ap_error + " m, pe " + round(ship:periapsis) + " m, " + ascent:reason.
local result is ascent.
if ascent:success {
  set result to circularize(lexicon("at", "ap", "stage_limit", 2)).
  print "circularize: success " + result:success + ", ecc " + result:ecc + ", " + result:reason.
}
for key in ascent:keys {
  set result["launch_" + key] to ascent[key].
}
addons:claudia:report(result).
