# reentry carries on through a part loss in the air

In-game check for #958. Once `reentry` is falling through the air (at or under the air top, off the ground), losing parts no longer ends the block, whatever `abort_on_part_loss` says. Core's `ENTRYLOSS` re-reads `ENTRYFINISH` and `CHUTEGROUP` for the craft that is left, and the block flies on with it. Above the air the abort stands, as in every other block. The agent run that wrote this did not launch KSP.

The block can't tell a part you decouple by hand from one torn off: both are parts it didn't drop itself. So a manual decouple stands in for a loss here.

## Fly: `tests/blocks/reentry_duna_chutes_then_engine`

The craft is a probe, a Mk16, a TD-12, an FL-T800 and an LV-T45, so it is a powered finish with chutes. Results are in `KSP.log` and the Script Result.

### A lost engine with chutes left

1. Let `deorbit` and `reentry` run. Once the craft is below 50,000 m (Duna's air top) and before braking starts, right-click the TD-12 and choose **Decouple**. Tank and engine go.
2. `reentry` doesn't end: no `abort abort_on_part_loss`. A `blocknote` with `phase parts_lost` shows `lost` (2 or more), `powered` false, `chutes` 1, and `why` from `ENTRYFINISH` (chutes, no engine with fuel). The `ENTRYLOSS` log line says `carry on True`.
3. The Mk16 opens on its normal schedule and the craft lands on it: `success` true, `parts_lost` in the result matches the note, and `touchdown_speed` is under 15.
4. Fly it once more and decouple only after the `phase braking` note: the burn's loop runs on to the ground with nothing to burn, and the Mk16 may still open under it (the "chutes open" hold on calling is cleared). At the end the controls come back (`claudia_release_controls`) and no gimbal is left locked.

### Before the air, the abort stands

5. Fly it again and decouple the TD-12 while `reentry` is still waiting above 50,000 m. `reentry` ends with `abort abort_on_part_loss: N part(s) lost` and no `parts_lost` note, as before #958. Expect a mid-air abort to open the chute if that is safe.

### Watch

6. **Watch, on any flight that really loses parts in the air** (overheating, a crash into the air): one `parts_lost` note per tick that saw a loss, and the Debrief's events name each part that went (`… was destroyed` or `… broke off`).
7. **Watch, after the Mk16 is out:** decouple the TD-12 under the open canopy (fly it again). The note shows `chutes` 1 and the chute keeps the craft. The block mustn't light anything, and no other chute is armed.
8. **Watch:** a loss inside the half second after the block drops its own shield is taken as part of that drop, with no note. That costs nothing but the note: the chutes are read again before each opening, and thrust before every tick.

## Check: a chute-only craft that loses its chutes but keeps an engine

No block test craft has a chute that can be removed by hand. On a craft with a radially decoupled chute and an engine that has fuel, fly `reentry` on chutes and decouple the chute below the air top. The note shows `powered` true, the engine lights (no stage fires), the craft turns back to retrograde if it was held, and braking starts from Core's brake height.
