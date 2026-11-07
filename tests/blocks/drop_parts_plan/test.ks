// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// drop_parts_plan block test (#825): the craft arrives in a 100 km circular Kerbin orbit with no stage fired and its first
// stage tagged "spent", and the plan in test.cfg (a 99,999 m/s burn still to fly) stands in for a Mission Plan. drop_parts of
// that fuelled stage takes delta-v the plan needs, so it must fail before firing, naming the figures, whether or not
// allow_no_engine is true, and leave every part aboard.

@lazyglobal off.

wait until ship:unpacked.
local parts_before is ship:parts:length.
local refused is drop_parts(lexicon("parts", list("spent"))).
local parts_after_refusal is ship:parts:length.
local override is drop_parts(lexicon("parts", list("spent"), "allow_no_engine", true)).
set refused["test_refused"] to not refused:success.
set refused["test_reason"] to refused:reason.
set refused["test_reason_names_plan"] to refused:reason:contains("the plan still needs 99999 m/s").
set refused["test_refused_nothing_dropped"] to parts_after_refusal = parts_before.
set refused["test_override_refused"] to not override:success and override:reason:contains("the plan still needs").
set refused["test_override_nothing_dropped"] to ship:parts:length = parts_before.
set refused["test_tagged_left"] to ship:partstagged("spent"):length.
print "drop_parts_plan test: " + refused:reason.
addons:claudia:report(refused).
