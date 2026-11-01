// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// drop_parts block test (#279): the craft arrives in a 100 km circular Kerbin orbit with no stage fired
// and its first stage tagged "spent". drop_parts drops that stage now, full tank and all, and reports it.
// A name no part carries must drop nothing and say why. #608: naming both decouplers (every engine and
// all the fuel) on an orbit that misses the air is refused with nothing dropped; once the first stage
// is gone, the second goes with allow_no_engine and reports the delta-v it took.

@lazyglobal off.

wait until ship:unpacked.
local parts_before is ship:parts:length.
local missing is drop_parts(lexicon("parts", list("no_such_part"))).
local parts_after_missing is ship:parts:length.
local stranding is drop_parts(lexicon("parts", list("Decoupler.1"))).
local parts_after_stranding is ship:parts:length.
local result is drop_parts(lexicon("parts", list("spent"))).
local dropped_count is 0.
if result:haskey("dropped") {
  set dropped_count to result:dropped:length.
}
set result["test_missing_failed"] to not missing:success.
set result["test_missing_reason"] to missing:reason.
set result["test_missing_dropped_nothing"] to parts_after_missing = parts_before.
set result["test_strand_failed"] to not stranding:success.
set result["test_strand_reason"] to stranding:reason.
set result["test_strand_names_dv"] to stranding:reason:contains("m/s of delta-v").
set result["test_strand_dropped_nothing"] to parts_after_stranding = parts_before.
set result["test_strand_dropped_dv"] to stranding:dropped_dv.
set result["test_dropped_count"] to dropped_count.
// The report names every part that left: as many names as parts the craft lost.
set result["test_report_matches"] to dropped_count = parts_before - ship:parts:length.
set result["test_tagged_left"] to ship:partstagged("spent"):length.
set result["test_parts_left"] to ship:parts:length.
local allowed is drop_parts(lexicon("parts", list("Decoupler.1"), "allow_no_engine", true)).
set result["test_allowed_success"] to allowed:success.
set result["test_allowed_reason"] to allowed:reason.
set result["test_allowed_dropped_dv"] to allowed:dropped_dv.
print "drop_parts test: success " + result:success + ", dropped " + dropped_count + ", " + result:reason.
print "refused: " + stranding:reason.
addons:claudia:report(result).
