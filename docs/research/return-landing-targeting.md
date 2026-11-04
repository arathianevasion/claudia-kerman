# Timing a moon return to land near a target, and what reentry prediction still misses

Research spike #482 (spec #435, Phase 5). No product code. Measured against the merged code at `45de72a`: #449 (`CorrectionBurn`), #450 (`ReturnBurn`, `return` from any moon) and #462 (`ReentryPredictor` with an adaptive step, lift, parachutes and the heat shield's drop).

## Summary

- **Aim the landing at departure, not afterwards.** If the ejection burn comes 30 s before or after the least-Δv point, the merged `RETURNBURN` still reaches target_pe 35 km for +0.3 m/s, and the landing moves about 115 km. At ±60 s the cost is +1.1 to 1.2 m/s and the landing moves about 230 km. Successive windows from a 38 km Mun orbit move the landing 40° (about 420 km) each. So with up to 9 windows (7.1 h) and ±60 s of ejection timing, `return` can put the landing on any longitude for about 1.2 m/s. Today nothing does this: `return`'s ejection ignores `target`.
- **A Course Correction can't steer the landing far without moving the periapsis.** Halfway home, holding the periapsis, it moves the landing only about 2.6 km per m/s. The aimed correction in `return` therefore steers through the periapsis: 1 m/s prograde moves the periapsis 3.6 km and the landing 56 km. That pushes the entry shallow, where 0.1 m/s of execution error is 5 to 10 km on the ground. The flight's 162 km is consistent with this.
- **`deorbit` from a Mun-return orbit is cheap only where the natural landing already falls.** In a model of the flight's orbit, a target near that landing costs 18 to 33 m/s. Elsewhere the search's only answer is a capture-and-lap burn just after periapsis, costing 160 to 900 m/s. For 3 of 12 target longitudes there was no answer at all. The flight's 402 m/s burn was one of these capture-and-lap burns. The ticket's premise needs a correction: the stage had about 545 m/s at the burn, not about 400. `deorbit_short_of_dv` passed correctly, and about 143 m/s was left in the stage when it was dropped.
- **The predictor is not the weak link.** It flies the vacuum leg as an exact conic and takes Kerbin's rotation analytically, so a long coast from a moon costs no extra accuracy and no extra time: 0.44 ms per prediction from apoapsis in the tests, about 200 steps, all of them in the air. The error budget is execution error multiplied by sensitivity. Three things are left: shallow entries, the plan-to-flown gap of the flight's deorbit (282 km, which no modelled execution error explains), and making the error budget something the blocks report.

## Sources

- **MechJeb2**, pinned at [`0632b71`](https://github.com/MuMech/MechJeb2/tree/0632b71c27b568939ae726d20868cdf54b72fc1f) (`dev`, 2026-11-04). Core's ports cite `a958383`, and the files below are unchanged in what is cited here.
- **KSPTrajectories**, pinned at [`1304f46`](https://github.com/neuoy/KSPTrajectories/tree/1304f469ae8881a3b4658ff07fcb72040890d8b1) (`master`, 2023-05-15, its latest commit).
- **The probe:** [`return-landing-targeting/Spike482Probe.cs.txt`](return-landing-targeting/Spike482Probe.cs.txt). It drives the merged `ReentryPredictor`, `DeorbitBurn` and `ReturnBurn` with the research's capsule and atmosphere (`ReentrySim`, #247), and the file says how to run it. All the "measured" figures below come from it.
- **The lever model:** [`return-landing-targeting/levers.py`](return-landing-targeting/levers.py) (patched conics, run with `python3 -I`).
- **The flight:** Mission m20261028-122945-db4b (`KSP_spike/saves/Claudia Test 2/ClaudiaKermin/missions/`), read with `python3 -I`. Its `events.jsonl`, `transcript.json` and `plans.jsonl` are in the mission folder. Some figures in the ticket aren't in that folder: the 27 `REENTRYIMPACT` calls at about 10.5 ms each, the descent's best of about 23 km, `DEORBITBURN`'s −402.4/24.0 m/s at 0.00 km, and the post-burn `REENTRYIMPACT` of 0.84°, −99.37°. They came from that session's KSP.log, which has since been overwritten (KSP_spike's KSP.log is from 2026-11-03). They are used here as the ticket gives them.
- **The code the flight ran:** the flight ran 07:30 to 08:07 CDT on 2026-10-28, before #449 (`6c1efdc`, 09:28), #462 (`edd80d9`, 09:04) and #450 (`73a0e59`, 13:23) merged that day. Its `return` ejection came from `claudia_descend`, its predictor was the fixed-step one, and `deorbit`'s accuracy_km defaulted to 10. The aimed correction (`return_aimed_correction`: `claudia_descend` over the node's three components, 1 m/s first steps, 10 halvings, stopping at 2 km) and `deorbit_short_of_dv` are the same today as then.

## Q1. Timing the departure: how far each lever moves the landing

Landing shifts are along the equator (1° of longitude is 10.47 km). They are measured with an equatorial Mun return to target_pe 35 km from a 38 km circular Mun orbit, unless a row says otherwise.

| Lever | Moves the landing by | Costs | Source |
|---|---|---|---|
| Ejection time off the least-Δv point (`RETURNBURN` with the burn time pinned, re-solved for target_pe) | −30 s: +113 km (arrival 675 s sooner). +30 s: −119 km. ±60 s: +220 / −244 km. ±150 s: +500 / −656 km | +0.30, +0.28, +1.2, +1.1, +8.7, +6.2 m/s | probe P1 |
| Waiting one parking orbit (38 km, 2,858 s) | −40.3° (about −423 km) per orbit. 15 km orbit: −34.6°. 100 km orbit: −57.1° | time only | levers.py |
| Waiting on the surface, then ascend and `return` | −50.8°/h (−532 km/h) continuously: 50 km is 338 s of wait, and every longitude comes round within 7.08 h. The Mun is tidally locked, so the site and the burn point keep their place against Kerbin | time only | levers.py |
| target_pe (arrival from 12,519 km apoapsis) | Per km of periapsis: 5.6 km (15→20 km), 8.0 (25→30), 10.5 (30→35), 16.1 (35→40), 38.5 (40→45). At 50 km the craft skips out and lands about 400 km off, 3,234 s later | about nothing if chosen at ejection, but it is the entry's shape: peak 5.1 g at 15 km, 2.3 g at 40 km | probe P2 |
| Arrival inclination (#450's target_inc) | Across the track, not along it. It decides whether the track passes over the target's latitude. KSC at −0.1° is on any track's equator crossing, so 0 to 3° is enough there | tens of m/s for tens of degrees: a 31° arrival took 69.8 m/s normal on a 291 m/s ejection (KSP_spike KSP.log, 2026-11-03) | log |
| Course Correction halfway (15,000 s before entry), periapsis held | 2.6 km per m/s (arrival 17 s sooner per m/s) | about 0.4 m/s per km | probe P3 |
| Course Correction 6,000 s and 1,500 s before entry, periapsis held | 0.4 and 0.2 km per m/s | 2.5 to 5 m/s per km | probe P3 |
| Course Correction halfway, through the periapsis | prograde: 56 km per m/s (periapsis +3.6 km). Radial: −61 km per m/s (periapsis −6.0 km). Normal: 0.5 km per m/s | cheap in Δv, but it moves the entry out of target_pe's band | probe P3 |

**The cheapest lever per km is the ejection's timing.** Its cost is quadratic near the least-Δv point: about 0.003 m/s per km out to ±120 km, then about 0.005 m/s per km out to ±230 km. It works by trading a little Δv for a faster or slower fall from the Mun's distance. levers.py's patched-conic sums agree: 50 m/s of radial speed at the Mun's distance changes the arrival by 2,100 s for +1.5 m/s at ejection. Waiting is free in Δv but comes in 40° steps from orbit. On the surface it is continuous, so the Debrief's "no separate wait on the surface was flown (ejection points come once per Mun orbit)" is true of the windows and not of the landing. target_pe is a fine trim of about ±50 km inside a safe 25 to 38 km band, not a lever. The Course Correction is the dearest per km as soon as the periapsis is held.

**MechJeb doesn't aim a moon return at a landing point.** `OperationMoonReturn` takes only `Periapsis`, `Inclination` and `InclinationFlag` ([MechJeb2/Maneuver/OperationMoonReturn.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJeb2/Maneuver/OperationMoonReturn.cs)). `ReturnFromMoon.NextManeuver(centralMu, moonMu, moonR0, moonV0, moonSOI, r0, v0, peR, inc)` minimises the ejection burn with ALGLIB SQP. The burn time is a free variable bounded below at 0, so in effect it takes the next window ([MechJebLib/Maneuvers/ReturnFromMoon.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJebLib/Maneuvers/ReturnFromMoon.cs)); ADR 0010 already follows it here. `OperationCourseCorrection` aims a body at a periapsis and an inclination, or a vessel at a distance, and never at a surface point ([MechJeb2/Maneuver/OperationCourseCorrection.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJeb2/Maneuver/OperationCourseCorrection.cs)). MechJeb aims at a landing site only from orbit round the landing body:
- **Its deorbit** waits until the target is within 10° of the orbit normal, or 60 to 90° ahead with a plane change under 90°. It then burns one-shot to a periapsis of 0.9 × the body's radius, aimed at the target rotated by `360 * freefallTime / rotationPeriod`, and never checks the Δv available ([MechJeb2/LandingAutopilot/DeorbitBurn.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJeb2/LandingAutopilot/DeorbitBurn.cs)).
- **Its descent `CourseCorrection`** burns while the predicted landing is 150 m or more off. It linearises 1 m/s prograde, radial and normal against impact points computed from the orbit alone, without drag ([MechJeb2/LandingAutopilot/CourseCorrection.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJeb2/LandingAutopilot/CourseCorrection.cs), `ComputeCourseCorrection` in [MechJeb2/MechJebModuleLandingAutopilot.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJeb2/MechJebModuleLandingAutopilot.cs)).

**Trajectories plans nothing.** It predicts and draws the landing, keeps a target point, and gives a display-only navball hint ([src/Plugin/Predictor/TargetProfile.cs](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae8881a3b4658ff07fcb72040890d8b1/src/Plugin/Predictor/TargetProfile.cs), [src/Plugin/Display/NavBallOverlay.cs](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae8881a3b4658ff07fcb72040890d8b1/src/Plugin/Display/NavBallOverlay.cs)). There is no departure-time search and no burn planning. Neither mod solves this problem, so the timing search below is ours.

**Recommendation (new):** `return`'s `target` should drive the ejection as well as the correction. `RETURNBURN` would pick the window within a `max_wait` and the burn-time offset that put the landing on the target, at target_pe. The correction then trims only what execution left. Proposed ticket A.

## Q2. Where the search belongs, and what it costs

**In `return`, not in a Planning Tool.** The search is one-dimensional and smooth. The landing longitude moves about −0.37° per second of ejection offset, and windows step 40° apart. So:
1. One least-Δv solve and one prediction place every window's landing (they repeat at −40.3° per orbit).
2. The block picks the window whose landing is nearest the target within `max_wait`.
3. A few secant steps on the burn time within ±150 s finish the aim. Each step is a pinned `RETURNBURN` solve plus one prediction.

Waiting on the surface would need a Planning Tool ("if I launch at T, where do I land?"), because Claudia must choose the wait before `ascend`. But windows plus ejection timing reach every longitude within 7.1 h for about 1.2 m/s, so the surface wait buys nothing that `return`'s own `max_wait` doesn't. No new Planning Tool is needed. Claudia's Mission Goal "wait on the Mun until..." becomes `return(target ..., max_wait ...)`.

**The cost is in solves, not in the length of the path.** A prediction from the Mun's distance flies the vacuum leg as a conic (`Conic.TimeAtRadius`), so it costs the same as one from low orbit: 0.44 ms and about 200 steps in the tests. In the game it is about 10.5 ms (the ticket's figure: the adapter's drag-cube reads and Mono). A pinned `RETURNBURN` solve took 4 to 8 ms in the tests; the free solve took 91 ms in the game (KSP_spike KSP.log, 2026-11-03). Estimate per aim: 1 free solve plus about 2 windows × 3 secant steps of a pinned solve and a prediction. That is about 50 ms in the tests, and about 0.3 to 0.6 s in the game if a pinned solve costs about a third of a free one there (an estimate, not measured). It is over the 1.7 ms `FrameBudgetMs` either way, as `RETURNBURN` already is.

**#463's off-main-thread plan fits, with two additions** (scope change for #463):
- the landing-aimed `RETURNBURN` and `CORRECTIONBURN` calls (tickets A and B) are among the calls it moves to a worker;
- the aimed correction's kerboscript loop disappears into one Core call (ticket B). Today that loop is about 27 `REENTRYIMPACT` calls of 10.5 ms each, one per tick (about 280 ms of main-thread work spread over many frames), and #463 would otherwise have to make every one of them pending-then-result.

## Q3. The Course Correction's authority

**Why 27 predictions reached about 23 km and the block ended at 162 km.**

- **It isn't the predictor.** Later in the same flight, the predictor's post-burn landing was within about 3 km of the splashdown. #666's 238 km miss was the craft it was given (the whole stack), not the prediction. The probe's sensitivities are the physics, not prediction error.
- **It is mostly the search.** `return_aim_cost` lowers only the km to the target, so `claudia_descend` aims through the periapsis, the only cheap lever halfway (56 km per m/s). Holding the periapsis gives only 2.6 km per m/s. The flight arrived with a periapsis of 38.9 km against target_pe 35 km, the mark of a correction that steered by the periapsis. At 35 to 40 km the landing moves 16 km per km of periapsis, and at 40 to 45 km it moves 39 km per km. The skip-out cliff, where the landing jumps about 670 km, lies between 45 and 50 km. A search with 1 m/s first steps in that landscape can stop well short of 2 km. Its 23 km best came from the shallow side, where small errors cost the most.
- **The burn as flown turns the remainder into the miss.** At a 35 km periapsis, a correction error of 0.1 m/s prograde is 4.8 km on the ground and 0.1 m/s radial is 7.3 km. At 39 km these are about 1.5 to 2.5 times larger. `exec_node` stops under 0.1 m/s left and accepts up to 0.5 m/s, and 1° of pointing on a 30 m/s burn is another 0.5 m/s. Going from 23 km to 162 km needs about 1.5 to 2.5 m/s of error at that periapsis. That is more than the cutoff alone, but within a residual plus a few degrees of pointing on a correction of a few tens of m/s. The flight's correction Δv isn't in its result (it predates `correction_dv`) and its log is gone, so the split between search and burn can't be settled from the evidence. Ticket D logs what would settle it next time.

**The residual to expect from a correction that aims a landing point.** Roughly the sensitivity times the execution error. Halfway home, with the periapsis held at 35 km and `exec_node`'s 0.1 to 0.5 m/s, that is about 6 to 30 km if the correction steers through the periapsis, and about 2 to 4 km of along-track error per m/s of error if the periapsis is held. Aiming a periapsis is forgiving, because a 1 km periapsis error is about 10 km on the ground. Aiming a landing without bounding the periapsis is not.

**Should #449's solver take a landing point as a constraint? Yes, with the periapsis kept as an equality.** The landing point becomes one more equality on the along-track miss (km), solved by the same SQP over the burn's components and time. With target_pe still an equality, the solve can't buy along-track distance by shallowing the entry, and the arrival inclination can stay optional. Its authority is honest: a few tens of km for 10 m/s halfway, more the earlier it burns. The block says so with `predicted_error_km` when the departure (ticket A) left more than that. This replaces kerboscript's `return_aimed_correction` and its 27-call loop (ADR 0014). New, ticket B. It builds on #449's `CorrectionBurn` and is not in #449's scope.

## Q4. Deorbiting from a high orbit

**What the flight did.** After `return`, the orbit was a 12,519 km apoapsis by 38.9 km periapsis, with a 60,323 s period. Two figures from the evidence correct the ticket's premise:
- **The stage had about 545 m/s at the burn, not about 400.** The Mission's snapshot gives the stage 555 m/s (vacuum) at the start of the plan, and `tune_pe` used about 9 m/s.
- **About 143 m/s was left after the burn.** The Telemetry Checks during the burn read 351.7 m/s left at 203 m/s used, 333.2 at 222, 146.1 at 409 and 143 at 412, with the throttle at 0.1. `deorbit` reported 403 m/s used.

The Debrief's "Fuel is at 0 m/s" is the stage that `separate 1` dropped, not a burn that ran dry. So `deorbit_short_of_dv(402.4, ~545)` passed correctly. The check's real gaps are elsewhere:
- it passes whenever stock reports 0 or less (`dv_left > 0 and ...`);
- it leaves no margin;
- `deorbit` has no `max_dv`, so nothing caps a burn that is affordable but far dearer than the plan's budget. Claudia's Sequence Plan budgeted 150 m/s for "Kerbin capture and reentry", and the block spent 403 m/s without a word.

**Why the burn cost 402 m/s.** `deorbit`'s default `max_wait` is one Kerbin day (21,549 s), and that covered one periapsis pass. On the inbound leg a burn of tens of m/s drops the periapsis, but the landing then falls wherever the orbit's arrival time puts it. To land anywhere else the search can only burn just after periapsis: a capture to an orbit of a couple of hours, then a landing one lap later. `DeorbitBurn.Better` keeps the smallest error, then the earliest burn, and never compares Δv. The probe models an orbit of the flight's shape, with periapsis at UT 21,000 s, swept over 12 target longitudes 30° apart:

| Target longitude (model) | Burn | Δv | Fall | Result |
|---|---|---|---|---|
| 0° (the natural landing) | 8,925 s before periapsis | 33 m/s | 9,152 s | lands on target |
| −30°, −60°, −90° | inbound | 18 to 19 m/s | 17,000 s | **fails**: best 298 to 926 km off |
| +30° | 789 s after periapsis | 159 m/s | 18,281 s | fails at 11.8 km |
| +60° to +150° | 495 to 712 s after periapsis | 172 to 233 m/s | 11,000 to 16,600 s | lands on target |
| −120° | 276 s after periapsis | **391 m/s** | **5,379 s** | lands on target, like the flight (402 m/s, splashdown 5,280 s after the burn) |
| −150°, −180° | just after periapsis | 313, 899 m/s | 7,418, 1,105 s | lands on target |

**The plan-to-flown gap of 282 km isn't explained by execution.** The probe flew the −120° plan as a finite burn: 23 s on a Terrier from 3.726 t, led by half its Δv, steered by either a fixed direction or the node's remaining burn as KSP gives it. Either way the landing moves 0.1 to 0.3 km. Its sensitivities are 23 km per 1° of pointing, 3.9 km per second late, 0.3 km per m/s and 1.1 km per 1% short. The flown burn's 403 m/s matched the plan's 403.1 m/s. A 282 km miss would need about 12° of pointing error.

Untested hypotheses:
- **KSP's inverse rotation.** Below a body's inverse-rotation threshold the game's frame turns with the planet. `DEORBITBURN` read its frame at 11,700 km, and the burn was flown at about 100 km.
- **A difference between the craft `DEORBITBURN` modelled and the capsule that flew.** The flight predates #462 and #476.

The evidence that would settle it (the logged post-burn orbit and the frame) wasn't kept. Ticket D logs it.

**What `deorbit` should do when one pass falls inside `max_wait`:**

- **Lower the apoapsis first?** Only as a planned step with its own Δv, never folded silently into the deorbit. From this orbit, capture to a 3 h orbit costs about 184 m/s at periapsis. A deorbit from there costs about 28 m/s and has several passes a day to choose from, so about 210 m/s against the flight's 402. That is still far dearer than aiming at departure (Q1: about 1.2 m/s).
- **Wait several passes?** Each pass of this orbit is 16.8 h, and it moves the natural landing about 66 to 72° (about 700 to 760 km; the flight's 60,323 s period gives 72°, the model's 60,725 s gives 66°). The cheap band around each landing is narrower than that, so a cheap pass near a given target is days away. Not useful as a default.
- **Refuse with a reason: yes, by default.** The search should prefer the cheapest burn within accuracy_km, not the earliest, because one pass ranges from 18 to 900 m/s. `deorbit` should gain `max_dv`, as `tune_pe` has. Its default would be the delta-v left less the Craft Check's 15% margin. If stock can't say what is left, it should be required rather than passed. A burn over it fails before burning: "the best burn lands within accuracy_km but costs <x> m/s, over max_dv <y>; the next pass is at <ut>". The check moves into `DeorbitBurn` (ADR 0014). New, ticket C.
- **Should the capture be a block of its own? No, not now.** With ticket A the common case never needs one. The capture is one burn that `exec_node` can fly from a node the plan sets, and `tune_pe` already shows the shape. Revisit if Missions keep needing it.

## Q5. What's left of prediction accuracy

| Path | State in the merged code | In scope of |
|---|---|---|
| Long coasts from a moon | Exact: the vacuum leg is a conic to the top of the atmosphere, and `return`'s query hands it the patch round Kerbin (`from_ut`). Cost and accuracy are independent of the coast's length. Error comes from what was flown, not from the prediction. | done (#282, #450) |
| Kerbin's rotation over hours | Analytic: `BodyFrame.Ground` turns the body by `360 / RotationPeriod × (ut − Ut)`, as MechJeb's `ReentrySimulation` (`ReferenceFrame.ToAbsolute`) and Trajectories' `CalculateRotatedPosition` do ([MechJeb2/ReentrySimulation.cs](https://github.com/MuMech/MechJeb2/blob/0632b71c27b568939ae726d20868cdf54b72fc1f/MechJeb2/ReentrySimulation.cs), [src/Plugin/Predictor/Trajectory.cs](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae8881a3b4658ff07fcb72040890d8b1/src/Plugin/Predictor/Trajectory.cs)). Not yet shown: that the frame read near KSP's inverse-rotation threshold agrees with one read high up. See Q4. | new: ticket D's check |
| Steep entries (periapsis 15 to 30 km) | Insensitive: 5.6 to 8 km on the ground per km of periapsis. `DeorbitBurn` aims 21 km for this reason. | done (#282) |
| Shallow entries (35 km and up) | Sensitive: 16 km per km at 35 to 40 km, 39 km per km at 40 to 45 km, skip-out by 50 km. `return`'s default target_pe of 35 km sits on that slope. | new: tickets A and B hold the periapsis. Lowering target_pe's default to about 30 km is worth weighing in A. |
| Parachutes, lift, heat-shield drop | Modelled (#462). Nothing sends `shield_drop` yet (#476), and `deorbit` with no `separate` models the whole stack (#666). | #462, #476, #666 |
| Execution of the burn before entry | The dominant term. No block reports it. | new: ticket D. #317's trim is the remedy. |

**#462 covers** the atmosphere's model: the adaptive step, lift, parachutes and the shield. **New in scope** is an error budget for a return path: the blocks report, besides `predicted_error_km`, the spread they expect from executing the burn (sensitivity × `exec_node`'s residual). Claudia can then see that "within 15 km" from halfway at a 39 km periapsis is a coin toss. This folds into tickets A and B as one extra result, not a ticket of its own.

## Q6. What Claudia's guidance should say

For a Mission Goal that names a landing point after a moon return, such as "splash down within 50 km of KSC":
- **Once ticket A ships:** pass the target to `return` with a `max_wait` of 8 h. The block waits for the window and times the ejection itself. Plan no separate wait on the surface or in orbit; it buys nothing, at about 1.2 m/s. Budget the Course Correction's few m/s, and expect `predicted_error_km` of a few km to a few tens of km.
- **Before ticket A:** no block or Planning Tool can say where a given departure lands. Don't promise a timed departure in the Mission Plan. Say in the plan that the landing point is aimed only by the correction, and expect tens to hundreds of km (this flight: 162 km).
- **If the return comes in far off:** don't deorbit from the return's orbit on hope. Moving the landing from there costs 150 to 900 m/s for one pass. Budget that delta-v explicitly in the Sequence Plan, or accept the landing where it falls. Splashing down safely 263 km away beat spending the stage on a burn that missed anyway.

This is a few lines in `return`'s and `deorbit`'s `//|` docs, plus the base prompt's Mission-Goal rule; it counts against the prefix budget. Proposed ticket E, after A.

## Worked example: m20261028-122945-db4b against the findings

| Flight figure | Finding |
|---|---|
| Debrief: "no separate wait on the surface was flown (ejection points come once per Mun orbit)" | True of the windows, but the landing moves continuously with a surface wait (−50.8°/h) or with ejection timing (about 115 km per 30 s for 0.3 m/s). No tool offered either. (Q1) |
| `return` ended at a periapsis of 38.9 km against target_pe 35 km, predicted −2.11°, −89.91°, 162 km | The aimed correction steered through the periapsis onto the 16 to 39 km-per-km slope. From 23 km to 162 km takes about 1.5 to 2.5 m/s of error there. (Q3) |
| 27 `REENTRYIMPACT` calls at about 10.5 ms | About 280 ms of main-thread prediction in a kerboscript loop: ticket B moves it into one Core call, which #463 then moves off the main thread. (Q2) |
| `tune_pe` to 100 km, then `deorbit` with one pass in `max_wait`: −402.4 m/s prograde, 24.0 normal, just after periapsis, predicted 0.00 km | A capture-and-lap burn: the model's −120° row is 391 m/s with a fall of 5,379 s, against the flight's 5,280 s. Cheaper options were the natural landing (tens of m/s, wrong place) or a capture first (about 210 m/s). (Q4) |
| "A ~400 m/s stage" got through `deorbit_short_of_dv` | The stage had about 545 m/s, the burn took 403 and left about 143: the check passed correctly. Its gaps are no margin, no `max_dv` against the plan's 150 m/s budget, and passing when stock reports 0. (Q4) |
| Flown burn predicted 282 km off; splashdown at 0.85°, −99.64°, 263 km from KSC; post-burn prediction about 3 km from the splashdown | The predictor was right about the orbit flown. The 282 km plan-to-flown gap is 0.1 to 0.3 km in the finite-burn model, so it is unexplained. Ticket D logs what would explain it. (Q4, Q5) |

## Recommendations by question

| Q | Recommendation | Scope |
|---|---|---|
| 1 | `return`'s target aims the ejection: window within `max_wait`, burn-time offset | new: A |
| 2 | In `return` (no Planning Tool); about 0.3 to 0.6 s in game, so on #463's worker | new: A; #463 grows (below) |
| 3 | Landing as an extra equality in `CorrectionBurn`, periapsis held; replaces the kerboscript loop | new: B (builds on #449) |
| 4 | `max_dv` and the cheapest burn within accuracy; refuse over budget, naming the next pass; no capture block | new: C |
| 4, 5 | Log the planned and flown orbits and the frame at the burn; explain the 282 km | new: D |
| 5 | Error budget as one result in A and B; shallow-entry default worth weighing in A | A, B; #462 done; #476, #666 open |
| 6 | Guidance for landing-point goals after a moon return | new: E (after A) |

**#317 (deorbit's correction burn before the atmosphere): scope changes, priority up.** The findings make it the remedy for the plan-to-flown gap. Execution error times sensitivity is the dominant term, and the predictor is good enough to trim against: about 3 km after the burn. It should use the same landing-constrained `CorrectionBurn` as ticket B (periapsis held at the entry's, landing as an equality), not a solver of its own. It should apply to `return`'s arrival as well as to `deorbit`, since a late trim 1,500 s before entry still moves the landing 9.5 km per m/s through the periapsis. So #317 depends on B, and its brief should say "trim with CORRECTIONBURN's landing mode", not "a new deorbit trim search".

**#463 (reentry predictions off the main thread): scope grows, approach unchanged.** Add the landing-aimed `RETURNBURN` (A) and `CORRECTIONBURN` (B) to the calls moved to a worker. Drop the per-call work for `return`'s 27-call kerboscript loop, which B replaces. Nothing found argues for a different threading design. The cost is in solves, not in path length.

## Proposed tickets

Not filed: for triage on #482. Each touches `kerboscript/library/` only as its Block migration line says.

**A. `return`'s target aims the ejection: it picks the window and times the burn so the landing falls on the target**
- **Area:** Return & Reentry
- **Block migration:** the window choice and burn-time aim are new logic in Core's `ReturnBurn` (a landing target and `max_wait` on `ReturnAsk`, the predictor in the loop). No Kerboscript logic moves: `return.ks` only passes the target and `max_wait`, and steering, warp and staging stay in Kerboscript. There is no parity test, since the behaviour is new; Core tests in `ReturnBurnTests` cover it. Player flies `tests/blocks/return_target` (Mun to KSC).
- **Brief:** With `target` given, `RETURNBURN` predicts each window's landing within a new `max_wait` (default "auto", about 7.1 h at the Mun: one turn of the landing longitude). It picks the nearest, and secant-solves the burn time within ±150 s so the predicted landing is on the target at target_pe. Measured: ±60 s off the least-Δv point moves the landing about ±230 km for about 1.2 m/s. Report the extra Δv and the expected spread (sensitivity × `exec_node`'s 0.5 m/s residual) in the result. Weigh lowering target_pe's default from 35 km to about 30 km: 10.5 km on the ground per km of periapsis against 16. One `//|` change at most: the prefix cost is in the ticket's allowance.

**B. The aimed Course Correction moves into Core: `CORRECTIONBURN` takes a landing target with the periapsis held**
- **Area:** Return & Reentry
- **Block migration:** `return_aimed_correction` and `return_aim_cost` (`return.ks`) move to Core's `CorrectionBurn` as a landing equality beside target_pe. The parity test is `AimedCorrectionParityTests`: the kerboscript descent and the C# solve on the same arrivals where the periapsis stays within 1 km, landings within 2 km of each other. The Kerboscript and the test are deleted in this ticket. Player flies `tests/blocks/return_target`.
- **Brief:** Today the correction lowers km-to-target alone with `claudia_descend`. It steers through the periapsis (56 km per m/s halfway), so a big miss ends with a shallow entry on the 16 to 39 km-per-km slope; in the flight that gave 162 km. Add the along-track landing miss as an equality to the #449 SQP with target_pe still an equality, so its authority is the honest 2.6 km per m/s halfway. One block call replaces about 27 `REENTRYIMPACT` calls. Report the unreached remainder in `predicted_error_km`. #317 builds its trim on this.

**C. `deorbit` gains `max_dv` and prefers the cheapest burn: it refuses a capture-and-lap burn over budget instead of flying it**
- **Area:** Return & Reentry
- **Block migration:** `deorbit_short_of_dv` (`deorbit.ks`) moves to Core's `DeorbitBurn` as a `MaxDv` on `DeorbitAsk`. The parity test is `DeorbitShortOfDvParityTests`; it and the Kerboscript function are deleted in this ticket. Player flies `tests/blocks/deorbit_kerbin`, plus a new `deorbit_high_orbit` scenario from a Mun-return orbit.
- **Brief:**
  - **Choose by cost.** `DeorbitBurn.Better` keeps the smallest error, then the earliest burn. From a Mun-return orbit one pass ranges from 18 to 900 m/s (the probe's sweep). Among burns within accuracy_km, choose the cheapest.
  - **Add `max_dv`.** Default "auto": the delta-v left less the Craft Check's 15% margin. When stock can't say what is left, require it rather than passing, as `deorbit_short_of_dv` does today when it reads 0.
  - **Fail before burning when over.** The reason gives the cost and the next pass's UT.
  - **Correct the record.** The flight's 402 m/s burn on a ~545 m/s stage was within the delta-v left, so today's check passed correctly. This ticket addresses the missing budget, not a broken check.

**D. Log the planned against the flown orbit at deorbit and return burns, to explain plan-to-flown misses like the flight's 282 km**
- **Area:** Return & Reentry
- **Block migration:** none. Log lines only, in Core's `DEORBITBURN`, `REENTRYIMPACT` and `CORRECTIONBURN` block-call logging; no `//|` change and no prefix cost.
- **Brief:**
  - **Log the plan and the result.** Log the planned post-burn orbit (periapsis, apoapsis, period, the periapsis's UT) and the frame (`LonOffset`, read altitude) when `DEORBITBURN` plans. Log the same when `REENTRYIMPACT` predicts after the burn.
  - **Check the frame in game.** Fly a deorbit from above and from below KSP's inverse-rotation threshold, and compare the frames.
  - **Why.** The flight planned 0.00 km and flew 282 km off. In the finite-burn model a 23 s burn moves the landing 0.1 to 0.3 km, and 1° of pointing moves it 23 km, so neither execution nor the predictor (3 km after the burn) explains it.
  - **If the frame is the cause, fix it here.** A frame fix stays in Core.

**E. Guidance: a Mission Goal that names a landing point after a moon return**
- **Area:** Claudia's Judgment
- **Block migration:** none. Prompt and `//|` text only (`return.ks`, `deorbit.ks` docs); goldens updated in `BlockDocsSnapshotTests`.
- **Brief:** After A, the guidance says to pass the target and a `max_wait` to `return`, with no separate wait; budget a few m/s; expect a few to tens of km. If the arrival is far off, deorbiting from the return's orbit costs 150 to 900 m/s for one pass: budget it explicitly or accept the landing. Until A ships, say that no tool aims the departure and don't promise a timed wait. Keep it within a small prefix allowance by cutting words in `return`'s target paragraph, which today says "The landing is hard to steer from here".
