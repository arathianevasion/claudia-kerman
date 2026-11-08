# Landing predictions fly the craft reentry flies (#814)

In-game checks for #814. `return` and `deorbit` now ask Core once at block start, through `ADDONS:CLAUDIA:ENTRYCRAFT`, for the craft the Entry ahead will fly (`EntryCraft`: from `EntryStack` and `EntryFinish`, as `reentry` reads them). Each landing prediction carries that answer as `craft`: `return`'s `RETURNBURN`, its aimed `CORRECTIONBURN` and both `REENTRYIMPACT` checks, and `deorbit`'s `DEORBITBURN` and its `REENTRYIMPACT` after the burn. Who pays a burn before the drop is `DropGuard`'s rule (#854): `deorbit` sends its engines' isp even with `separate` set. `reentry`'s `ENTRYFINISH` names both crafts in its `why` when the craft it flies isn't the one aimed for. The kick of the drop itself is not predicted (#815). The agent run that wrote this did not launch KSP.

These are read from the logs of the player's planned flights (the brief asks for no dedicated flight). Install as in `phase-3-gate.md`. Results are in `KSP.log` and the Mission's `plans.jsonl`.

## Check 1: a Mun return to KSC that drops its stack (flown with #815)

A Mission that lands on the Mun and returns to Kerbin with a target (`return` with `target` "ksc" or auto), then `reentry`.

1. In `KSP.log`, the `block call ENTRYCRAFT took ... ms:` line at the start of `return`: it reads `without its stack (<the stack's part names>), shield jettisoned, <t> t`, the capsule's mass, not the whole craft's. Record the ms.
2. The `block call RETURNBURN` line: record `aimed <x> km from the target` and `spread <y> km`. The spread should be near a capsule's figure from that orbit (about 80 to 130 km from a low Mun orbit), not the 1,632 km of m20261106-132839-970f.
3. At the end of `return`, its result's `predicted_error_km` and `predicted_geo`.
4. In `reentry`: the `phase finish` blocknote's `why` has no "the landing was aimed for" (the craft is the one aimed for). The `phase predicted_landing` note's `predicted_geo`, and the `phase landed` note's `actual_geo` and `prediction_miss_km`.
5. Pass: `return`'s `predicted_geo` is within a few km of `reentry`'s `predicted_geo` (the drop's kick is #815's), and both within a few km of `actual_geo`.

## Check 2: a Duna landing that keeps its stack

A Mission whose Duna Entry lands on its engines (`reentry`'s `phase finish` note reads `engines, keeping everything`), flown with `deorbit` then `reentry`.

1. The `block call ENTRYCRAFT` line at the start of `deorbit`: `as it is, its stack kept, on its engines, shield kept, <t> t` (or, with `separate` set, the craft without what it names).
2. The `block call DEORBITBURN` line: `landing <lat>, <lng>`, `<x> km from the target`, and its `[plan-vs-flown]` periapsis. The `block call REENTRYIMPACT` line after the burn: its landing, km and periapsis.
3. Record the slope: km between the two landings for the metres between the two periapses. In the Core model a kept stack at Duna moves about 1.2 km per 100 m of periapsis at the 15 km `deorbit` aims for (the capsule about 0.9), so 169 m should move it about 2 km, not the 145 km of m20261106-203509-34a6. A much steeper reading means the real craft flies something the model leaves out (see the ticket's notes): bring the figures to the player.
4. Pass: `deorbit`'s `predicted_error_km` within `accuracy_km`; `reentry`'s `phase finish` `why` has no "the landing was aimed for"; `prediction_miss_km` at touchdown within a few km of `deorbit`'s prediction.

## Check 3: a craft that changes between aim and entry

When it happens on a planned flight (a plan revised between `return` and `reentry`, or fuel enough at `return` but not at `reentry` to land on the engines), `reentry`'s `phase finish` blocknote and the `block call ENTRYFINISH` line end with `; the landing was aimed for the craft <aimed>, it flies <flown>`. The record is in the session only: after a quickload between the two blocks there is nothing to compare, and no line. It is kept for the vessel that aimed, and dropped when `return` or `deorbit` starts again or ends without success, so another vessel's `reentry`, or one after an aiming block gave up, prints no such line.

Known limit (check 1): `return` reads `ENTRYCRAFT` once, before the ejection, so its correction's who-pays figures (`whole`, `gone_prop`) are the craft's before the ejection burn, paired with the kept mass now. The landing it aims is the kept craft's either way; only the correction's mass-after-burn estimate with a kept burner leans on them.

## Check 4: who pays, read from the real stack (#870)

The adapter's per-part resources and engine propellants have no unit test. On a staged `return` (a Mun return with a dropped stage), compare the `block call ENTRYCRAFT` line's `gone_prop` with the propellant actually in the dropped tanks (LiquidFuel and Oxidizer in t, not monopropellant or ore). With a flamed-out stage and the next one unstaged it should still be that stage's propellant, not 0. A RAPIER in air-breathing mode must not count Oxidizer.
