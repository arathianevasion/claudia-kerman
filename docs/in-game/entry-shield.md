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

## Landing on its engines (#824, ADR 0020)

The decision moved to Core (`ADDONS:CLAUDIA:ENTRYFINISH`); braking, throttle and lighting the engines stay in `reentry`. `keep_engines` and its walk are deleted on purpose, so no parity test covers this. Each `reentry` writes a `block call ENTRYFINISH took ... ms: ...` line to `KSP.log`, and the same words to a `finish` block note: `engines, keeping everything: <speed> with its drop and chutes, <speed> as kept, on <engine>`, or `chutes, <speed>`.

1. **Run `reentry_duna_powered`** (`chutes: "none"`, no `touchdown`). `KSP.log` reads `engines, keeping everything`. It passes with `touchdown_speed` under 6 and `powered_dv` over 20, and its chute stays stowed.
2. **The Duna flight's Duna landing, on the craft above.** The script has no `touchdown`. `KSP.log` reads `engines, keeping everything: ...` naming the engine, with a speed near 24 m/s. No `separated` event appears, and the radial tanks are still attached after landing. It lands on the engines under stage 2's chutes at **under 6 m/s**. The Script Result has `powered_dv` over 0 and `chutes_used` with stage 2's four. Stock's delta-v before and after differs only by what the burn used.
3. **The same craft as the Last Entry.** On a second try, revise the plan so nothing follows the Duna landing. If the capsule alone predicts at or under 10 m/s, `KSP.log` reads `chutes, ...` and the stack drops. If over, it reads `engines, keeping everything` with a second, kept, speed, and nothing drops.
4. **An unlit engine is lit with no stage fired.** Before the Duna entry, in a throwaway save, right-click the engine and shut it down, leaving the stage list where it is. Run `reentry`. The engine lights at block start, the stage list doesn't move, and the landing brakes on it. In `KSP.log`, check that no stage was fired in that run.
5. **Heat on the way down with an engine lit at zero throttle.** Watch that no thrust shows before braking starts, and that the throttle reads 0 through the fall. Tell #824 if the craft burns before the braking.
6. **A shield with nothing below it.** On a throwaway craft, a pod with radial engines and tanks and a shield at its base, on a Duna entry with no chutes: the shield goes at 400 m/s or 3000 m, or as braking starts if that is first, and before the engines fire.
7. **Short of fuel.** On a throwaway lander with one Mk16 and about 10 m/s of fuel at Duna: it burns to empty. A touchdown at or under 15 m/s reads `success: true`; over it fails with `touchdown at ... m/s is over 15 m/s`, and the fuel running out is not the reason.
8. **Fuel by fuel line.** On a throwaway lander whose only fuel reaches the engine by a fuel line from a tank on a separate branch: `KSP.log` reads `engines`. With the fuel line's tank drained, or its crossfeed cut by a decoupler with crossfeed off, it reads `chutes, ..., no engine with fuel`.
9. **A Sepratron isn't an engine for this.** On the throwaway lander, add an unlit Sepratron in a higher stage: it stays unlit, and the log names the liquid engine.
10. **`reentry_orbit` and a Mun return stay on chutes.** Run `reentry_orbit`: `KSP.log` reads `chutes, ...` at about 7.5 m/s, and the stack drops as before.

Option: run `local res is reentry(lexicon("touchdown", "powered")). addons:claudia:report(res).` from a script variable that the Script Check can't read (put the lexicon in a local first): it fails at once with `touchdown is no longer an option: reentry decides for itself whether to land on its engines`.

### On the card (no flight needed)

In flight with a Mission Plan: an Entry predicted from 10 to 15 m/s with an engine reads `..., powered finish.` on the Mission Plan card's `Chutes:` line and has no chute finding, and the margin's need is that speed higher. Over 15 m/s it reads the same, with the finding it had.

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
