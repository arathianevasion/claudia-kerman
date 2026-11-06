# A long coast with a capture isn't stopped short (#830)

The duration check reads through `coast`, and the mod raises a too-short `expected_duration_s` itself. Core tests cover the rules (`BurnPointsTests`, `PredictedCrossingTests`); no dedicated flight is needed. Read this from the next interplanetary flight's log.

## Check 1: a short duration is raised, with no request

1. On the next flight with a `coast` (to a UT written as a number or `time:seconds + n`) followed by a `capture`, `circularize` or similar burn, look at the Sequence Plan card at Approval.
2. Its Expected duration covers the coast and the wait to the burn, even if Claudia's figure was shorter. The log shows no extra "Claudia is rewriting it" request for the duration.
3. In the Mission's `plans.jsonl`, the plan's `lints` has "duration raised by the mod: expected_duration_s … to …" when the mod raised it, and nothing when her figure was already long enough.
4. The run isn't ended with "the mod stopped it" before the capture.

## Not built: holding the stop during a warp

A script the check can't read (a `coast` to `ship:orbit:nextpatcheta`, `transfer_to` then `capture`) is still stopped at 300% of its planned duration. Holding the stop while a warp target is ahead needs the run's warp requests live in the core, which they aren't (the warp trace is kept in the kOS bridge and handed over when the run ends).
