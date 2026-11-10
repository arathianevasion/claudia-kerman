# reentry: reports only after about 2 s on the ground

In-game check for #968. `tests/blocks/reentry_duna_weak_group`'s first run reported FLYING at 2,457 m: `reentry` saw LANDED at 4 m/s, cut the chutes and reported at once, and the 1.7 t craft bounced. Now, after first touch, `reentry` waits until the craft has been LANDED or SPLASHED for 2 s without a break (a bounce starts the 2 s again), for 30 s at most from first touch. The agent run that wrote this did not launch KSP.

**Choices:** the chutes are cut at first touch, as before, so a canopy can't drag the craft over during the wait. `touchdown_speed` is the speed at first touch. The result's `geo`, `splashed` and `sun_elevation_deg` are read after the wait. The controls are released before the wait, so nothing in it lights or throttles an engine, on a chute landing or a powered one.

**Block migration:** the 2 s and 30 s rule is Core's (`GroundSettle`, behind the block-internal `GROUNDSETTLE`); parity is not needed as no Kerboscript rule was replaced; the loop that asks it is two lines in the library, paid for by removing `reentry_touchdown_ok` (net zero, 10,715). No `//|` change. Player flies `tests/blocks/reentry_duna_weak_group` (unchanged).

## Fly it

1. Run `tests/blocks/reentry_duna_weak_group`. Its `landed_or_splashed` check must pass (it failed before this change), with the other checks as before.
2. In the log, the `phase chutes_cut` note comes at first touch, and the `phase landed` note (`actual_geo`, `touchdown_speed`) about 2 s later. The `GROUNDSETTLE` log lines (one a second) read `settled False`, then `settled True`.
3. `touchdown_speed` in the result is the speed at first touch (a few m/s), not zero from a craft already at rest.
4. The result's `success` is unchanged from before for the same landing.

## Watch

5. **A bounce:** if the craft bounces in step 1, the `phase landed` note comes later than 2 s after first touch (2 s after it settles), and the result's situation is LANDED.
6. **A powered landing** (`tests/blocks/reentry_duna_powered`): the engine is out and the throttle reads 0 for the whole wait.
7. **A splashdown or a long slide:** it ends within 30 s of first touch whatever the craft is doing; `splashed` is true on water.
8. **Say so on #968** if the report ever reads FLYING for a craft that sat on the ground, or the wait outlasts 30 s.
