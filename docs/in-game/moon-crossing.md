# A parking orbit that crosses a moon: what to watch

#867: in flight m20261107-055424-908b, `return` from Ike left a 100 × 2,144 km Duna orbit. Its apoapsis, 2,464 km from Duna's centre, reached into Ike's sphere of influence (down to 2,054 km from the centre, 1,734 km up). The plan waited there about 12 million s for Kerbin's window, and Ike flung the craft out of Duna. Now one Core check judges whether an orbit crosses a moon over a wait:

- **Plan time.** A Sequence Plan that waits for its Due Time in such an orbit goes back to Claudia once, with the fix: `change_orbit` `set_ap` 1,520 km, about 26 m/s (a circle costs about 260 m/s). If it comes back still waiting there, its card has a Blocker, the mod won't warp to its Due Time, and Auto Mode waits for you.
- **The Debrief.** After a run that leaves the craft in such an orbit, the Debrief's events have an `orbit crosses a moon` line with the moon, the time and the fix. The rating is Claudia's as before.
- **The next-patch watch.** While the Mission's craft warps with no script running (a plan pending, Auto Mode, the mod's warp to a Due Time, or your own warp, in any scene), the mod reads the craft's path and drops the warp one loop (at least an hour) before a moon nobody planned. You get a screen message and a Mission log line. `transfer_to`'s own warp to a far ejection is watched the same way: the block fails with `would meet Ike at UT ...` instead of burning.
- **`transfer_to`'s refusal.** When the ejection meets a moon and the orbit itself crosses it, the refusal adds `and the orbit you're in crosses Ike's SOI: lower its apoapsis below 1,734 km (about 26 m/s) before waiting`.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look"). `ClaudiaKerman.Core` and `ClaudiaKerman.Adapter` changed, so install both. No Kerboscript changed.

To put a craft in 908b's orbit, use the cheat menu (Alt+F12, Cheats, Set Orbit): body Duna, semi-major axis 1,442,000 m, eccentricity 0.7087, inclination 0. Any craft with about 600 m/s will do; `tests/blocks/transfer_to_ike_crossing`'s craft is one.

## Check 1: the block test

1. Pick `transfer_to_ike_crossing` and press **Run**. Don't touch anything.
2. It ends with `PASS transfer_to_ike_crossing`: `success` false, no burn, still round Duna, and the reason names Ike. Either:
   - the block warped toward the ejection and `KSP.log` has `Next-patch watch: would meet Ike at UT ...; warp dropped before it`, before any `On-Rails SOI Transition from Duna to Ike`, then the block fails with `would meet Ike at UT ...`; or
   - it refused at once with `... meets Ike on its way out of Duna's sphere of influence and the orbit you're in crosses Ike's SOI: lower its apoapsis below 1,734 km (about 26 m/s) before waiting`.
3. If the craft passes into Ike's sphere of influence at all, it's a failure: tell the ticket the UT.

## Check 2: the plan is blocked

1. With the craft in 908b's orbit and Kerbin's window months away, start a Mission: "Return to Kerbin". Approve the Mission Plan.
2. If Claudia's first Sequence Plan waits for a Due Time where the craft is, the Mission log says `The craft would wait in an orbit that crosses Ike's sphere of influence; Claudia is revising it.` Her next plan should fly `change_orbit` with `set_ap` at about 1,520 km first, with no Due Time.
3. If she sends a waiting plan again, its card ends with `Blocker: the orbit round Duna crosses Ike's sphere of influence (from 1,734 km up) about UT ...: fly change_orbit set_ap 1,520 km (about 26 m/s; a circle costs about 260 m/s) before waiting.`
4. Press **Warp to Due Time**: the mod doesn't warp, and the log says `... The mod won't warp to the Due Time: Re-plan, or fly the fix yourself first.`
5. Tick Auto Mode: it says `Auto Mode is waiting for you: a Blocker: the orbit round Duna crosses Ike's ...`.

Pass: the plan is blocked, naming Ike and a fix of about 25 m/s (26 here).

## Check 3: after the fix, the wait is allowed

1. Fly the fix: approve Claudia's `change_orbit` plan, or lower the apoapsis by hand below 1,734 km (about 1,520 km).
2. The Debrief after Claudia's fix has no `orbit crosses a moon` event.
3. The next plan that waits for Kerbin's window has no Blocker, and **Warp to Due Time** warps.

## Check 4: a forced warp drops before Ike

1. Put the craft back in 908b's orbit (cheat menu), with a plan pending or none (no script running).
2. Warp yourself with the warp arrows at the top rate, in Flight, then again from the Tracking Station.
3. The warp drops to 1x about one loop (19,800 s, about 5.5 h of game time) before Ike, and before any `On-Rails SOI Transition from Duna to Ike` in `KSP.log`. A screen message reads `Warp stopped: the craft would meet Ike's sphere of influence at UT ..., in ... Fly change_orbit set_ap 1,520 km (...) first, or Re-plan.`, the Mission log has the same line, and `KSP.log` has it after `Next-patch watch:` with the check count.
4. Warp again: it isn't dropped a second time for the same meeting.

Pass: the warp drops and the request is raised before Ike's sphere of influence.

## Check 5: the Debrief's fact

1. Fly any sequence that ends in a crossing orbit (for example `return` from Ike with its default `target_pe`).
2. Its Debrief card's events have `orbit crosses a moon: The orbit round Duna crosses Ike's sphere of influence ...; a plan that waits in it is blocked`. The rating is whatever Claudia gave.

## What the watch costs

One check follows the path 6 h plus one loop ahead: in Core about 0.2 ms for 908b's orbit with Ike in reach (`MoonCrossingTests`, budget 2 ms). Checks come once per 6 h of game time while nothing is found, and when a meeting is found, again only a loop before it. At KSP's top rate (100,000x) that is about one check every 0.2 s of real time. Note it on the ticket if the game stutters at high warp with the Mission's craft in a high orbit.

## Report

Tell the ticket which way Check 1 ended, the fix Claudia planned in Check 2 (its `set_ap` and m/s), and in Check 4 the UT of the drop against the UT KSP gives for the SOI change (warp on past the drop to see it).
