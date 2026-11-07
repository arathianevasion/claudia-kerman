# A long coast with a capture isn't stopped short (#830)

The duration check reads through `coast`, and the mod raises a too-short `expected_duration_s` itself. Core tests cover the rules (`BurnPointsTests`, `PredictedCrossingTests`); no dedicated flight is needed. Read this from the next interplanetary flight's log.

## Check 1: a short duration is raised, with no request

1. On the next flight with a `coast` (to a UT written as a number or `time:seconds + n`) followed by a `capture`, `circularize` or similar burn, look at the Sequence Plan card at Approval.
2. Its Expected duration covers the coast and the wait to the burn, even if Claudia's figure was shorter. The log shows no extra "Claudia is rewriting it" request for the duration.
3. In the Mission's `plans.jsonl`, the plan's `lints` has "duration raised by the mod: expected_duration_s … to …" when the mod raised it, and nothing when her figure was already long enough.
4. The run isn't ended with "the mod stopped it" before the capture.

## Check 2: the stop holds while a warp's target is ahead

A script the check can't read (`transfer_to` then `capture`, or a `coast` to an `until` the check doesn't read) can still outrun 300% of its planned duration. The kOS bridge now tells the core each warp request's target UT while the run goes (`KosWarpRequested`). At the deadline, if the latest request's target is still ahead, the stop waits until that target has passed plus an allowance of 60 s (`RunawayStop.WarpAllowanceSeconds`). A script that isn't warping, or is past all its warps, stops at the deadline as before, with the same make-safe and Debrief and no Alert.

1. On a flight whose script warps past its deadline (a long `transfer_to` then `capture`), read the log: no "The script was still running at 300%" notice appears before the warp's target.
2. If the run is still going 60 s of game time after the warp's target, it is stopped then, with the usual Debrief ("timed out").
3. A script that hangs without warping is still stopped at 300% of its planned duration.

### Check 2b: the hold ends once warp has (#834)

Each UT reading the adapter sends now says whether warp is running (`TimeWarp.CurrentRateIndex > 0`). Past the deadline the hold continues only while warp runs, or within 60 s of game time after it last ran (a warp that never ran: 60 s after the deadline). The target has no cap.

1. Run a script past its deadline with a long warp running (e.g. 100000x toward a far `transfer_to`/`coast` target): the stop doesn't fire while warp runs.
2. During that warp, drop to 1x for about 30 s, then warp again: the stop doesn't fire.
3. Drop to 1x and stay: the run is stopped about 60 s of game time later with the usual "timed out" Debrief.

## Check 3: the adapter reads the next-patch ETA (#833)

The adapter's read of `orbit.EndUT` into `GameSnapshot.NextPatchEndUt` (only for an ENCOUNTER or ESCAPE end; a MANEUVER end is read as no next patch, unconfirmed) is confirmed from the next interplanetary flight's log: for a `coast` to `time:seconds + ship:orbit:nextpatcheta + n` and then a `capture`, the duration is raised at Approval (as in Check 1) without an extra request, and the plan's `lints` shows the raise. After a coast to the next patch, a `capture` or `circularize` counts only the coast's wait (the next patch's own timing isn't in the snapshot). A second coast to the next patch leaves the plan unchecked.
