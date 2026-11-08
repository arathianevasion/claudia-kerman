# reentry drops its stack 600 s before the air (#815)

In-game checks for #815, to fly together with #814 (`entry-craft.md`). `reentry` used to drop the stack below its Entry Shield at block start, about 230,000 s before entry in m20261106-132839-970f, and the drop's kick moved the landing about 70 km. Now:

- **At block start** `ADDONS:CLAUDIA:ENTRYDROP` is read with the block's `separate`, `chutes` and the kOS processor's part. Above the air, a stack `separate` "auto" can't drop (a part with no decoupler, or one that takes the processor) fails the block as before ("stack attached below the heat shield: ..."), unless the Entry lands on its engines and keeps it. Nothing fires and no engine lights.
- **The drop point** is `ENTRYDROP`'s `lead` (600 s, Core's `EntryStack.DropLeadS`) before the atmosphere. The block warps there on rails and is back at 1x 30 s early, so the drop fires off rails. A block that starts inside 600 s, or a warp that ran past the point, drops at once.
- **At the drop point** `ENTRYDROP` and `ENTRYFINISH` are read again, and that read is the one flown: the engines light (a powered entry), the stack drops, and then come the Chute Group, `shipsystems("entry")`, the Entry Shield, the `predicted_landing` note and the retrograde hold. `ENTRYFINISH` is read only here, so #814's "the landing was aimed for the craft ..." line compares the craft that really flies.
- **An abort before the drop** (the player takes control, or the block times out) keeps the stack: nothing fires, and the parachutes go through the abort's usual check against a freshly read Chute Group. Above the air that check opens nothing.
- **At the air** a new blocknote, `phase stack_dropped`, gives `pe_before` and `pe_after` (m, either side of the drop) and `stack_m`, the distance to the dropped stack (-1 if KSP has already removed it).

What the 600 s figure is based on (Core, `EntryDropLeadTests`, the capsule model, a 0.1 m/s kick along each axis, the worst one reported):

| Kick before the air | Mun return: landing moves | gap to the stack's path at the air | Duna low-orbit descent: landing moves | gap |
|---|---|---|---|---|
| 600 s | 0.61 km | 84 m | 1.76 km | 83 m |
| 1,200 s | 1.08 km | 208 m | 0.41 km | 377 m |
| 1,800 s | 1.48 km | 351 m | 0.04 km | 715 m |

The gap is from the capsule to the path the stack would fly unkicked. The stack also takes its share of the kick in the other direction, so the real gap is somewhat larger. On a low Duna orbit (a period of about 2,450 s) the kick's effect varies with where on the orbit it comes, so a longer lead is not always worse there. Whether the gap is enough is for the player to judge after a flight.

The agent run that wrote this did not launch KSP. Install as in `phase-3-gate.md`.

## Check 1: `tests/blocks/return_target` (block test runner)

1. In the block test runner window pick `return_target` and press **Run** (allow up to 3000 real seconds).
2. In `KSP.log`, during `reentry`:
   - The stack stays attached until about 600 s before entry: warp drops to 1x about 30 s before that, then the stack separates.
   - The `block call ENTRYDROP` line appears twice before the drop (block start and the drop point), then once more after it.
   - The `phase finish` blocknote comes after the wait, not at block start.
   - The `phase stack_dropped` blocknote: record `pe_before`, `pe_after` and `stack_m`. Expect a periapsis change of a few hundred metres at most (the flight saw 5,562 m from a drop 230,000 s out) and `stack_m` in the tens to low hundreds of metres.
3. Watch the capsule from the drop to the chutes: the stack must not hit it.
4. Pass: the test's `PASS return_target`, the capsule intact, and the `phase landed` note's `prediction_miss_km` (against the `predicted_landing` note made after the drop) within a few km.

## Check 2: a stuck stack still refuses at block start

Fly `reentry` from a Kerbin orbit above 70 km with a tank fixed to the Entry Shield and no decoupler between them, with `separate` "auto" (any craft; `reentry_orbit` with the decoupler removed in the VAB will do). The block must fail at once, before any warp, with "stack attached below the heat shield: <names>; drop it first or use separate (separate: <names>: no decoupler between it and the heat shield)", and nothing must separate.

## Check 3: an abort before the drop keeps the stack

Start a `reentry` far from the air (`tests/blocks/return_target` after the correction, or a Mun return Mission), and before the drop point take control or press Abort. The block fails with that reason, the stack is still attached, no decoupler fired, and no parachute opened.
