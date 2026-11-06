# A drop the Mission Plan still needs is refused

In-game checks for #825 (ADR 0014 block migration: the comparison is Core's `DropGuard`, behind `ADDONS:CLAUDIA:DROPCHECK`; the delta-v estimate and the firing stay in Kerboscript, in `claudia_drop_refusal` and `claudia_fire_drop`). The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: tests/blocks/drop_parts_plan refuses with the figures

Run `tests/blocks/drop_parts_plan`. Its `test.cfg` gives a one-sequence plan (a 99,999 m/s burn still to fly) in place of a Mission, and `test.ks` calls `drop_parts` on the fuelled first stage, then again with `allow_no_engine` true.

1. The run passes. Both calls return `success: false`, with a reason like `this drop would take <n> m/s and leave <m>; the plan still needs 99999 m/s: revise the Mission Plan first` (the `blocknote` line `drop` in `KSP.log` says the same).
2. The craft is intact: no part left, and the `spent` tag is still on the tank. `allow_no_engine` did not pass it.

## Check 2: drop_parts is unchanged without a plan

Run `tests/blocks/drop_parts`. It passes as before (the guard has no Mission Plan to check, so nothing is refused for it).

## Check 3: a Mission (optional, with an API key)

Start a Mission whose plan has a burn after the sequence that drops a fuelled stage. The drop is refused with the figures in the reason: `drop_parts` and `deorbit`'s `separate` fail before firing or burning, and `reentry` flies on with the stack kept and a `drop` note in `KSP.log`. A revised plan with that burn removed lets the same drop go.
