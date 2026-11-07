# Heat shields decide what an Entry drops (#823), and staging which chutes open (#826)

ADR 0018 block migration: the Entry count, the shield choice and the part-tree walk moved to Core (`ADDONS:CLAUDIA:ENTRYDROP`); firing the decouplers stays in `reentry`. Core and Kerboscript tests cover the rule. These checks need flight. Each `reentry` writes a `block call ENTRYDROP took ... ms: shield number <n>: ...` line to `KSP.log`, with the Entry Shield's part or `none, nothing drops`.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Block tests

1. Run `reentry_orbit`. It passes as before, with `parts_dropped` at least 1 and `shield_dropped` true. Its script no longer names a stage: with no Mission Plan, the first shield is the Entry Shield.
2. Run `reentry_duna_keep_stage`: `reentry_duna_powered`'s craft with `reentry`'s defaults. It has no Entry Shield, so `parts_dropped` is 0 and `shield_dropped` is false. It brakes to the ground on its engine. **Write down `max_temp_pct`, and the hottest part if the log names one.** It flies engine first, as a stage kept through Duna does. Its test.cfg's plan stands in for a Mission's, so `KSP.log` reads `shield number 2: none, nothing drops`. The craft has no shield at all, so it can't show a shield being kept: the Mission below does.
3. Run `reentry_hop`, `reentry_tumble`, `deorbit_kerbin_defaults` and `return_target`: each calls `reentry` with its defaults and no Mission Plan, so the first shield is the Entry Shield, as before. Each passes as before.
4. Run `deorbit_kerbin`, `deorbit_duna`, `deorbit_eve` and `deorbit_laythe`: `deorbit` now reads the Entry Shield for its prediction. `predicted_error_km` is as before.

## The Duna land-and-return (the flight that found this)

Fly the craft from m20261106-203509-34a6 (pod, one heat shield, the ascent stage with its radial tanks) with the Mission Goal "Fly to Duna and land near the equator in daylight. Take a crew report. Then return to Kerbin."

1. **The Duna entry keeps its stage.** The script may be `deorbit`, then `reentry`, with no `separate`. `KSP.log` reads `shield number 2: none, nothing drops`. No `separated` event appears in the Mission's `events.jsonl`, and stock delta-v doesn't fall across the entry.
2. **The shield stays on through Duna.** The Script Result has `shield_dropped: false`.
3. **Note the craft's attitude.** The craft holds surface retrograde engine first, with the shield not leading. Note whether it stays stable with the stack on. If it flips or tumbles, tell the ticket: the brief left this to flight.
4. **Peak heat.** Write down the Duna `reentry`'s `max_temp_pct` and the speed at atmosphere entry. This figure is what sets the Craft Check's hot-entry threshold: post it on #823.
5. **The ascent and the return fly.** Ascend from Duna and Return to Kerbin run on the kept stage.
6. **The Kerbin entry drops the stage and lands.** `KSP.log` reads `shield number 1: part <id>`. The stage goes before the atmosphere, through the decoupler under the shield. The shield jettisons after the worst heating, and the capsule lands under its chutes. `shield_dropped: true`.

## Chute Groups on the same flight (#826, ADR 0019)

The group choice moved to Core (`ADDONS:CLAUDIA:CHUTEGROUP`); opening and cutting stay in `reentry`. Behaviour changes on purpose, so no parity test covers this. Each `reentry` writes a `block call CHUTEGROUP took ... ms: stage <n>: <titles>` line to `KSP.log`, or `Last Entry, every chute aboard: <titles>`. After a landing its `chutes_cut` block note lists what it cut.

On the craft above, whose chutes are stage 2 (four, on the radial tanks) and stage 0 (the pod's):

1. **Stage 2 opens at Duna, without the stage firing.** `KSP.log` reads `stage 2: ...` with the four titles. The Script Result's `chutes_used` has those four, and `chutes_kept` still has the pod's. The stage list in the UI doesn't move, and the rocket stays on its stage.
2. **Drogues first.** If the craft has drogues in the group, they open before its mains.
3. **Nothing drags on the ascent.** After the Duna landing, read the `chutes_cut` note. Titles there are chutes `reentry` cut because they were still open. Then fly the Ascend: no chute is open, and the climb isn't dragged. Say on the ticket which of these you saw at touchdown, cut by the block or already gone.
4. **Stage 0 opens at Kerbin.** `KSP.log` reads `Last Entry, every chute aboard: ...`. The capsule lands under the pod's chute, and no `chutes_cut` titles appear for it (empty on a Last Entry).

**Does stock already cut chutes on landing?** The Mk16's config has `autoCutSpeed = 0.5`, which suggests it does once settled, but no flight has confirmed it. To check, run `reentry_orbit`: with no Mission Plan it is a Last Entry, so the block cuts nothing. Watch the chute for a minute after touchdown. If it cuts itself, stock does it and the block's cut only comes first. If it stays open, the block's cut is the only one. **Write down which applies, on #826.**

5. **A repack at Duna counts again.** After the Duna landing, have an Engineer repack one stage-2 chute on EVA. The next Debrief's `Chutes:` line counts it among those stowed. **A restage counts too:** before the Duna entry on a second try, move one stage-2 chute to stage 1 and read `stage 2:` with three titles.

### Fresh reads (both need KSP to report them)

Do these on a throwaway craft in a Mission whose plan has burns after the Entry, so it isn't the Last Entry: a pod with a stage-0 Mk16 and two more Mk16s in stage 2, with the Mission Goal "Land on Kerbin, ascend and land on Kerbin".

1. **A chute restaged in flight joins its new group.** Before the entry, drag one of the stage-2 chutes to stage 1 in the staging list. Print `ship:partsdubbed("parachuteSingle")` with each part's `:stage` in the kOS terminal: the moved part must read stage 1. Run `reentry`. `KSP.log` reads `stage 2:` with one chute, and only that one opens. If `:stage` doesn't follow the restage, say so on #826: the rule rests on it.
2. **A chute repacked on EVA is used again.** After a landing with stage 2 cut, send an Engineer on EVA, and repack one of those chutes. The next `reentry`, on an Entry that isn't the last, reads `stage 2:` naming it, and it opens. If it doesn't, say so on #826: the rule rests on KSP reporting it as stowed.

### The Craft Check's findings

In the editor, with the goal "Land on Duna and return to Kerbin". All are the mod's own, so no flight is needed:

1. Chutes in stage 2 and stage 0, and an engine in stage 3: no chute finding.
2. Only a stage-0 chute: a Blocker, `chutes_short_for_landing`, saying the Kerbin entry has no Chute Group. Add "an engineer repacks the chutes" to the goal: a Warning, saying it relies on a repack at Duna.
3. Stage-2 chutes on radial tanks dropped in stages 3 and 4, as on the flight's craft: a Warning, `chute_group_leaves_early`, saying stage 0 would open at Duna, leaving Kerbin none.
4. Stage-2 chutes with the next engine in stage 1: a Blocker, `chute_group_above_engine`. Light that engine by hand in flight and run a recheck: the finding goes.

## A revised plan

On the same flight, before the Duna entry, revise the Mission Plan so nothing follows the Duna landing. The next `reentry` reads `shield number 1`, and the stage drops. Abandon the Mission after this check.

## The Craft Check's Warning

In the editor, with a one-shield craft, run the Craft Check on each goal. It is the mod's finding, so no flight is needed:

1. "Land on Eve, ascend, and return to Kerbin": a Warning, `hot_entry_no_shield`, naming "Land on Eve" and saying it needs heat shield number 2. It can be acknowledged; Start isn't blocked by it.
2. "Land on Duna and return to Kerbin": no such finding.
3. Add a second shield further out and run the Eve goal again: no such finding.

## Options

Run `local res is reentry(lexicon("chutes", "all")). addons:claudia:report(res).` from orbit: it fails at once with `chutes must be 'auto' or 'none', not all`, and nothing opens. A list of tags fails the same way.

Run `local res is reentry(lexicon("separate", 2)). addons:claudia:report(res).` from orbit: it fails at once with `separate must be 'auto' or 'none', not 2`, and nothing separates. `jettison_stage` given 1 fails the same way.
