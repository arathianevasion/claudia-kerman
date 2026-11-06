# reentry in KSP: predicted against actual landing (#478)

In-game checks for #478 (ADR 0014 block migration: the log lines are Kerboscript wiring; the miss distance is Core's `ADDONS:CLAUDIA:PREDICTIONMISS`). `reentry` asks `REENTRYIMPACT` once, before the entry, with the shield drop and chutes it will fly, and notes the prediction; after touchdown it notes the actual landing and the miss. Log-only: the prediction never changes how reentry flies. No test.ks changed. The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: Run reentry_orbit, reentry_hop and reentry_tumble

For each, pick it and press **Run**. It passes as before. In `KSP.log` find the block's two notes (the `reentry` block-note lines) and record the figures:

1. `phase predicted_landing`: `predicted_geo` (lat, lng) and `landing_ut`, written before the entry (before the wait for the atmosphere in reentry_orbit). The `block call REENTRYIMPACT took ... ms: landing ...` line just before it has the predictor's own figures (the frame's inverse rotation).
2. `phase landed`: `actual_geo` (lat, lng, alt), `touchdown_speed` (m/s) and `prediction_miss_km` (the great-circle km between the two). The `block call PREDICTIONMISS` line repeats the km.

Record the three `prediction_miss_km` values and the touchdown speeds against #462's figures (the chute shortening, 7.7 m/s under a Mk16). There is no predicted touchdown speed: REENTRYIMPACT returns none.

## Check 2: a predictor error flies on

On any reentry the predictor cannot predict (for example `reentry` started on the ground, or an orbit that never reaches the atmosphere), the note reads `phase predicted_landing` with `error` and `note: flying on without a prediction`, and the flight goes on as before. After a touchdown the `phase landed` note has no `prediction_miss_km`.

## Check 3: a real Mission

Fly a Mission with a reentry (a Mun return will do). Its `reentry` block writes the same two notes, giving a predicted-against-actual record from a real flight.
