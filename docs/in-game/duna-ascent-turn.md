# launch in KSP: a flatter turn for a strong craft in thin air (#978)

In-game checks for #978 (ADR 0014 block migration). `launch`'s turn law (the pitch program and the thin-air rule) is now Core's `LaunchTurn`, asked every tick of the climb through `ADDONS:CLAUDIA:LAUNCHPITCH`; `plan_ascent`'s sim flies the same law. Steering, throttle and staging are still kOS's. The agent run that wrote this did not launch KSP; the tests and the gate are green in the repo.

What changed: under 500 Pa the nose used to be held within 3 degrees of the prograde. On Duna the air is under 500 Pa when the turn starts (60 m/s), so the turn's first kick never happened, and the m20261110-203402-45d1 ascent (thrust-to-weight 3.5) cut off 73 degrees above the horizon at 16 km. Now, low in the air (over a tenth of the surface pressure), a craft with thrust to spare may pitch below the prograde, down to where its thrust still holds its vertical speed. Up high the 3 degrees hold as before. `plan_ascent` now tries turn_shape 0.05 to 0.80, not 0.20 to 0.80, and a tie goes to the gentlest turn. On that flight's craft the sim's best is turn_shape 0.10: 1,176 m/s to a 60 km orbit, against 1,386 m/s for the same sim with the old law (the flight's own `plan_ascent` said 1,463, with a drag table the flight didn't record). The nose is about 16 degrees above the horizon at cutoff.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: Kerbin launches fly as before

1. Pick `launch_orbit` and press **Run** (up to 900 s). It passes (`PASS launch_orbit`, every `ok` line), as before #978.
2. Pick `launch_no_flip` and press **Run**. It passes, as before.
3. In `KSP.log`, `block call LAUNCHPITCH` lines come about every 2 s on the climb with the call's time (well under 1 ms). Above about 35 km, the `launch trace` lines with `phase thin_air` have `pitch` within about 3 degrees of `prograde`, as before.

## Check 2: a Duna ascent turns over early (the craft of m20261110-203402-45d1, or any lander with thrust-to-weight over 2 on Duna)

1. Load a save with the craft landed on Duna (in "Claudia Test 2", the Duna round trip's lander after touchdown, if a quicksave from then is kept).
2. Ask Claudia for `plan_ascent` to `target_ap` 60000. It answers a turn_shape under 0.20 (0.10 for that craft by the sim), with `dv_to_orbit` well under 1,463 m/s, and the `tried` list runs from 0.05 with more than 31 m/s between the best and the worst.
3. Fly `launch(lexicon("target_ap", 60000, "turn_shape", <that shape>))` then `circularize(lexicon("at", "ap"))` in one Mission Script.
4. In `KSP.log`, the `launch trace` lines just after the turn starts (`phase thin_air`, about 3.2 km) have `pitch` (degrees from vertical) well past `prograde`: the nose leads the path over. The last burning line before the first `phase coast` line (the cutoff) has `pitch` over 70 degrees from vertical, so the launch phase ends under 20 degrees above the horizon (the sim: about 16), and the coast line's `prograde` is over 65 from vertical, with `h_speed` several hundred m/s (the flight before #978: `pitch` 16.6 from vertical, `h_speed` 133).
5. The launch and the circularize succeed. Compare the delta-v used (`dv_used` on the `launch trace` `phase end` line, plus circularize's) with about 1,490 m/s for the flight before #978, and post both figures, with the trace lines from step 4, on #978.
6. If the craft falls back into the air or `launch` stops with `lost control`, copy the `launch trace` lines and the reason into #978.
