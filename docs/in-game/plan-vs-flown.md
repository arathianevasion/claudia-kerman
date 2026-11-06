# planned against flown orbit at deorbit and return burns (#791)

In-game checks for #791 (from spike #482, `docs/research/return-landing-targeting.md`). Mission m20261028-122945-db4b planned a 0.00 km miss and landed 282 km off; execution and the predictor did not explain it. `DEORBITBURN`, `REENTRYIMPACT`, `CORRECTIONBURN` and `RETURNBURN` now add one line to their `block call` line in `KSP.log`, tagged `[plan-vs-flown]`. Log only: nothing flies differently. The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md`. Then `grep "plan-vs-flown" KSP.log`.

## Check 1: the lines

Fly any Mission with a `deorbit` (or `return` with a landing target). Expect:

- `DEORBITBURN planned orbit: Pe .. m, Ap .. m, period .. s, Pe at UT ..; frame LonOffset .. deg, handed .., ground_alt .. m`: the orbit after the planned burn, and the frame and altitude the landing was read in.
- `REENTRYIMPACT planned orbit: ...`: the same for the orbit the predictor was given after the burn (for `return`, its planned path round Kerbin).
- `CORRECTIONBURN` / `RETURNBURN planned arrival: burn at UT .., Pe .. m, inclination .. deg; landing target ..`: the arrival the burn plans. `CORRECTIONBURN` and `RETURNBURN` log `landing target none` until their asks carry the landing site (`lat`/`lng`); that is expected, not a fault. The frame for those burns is on the `REENTRYIMPACT` line that follows.

To check the frame: read `frame LonOffset` and `handed` on the `DEORBITBURN` line, then the same fields on the `REENTRYIMPACT` line of the same flight. The two should name the same frame for the same site; a different `LonOffset` between them is the mismatch to report.

## Check 2: compare the frames across the inverse-rotation threshold

Stock KSP applies its inverse-rotation angle (the `inverse rotation` figure already on the `landing` part of the same line) only below a threshold altitude, so the frame the ground is read in can change between a deorbit planned from high and one from low. Fly one deorbit planned from above the threshold and one from below, and compare the two `frame LonOffset` values and the `inverse rotation` figures. A `LonOffset` that differs between the two for the same site is the cause to file separately.

After landing, set the flown orbit (`Pe`, `Ap`, `period` of the orbit after the burn, from the game) against the `planned orbit` line, and the landing against the `landing` figures on the same `block call` line.
