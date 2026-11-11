# deorbit, return and reentry: delta-v read after a recount

In-game check for #911: all three blocks read the delta-v left through `claudia_dv_recounted` (forcecalc, wait 0.5 s, then `claudia_dv_remaining`). The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md`. Results are in `block-tests.log` and `KSP.log`.

## Check 1: a return right after staging

Fly a craft with a stage to drop, in a Kerbin-return trajectory that needs a correction. Stage, then run `return` at once. The log shows a non-zero delta-v left for the aimed correction, and the correction flies (it is not skipped for want of delta-v). The same holds for `reentry`'s late trim right after a staging or a load.
