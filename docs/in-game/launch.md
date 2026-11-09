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

## Check 4: a low-TWR upper stage burns through (#932)

Fly `launch_low_twr` (`tests/blocks/launch_low_twr`), the block test for this, or the Duna ship of m20261108-220659-ac85. The craft file was written by hand and not loaded in KSP when this went in: if it doesn't load or lift off, say so on #932.

1. Pick `launch_low_twr` and press **Run** (up to 1500 s).
2. The engines stay on past `target_ap`: no coast. The weak stage takes over, and the `launch trace` lines in `KSP.log` read `phase burn_through`.
3. `KSP.log` has a `block call BURNTHROUGH` line with `burn_through True`, then one every 30 s with the slowest re-check's time, and a `BURNTHROUGH ended it` line if a re-check found the coast works again, its half burn, its turn (the angle at cutoff, not 190 degrees), the time left as `launch` ends at the atmosphere top, and the time to apoapsis less the climb that comes from.
4. The periapsis rises above 70 km without a coast (or the burn ends early on that second line and the craft coasts), `launch` ends with a positive `ap_error` (the overshoot), and `circularize` finishes with a small trim (by the Core flight model, the Duna ship ends at about 104 x 70 km).
5. It passes (`PASS launch_low_twr`, every `ok` line).
6. Fly `launch_orbit` too (Check 1): its `block call BURNTHROUGH` line reads `burn_through False` and it flies as before.
7. The descent guard (#936): in step 4's flight, `launch` never returns a `fell back into the atmosphere: periapsis ...` reason: a healthy burn-through doesn't trip it. The failure case needs a stage too weak to hold its climb, and isn't required to be flown. If you do fly one (a stage under about 0.18 TWR at cutoff by the Core model), `launch` returns with fuel left, the reason starting `fell back into the atmosphere: periapsis`, and the craft made safe.
