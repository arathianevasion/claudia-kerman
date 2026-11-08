# Landing predictions read the Ship Systems stowed (#868)

## What the flight showed

In flight m20261106-203509-34a6 (Duna, the stack kept), `deorbit`'s burn search (`DEORBITBURN`) planned a landing 3.89 km from 0, 0 at Pe 15,000 m. After the burn, its check (`REENTRYIMPACT`) read 148.83 km at Pe 15,169 m. #814's model gave about 2 km for those 169 m. That flight's KSP.log is gone. This comparison uses the code the flight ran (main at 2f78281e, before #814), main now, and the Mission folder: the transcript, `events.jsonl` and the craft check.

## The two calls' inputs

| Input | `DEORBITBURN` (the search) | `REENTRYIMPACT` (after the burn) | Verdict |
|---|---|---|---|
| Target, ground height | 0, 0 and the terrain there | the same | same |
| Parts left out | `separate` "none": none | none | same |
| `shield_drop` (the stack still on) | `claudia_shield_drop(ship:parts, none)` | the same | same in both calls, so **ruled out** for the gap. (The craft `reentry` then flew dropped its stack: the plan's lint said so, and the events show the separation. That is the miss at touchdown, which #814's `ENTRYCRAFT` now aims for. It is not the gap between these two calls.) |
| `chutes` | not given: open | not given: open | same |
| Mass | 13.2 t, less the burn's propellant at the engines' isp | the craft's mass after the burn | same within about 0.1 t, which moves the landing well under 1 km: **ruled out** |
| Orbit | the planned orbit after the burn, Pe 15,000 m | the orbit as flown, Pe 15,169 m | 169 m. In the Core model a 13.1 t stack moves 2 to 7 km for that, with Duna's scale height taken as 4 to 7 km: **ruled out** as the 145 km |
| The burn flown: normal part | the search's normal | — | The burn flown was 28.37 m/s. A purely retrograde burn from 58.8 × 60.7 km down to Pe 15 km needs about 28.3 m/s, so the normal part was a few m/s at most. Even with its sign flipped, that moves the landing about 1 km: **ruled out** |
| The burn flown: timing | `burn_ut` | — | `exec_node` flies the node at `burn_ut`. 145 km along the track is about 160 s at Duna's 0.89 km/s, far more than a node can be off: **ruled out** |
| Frame and time | the frame read at the call (through Zup since #326) | the same code, 42,700 s later | Same code path. A frame error would show the body's turn between the calls, 235°, not 26°: **ruled out** by the code (the lines' inverse-rotation figure is gone with the log) |
| **Drag cubes** (the adapter's cube drag and lift) | **read as the parts were: 4 solar panels and 5 antennas out** | **read after `deorbit`'s entry cue had retracted them** | **Ruled in.** `events.jsonl` has "Ship Systems: retracted 4 panels, 5 antennas" at UT 168308362.9, 2.7 s into the plan. That is after the search (`deorbit.ks`: `DEORBITBURN`, then `shipsystems("entry")`) and long before the burn. |

From `PartDatabase.cfg`, one extended 1x6 panel (`solarPanels2`) has a face of about 1.1 m² (0.1 to 0.3 m² stowed). A DTS-M1 dish (`mediumDishAntenna`) has 0.71 m² against 0.02 m². The shield face is 1.27 m². The panels track the sun, so how much of that meets the air changes, but it can be 3 to 4 times the stowed stack's drag. In the Core model, that much drag moves a 13.1 t stack at Duna 103 to 170 km for scale heights of 4 to 7 km, close to the flight's 145 km (`StowedForEntryTests`). The search flew a craft that never meets the air. The landing it aimed for was short by about the amount the panels and dishes slowed it.

Two further findings:
- #814 measured its slope on a 3.9 t stack, but the flight's craft weighed 13.2 t. At 13.1 t the model gives 2 to 7 km per 169 m, not 1.2 km per 100 m. Still nowhere near 145.
- The cause holds in main today. `deorbit` still searches before its entry cue, and `return`'s `RETURNBURN` and `CORRECTIONBURN` aim with the panels out far from the planet.

## The fix

`ReentryCraft` (the adapter) reads each Ship System that the entry will stow with its RETRACTED cube, occluded like its live faces. `ShipSystemsMoves.StowedForEntry` (Core) decides which parts those are: with the setting on, every panel, antenna and radiator that can retract and isn't broken; with it off, none. All four calls (`DEORBITBURN`, `REENTRYIMPACT`, `RETURNBURN`, `CORRECTIONBURN`) build that craft. Body lift is still read from the live cubes.

## In-game check

Any `deorbit` (Kerbin or Duna) on a craft with panels or dishes out, flown with Ship Systems on.

1. In KSP.log, the `block call DEORBITBURN` line ends `; craft <t> t, drag area <a> m² at Mach 2, <n> Ship Systems read stowed`, with n the panels and antennas out. Record a.
2. The `block call REENTRYIMPACT` line after the burn reads `0 Ship Systems read stowed` (they are in by then), with a drag area within a few % of step 1's.
3. Pass: the two lines' landings are within a few km of each other, as the periapsis difference predicts (about 3 km for 169 m on a Duna stack). A gap like 145 km with equal drag areas means the cause is elsewhere: bring both lines to the player.
