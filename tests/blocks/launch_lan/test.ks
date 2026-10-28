// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch into a plane block test (#464): from Kerbin's pad, launch waits for the pad to pass under Minmus's plane
// (inclination 6, LAN 78) through ADDONS:CLAUDIA:LAUNCHWINDOW, then flies the ascent; the orbit it ends in must
// have its LAN within 1 degree of Minmus's. The Script Result is launch's own.

@lazyglobal off.

wait until ship:unpacked.
local ascent is launch(lexicon("launch_at", "Minmus", "target_ap", 80000, "stage_limit", 2)).
print "launch: success " + ascent:success + ", ap_error " + ascent:ap_error + " m, lan_actual " + ascent:lan_actual + ", " + ascent:reason.
local result is ascent.
set result["test_lan_target"] to body("Minmus"):orbit:lan.
set result["test_lan_error"] to abs(ascent:lan_actual - body("Minmus"):orbit:lan).
addons:claudia:report(result).
