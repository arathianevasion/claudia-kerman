# Return: the periapsis asked is the periapsis flown

#759: `return` from a moon used to stall when asked for a high periapsis at its planet. The least burn sits where the path's apoapsis meets the moon's sphere of influence, and there the solve stops 1 to 8 km off. It now holds the burn about 1.5 m/s clear of that edge, finishes a solve that stops short, and fails with the figure if the path flown ends over 1 km from what was asked. Core tests cover the solve from the Ike Mission's real orbit; these checks need flight.

Each `RETURNBURN` line in `KSP.log` gives the node and when the craft leaves the moon. A solve that needed a finish adds `(the SQP stopped short: finished by a search on the prograde component)` or `(... a time shift)`. One that names `the inertial solve, without the bound` has no margin from the edge: if you see it from a low orbit, note the node and tell the ticket.

## A high periapsis from Ike

Use `tests/blocks/return_ike`'s craft, or any craft in a low orbit round Ike (about 20 km, inclined about 18° if you can). Run `local res is return(lexicon("target_pe", 300000)). addons:claudia:report(res).`

1. The block doesn't refuse: there is no `found no return` in its Script Result.
2. The `RETURNBURN` line gives about 116 m/s, with the craft leaving Ike about 8,000 to 9,000 s after the node. A figure near 114.3 m/s with about 10,500 s means it is back at the edge.
3. The craft leaves Ike's sphere of influence on that path, and a correction follows if the path is over 1 km off.
4. The Script Result has `success: true` and `planet_pe` from 299,000 to 301,000.

Repeat with `target_pe` 310000 and 323095.

## The 35 km return is unchanged

Run `tests/blocks/return_ike` and `tests/blocks/return` as they are. Both report `success: true`, with `planet_pe` from 34,000 to 36,000 and `dv_used` as before (about 124 m/s from Ike's low orbit). These are the flights that show whether 1 km is a fair test of the path flown. If either fails only on this new check, note the `planet_pe` it gives and tell the ticket.

## A miss is a failure

Run `local res is return(lexicon("target_pe", 300000, "correction", false)). addons:claudia:report(res).` from Ike. With no correction the ejection alone usually ends a few km off.

1. If `planet_pe` is over 1 km from 300,000: `success: false`, and the reason reads `the return flown arrives at periapsis <m>, not 300000 m`. `planet_pe` is in the Script Result with the same figure.
2. If it happens to land within 1 km: `success: true`.
3. In either case the craft is still on its way to Duna, with nothing burned after the failure.

## With a landing target

#792: the ejection now aims too. **Block migration:** the window choice and burn-time aim are new logic in Core (`ReturnAim`); `return.ks` only passes the target and `max_wait`; no parity test, as nothing moved.

1. Run `tests/blocks/return_target`, from the Mun to KSC. The `RETURNBURN` line in `KSP.log` ends `aimed <km> km from the target, +<m/s> m/s, spread <km> km`: expect about 1 km, under 2 m/s. It takes that node up to about 7.1 h on.
2. Note `predicted_error_km` and `aim_spread_km` from the Script Result against `target_km` and `miss_km`, where it came down. Tell the ticket the figures: these are what say whether the craft's drag and the burn's error are what the aim assumes. Expect `correction_dv` of a few m/s at most; tens would mean the burn was far from the plan.
3. From Minmus, give the same target: `local res is return(lexicon("target", "ksc")). addons:claudia:report(res).` `predicted_error_km` is up to about 60 km depending on where Minmus is in its inclined orbit, and all of it across the track.
4. Approve a plan with `return(lexicon("target", "ksc"))`: a too-short `expected_duration_s` goes back asking for about 7 h. If the ejection is over 7 h off, the sequence gets a Due Time an hour before it.

5. #880: from an inclined Mun orbit (66° or a polar one, with chutes and some spare delta-v), run `local res is return(). addons:claudia:report(res).`, stating no target. It lands within about 50 km of KSC. The `RETURNBURN` line's plan and the Script Result's `aim_note` state the wait, in hours, the delta-v and the expected distance from the target (`predicted_error_km` once the correction is flown). Use the craft from `tests/blocks/return_target`. If no window fits, it says `no window it can afford lands within 50 km` and takes #816's rule; with plenty of delta-v it leaves sooner on a costlier window. Run from an approved plan with a window recorded, it keeps that window if it still lands within about 100 km and is affordable, so its wait matches the plan's.
6. #880: fly `transfer_to(lexicon("target", "Kerbin", "target_pe", 30000))` home from Duna with no place named in the Mission Goal, then `reentry`. The window is the one `plan_transfer` showed. The `ENTRYTRIM` line in `KSP.log` aims at KSC (not `no landing target`), and its burn stays within the delta-v above the reserve. #915: on entering Kerbin's sphere of influence `transfer_to`'s Course Correction also aims at KSC: the `block call TRANSFERSTEP` line for that step in `KSP.log` ends `landing aim: <n> km from the target, periapsis <p> km, <d> m/s`, with `<p>` inside `pe_tolerance` of `target_pe` and `<d>` within the delta-v above the reserve; or it ends `landing aim: target_pe only: <why>` and the correction holds only `target_pe`. Far out on the Sun's patch, and in the finer trim after it, the corrections still hold only `target_pe`.

The next paragraph holds as before. It reports `success: true` even with `planet_pe` far from `target_pe`: with a target, the correction aims the landing and `target_pe` only draws the path, so the new check doesn't apply.
