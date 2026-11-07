# A landing site on a body with air (#828)

`find_landing_site` now works on bodies with air, for a `deorbit` target. Core tests cover the search and the Debrief's line. These checks need flight.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Scenario

Run `tools/reentry_duna_site`. It's in `tests/tools`, not `tests/blocks`, because only a tool scenario may call the test-only Planning Tool bridge. It asks `find_landing_site` for a daylight site near the equator, gives the first to `deorbit` as its target with the stack kept, and flies `reentry`.

1. **Daylight:** the Script Result has `site_sun_deg` over 20 and `sun_elevation_deg` over 20.
2. **The miss from the site.** Write down `miss_km` below, and post it on #828.

| Date | Flight | `miss_km` | `sun_elevation_deg` | Stack |
|---|---|---|---|---|
| | | | | |

**This figure is not yet a measure of the search's accuracy.** #814 (predict the craft after the drop) isn't in, so `deorbit` aims for the stack as it is now, and the miss may read high until it lands. The flight that found this ticket predicted 149 km with the stack on. No accuracy is promised, and the scenario doesn't judge the figure.

## On a Mission

Fly the craft from m20261106-203509-34a6 with the goal "Fly to Duna and land near the equator in daylight".

1. **The search:** in Duna orbit, `find_landing_site` answers with sites, not a refusal. Each has `landing_ut` and `sun_elevation_deg`, and the answer's `slope` says it wasn't applied.
2. **The site is `deorbit`'s:** the Sequence Plan's `deorbit` has that site as its `target` and the site's `max_wait_s` as its `max_wait`.
3. **The sun holds:** the `reentry` result's `sun_elevation_deg` is near the site's `sun_elevation_deg`. If not, compare the `deorbit` result's `burn_ut` with the site's `burn_ut`. A different pass means the search's estimate of `deorbit`'s choice is wrong: post it on #828. On an eccentric orbit, check this one twice: the estimate has been tested in Core only against a circular and a slightly eccentric orbit, without `deorbit`'s predictor.
4. **A dark exact place:** start a second flight with a goal naming one place on the far side from the sun, such as "land at 0N 180E". The site comes back with `wait_until_ut` and a note. Claudia says so in the plan, holds the craft to that time and gives `max_wait_s`, and `deorbit` flies the pass with Daylight.

## Saying when it failed

Land on Duna or Kerbin on the night side, with Daylight landing on in Mission Settings. The Debrief's events have a `landed in the dark` line with the sun's figure. With Daylight landing off and no "daylight" in the goal, no such line appears. A landing with the sun over 20 degrees has none.

## Water

These read from Core tests too. Check one in flight if a Kerbin return is due.

1. In Kerbin orbit before a further burn (say a Kerbin landing with a relaunch still in the plan), the answer says `water` is excluded, and no site's `terrain_altitude` is 0.
2. On a Mission's Last Entry, it says `water` is allowed, and a site over the sea has `"water": true`.
3. `land` still refuses a body with air, as does `find_landing_site`'s `poi`.
