# reentry: a chute-only landing that loses its mains goes over to its engine

In-game check for #963. A chute-only `reentry` with its drogues out or armed that loses its main chutes in the air no longer just falls on under the drogues. Core's `ENTRYLOSS` sees a chute of the group gone and no main left aboard, and lets `ENTRYFINISH`'s fresh answer decide. If that says engines, the block lights them (part by part, at zero throttle, no stage) and flies #954's powered finish from there. The group is kept, drogues included, so `BRAKETICK` holds the thrust at zero until they are fully open and settled. If it says chutes (no engine with fuel, or none that will light) the craft stays on its drogues. Either way the `parts_lost` note's `why` says which. The agent run that wrote this did not launch KSP.

**Block migration:** the decision is in Core (`BlockCalls.EntryPartLoss`, the mains-lost rule); `BRAKETICK`'s held times start fresh at the switch (`BlockCalls.EmptyBrakeHold`, the addon). The zero throttle at lighting moved into `reentry_light`, so the library count didn't change. No `//|` change.

## The craft

No block test craft has drogues, a main you can drop and an engine. Build one from `tests/blocks/reentry_duna_chutes_then_engine`'s craft in the VAB: keep the probe, TD-12, FL-T800 and LV-T45; replace the Mk16 with a Mk25 drogue on the probe and a Mk16 on a radial decoupler (TT-38K) beside it, both in the chute stage; empty the FL-T800 to about a fifth, so the drogue alone predicts over 10 m/s. Put it in an 80 km Duna orbit (the test's `scenario.sfs` orbit) and run a Mission Script with `deorbit` then `reentry()` on its defaults.

The first `phase finish` note must say `chutes`. If it says engines, empty the tank further or add a second Mk16: this check needs a chute-only start.

## Fly it: engine aboard

1. Let `reentry` run to the drogue: a `chutes_used` entry for the Mk25 and the drogue out in the air.
2. Once the drogue reads deployed and before the Mk16 is called (no `chutes_used` entry for it yet), right-click the radial decoupler and choose **Decouple**. The Mk16 goes.
3. A blocknote `phase parts_lost` shows `powered` true, `chutes` 2 (the kept group: drogue and the lost main), and `why` beginning `mains lost, on to the engines:`. The `ENTRYLOSS` log line says `carry on True`. The LV-T45 lights with no stage firing, and the craft turns to surface retrograde.
4. **The engine burns nothing yet.** Trace lines (`phase braking`) only start once braking does; until then the throttle reads 0 in the flight UI. `BRAKETICK`'s log lines show `throttle 0.00 (zero: chute opening)` for at least 4 s after the switch, while the drogue settles.
5. A blocknote `phase braking` follows with `why` `chutes open` (the drogue settled) at Core's brake height, or `last resort` if the drogue had failed. The burn then runs as #954's: trace lines once a second with `rate_deg` and `zero_why`, thrust zero while tipping over 20 deg/s or over 30° off the aim.
6. The craft lands: `success` true, `touchdown_speed` under 15 (ideally under 5), `powered_dv` over 0, `parts_lost` in the result matching the note's `lost`. The result reports the switch through `powered_dv` and the `parts_lost` note's `why`.

## Fly it: no engine that can land it

7. Fly it again with the FL-T800 emptied completely (no fuel). Decouple the Mk16 as in step 2.
8. The `phase parts_lost` note shows `powered` false and `why` beginning `mains lost, staying on the drogues:` and ending `no engine with fuel`. Nothing lights; the craft falls on the drogue. Expect a touchdown over 15 m/s and the block failing on it: that is the drogue's speed, not this change.

## Watch

9. **Watch, mains called but still packed when lost:** decouple the Mk16 just after its `armed` note. The same switch should happen (`mains lost, on to the engines`), and the engine stays at zero throttle while the drogue is not yet settled.
10. **Watch, a main left aboard:** with two Mk16s, one radial, drop only the radial one under the drogue. The note's `why` must not begin `mains lost`, the block stays on its chutes and the other Mk16 opens.
11. **Say so on #963** if the engine ever gives thrust while a `phase chutes` note reads `armed` or `semi`, or within 4 s of the switch with the drogue out.
