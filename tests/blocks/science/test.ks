// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// science block test: the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The probe arrives in a 100 km circular Kerbin orbit in Sandbox.
// science runs once, keeping the data, then again: the second call must run nothing, because every
// experiment now holds data. The Script Result is the first call's plus counts of the entries, and
// kept_all: every experiment that ran kept its data.

@lazyglobal off.

wait until ship:unpacked.
local first is science(lexicon("transmit", "keep")).
local second is science(lexicon("transmit", "keep")).
local ran is 0.
local kept is 0.
for entry in first:results {
  if entry:action = "ran" {
    set ran to ran + 1.
    if entry:transmit = "kept" {
      set kept to kept + 1.
    }
  }
}
// #612: the kept data is still aboard; asking to transmit sends it (with a link) or says why not (kept hint).
local third is science(lexicon("transmit", "transmit")).
local sent_held is 0.
for entry in third:results {
  if entry:action = "sent held data" {
    set sent_held to sent_held + 1.
  }
}
local ran_again is 0.
for entry in second:results {
  if entry:action = "ran" {
    set ran_again to ran_again + 1.
  }
}
set first["ran"] to ran.
set first["kept"] to kept.
// The checker compares a field with a literal only, so the comparison of two counts is reported.
set first["kept_all"] to kept = ran.
set first["ran_again"] to ran_again.
set first["sent_held"] to sent_held.
set first["third_kept"] to third:haskey("kept").
set first["success_again"] to second:success.
print "science test: success " + first:success + ", ran " + ran + ", kept " + kept + ", ran again " + ran_again + ", " + first:reason.
addons:claudia:report(first).
