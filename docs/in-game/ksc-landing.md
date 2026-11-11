# Returns land near KSC by default

In-game checks for #790 (ADR 0017; ADR 0014 block migration: no flight logic moved out of Kerboscript, the default target, the fix across the track and the arrival's choice are new logic in Core; `return.ks` and `deorbit.ks` only ask `ADDONS:CLAUDIA:LANDINGTARGET` and pass on `aim_note`). The agent run that wrote this did not launch KSP. Block scenarios to fly as before: `tests/blocks/return_target`, `return` and `tune_pe_kerbin` (both now with `target` "none", as they fly no landing), `deorbit_kerbin` and `deorbit_kerbin_defaults`.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). The line is in the Debrief card's events, and in `KSP.log`.

## Check 1: a Mun return lands near KSC

Fly a Mission whose goal names no place, such as "Orbit the Mun and come home", on a craft with chutes.

1. The Sequence Plan's `return` needs no `target`, and the plan is not sent back for its duration: the mod raises `expected_duration_s` to about 7 h itself.
2. `KSP.log` has `auto target: ksc`. `return`'s burn waits for a window, then `RETURNBURN` logs an aim within a few km of the target.
3. After the correction, `predicted_error_km` is under 15.
4. The Debrief after the `reentry` has one event line: `landing: predicted <x> km from KSC, came down <y> km from KSC`. `<y>` is under about 50.

## Check 2: a goal that names a place

Fly "Orbit the Mun and splash down in the ocean". `KSP.log` has `auto target: none`, `return` leaves at its next window, and no landing line appears.

## Check 3: Minmus, with spare delta-v

From a Minmus orbit, away from Minmus's node, with over 100 m/s more than the ejection needs, run `local res is return(). addons:claudia:report(res).` then `reentry()`.

1. `RETURNBURN` aims 20 to 60 km off: the ejection takes the moon's latitude with it, and it does not wait for the node.
2. `CORRECTIONBURN` burns about 15 to 40 m/s, and `aim_note` in the report reads `fix flown: <dv> m/s, <x> km down to <y> km`.
3. The landing line carries the same words (a window note may follow them), and `<y>` from KSC is a few km plus the burn's own error.
4. There is no visible hitch beyond a moment at the correction (at most 60 predictions, about 630 ms in the game).

## Check 4: Minmus, without spare delta-v

As Check 3, with a craft that has only the ejection and under about 60 m/s more. `aim_note` reads `miss accepted: <x> km from the target; the fix costs <dv> m/s, over the <m> m/s it may spend above the reserve`, the correction burns only along the track, and the landing line carries the same words (a window note may follow them).

## Check 5 (later): a Duna return

From low Duna orbit with plenty of delta-v, ask for `plan_transfer` to Kerbin; it needs no `target_pe`. Its `capture` is true and `arrival_note` reads `arrival: <need> of <spare> m/s: target_pe 80000, capture(), deorbit(), reentry()`. Fly that; `deorbit` takes `ksc` by default, and the Debrief has the landing line. With too little delta-v for that plus the reserve, `capture` is false and the note reads `arrival: entry at 21 km, no capture burn`.
