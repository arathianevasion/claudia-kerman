// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// deorbit_separate_plan block test (#835): see test.cfg. The upper engine (stage 2) is lit first, so the kept tanks pay and deorbit must
// fail before any node, naming the burn. With it shut down, deorbit stages into the first stage's engine, which flies the burn from its own
// tanks; the same separate must go, and the upper stage must keep the delta-v it had.

@lazyglobal off.

wait until ship:unpacked.

local upper is list().
for e in ship:engines {
  if e:stage = 2 { upper:add(e). }
}
for e in upper { e:activate(). }
wait 0.5.
local ask is lexicon("target", "ksc", "stage_limit", 2, "separate", 3, "max_dv", 500).
local refused is deorbit(ask).
local refused_reason is refused:reason.
local refused_burn_ut is refused:burn_ut.
local no_node is not hasnode.
for e in upper { e:shutdown(). }
wait 0.5.

local left is drop_parts_dv_left(claudia_stage_drop_plan(3)).
local result is deorbit(ask).
set result["test_left"] to round(left).
set result["test_left_after"] to round(drop_parts_dv_left(lexicon("gone", list()))).
set result["test_kept_pays_reason"] to refused_reason.
set result["test_kept_pays_refused"] to refused_reason:contains("the plan still needs 3000 m/s").
set result["test_kept_pays_reason_names_burn"] to refused_reason:contains("m/s burn").
set result["test_kept_pays_no_burn"] to refused_burn_ut = -1.
set result["test_kept_pays_no_node"] to no_node.
set result["test_dropped_stage_pays_not_refused"] to not result:reason:contains("the plan still needs").
set result["test_kept_tanks_untouched"] to abs(result:test_left_after - left) < 10.
print "deorbit_separate_plan test: refused with " + refused_reason + "; then " + result:reason + ", left " + result:test_left + " then " + result:test_left_after.
addons:claudia:report(result).
