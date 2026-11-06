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
2. Watch: a long rails warp to the window (about day 231), an ejection burn of about 1,030 to 1,080 m/s, a warp out of Kerbin's sphere of influence and 5 % past its edge (#770), a correction of a few m/s at most on the Sun's patch, a warp to halfway (perhaps a second small correction). Since #747 the periapsis is not trimmed out there: if it is still more than 1 km off, the block warps into Duna's sphere of influence (`block call TRANSFERSTEP ... wait ... next near`), may burn a coarse correction just inside it, and then the trim a few hours before Duna's periapsis (`next fine_flown`), all with the craft round Duna.
3. `KSP.log` reads `PASS transfer_to_duna`; `correction_dv` is under 20 m/s; `target_pe_actual` 49 to 51 km; `body` is Duna when a trim was flown (Sun when the periapsis was already within 1 km).
4. The ejection's `ejection_dv_used` may run about 2 % over `ejection_node_dv` on a long burn (#747: a 230 s burn from 80 km lost 21 m/s of 1,094): note both and `ejection_burn_time`. Much more than 3 % on a burn under 4 minutes is worth a ticket.
5. In `KSP.log`, the `block call TRANSFERSTEP` line for the ejection reports its time; it should be well under a second (the window search's own budget is 250 ms on a quiet machine).

The coasts are also checked by the Encounter Watch (#770): see `encounter-watch.md`.

## Check 4: the Mission flights

1. Fly the Mun to Minmus as a Mission (a craft in Mun orbit, goal "Orbit Minmus"): the Mission Script calls `transfer_to` once, then `circularize` at pe. Since #734, `plan_transfer` can plan this transfer too (Minmus from the Mun, through the Transfer Windows), so Claudia may plan before she flies.
2. Fly the 2026-11-02 goal again: "Orbit Duna, use the optimal window" (Mission m20261102-161833-be82 ended in a Sun orbit with no encounter). Claudia should plan with `plan_transfer`, wait for the window as a Due Time, and fly `transfer_to` with `window` set to the window's departure UT; no hand-built ejection node, no `predict_burn` search for the burn time.

## Check 5: the Duna miss of #747, flown again

1. Fly the 2026-11-04 goal again: "Fly to Duna. Land anywhere" (Mission m20261104-181222-e076 lost its Duna encounter after a 0.13 m/s Course Correction and failed; a later transfer_to from the Sun's orbit ended 2.9 km against target_pe 60 km).
2. Watch `KSP.log` for `block call TRANSFERSTEP` lines: far out on the Sun's patch, corrections only keep the encounter; the trim to target_pe comes after `next near`, inside Duna's sphere of influence.
3. If a correction loses the encounter, the next TRANSFERSTEP line is a `burn correction ... next made` and the Script Result carries `recovered` (why the encounter was lost) instead of failing with "no encounter with Duna after the correction". Note whether it happened.
4. The Script Result's `target_pe_actual` is within `pe_tolerance` of `target_pe` and `body` is Duna; the capture (`circularize` at pe) follows.

## Report

Post on #459 (Checks 1 to 4) and #747 (Checks 3 and 5): pass or fail for each check, the `TRANSFERSTEP` log lines of the ejection, the `correction_dv` and `target_pe_actual` figures, and the Mission ids of Check 4.
