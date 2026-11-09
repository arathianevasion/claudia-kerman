# reentry flies chutes first, engines last on a powered finish

In-game check for #954 (flight m20261109-140705-ae3a braked at 1.7 km under chutes still opening and tumbled; ADR 0020's dated note has the sequence). Core's `BRAKEHEIGHTS` and `BRAKETICK` decide when braking starts and guard the throttle; `reentry` flies it. The agent run that wrote this did not launch KSP.

**Block migration:** the brake height, the stopping distance, `reentry_brake_due`, the last-resort height and the throttle guard are in Core (`ReentryBrake`), behind `BRAKEHEIGHTS` and `BRAKETICK`; the parity test against the Kerboscript was deleted with it (ADR 0014). `reentry_duna_powered` flies with `chutes: "none"`, so it can't check this.

## Fly: `tests/blocks/reentry_duna_chutes_then_engine`

It has the Mk16 and an LV-T45 (with a gimbal) from `reentry_duna_powered`, flown on `reentry`'s defaults. Results are in `KSP.log`.

1. Blocknotes `phase chutes` appear as the chute goes through `armed`, `semi` and `deployed`, one for each change. The engine stays off until the note reads `deployed`, and then until the speed settles or about 8 s have passed.
2. A blocknote `phase braking` says why: `chutes open`. Its height should be a few tens of metres at about 12 m/s. **If it says `last resort` above 80% of the chute's deploy altitude (`deployAltitude` in its part `.cfg`; 800 m for a Mk16) with a healthy chute, say so on #954**: the player's option (b) should have held it.
3. It burns only the last stretch: `powered_dv` in the tens of m/s, not hundreds, and `touchdown_speed` under 5 m/s, standing on its legs.
4. The rate stays low: no tumbling, the craft holding surface retrograde on the wheels with RCS off. Below about 3 m/s it holds straight up. No site correction.
5. Trace lines (`phase braking`) once a second give `throttle` and `off_deg`. Throttle reads 0 while `off_deg` is over 30.
6. The result's `gimbal_locked` is false, and the LV-T45 gimbals for the next flight (a craft relaunched after this lands has its gimbal back).

## Watch

- With chutes that never open (chute_mode "safe" but the chute gone, or still packed by 1,500 m), the burn starts anyway at the last-resort height: the note reads `last resort`.
- The 30° guard has no hysteresis: thrust may chatter when `off_deg` sits near 30, or drop to 0 for a moment at the 3 and 5 m/s aim flips. Note it if the trace shows the throttle going in and out.
- How long a real canopy takes to fill is not measured: the 8 s comes from the part's `deploymentSpeed` of 0.12. Compare the `deployed` note with the speed in the log.
- A `chutes: "none"` flight (`reentry_duna_powered`) brakes as before and opens nothing.
