// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, a Duna entry with its batteries nearly empty (#942): the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). The craft starts at the apoapsis of an 80 km by 15 km Duna orbit at 1.3% charge: reentry
// doesn't end on min_ec_pct with the periapsis in the air, so it releases the steering, arms the stowed chute and comes down.

@lazyglobal off.

wait until ship:unpacked.
local result is reentry().
// The checker compares a field with a literal only, so the test reports the comparisons it needs.
set result["aborted_on_charge"] to result:reason:contains("min_ec_pct").
set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
if not result:haskey("chutes_used") {
  set result["chutes_used"] to list().
}
set result["chutes_used_count"] to result:chutes_used:length.
print "reentry low charge test: success " + result:success + ", chutes used " + result:chutes_used:join(", ") + ", " + result:reason.
addons:claudia:report(result).
