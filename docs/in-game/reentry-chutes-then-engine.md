# reentry flies chutes first, engines last on a powered finish

In-game check for #954 (flight m20261109-140705-ae3a braked at 1.7 km under chutes still opening and tumbled; ADR 0020's dated note has the sequence). Core's `BRAKEHEIGHTS` and `BRAKETICK` decide when braking starts and guard the throttle; `reentry` flies it. The agent run that wrote this did not launch KSP.

**Block migration:** the brake height, the stopping distance, `reentry_brake_due`, the last-resort height and the throttle guard are in Core (`ReentryBrake`), behind `BRAKEHEIGHTS` and `BRAKETICK`; the parity test against the Kerboscript was deleted with it (ADR 0014). `reentry_duna_powered` flies with `chutes: "none"`, so it can't check this.

## Fly: `tests/blocks/reentry_duna_chutes_then_engine`

It has the Mk16 and an LV-T45 (with a gimbal) from `reentry_duna_powered`, flown on `reentry`'s defaults. Results are in `KSP.log`.

1. Blocknotes `phase chutes` appear as the chute goes through `armed`, `semi` and `deployed`, one for each change. The engine stays at zero throttle from the first `armed` note until the note reads `deployed` and the speed settles or about 8 s have passed.
2. A blocknote `phase braking` says why: `chutes open`. Its height should be a few tens of metres at about 12 m/s. **If it says `last resort` with a healthy chute, say so on #954**: only a cut, missing or still-packed chute may fire it.
3. It burns only the last stretch: `powered_dv` in the tens of m/s, not hundreds, and `touchdown_speed` under 5 m/s, standing on its legs.
4. The rate stays low: no tumbling, the craft holding surface retrograde on the wheels with RCS off. Below about 3 m/s it holds straight up. No site correction.
5. Trace lines (`phase braking`) once a second give `throttle`, `off_deg`, `rate_deg` and `zero_why`. `rate_deg` is the tipping rate only, measured as `land` does: a craft rolling about its thrust axis reads low and keeps its thrust. Throttle reads 0 while `zero_why` is set: `chute opening`, `chutes unreadable`, `spinning` (tipping over 20 deg/s) or `off aim` (over 30).
6. The result's `gimbal_locked` is false, and the LV-T45 gimbals for the next flight (a craft relaunched after this lands has its gimbal back).

## Watch

- With a chute that never opens (cut, gone, or still packed 1 s after its call), the burn starts at the last-resort height with nothing filling: the note reads `last resort`. A later set being called (the mains, after failed drogues) does not stop that burn, even as it opens; note what the craft does under it. If the chute that counted as failed opens after all, the throttle goes to 0 until it settles. The tipping and 30° rules stay on throughout.
- If the addon can't read the chute states for about a second on a powered finish (never seen yet, so look for `BRAKETICK failed` in the log), the note reads `phase chutes`, `states unreadable`, then `phase chutes_cut`, `why unreadable`, and no chute is called after that, the abort included: it lands on the engine with its gimbals free. The thrust is 0 for that second, even in a burn already under way: note it if that second costs the landing. A chute armed but not yet out has nothing to cut and may still open later; note it if one does. A chute-only descent reads no states and is unaffected.
- With healthy chutes the engine can't help a fast fall: a stop from 140 m/s takes about 1,080 m with Duna's gravity, and there is no burn while a canopy fills. The player accepts this.
- The 30° guard has no hysteresis: thrust may chatter when `off_deg` sits near 30, or drop to 0 for a moment at the 3 and 5 m/s aim flips. Note it if the trace shows the throttle going in and out.
- The 20 deg/s limit is unflown (`ReentryBrake.MaxSpinDeg`, one constant to tune from the log): note the `rate_deg` the trace shows if thrust stays off when the craft could have used it, or if it pulses.
- A chute that reads `armed` for over a second counts as failed, though it may only be waiting for thicker air (the player's call: this stays): the last resort can then fire, and if the canopy opens after all the throttle goes back to 0 until it settles. Note any `last resort` with an `armed` chute that opens later.
- A chute that sits `semi` for a long time holds the engine off: it is healthy, opening fully at its deploy altitude. Note any flight where one reads `semi` close to the ground.
- How long a real canopy takes to fill is not measured: the 8 s comes from the part's `deploymentSpeed` of 0.12. Compare the `deployed` note with the speed in the log.
- A `chutes: "none"` flight (`reentry_duna_powered`) brakes as before and opens nothing.
