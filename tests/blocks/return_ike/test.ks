// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// return block test from Ike: the Mission Script the debug block test runner starts (bundled with the
// Core Library, as for a real Mission). The reference craft arrives in a 30 km circular Ike orbit with no
// stage fired; the ejection burn stages into its engine (stage_limit 2 keeps the decoupler and chute).
// return must put it on a path home with a Duna periapsis near 35 km.

@lazyglobal off.

wait until ship:unpacked.
// Called as a value: a bare return(). would be kOS's return statement.
local result is return(lexicon("stage_limit", 2)).
print "return ike test: success " + result:success + ", Duna pe " + result:planet_pe + " m, correction " + result:correction_dv + " m/s, reentry at " + result:reentry_ut + ", " + result:reason.
addons:claudia:report(result).
