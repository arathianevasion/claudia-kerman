# A landing drops everything below its legs (#940)

In-game check for #940. With parts tagged `landing_leg` on the home side of the Entry Shield, `reentry` drops the stack below that shield whether it finishes on chutes or engines, and picks engines only from the craft that stays (ADR 0020). The agent run that wrote this did not launch KSP.

Read from the logs of the player's own flight. Results are in `KSP.log`.

## Check: the Duna ship

Fly the Duna ship (the lander with its aerospike and legs above heat shield 2, as in m20261108-220659-ac85) to a Duna landing through `reentry`.

1. In `KSP.log`, the `phase finish` blocknote's `why` at the drop point. It reads `engines, dropping the stack: <x> m/s with its chutes, on <the aerospike>`, or `chutes, <x> m/s` at or under 10 m/s. It no longer says `keeping everything`, and it never names the launch stack's engine.
2. The launch stack separates about 600 s before the air: it shows on the map as a separate vessel while the craft is still above the air. (The `phase stack_dropped` note is only written once the craft reaches the air, so its place in the log doesn't show this.)
3. Heat shield 2 stays on through the heating, then jettisons at its usual timing (400 m/s or 3,000 m), or as braking starts if that comes first.
4. The chutes open and, over 10 m/s, the aerospike brakes the last of the fall. When braking starts, nothing is attached under the aerospike: the TD-12 between it and heat shield 2 has gone with the shield. `reentry`'s result has `shield_dropped` true.
5. Pass: the `phase landed` note reads under 15 m/s, and the lander stands on its legs.

## Check: with no legs nothing changes

No `tests/blocks` scenario has this layout, so the Duna ship is the one to fly for the rule above. Run `reentry_duna_powered` (no legs): `KSP.log` still reads `engines, keeping everything`.

If the Duna ship's line still says `keeping everything`, bring the log's `phase finish` and `block call ENTRYDROP` lines to the player.
