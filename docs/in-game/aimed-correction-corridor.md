# The aimed correction spends spare delta-v (#817)

In-game check for #817 (with #805 and #832). The agent run that wrote this did not launch KSP.

**Block migration:** the solver is in Core: `CorrectionBurn.Landing.cs` (the periapsis corridor, the spare delta-v, a reason for each fallback), and the small-burn rule is `BurnWorth`'s `correction` case. Kerboscript only caps the throttle: `claudia_fly_correction` asks `BURNWORTH`, and `claudia_burn` passes its `max_throttle` and `done_dv` to `exec_node`. No `//|` line changed; the library is two lines shorter than main's.

## What changed

- **Periapsis corridor (Kerbin only):** the aimed solve may move the periapsis 5 km either way of `target_pe` (25 to 35 km for `return`'s 30 km), never under 25 km, before it uses the lever with the periapsis held. A `target_pe` under 25 km holds the periapsis. On the capsule model the landing moves about 8 km per km of periapsis, so the corridor alone moves it about 40 km either way for under 1 m/s.
- **The g and heat limits it rests on:** a corridor end whose predicted entry pulls over 8 g moves back toward `target_pe`, 1 km at a time. The predictor flies the craft `reentry` will fly (#814's `IReentryCraft`). On the capsule model a Mun return pulls 4.0 g at 25 km, 3.4 at 30 and 2.7 at 35. The predictor has no heat model. Heat rests on what `return` already flies (30 km from the Mun, about 3.2 km/s at the air) and on `deorbit`'s 21 km entry, which is as deep but slower. Other planets keep the periapsis held.
- **Spare delta-v:** the landing may spend the delta-v left less the reserve (the larger of 50 m/s or 10%, plus a powered landing without chutes), less the Mission Plan's later legs (#832), up to 150 m/s. When stock can't say the delta-v left, it may spend 50 m/s as before. At the reserve it burns only the plain burn and reports the rest. The aimed burn is flown again in the predictor after the solve: an entry over 8 g or under the corridor falls back with its reason, and a burn more than 0.5 m/s over what it may spend is brought back to it.
- **Later legs in flight (#832):** a block call's snapshot gets the later legs' delta-v from the Mission's view (the craft's legs after the one in hand), so `deorbit`'s auto `max_dv`, the capture-first choice and the correction keep it back.
- **A reason for each fallback:** `aim_note` says why the solve stopped at the burn it kept, how far that burn lands from the target and how much delta-v is left. The `CORRECTIONBURN` block call line ends with the same note.
- **Small burns:** a correction under 0.1 m/s is skipped only when its landing is already within 2 km. Under 2 m/s the throttle is capped so one 0.02 s tick is at most 5% of the burn, and `exec_node` stops at a tenth of the burn left (`done_dv`, 0.005 to 0.05 m/s), so it ends within 0.05 m/s of the node. Its `max_residual` stays 0.5 m/s: a residual of a few tenths doesn't fail `return`.
- **#805:** when the plain burn converged but missed `pe_tolerance`, it still flies if its periapsis is in the corridor or within 2 `pe_tolerance` of `target_pe`; `aim_note` starts `plain burn:` with the reason. When no burn was solved, none flies: `aim_note` starts `no correction flown:` and `return` carries on to `reentry`, unless the path as it is has its periapsis outside the corridor or no landing; then `return` fails with `found no correction: ...; without it ...`. Not built: the Debrief rating such a return Off-Nominal (waiting on the player's wording).

## Steps

Fly `tests/blocks/return_target` (Mun to KSC). Install as in `phase-3-gate.md`. If you have it, use a Mun polar return as well, after #816's aimed departure.

1. The block test passes its checks, and `return` succeeds.
2. Run `grep "CORRECTIONBURN" KSP.log`. The correction's line ends with `Pe <m> m`, then `aimed <km> km from the target`. If the solve stopped short, a `; ` note follows with its reason, the km and the m/s left. `Pe` is between 24,000 and 36,000 m.
3. Run `grep "BURNWORTH" KSP.log`. A correction under 2 m/s shows `fly it: throttle at most <x>, stopping within <y> m/s`. A skipped one shows why it was skipped.
4. The `exec_node` result after the correction has a `residual` under 0.05 m/s for a correction under 1 m/s.
5. Write down the final miss against each stage's prediction:
   - the ejection's `error_km` (`RETURNBURN`);
   - the correction's `aimed` km (`CORRECTIONBURN`);
   - `return`'s `predicted_error_km`;
   - `reentry`'s `prediction_miss_km`;
   - the Debrief's distance from KSC.

   Target: within 20 km of KSC on a Mun return.
6. Check `reentry`'s `max_g`. It should be under 8.
7. On a Mission with a leg after the landing, `grep "DEORBITBURN\|CORRECTIONBURN" KSP.log`: a burn the reserve refuses says `and <m/s> m/s for the later legs`, and the correction's note gives the m/s it may spend above the reserve less those legs.
8. Note how long the frame hitches at the correction. The corridor adds up to 14 predictions to the solve's 60, about 150 ms more at 10.5 ms each.

## Not in this ticket

The late trim at the start of `reentry` (from #317) is not built yet. `reentry` has no landing target to aim at. See the result notes for #817.
