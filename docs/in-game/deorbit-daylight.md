# deorbit: the Daylight landing setting picks a lit pass

In-game check for #957 (Core only: `DeorbitBurn` skips a pass whose sun at the target, at its `landing_ut`, is under `LandingSiteSearch.MinSunElevationDeg`, 20 degrees, when the Daylight landing Mission Setting is on). The agent run that wrote this did not launch KSP.

Fly a descent like m20261109-140705-ae3a: a near-equatorial Duna orbit (about 2 degrees inclination), Daylight landing **on**.

1. Ask `find_landing_site` for a daylight site near the equator. Note the site's `burn_ut`, `landing_ut`, `sun_elevation_deg` and `max_wait_s`.
2. Run `deorbit` to that site, passing the site's `max_wait_s`. The burn is at (or within a pass of) the site's `burn_ut`, not at the nearest pass. In `KSP.log` the `DEORBITBURN` line's `landing_ut` is within one orbit of the site's `landing_ut`.
3. After the landing, the Debrief's landing `sun_elevation_deg` is over 20 and there is no "landed in the dark" event.
4. Repeat with a `max_wait` too short to reach a lit pass (a few thousand seconds on this orbit). `deorbit` fails before any burn (`burn_ut` -1, no node left) with `no pass in the next <h> h lands in daylight: the best sun at the target is <n> degrees up, under 20; allow a longer max_wait or pick a different site`.
5. Turn Daylight landing **off** and run step 2 again with `max_wait` "auto" (#974: with the site's `max_wait_s` it flies the site's pass): `deorbit` burns at the nearest pass as before.

## #970: the craft deorbit aims for, and what "lit" means

Flight m20261109-200745-96f6 had Daylight landing on (saved `setting.daylight_landing = true`; its prompts said on), so #957's rule ran. The pass `deorbit` flew landed, at the target, with the sun about 22.8 degrees up at dawn: lit by the 20 degree rule. It was aimed for the craft without its stack (19 t) while the script's `reentry` kept it (`separate` "none", 41 t), so the craft flew 95 km further west, where the sun was 6.2 degrees. Now `deorbit` (and `return`) aim for the craft the running script's `reentry` flies (Core reads its `separate` and `jettison_stage`). The agent run that wrote this did not launch KSP.

6. Fly a Duna descent whose script is `deorbit(...)` then `reentry(lexicon("separate", "none", "jettison_stage", "none"))` (the plan needs a custom_script_reason since #971), on a craft with a stack below its heat shield. In `KSP.log` the `ENTRYCRAFT` line reads `as it is, its stack kept, shield kept, <whole mass> t`, the `DEORBITBURN` line's `craft` mass is the whole craft, and `reentry`'s ENTRYFINISH line has no "the landing was aimed for the craft ..." part.
7. The craft lands within about accuracy_km (plus the few km chutes and lift add) of the `DEORBITBURN` landing, and the Debrief's landing `sun_elevation_deg` is within a few degrees of the sun at the target at the predicted `landing_ut`.
8. Run the same script with `reentry()` (no `separate`): `ENTRYCRAFT` reads `without its stack (...)` as before.
9. Run it with a `stage.` (or a `drop_parts` call) between `deorbit` and `reentry(lexicon("separate", "none"))`: `ENTRYCRAFT` reads `without its stack (...)`, since the script sheds the stack itself and flies light.

## #974: deorbit flies find_landing_site's pass

On m20261109-200745-96f6 `find_landing_site` gave a pass landing with the sun at 73 degrees (burn UT 188629422, landing UT 188630624, `max_wait_s` 43,740), but `deorbit` flew the cheapest lit pass in the window, a dawn pass about 15,300 s earlier at about 22.8 degrees. Now `find_landing_site` on a body with air records its sites in Core (`SitePasses`, in memory for the game session), and `deorbit` given one of them as `target` and a number as `max_wait` flies that site's pass. A target within `accuracy_km` of a site from one of the last 8 answers on that body is that site, whichever vessel the tool ran for (the vessel's own answer first, then the site itself, within 100 m, then the latest). If it can't (the pass is over accuracy_km, over budget, or past max_wait), it flies the lit pass within budget whose sun is nearest the site's, never one under 20 degrees, and says so in the result's `pass_note` and a `deorbit` blocknote (`phase pass`). With `max_wait` "auto", after a game restart, or after a quickload or revert to before the answer (the flight resumes at an earlier UT), its own rule stands; with Daylight landing on, a number as `max_wait` and a target matching no site, its own rule with a `pass_note` saying the target is not one of find_landing_site's sites. The agent run that wrote this did not launch KSP.

10. On a near-equatorial Duna orbit with Daylight landing **on**, ask `find_landing_site` for a daylight site. Pick a site whose pass is not the soonest lit one (a later `landing_ut`, a higher `sun_elevation_deg`). Note its `latitude`, `longitude` (as printed), `burn_ut`, `landing_ut`, `sun_elevation_deg` and `max_wait_s`.
11. Without restarting the game, run `deorbit(lexicon("target", lexicon("lat", <latitude>, "lng", <longitude>), "max_wait", <max_wait_s>))` then `reentry`. In `KSP.log` the `DEORBITBURN` line's `landing_ut` is within a quarter orbit of the site's `landing_ut` (not an earlier pass), and there is no `deorbit` blocknote with `phase pass`; the result has no `pass_note`.
12. After the landing, the Debrief's landing `sun_elevation_deg` is close to the site's `sun_elevation_deg` (within a few degrees, more if the craft lands far from the target).
13. Run step 11 with `max_wait` shorter than the site's pass (burn before the site's `burn_ut` minus a few minutes): `deorbit` flies a lit pass in the window, the result's `pass_note` and a `deorbit` `phase pass` blocknote in `KSP.log` start `find_landing_site's pass (landing UT ..., sun ... degrees) has no burn in max_wait ...` and name the sun it flew, which is at 20 degrees or more.
14. Run step 11 with `max_wait` "auto": `deorbit` flies its own choice, the cheapest lit pass (as in step 2 before #974), with no `pass_note`.
15. Run step 10 twice, for different sites, then step 11 with a site from the **first** answer, typed to 2 decimal places: `deorbit` still flies that site's pass (no `pass_note`).
16. Run step 10, then decouple a lander and run step 11 on the lander: it flies the site's pass.
17. Quicksave, run step 10, F9 to quickload, then step 11: `deorbit` flies its own choice, and the result's `pass_note` says `the target is not one of find_landing_site's sites` (the load went back before the answer). Then run step 10, go to the Space Center and back to the craft, and run step 11: it still flies the site's pass.
