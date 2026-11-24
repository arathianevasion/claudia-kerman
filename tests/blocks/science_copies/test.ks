// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// science copies test (#1297): the Mission Script the debug block test runner starts. The probe carries two 2HOT thermometers.
// Each call names temperatureScan, so a reading is wanted every time. The Script Result is the first call's plus what the
// later calls did: one thermometer per call, a free one first, then "no free part" under "keep", then a part freed by "auto".

@lazyglobal off.

// How many thermometer entries in res have the given action and, if detail_has is not "", a detail containing it.
function copies_count {
  parameter res, action, detail_has.
  local n is 0.
  for entry in res:results {
    if entry:experiment = "temperatureScan" and entry:action = action and (detail_has = "" or entry:detail:contains(detail_has)) {
      set n to n + 1.
    }
  }
  return n.
}

wait until ship:unpacked.
local named is lexicon("experiments", list("temperatureScan"), "transmit", "keep").
local first is science(named).
local second is science(named).
local third is science(named).
local fourth is science(lexicon("experiments", list("temperatureScan"), "transmit", "auto")).
local held is 0.
for m in ship:modulesnamed("ModuleScienceExperiment") {
  if addons:claudia:science(m:part):experiment = "temperatureScan" and m:hasdata {
    set held to held + 1.
  }
}
set first["ran_first"] to copies_count(first, "ran", "").
set first["ran_second"] to copies_count(second, "ran", "").
// The checker compares a field with a literal only, so these comparisons are reported.
set first["kept_two"] to held = 2.
set first["third_success"] to third:success.
set first["third_no_free_part"] to third:reason:contains("no free part").
set first["fourth_success"] to fourth:success.
set first["fourth_freed"] to copies_count(fourth, "ran", "held data sent to free the part").
print "science copies test: ran " + first["ran_first"] + "/" + first["ran_second"] + ", third " + third:reason + ", fourth freed " + first["fourth_freed"] + ", held " + held.
addons:claudia:report(first).
