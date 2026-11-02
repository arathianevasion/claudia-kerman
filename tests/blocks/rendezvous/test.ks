// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// rendezvous block test: the Mission Script the debug block test runner starts (bundled with the Core Library, as for
// a real Mission). The reference craft arrives in a 100 km circular equatorial Kerbin orbit with "Block Test Target"
// 200 km behind it, slightly off plane and higher; rendezvous must end with the target loaded and under 2 m/s.

@lazyglobal off.

wait until ship:unpacked.
local result is rendezvous(lexicon()).
// The steps flown, as a count a check can compare (all five: match_plane, phasing, transfer, correction, match_velocities).
set result:test_steps to result:burns:length.
print "rendezvous test: success " + result:success + ", " + result:distance_actual + " m at " + result:relative_speed_actual
  + " m/s, burns " + result:burns:join(", ") + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
