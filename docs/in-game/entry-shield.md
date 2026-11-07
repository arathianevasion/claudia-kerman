# Heat shields decide what an Entry drops (#823)

ADR 0018 block migration: the Entry count, the shield choice and the part-tree walk moved to Core (`ADDONS:CLAUDIA:ENTRYDROP`); firing the decouplers stays in `reentry`. Core and Kerboscript tests cover the rule. These checks need flight. Each `reentry` writes a `block call ENTRYDROP took ... ms: shield number <n>: ...` line to `KSP.log`, with the Entry Shield's part or `none, nothing drops`.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Block tests

1. Run `reentry_orbit`. It passes as before, with `parts_dropped` at least 1 and `shield_dropped` true. Its script no longer names a stage: with no Mission Plan, the first shield is the Entry Shield.
2. Run `reentry_duna_keep_stage`: `reentry_duna_powered`'s craft with `reentry`'s defaults. It has no Entry Shield, so `parts_dropped` is 0 and `shield_dropped` is false. It brakes to the ground on its engine. **Write down `max_temp_pct`, and the hottest part if the log names one.** It flies engine first, as a stage kept through Duna does.

## The Duna land-and-return (the flight that found this)

Fly the craft from m20261106-203509-34a6 (pod, one heat shield, the ascent stage with its radial tanks) with the Mission Goal "Fly to Duna and land near the equator in daylight. Take a crew report. Then return to Kerbin."

1. **The Duna entry keeps its stage.** The script may be `deorbit`, then `reentry`, with no `separate`. `KSP.log` reads `shield number 2: none, nothing drops`. No `separated` event appears in the Mission's `events.jsonl`, and stock delta-v doesn't fall across the entry.
2. **The shield stays on through Duna.** The Script Result has `shield_dropped: false`.
3. **Note the craft's attitude.** The craft holds surface retrograde engine first, with the shield not leading. Note whether it stays stable with the stack on. If it flips or tumbles, tell the ticket: the brief left this to flight.
4. **Peak heat.** Write down the Duna `reentry`'s `max_temp_pct` and the speed at atmosphere entry. This figure is what sets the Craft Check's hot-entry threshold: post it on #823.
5. **The ascent and the return fly.** Ascend from Duna and Return to Kerbin run on the kept stage.
6. **The Kerbin entry drops the stage and lands.** `KSP.log` reads `shield number 1: part <id>`. The stage goes before the atmosphere, through the decoupler under the shield. The shield jettisons after the worst heating, and the capsule lands under its chutes. `shield_dropped: true`.

## A revised plan

On the same flight, before the Duna entry, revise the Mission Plan so nothing follows the Duna landing. The next `reentry` reads `shield number 1`, and the stage drops. Abandon the Mission after this check.

## The Craft Check's Warning

In the editor, with a one-shield craft, run the Craft Check on each goal. It is the mod's finding, so no flight is needed:

1. "Land on Eve, ascend, and return to Kerbin": a Warning, `hot_entry_no_shield`, naming "Land on Eve" and saying it needs heat shield number 2. It can be acknowledged; Start isn't blocked by it.
2. "Land on Duna and return to Kerbin": no such finding.
3. Add a second shield further out and run the Eve goal again: no such finding.

## Options

Run `local res is reentry(lexicon("separate", 2)). addons:claudia:report(res).` from orbit: it fails at once with `separate must be 'auto' or 'none', not 2`, and nothing separates. `jettison_stage` given 1 fails the same way.
