# A long coast with a capture isn't stopped short (#830)

The duration check reads through `coast`, and the mod raises a too-short `expected_duration_s` itself. Core tests cover the rules (`BurnPointsTests`, `PredictedCrossingTests`); no dedicated flight is needed. Read this from the next interplanetary flight's log.

## Check 1: a short duration is raised, with no request

1. On the next flight with a `coast` (to a UT written as a number or `time:seconds + n`) followed by a `capture`, `circularize` or similar burn, look at the Sequence Plan card at Approval.
2. Its Expected duration covers the coast and the wait to the burn, even if Claudia's figure was shorter. The log shows no extra "Claudia is rewriting it" request for the duration.
3. In the Mission's `plans.jsonl`, the plan's `lints` has "duration raised by the mod: expected_duration_s … to …" when the mod raised it, and nothing when her figure was already long enough.
4. The run isn't ended with "the mod stopped it" before the capture.

## Check 2: the stop holds while a warp's target is ahead

A script the check can't read (a `coast` to `ship:orbit:nextpatcheta`, `transfer_to` then `capture`) can still outrun 300% of its planned duration. The kOS bridge now tells the core each warp request's target UT while the run goes (`KosWarpRequested`). At the deadline, if the latest request's target is still ahead, the stop waits until that target has passed plus an allowance of 60 s (`RunawayStop.WarpAllowanceSeconds`). A script that isn't warping, or is past all its warps, stops at the deadline as before, with the same make-safe and Debrief and no Alert.

1. On a flight whose script warps past its deadline (a long `transfer_to` then `capture`), read the log: no "The script was still running at 300%" notice appears before the warp's target.
2. If the run is still going 60 s of game time after the warp's target, it is stopped then, with the usual Debrief ("timed out").
3. A script that hangs without warping is still stopped at 300% of its planned duration.
