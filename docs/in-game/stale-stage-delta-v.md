# A stale stock stage reading doesn't overstate the delta-v left (#980)

Stock's per-stage delta-v can lag a burn: after a Duna ascent with crossfed radial tanks its stage still started heavier than the whole craft and read about 684 m/s too many until a later warp. The mod now treats a stage that starts heavier than the craft as stale and works its figure out again from the craft's propellant and the stage's own Isp (`StaleStageReading`), before a plan, a margin or a tolerance gate reads it. The pending Sequence Plan's tolerance gate ignores a delta-v change at the same mass. Core tests cover the rules (`StaleStageReadingTests`, `PlanDriftTests`); these checks need the game. Install and where to look are as in `phase-3-gate.md`.

## Check 1: a Duna ascent reports its real cost

1. Land at Duna with a craft whose ascent stage draws from crossfed radial tanks that drop once empty (the "Duna Ship 1" of m20261110-203402-45d1 is one: about 17.7 t landed, 3,928 m/s left).
2. Approve the ascent's Sequence Plan and let it fly to orbit.
3. The Debrief's "Delta-v used by the whole run" line says about 1,450 to 1,500 m/s, not about 800. The "now" figure in that line is about the same as stock's delta-v app shows after you open and close it, or after a short warp.
4. In the Debrief request's Game Snapshot (the Mission folder's request log), no stage has a `start_mass_t` above `craft.mass_t`.

## Check 2: no Re-plan after a warp

1. After Check 1, let Claudia propose the next Sequence Plan (the transfer home) with a Due Time some hours away.
2. Press Warp to Due Time and don't approve before the warp ends.
3. At the warp's end the plan's button still reads Approve, not Re-plan, and nothing says "delta-v changed from". Approve once; no second approval is asked for.

## Check 3: a real loss still forces a Re-plan

1. With a Sequence Plan pending in orbit, burn normal (so the orbit barely moves) until over the player's Re-plan tolerance (15% by default) of the delta-v left is used.
2. The button turns to Re-plan, and the reason reads "delta-v changed from ... to ... m/s".

Tell the ticket the figures from Check 1 step 3.
