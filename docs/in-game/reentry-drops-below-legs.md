# A landing drops everything below its legs (#940)

In-game check for #940. With parts tagged `landing_leg` on the home side of the Entry Shield, `reentry` drops the stack below that shield whether it finishes on chutes or engines, and picks engines only from the craft that stays (ADR 0020). The agent run that wrote this did not launch KSP.

Read from the logs of the player's own flight. Results are in `KSP.log`.

## Check: the Duna ship

Fly the Duna ship (the lander with its aerospike and legs above heat shield 2, as in m20261108-220659-ac85) to a Duna landing through `reentry`.

1. In `KSP.log`, the `phase finish` blocknote's `why` at the drop point. It reads `engines, dropping the stack: <x> m/s with its chutes, on <the aerospike>`, or `chutes, <x> m/s` at or under 10 m/s. It no longer says `keeping everything`, and it never names the launch stack's engine.
2. The `phase stack_dropped` note appears before the craft reaches the air, and the launch stack is gone from the map as a separate vessel.
3. Heat shield 2 jettisons at its usual timing.
4. The chutes open and, over 10 m/s, the aerospike brakes the last of the fall.
5. Pass: the `phase landed` note reads under 15 m/s, and the lander stands on its legs.

If the line still says `keeping everything`, bring the log's `phase finish` and `block call ENTRYDROP` lines to the player.
