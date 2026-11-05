# Direct route: from a moon to another planet as one leg

#760: from a moon's orbit, `plan_transfer` to another planet of its star gives two rows in the same Transfer Window. `two_step` returns to the planet's low orbit, captures, and ejects from there. `direct` waits at the moon, falls to the same low periapsis at the time the moon's place lines that periapsis up with the ejection, and ejects at the periapsis, so it flies no capture. The direct route is flown by the existing blocks, `return` then `transfer_to`, each with its `window` from the row. No block or option changed.

Core tests plan this from Ike's and the Mun's low orbits and fly it on the patched conics (`DirectRouteTests`). The figures below need flight: real burns, a real turn before each, and KSP's own patches.

The expected figures from a 20 km orbit round Ike to Kerbin: `return_dv` about 122 m/s, a fall reaching Duna's 60 km about 283 m/s over circular, `direct` about 434 m/s in total, `two_step` about 992 m/s. The saving is about 558 m/s: twice that capture, less a few m/s for the asymptote's tilt and the moon's phase. Ike's eccentric orbit moves the capture from 278 to 296 m/s, and the window moves `ejection_dv` from about 310 to 375 m/s.

## Flying the direct row from Ike

Use `tests/blocks/return_ike`'s craft, or any craft in a low orbit round Ike with about 500 m/s. Its stages must not fire a decoupler that the second burn needs.

1. Ask `plan_transfer` with `{"target":"Kerbin"}`. The result has `direct`, `two_step` and `pick`. `arrival_note` says there is no capture burn, and `direct` has no `capture_dv`. Note `return_pe`, `target_pe`, and from `direct` its `return_window`, `depart_ut`, `eject_ut`, `eject_window` and `ejection_dv`.
2. Run the row's `fly` text: `local res is return(lexicon("target_pe", <return_pe>, "window", <return_window>)). addons:claudia:report(res).` It warps to the window and burns about `return_dv`. Check that `dv_used` is within 5 m/s of `return_dv` and the burn comes within about 200 s of `depart_ut`, then that `success` is true and `planet_pe` is within 1 km of `return_pe`.
3. If `ship:body` is still Ike, coast until it is Duna. `status` must be `ORBITING`, on a closed orbit reaching back out to about Ike's height.
4. Run `local res is transfer_to(lexicon("target", "Kerbin", "target_pe", <target_pe>, "window", <eject_window>)). addons:claudia:report(res).`
5. Check that the node sits at or within a few degrees of the orbit's periapsis, and `departure_ut` is `eject_ut` or a whole number of orbits after it (about 27,000 s each). `ejection_node_dv` should be within about 15% of the row's `ejection_dv`.
6. The craft leaves Duna's sphere of influence, and `success` is true with an encounter with Kerbin (`encounter_ut` above 0). `correction_dv` is under about 20 m/s.

Tell the ticket the figures from steps 2 and 5, and the time between `eject_ut` and `departure_ut`.

## Things to watch

- **Waiting on the fall.** `transfer_to` re-solves the window and may take a later pass of the periapsis. Each pass reaches back out to Ike's height, and a pass that meets Ike's sphere of influence changes the orbit. If that happens, tell the ticket how many passes it waited.
- **Passing Ike on the way out.** `transfer_to` refuses an ejection that meets a moon on the way out of the planet's sphere of influence, with `meets <moon> on its way out`. It's a real refusal: note the date.

## The two-step row is as it was

Fly `two_step`'s `fly` text from the same orbit. `return` with no window, `circularize(lexicon("at", "pe"))`, then `transfer_to` with `eject_window`. The total is near the row's `total_dv`.

## Other moons

From a 30 km Mun orbit, ask `{"target":"Duna","target_pe":60000}`. Both rows carry `capture_dv`. The saving is about 1,590 m/s, not twice the 852 m/s capture: that window's asymptote is about 5° out of the Mun's plane. Fly it as above if you have a craft for it.
