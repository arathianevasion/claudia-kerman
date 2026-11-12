# reentry carries on through low charge once it is falling into the air

In-game check for #942. With the periapsis at or under the air top, `min_ec_pct` no longer ends `reentry`. Under it the block lets go of the steering (unlocked, RCS and SAS off) and carries on. Under 2% it arms every stowed chute in the group once with the stock deploy event, whatever the schedule. A powered finish keeps the braking burn's steering. The rule is Core's `LOWCHARGE`. The agent run that wrote this did not launch KSP.

## Fly: `tests/blocks/reentry_low_charge`

Its craft is `reentry_duna_powered`'s at 1.3% charge, on an 80 by 15 km Duna entry orbit. Results are in `KSP.log`.

1. `reentry` doesn't fail with `abort min_ec_pct`; the report's `aborted_on_charge` is false.
2. Two `blocknote`s with `phase low_charge`, each with `steering_released` true and `chutes_used` naming the Mk16 once. The first comes near the block start with `pct` about 1.3. The second comes on the next tick after the block chooses its group after any drop, with `pct` under 2, when it re-arms that group and finds nothing new to arm. If the panels have lifted the charge over 2% by then, the second comes later, or not at all. Expect only one note if the block started under 600 s from the air (this orbit gives about 640 s).
3. No `lock steering` hold: the craft doesn't turn to retrograde and RCS stays off. The next `lock steering` is the braking burn's, if this flight lands on the engine.
4. **Watch:** the armed chute does not tear off at speed. It should wait for safe conditions by itself; the `.cfg` shows `minAirPressureToOpen` and `deployAltitude`, not that behaviour. If it does tear off, note the speed and altitude.
5. **Watch:** whether the unheld craft keeps its shield forward through the heating.
6. **Watch, on the powered path:** the engine is unheld until braking starts, and `reentry_brake_height` allows only 1 s for the turn. Check that the braking burn still points the engine retrograde. If it burns off-axis, note how far off and for how long.
7. The chute opens and the craft comes down (`landed_or_splashed` true).

## Check: a flight in between

To see the steering released without the chutes armed, start a `reentry` on an entry orbit with charge between 2% and `min_ec_pct`: the note has `steering_released` true and an empty `chutes_used`, and the chutes open on their normal schedule.
