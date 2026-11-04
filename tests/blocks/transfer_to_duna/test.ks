// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// transfer_to block test to Duna (#459): the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The reference craft arrives in a 100 km circular Kerbin orbit with no stage fired; the
// ejection burn stages into its engine (stage_limit 2 keeps the decoupler and chute). transfer_to must take the next Duna
// window and reach Duna's sphere of influence with a 50 km periapsis.

@lazyglobal off.

wait until ship:unpacked.
local result is transfer_to(lexicon("target", "Duna", "target_pe", 50000, "stage_limit", 2)).
print "transfer_to duna test: success " + result:success + ", Duna pe " + result:target_pe_actual + " m, correction " + result:correction_dv + " m/s, " + result:reason.
print "transfer_to duna ejection: node " + result:ejection_node_dv + " m/s at UT " + result:departure_ut + ", burned " + result:ejection_dv_used + " m/s; v_inf " + result:v_inf + " m/s, arrival UT " + result:arrival_ut.
addons:claudia:report(result).
