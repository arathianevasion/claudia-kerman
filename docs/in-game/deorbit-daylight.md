# deorbit: the Daylight landing setting picks a lit pass

In-game check for #957 (Core only: `DeorbitBurn` skips a pass whose sun at the target, at its `landing_ut`, is under `LandingSiteSearch.MinSunElevationDeg`, 20 degrees, when the Daylight landing Mission Setting is on). The agent run that wrote this did not launch KSP.

Fly a descent like m20261109-140705-ae3a: a near-equatorial Duna orbit (about 2 degrees inclination), Daylight landing **on**.

1. Ask `find_landing_site` for a daylight site near the equator. Note the site's `burn_ut`, `landing_ut`, `sun_elevation_deg` and `max_wait_s`.
2. Run `deorbit` to that site, passing the site's `max_wait_s`. The burn is at (or within a pass of) the site's `burn_ut`, not at the nearest pass. In `KSP.log` the `DEORBITBURN` line's `landing_ut` is within one orbit of the site's `landing_ut`.
3. After the landing, the Debrief's landing `sun_elevation_deg` is over 20 and there is no "landed in the dark" event.
4. Repeat with a `max_wait` too short to reach a lit pass (a few thousand seconds on this orbit). `deorbit` fails before any burn (`burn_ut` -1, no node left) with `no pass in the next <h> h lands in daylight: the best sun at the target is <n> degrees up, under 20; allow a longer max_wait or pick a different site`.
5. Turn Daylight landing **off** and run step 2 again: `deorbit` burns at the nearest pass as before.
