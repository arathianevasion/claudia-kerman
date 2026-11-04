# transfer_to to a planet and between moons in KSP (#459)

#459 moved transfer_to's flight into Core (ADR 0014): one block-internal call, `ADDONS:CLAUDIA:TRANSFERSTEP`, plans each step (the ejection, a wait out of the sphere of influence, each Course Correction) and checks the encounter; the Kerboscript only flies the nodes with exec_node, warps and reports. It now also flies to a body sharing the parent of the body the craft orbits: Duna from Kerbin, Minmus from the Mun. To a moon of the craft's body (the Mun from Kerbin) it flies as before.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look").

## Check 1: the moon transfer, unchanged

1. In the block test runner window pick `transfer_to` and press **Run**.
2. `KSP.log` reads `PASS transfer_to`, with the same checks as before #459 (Mun periapsis 29 to 31 km, ejection node under 900 m/s, ejection wait under 2,300 s, first warp request within 10 real seconds).
3. In `KSP.log`, one `block call TRANSFERSTEP took ... ms: burn ejection at UT ...` line, then one per later step (`next mid`, `next fine`, ...), each well under a second.

## Check 2: the Mun to Minmus

1. Pick `transfer_to_minmus_from_mun` and press **Run**. The craft starts in a 30 km circular Mun orbit.
2. Watch: an ejection burn of about 250 to 300 m/s, a warp out of the Mun's sphere of influence, then a small correction on Kerbin's patch (or none), and a warp towards Minmus.
3. `KSP.log` reads `PASS transfer_to_minmus_from_mun`. Note `target_pe_actual`, `correction_dv` and where the block ended (`body`: Kerbin, or Minmus if a finer correction was flown inside its sphere of influence).
4. Then, by hand in the same save: `circularize(lexicon("at", "pe"))` after `coast(lexicon("until", res:encounter_ut + 60))` should capture at Minmus.

## Check 3: Kerbin to Duna

1. Pick `transfer_to_duna` and press **Run**. The craft starts in a 100 km circular Kerbin orbit at UT 100 of a new game.
2. Watch: a long rails warp to the window (about day 231), an ejection burn of about 1,030 to 1,080 m/s, a warp out of Kerbin's sphere of influence, a correction of a few m/s at most on the Sun's patch, a warp to halfway (perhaps a second small correction), and a finer correction a few hours before Duna's periapsis if the periapsis is still more than 1 km off.
3. `KSP.log` reads `PASS transfer_to_duna`; `correction_dv` is under 20 m/s; `target_pe_actual` 49 to 51 km.
4. In `KSP.log`, the `block call TRANSFERSTEP` line for the ejection reports its time; it should be well under a second (the window search's own budget is 250 ms on a quiet machine).

## Check 4: the Mission flights

1. Fly the Mun to Minmus as a Mission (a craft in Mun orbit, goal "Orbit Minmus"): the Mission Script calls `transfer_to` once, then `circularize` at pe. Since #734, `plan_transfer` can plan this transfer too (Minmus from the Mun, through the Transfer Windows), so Claudia may plan before she flies.
2. Fly the 2026-11-02 goal again: "Orbit Duna, use the optimal window" (Mission m20261102-161833-be82 ended in a Sun orbit with no encounter). Claudia should plan with `plan_transfer`, wait for the window as a Due Time, and fly `transfer_to` with `window` set to the window's departure UT; no hand-built ejection node, no `predict_burn` search for the burn time.

## Report

Post on #459: pass or fail for each check, the `TRANSFERSTEP` log lines of the ejection, the `correction_dv` and `target_pe_actual` figures, and the Mission ids of Check 4.
