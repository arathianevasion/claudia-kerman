// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// transfer_to block test from flight 908b's Duna orbit (#867): the Mission Script the debug block test runner starts (bundled with
// the Core Library, as for a real Mission). The reference craft arrives at the apoapsis of a 100 x 2,144 km Duna orbit, which
// crosses Ike's sphere of influence, with Kerbin's window months away. transfer_to must fail naming Ike and burn nothing.

@lazyglobal off.

wait until ship:unpacked.
local result is transfer_to(lexicon("target", "Kerbin", "target_pe", 30000, "stage_limit", 2)).
set result:names_ike to result:reason:contains("Ike").
set result:burned_nothing to result:ejection_dv_used < 0.
print "transfer_to ike crossing test: success " + result:success + ", " + result:reason.
addons:claudia:report(result).
