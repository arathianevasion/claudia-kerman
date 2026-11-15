# Part tests in launch and reentry (#1029)

Block migration: the test timing and the band are Core's (`PartTestWatch`, behind the block-internal `ADDONS:CLAUDIA:PARTTEST`); `launch` and `reentry` only stage or run the part's Run Test at its signal, from `claudia_tick` (ADR 0014 guardrail 2). No block logic moved, so there is no parity test. Core and Kerboscript tests cover the rule; these need flight. The playtest is #1057.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Block tests

1. Run `launch_part_test`. It passes as `launch_hop` does. `KSP.log` has one `PARTTEST launch: ran the test of liquidEngine2.v2 (run)` line, at an altitude between 3,000 and 6,000 m. With no Run Test offered for the Swivel (no PartTest Contract for it in the save), the line may read `(stage)` or be missing: **write down which**.
2. Run `reentry_chute_test`. It passes as `reentry_hop` does. The Mk16 opens at reentry's own timing, and only after that `KSP.log` has `PARTTEST reentry: ran the test of parachuteSingle (run)` under 5,000 m. **No stage fires for the test** (the staging list is unchanged).

## In career (playtest #1057)

3. Accept a PartTest Contract whose band a launch passes, and plan it from the Contract check card. The Contract completes on the ascent with no staging of yours.
4. Fly a Mission whose ascent passes the band too fast. The Debrief's Contract data reads `speed … m/s, above the band, through …` and Claudia proposes a retry leg or dropping the Contract.
