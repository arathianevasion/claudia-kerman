# RCS burst turns in KSP (#692)

#692 moved RCS Use's big turns to a burst turn: one RCS burst spins the craft up, it coasts with RCS off, one burst stops it a few degrees short, and the reaction wheels settle the rest. The plan (TURNPLAN) and each tick of the turn (BURSTTURN) are in Core; kOS switches RCS and steers, with cooked steering tuned per phase and RCS on only in the two bursts. #408 is folded in: the "turning" test now compares the wheels' time for the error now, not a 190° turn, with the time left.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look").

## Check 1: the block test

1. In the block test runner window pick `exec_node_rcs_turn` and press **Run**. It spawns dock's craft (one Small Inline Reaction Wheel, four RV-105 blocks, 20 units of monopropellant) in a 100 km Kerbin orbit, and exec_node flies a 10 m/s node 120 s ahead pointing straight behind the craft.
2. `KSP.log` reads `PASS exec_node_rcs_turn`; `test_turn_deg` is over 150 and `test_monoprop_used` under 1.
3. Watch the turn: the RCS plumes fire in one short burst, stop while the craft coasts round, fire again in one short burst as it nears the node, then stay dark while the wheel settles. They never fire through the coast.
4. In `KSP.log`: a `block call TURNPLAN took ...` line with `rcs ..., coast ... deg/s, bursts ... s`; `block call BURSTTURN ...` lines reading `spin, RCS on`, `coast, RCS off`, `stop, RCS on`, `settle, RCS off` in that order; the RCS Use trace (`phase rcs`) shows `rcs False` through the coast; and the mission line `RCS off: turned (coast ...°/s planned, ...°/s flown, bursts ... s up, ... s down, monoprop ... used, ...° left after the stop, 0 corrections, orbital velocity changed ... m/s)`.
5. Note the measured monopropellant in the report so the bound in `tests/blocks/exec_node_rcs_turn/test.cfg` can be tightened.

## Check 2: the Mun validation flight

Fly the Mun validation flight on Mun Shot v2. At the first large RCS-assisted turn (a turn of about 140° before a burn):

1. Two short RCS bursts with a coast between them, not continuous firing.
2. The turn's `RCS off: turned (...)` line in the mission log shows at most about 1.5 units of monopropellant used, and its orbital velocity change is small.
3. The apoapsis moves under 100 m across the turn with no engine firing (note it in the map view before and after).
4. A one- or two-degree error close to a Burn Start stays on the wheels: no `RCS on: turning` line with an error under 10°.

## Report

Post on #692: pass or fail for each check, the `turned` line(s), and the monopropellant figures.
