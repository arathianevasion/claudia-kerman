// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, a Duna entry whose Chute Group is too weak and no engine (#827): the Mission Script the debug block test
// runner starts (bundled with the Core Library, as for a real Mission). The craft arrives at the apoapsis of an 80 km by 15 km
// Duna orbit with a Mk16 in stage 2 and another in stage 0 and nothing to burn: reentry coasts to the atmosphere, finds stage 2's
// chute can't land it, and opens stage 0's too. The Script Result is the block's plus how many chutes were used and left.

@lazyglobal off.

wait until ship:unpacked.
local result is reentry().
// The checker compares a field with a literal only, so the test reports the comparisons it needs.
set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
set result["chutes_used_count"] to result:chutes_used:length.
set result["chutes_kept_count"] to result:chutes_kept:length.
print "reentry weak group test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, chutes used " + result:chutes_used:join(", ") + ", " + result:reason.
addons:claudia:report(result).
