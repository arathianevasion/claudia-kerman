// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// exec_node_rcs_turn block test (#692): the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The dock reference craft (four RV-105 blocks, an FL-R10 of monopropellant) arrives in a
// 100 km circular Kerbin orbit; exec_node flies a 10 m/s node 120 s ahead pointing straight behind the craft, so the turn
// to it is about 180 degrees: a big turn, flown as a burst turn (spin up on RCS, coast with RCS off, stop on RCS, settle
// on the wheel). KSP.log's "RCS off: turned (...)" line gives the planned and flown coast rate, the bursts and the push.

@lazyglobal off.

wait until ship:unpacked.
// The node's prograde, normal and radial directions now, read off kOS's own nodes.
function test_basis {
  parameter radial, normal, prograde.
  local nd is node(time:seconds + 120, radial, normal, prograde).
  add nd.
  local dir is nd:deltav:normalized.
  remove nd.
  return dir.
}
local behind is -ship:facing:forevector.
local rad is test_basis(1, 0, 0).
local nrm is test_basis(0, 1, 0).
local pro is test_basis(0, 0, 1).
add node(time:seconds + 120, 10 * vdot(behind, rad), 10 * vdot(behind, nrm), 10 * vdot(behind, pro)).
local turn_deg is vang(ship:facing:forevector, nextnode:deltav).
local mono0 is 0.
for res in ship:resources {
  if res:name = "MonoPropellant" {
    set mono0 to res:amount.
  }
}
local result is exec_node(lexicon("stage_limit", 2)).
local mono1 is 0.
for res in ship:resources {
  if res:name = "MonoPropellant" {
    set mono1 to res:amount.
  }
}
set result["test_turn_deg"] to round(turn_deg, 1).
set result["test_monoprop_used"] to round(mono0 - mono1, 2).
print "exec_node_rcs_turn test: success " + result:success + ", turn " + round(turn_deg) + " deg in " + result:turn_s + " s, monoprop "
  + round(mono0 - mono1, 2) + ", " + result:reason.
addons:claudia:report(result).
