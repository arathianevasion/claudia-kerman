# launch and ascend retract the legs once clear of the ground (#946)

In-game check for #946. A block that started on the ground (`launch` always; `ascend` when its start is "ground") issues `gear off` the first tick the craft is over 100 m above the ground and climbing with the Gear group on. `ascend` from flight leaves the gear alone. It never fails or delays the block, and adds no trace line. The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md`. Results are in `block-tests.log` and `KSP.log`.

## Check 1: launch from the pad

Use a craft with legs set out and the Gear group on. Run `launch_hop`. The legs stay out until the craft passes 100 m, then fold in within a tick or two. The block passes as before.

## Check 2: ascend from the ground

Place a craft with legs out on a body and run `ascend` from the ground. The legs fold in the first tick above 100 m radar altitude while climbing.

## Check 3: legs stay out when the group is off

Put the legs out by right-click with the Gear group off, then `launch`. They stay out the whole climb.

## Check 4: ascend from flight

From flight (a craft in the air with legs out, e.g. the abort to orbit), run `ascend`. The legs stay out.
