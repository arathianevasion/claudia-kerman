# Return: the periapsis asked is the periapsis flown

#759: `return` from a moon used to stall when asked for a high periapsis at its planet. The least burn sits where the path's apoapsis meets the moon's sphere of influence, and there the solve stops 1 to 8 km off. It now holds the burn about 1.5 m/s clear of that edge, finishes a solve that stops short, and fails with the figure if the path flown ends over 1 km from what was asked. Core tests cover the solve from the Ike Mission's real orbit; these checks need flight.

Each `RETURNBURN` line in `KSP.log` gives the node and when the craft leaves the moon. A solve that needed a finish adds `(the SQP stopped short: finished by a search on the prograde component)` or `(... a time shift)`.

## A high periapsis from Ike

Use `tests/blocks/return_ike`'s craft, or any craft in a low orbit round Ike (about 20 km, inclined about 18° if you can). Run `local res is return(lexicon("target_pe", 300000)). addons:claudia:report(res).`

1. The block doesn't refuse: there is no `found no return` in the report.
2. The `RETURNBURN` line gives about 116 m/s, with the craft leaving Ike about 8,000 to 9,000 s after the node. A figure near 114.3 m/s with about 10,500 s means it is back at the edge.
3. The craft leaves Ike's sphere of influence on that path, and a correction follows if the path is over 1 km off.
4. The report has `success: true` and `planet_pe` from 299,000 to 301,000.

Repeat with `target_pe` 310000 and 323095.

## The 35 km return is unchanged

Run `tests/blocks/return_ike` and `tests/blocks/return` as they are. Both report `success: true`, with `planet_pe` from 34,000 to 36,000 and `dv_used` as before (about 124 m/s from Ike's low orbit). These are the flights that show whether 1 km is a fair test of the path flown. If either fails only on this new check, note the `planet_pe` it gives and tell the ticket.

## A miss is a failure

Run `local res is return(lexicon("target_pe", 300000, "correction", false)). addons:claudia:report(res).` from Ike. With no correction the ejection alone usually ends a few km off.

1. If `planet_pe` is over 1 km from 300,000: `success: false`, and the reason reads `the return flown arrives at periapsis <m>, not 300000 m`. `planet_pe` is in the report with the same figure.
2. If it happens to land within 1 km: `success: true`.
3. In either case the craft is still on its way to Duna, with nothing burned after the failure.

## With a landing target

Run `tests/blocks/return_target`. It reports `success: true` even with `planet_pe` far from `target_pe`: with a target, the correction aims the landing and `target_pe` only draws the path, so the new check doesn't apply.
