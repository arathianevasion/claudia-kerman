// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// circularize_short block test (#577): the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The exec_node reference craft arrives in a 100 km circular Kerbin orbit with no
// stage fired. Three times, exec_node raises the apoapsis with a 100 m/s prograde burn (to about 240 km the first time,
// for a circularization of about 95 m/s), then circularize at "ap" flies with its node cut short by a trigger: once
// main m/s is left, the node's prograde drops by main + 5, so the burn overshoots the edited node and stops main m/s
// short. A burn short by dv at the circular speed v leaves the periapsis about 4 dv / v of the radius low.
//   short:   main 15 leaves about 15 m/s, ecc about 0.015 and pe about 3% low, within finish_fraction (5%); the trim
//            node then has its prograde cut once 1 m/s of it is burned, so about 14 m/s stays: success, finished_short.
//   trimmed: main 15 as above; the trim finishes it: ecc within tolerance 0.01, success.
//   over:    main 50 leaves pe about 10% low, over the fraction: a failure, no trim.
// The short case flies first, at the lowest orbit, where its 14 m/s is the most eccentricity.

@lazyglobal off.

wait until ship:unpacked.
local main is 0.
local cut_trim is false.
local main_done is false.
local trim_done is false.
local cuts is 0.
local watching is true.
when true then {
  if hasnode and main > 0 {
    local nd is nextnode.
    if not main_done and nd:deltav:mag < main {
      set nd:prograde to nd:prograde - main - 5.
      set main_done to true.
      set cuts to cuts + 1.
    } else if main_done and cut_trim and not trim_done and nd:prograde < 40 and nd:deltav:mag < 14 {
      // The trim's node (its prograde about 15 m/s; the edited main node's is about 75).
      set nd:prograde to nd:prograde - 14.
      set trim_done to true.
      set cuts to cuts + 1.
    }
  }
  return watching.
}

// Raises the apoapsis, then circularizes at it with the burn cut main m/s short (and the trim cut too if trim).
function circularize_cut {
  parameter m, trim.
  add node(time:seconds + 300, 0, 0, 100).
  local raised is exec_node(lexicon("stage_limit", 2)).
  if not raised:success {
    return raised.
  }
  set main to m.
  set cut_trim to trim.
  set main_done to false.
  set trim_done to false.
  local res is circularize(lexicon("stage_limit", 2)).
  set main to 0.
  if hasnode {
    remove nextnode.
  }
  return res.
}

local short_res is circularize_cut(15, true).
local result is circularize_cut(15, false).
local over_res is circularize_cut(50, false).
set watching to false.

set result["test_cuts"] to cuts.
set result["test_short_success"] to short_res:success.
set result["test_short_finished_short"] to short_res:finished_short.
set result["test_short_ecc"] to short_res:ecc.
set result["test_over_success"] to over_res:success.
set result["test_over_finished_short"] to over_res:finished_short.
set result["test_over_ecc"] to over_res:ecc.
print "circularize_short test: short " + short_res:success + " finished_short " + short_res:finished_short + " ecc " + short_res:ecc
  + "; trimmed " + result:success + " ecc " + result:ecc + "; over " + over_res:success + " ecc " + over_res:ecc + ", " + over_res:reason.
addons:claudia:report(result).
