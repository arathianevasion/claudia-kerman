# deorbit: the cheapest burn within accuracy, and max_dv

In-game checks for #794 (ADR 0014 block migration: `deorbit_short_of_dv` moved to Core's `DeorbitBurn.ShortOfDv`; the choice of burn and the `max_dv` budget are Core's, `deorbit.ks` only passes `max_dv` and stock's `ship:deltav:current` to `ADDONS:CLAUDIA:DEORBITBURN`). The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: deorbit_kerbin is unchanged

Run `tests/blocks/deorbit_kerbin`. It passes as before (`success`, `dv_used` 40 to 150, `miss_km` under 25, `target_km` under 50). The craft has delta-v to spare, so the auto `max_dv` doesn't bind. If it fails with `over max_dv`, note the figures in the reason and tell the ticket.

## Check 2: deorbit_high_orbit

Run `tests/blocks/deorbit_high_orbit` (a 2,000 km by 100 km equatorial orbit, the craft starting at the apoapsis).

1. Either `success: true` with `dv_used` as the `DEORBITBURN` log line's burn, or `success: false` whose reason reads `the cheapest burn within accuracy_km needs <dv> m/s (at UT <ut>), over max_dv <m> (auto: <left> m/s left, less the reserve and a powered landing)`. Both are right; a burn flown that costs more than the `max_dv` in that reason is not.
2. If it failed, the craft has not burned (`burn_ut` is -1) and no node is left planned.
3. If it flew, `predicted_error_km` is under 5 and the capsule lands near KSC as in Check 1. On this orbit a burn that saves delta-v may be aimed up to 4 km off the target (80% of `accuracy_km`), so a predicted error near 4 km is the search spending the band, not a miss.
4. There is no visible hitch beyond a moment at the block's start (the search is about 30 ms in a Release build; on Unity's runtime expect a few times that).

## Check 3: max_dv by hand

From the same orbit run `local res is deorbit(lexicon("target", "ksc", "max_dv", 20)). addons:claudia:report(res).` It fails before any burn: `the cheapest burn within accuracy_km needs <dv> m/s (at UT <ut>), over max_dv 20`. Then run it with `"max_dv", 5000` and a craft with fewer m/s than the burn: it fails with `not enough delta-v: the burn needs <dv> m/s, <left> m/s left`.
