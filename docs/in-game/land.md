# land in KSP: the path guard and the vertical fallback (#712)

In-game checks for #712 (ADR 0014 block migration): BRAKEPATH's path now has the point where braking stops, and `land`'s path guard, with its vertical fallback, runs in Core (`ADDONS:CLAUDIA:LANDGUARD`, each braking tick). Steering, throttle and staging are still kOS's. The agent run that wrote this did not launch KSP; the tests and the gate are green in the repo.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKermin/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: Run land_biome_minmus

1. Pick `land_biome_minmus` and press **Run** (up to 2400 s).
2. It passes (`PASS land_biome_minmus`, every `ok` line).
3. In `KSP.log`, the `land trace` lines from `phase repredict` to `phase touchdown` have no `phase guard` line. A `phase vertical` line (with `why braking is above its predicted path: ...`) followed by `phase terminal` lines and `phase touchdown` also passes; then the report has `vertical_fallback: True`.
4. About once a second while braking, `block call LANDGUARD took ... ms (the slowest of <n> calls ...)`: the slowest should be well under 1 ms.

## Check 2: Run land

1. Pick `land` and press **Run**. It passes, with no `phase guard` line in `KSP.log` and `vertical_fallback: False` in the report (a Mun landing should fly its shape).

## Check 3: a real Minmus Flats landing

1. Fly a Mission to land on the Flats on Minmus (#712's was "Land on the flats on Minmus and return to kerbin", Mun Shot v2, from a 30 km orbit).
2. `land` lands: `success: True`, `situation: "LANDED"`, `biome: "Flats"`. The trace has either no `phase guard` line, or a `phase vertical` line and then `phase touchdown`.
3. If it aborts with `braking is above its predicted path`, copy the `phase guard` line (its `no_vertical` field says why it couldn't come straight down) and the `land trace` lines from `phase repredict` into the issue.
