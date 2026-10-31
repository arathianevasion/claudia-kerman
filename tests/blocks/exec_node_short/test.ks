// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// exec_node_short block test (#577): the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The exec_node reference craft arrives in a 100 km circular Kerbin orbit with no
// stage fired, and flies three 120 m/s prograde nodes, each cut short by a trigger that edits the node mid-burn: once
// 10 m/s is left, the node's prograde drops by cut, so the burn overshoots and stops that far short the other way.
//   trimmed: cut 14 leaves about 4 m/s, within finish_fraction (6 m/s of 120); the trim burns it off: success.
//   short:   as trimmed, but once the trim has 2 m/s left the node gains 2 m/s normal, off the trim's line, which the
//            trim leaves: about 2 m/s over max_residual 1, within the fraction: success with finished_short.
//   over:    cut 30 leaves about 20 m/s, over the fraction: a failure, no trim.

@lazyglobal off.

wait until ship:unpacked.
local cut is 0.
local add_normal is false.
local cut_done is false.
local normal_done is false.
local cuts is 0.
local watching is true.
when true then {
  if hasnode and cut > 0 {
    local nd is nextnode.
    if not cut_done and nd:deltav:mag < 10 {
      set nd:prograde to nd:prograde - cut.
      set cut_done to true.
      set cuts to cuts + 1.
    } else if cut_done and add_normal and not normal_done and nd:deltav:mag < 2 {
      set nd:normal to nd:normal + 2.
      set normal_done to true.
    }
  }
  return watching.
}

// Flies a 120 m/s prograde node 300 s ahead with the node cut by c m/s (and the normal added if normal).
function test_fly_cut {
  parameter c, normal.
  set cut to c.
  set add_normal to normal.
  set cut_done to false.
  set normal_done to false.
  add node(time:seconds + 300, 0, 0, 120).
  local res is exec_node(lexicon("stage_limit", 2, "max_residual", 1)).
  set cut to 0.
  // A failure leaves its node; the next case plans its own.
  if hasnode {
    remove nextnode.
  }
  return res.
}

local result is test_fly_cut(14, false).
local short_res is test_fly_cut(14, true).
set result["test_normal_added"] to normal_done.
local over_res is test_fly_cut(30, false).
set watching to false.

set result["test_cuts"] to cuts.
set result["test_short_success"] to short_res:success.
set result["test_short_finished_short"] to short_res:finished_short.
set result["test_short_residual"] to short_res:residual.
set result["test_over_success"] to over_res:success.
set result["test_over_finished_short"] to over_res:finished_short.
set result["test_over_residual"] to over_res:residual.
print "exec_node_short test: trimmed " + result:success + " residual " + result:residual + " m/s; short " + short_res:success
  + " finished_short " + short_res:finished_short + " residual " + short_res:residual + " m/s; over " + over_res:success + " residual " + over_res:residual + " m/s, " + over_res:reason.
addons:claudia:report(result).
