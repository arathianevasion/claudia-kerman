// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// dock block test: the Mission Script the debug block test runner starts (bundled with the Core Library, as for a real
// Mission). The reference craft arrives in a 100 km circular equatorial Kerbin orbit with "Block Test Target", a copy of it,
// 150 m ahead; dock must dock their Clamp-O-Tron Jr ports.

@lazyglobal off.

wait until ship:unpacked.
// The target spawns with the craft; give it a moment to load and unpack inside physics range.
wait 5.
local result is dock(lexicon()).
// Both Clamp-O-Tron Jr ports: the craft's by auto, the target's by size.
set result:test_ports to result:port = "size0" and result:target_port = "size0".
print "dock test: success " + result:success + ", docked " + result:docked + ", port " + result:port + " to " + result:target_port
  + ", monoprop " + result:monoprop_used + ", " + result:duration + " s, " + result:reason.
addons:claudia:report(result).
