# launch in KSP: the heading comes from Core (#475)

In-game checks for #475 (ADR 0014 block migration). `launch`'s default heading is now `LaunchWindow.HeadingInto` in Core (`ADDONS:CLAUDIA:LAUNCHHEADING`), the same heading `plan_ascent` reports; KSLib's LAZcalc is gone. Steering, throttle and staging are still kOS's. The agent run that wrote this did not launch KSP; the tests and the gate are green in the repo.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: Run launch_orbit

1. Pick `launch_orbit` and press **Run** (up to 900 s).
2. It passes (`PASS launch_orbit`, every `ok` line). `launch_ap_error` is within 2000 m and `launch_coast_ap` at least 79000 m, as before.

## Check 2: the flown heading matches plan_ascent's

1. At the launch pad at KSC, ask Claudia for `plan_ascent` to an 80 km orbit with `target_inc` 0 (the Mission's plan turn). Note the `heading` it reports (degrees).
2. Fly that launch: `launch(lexicon("target_ap", 80000, "target_inc", 0))` from the pad (a Mission whose plan is that launch, or `launch_orbit` with `target_inc` 0 set).
3. In `KSP.log`, the first `launch trace` line of the climb (the `pitch` phase, with `heading` in it) reads the same heading as step 1 to within 0.1 degree. Copy that line and the plan_ascent reply into #475.

## Check 3: a launch with target_inc 0 flies into the plane

1. Fly a Mission with `launch` to 80 km at `target_inc` 0 from KSC, then `circularize` at `ap`.
2. It succeeds: the orbit's inclination is within about 0.1 degree of 0 (KSC is at latitude -0.097, so the lowest reachable plane is near the equator).
3. If the orbit is off by more than about 1 degree, copy the `launch trace` heading lines and the `launch heading:` reason (if any) from `KSP.log` into #475.
