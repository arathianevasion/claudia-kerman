# The aimed Course Correction in Core (#793)

In-game check for #793 (from spike #482, `docs/research/return-landing-targeting.md`).

**Block migration:** `return_aimed_correction`, `return_aim_cost`, `return_aim_good_km` and `claudia_descend` moved to Core's `CorrectionBurn` (`CorrectionBurn.Landing.cs`). `AimedCorrectionParityTests` passed and was deleted with them. The agent run that wrote this did not launch KSP.

Fly `tests/blocks/return_target` (Mun to KSC). Install as in `phase-3-gate.md`.

1. The block test passes its checks.
2. `grep "CORRECTIONBURN" KSP.log` gives one line for the correction, not a run of `REENTRYIMPACT` lines before it: between `RETURNBURN` and the end of the block, expect two `REENTRYIMPACT` lines (the check of whether a correction is needed, and the result after the burn).
3. That line reads `Pe <m> m`, within 1 km of 30000, then `aimed <km> km from the target`, then `[plan-vs-flown] CORRECTIONBURN planned arrival: ...; landing target -0.0972, -74.5577`.
4. The correction's `correction_dv` in the Script Result is under 50 m/s, and `planet_pe` is within 1 km of 30000.
5. `predicted_error_km` matches the `aimed` figure to within what the burn's own error leaves (a few km); it says what is left, and `target_km` in the Script Result is the flight's distance from KSC.
6. Note how long the frame hitches at the correction: `grep` the `CORRECTIONBURN` line's time. It runs the predictor about 30 times in one call (about 300 ms at 10.5 ms each), on the main thread until #463.
