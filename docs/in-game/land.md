# land in KSP: the path guard and the vertical fallback (#712)

In-game checks for #712 (ADR 0014 block migration): BRAKEPATH's path now has the point where braking stops, and `land`'s path guard, with its vertical fallback, runs in Core (`ADDONS:CLAUDIA:LANDGUARD`, each braking tick). Steering, throttle and staging are still kOS's. The agent run that wrote this did not launch KSP; the tests and the gate are green in the repo.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: Run land_biome_minmus

1. Pick `land_biome_minmus` and press **Run** (up to 2400 s).
2. It passes (`PASS land_biome_minmus`, every `ok` line).
3. In `KSP.log`, the `land trace` lines from `phase repredict` to `phase touchdown` have no `phase guard` line. A `phase vertical` line (with `why braking is above its predicted path: ...`) followed by `phase terminal` lines and `phase touchdown` also passes; then the report has `vertical_fallback: True`.
4. About once a second while braking, `block call LANDGUARD took ... ms (the slowest of <n> calls ...)`: the slowest should be well under 1 ms.

## Check 2: Run land

1. Pick `land` and press **Run**. It passes, with no `phase guard` line in `KSP.log` and `vertical_fallback: False` in the report (a Mun landing should fly its shape).

## Check 3: a real Minmus Flats landing

1. Fly a Mission to land on the Flats on Minmus (#712's was "Land on the flats on Minmus and return to kerbin", Mun Shot v2, from a 30 km orbit).
2. `land` lands: `success: True`, `situation: "LANDED"`, `biome: "Flats"`. The trace has either no `phase guard` line, or a `phase vertical` line and then `phase touchdown`.
3. If it aborts with `braking is above its predicted path`, copy the `phase guard` line (its `no_vertical` field says why it couldn't come straight down) and the `land trace` lines from `phase repredict` into the issue.

# land in KSP: Points of Interest (#713)

In-game checks for #713 (ADR 0014 block migration): `find_poi`, `find_landing_site`'s `poi` (Core's ring search) and `land`'s target `lexicon("poi", name)`. The keep-clear decision (the Underhang's impact, a "vessel" target's Target Vessel, and now a Point of Interest) runs in Core (`ADDONS:CLAUDIA:KEEPCLEAR`); steering, throttle and staging are still kOS's. The agent run that wrote this did not launch KSP.

## Check 1: KSP.log lists the Points of Interest

1. In a flight scene, ask `find_poi` for Minmus and then the Mun (a Mission's plan turn, or a goal naming a monolith).
2. `KSP.log` has one line per body: `Points of Interest: <n> surface objects on Minmus (pqsSurfaceObjects); <name> at <lat>, <lng>, <h> m...`, and the same for the Mun. Minmus should list one object near 23.78, 60.05 (TMA-8); the Mun its arches and monoliths near the table's positions. Copy both lines into #713 (the internal names seed Core's table).

## Check 2: Run land_vessel

1. Pick `land_vessel` and press **Run**. It passes as before: the site stays beside the Target Vessel, and a `phase clear_vessel` trace line (if any) moves the site to 50 m or more from it.

## Check 3: Run land_poi_minmus

1. Pick `land_poi_minmus` and press **Run** (up to 2400 s).
2. It passes: the report's `poi` is `Monolith TMA-8` and `distance_to_poi` is between 150 and 1500 m. `KSP.log` has a `land trace` `phase site` line `beside Monolith TMA-8: <m> m from it, bearing <deg> degrees`, and `block call KEEPCLEAR took ...` lines.

## Check 4: the Minmus monolith Mission

1. Fly a Mission with the goal "Land next to the monolith on Minmus".
2. The plan requests carry `Monolith TMA-8 is a Minmus Point of Interest: land beside it with find_landing_site's poi and land's poi target, not on it.` until the landing step is ticked.
3. Claudia lands on flat ground 150 m to 1.5 km from TMA-8, never on it; the report's `distance_to_poi` is in that range.

# land in KSP: the final descent stays upright with far more thrust than it needs (#865)

In-game checks for #865 (ADR 0014 block migration): `land`'s steering for a demand (the old `land_steer`), its lean limit, its throttle and the new settle before touchdown run in Core (`ADDONS:CLAUDIA:LANDSTEER`, each braking tick). Steering, throttle and staging are still kOS's; RCS is unchanged (#506). In the final descent (`phase terminal`) the thrust leans at most 20 degrees (adding lift, up to what holds its height, to keep braking across the ground; tighter in the last 7.5 m, as before), turns its command no faster than TURNTIME's top rate, gives only the vertical need while more than 15 degrees off, and below 10 m holds height while more than 10 degrees off or turning faster than 5 deg/s, for up to 10 s or 2% of its delta-v (`settle_s`). The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md`. Results are in `block-tests.log` and `KSP.log`.

## Check 1: Run land_high_twr_ike

The land test lander with its tank 70% full: about 15:1 thrust at Ike by touchdown, like m20261107-055424-908b, which touched down 43 degrees off at 21 deg/s and tipped to 70.

1. Before running, check in the VAB or the flight that the craft's FL-T800 shows 252 of 360 LiquidFuel and 308 of 440 Oxidizer. If it shows full, the craft file's tank contents didn't load: say so on #865.
2. Pick `land_high_twr_ike` and press **Run** (up to 1800 s). It passes (`PASS land_high_twr_ike`, every `ok` line): `lean` under 10, `touchdown_h_speed` under 0.5 m/s, `settle_s` from 0 to 10.
3. In `KSP.log`, the `land trace` `phase touchdown` line has `error` no more than 5 and `rate` no more than 2 (degrees, deg/s). That is #865's pass.
4. From `phase terminal` on, the `phase rcs` lines' `error` stays under about 20 and `rate` under about 10, where 908b's swung 40-110 degrees at 30 deg/s.
5. About once a second while braking, `block call LANDSTEER took ... ms (the slowest of <n> calls ...): throttle ..., ... deg off, lean ... deg`: the slowest should be well under 1 ms. A `, settling` on lines below 10 m shows the hold.
6. If it tips (`tipped over on landing`) or `lean` is 10 or more, copy the `land trace` lines from `phase terminal` to the end, and the `LANDSTEER` lines, into #865. Don't change RCS for it: that is a separate ticket.

## Check 2: Run land_high_twr_mun

The same craft on the Mun (about 10:1): the Mun landing still lands clean.

1. Pick `land_high_twr_mun` and press **Run**. It passes, with `lean` under 10 and `touchdown_h_speed` under 0.5.
2. Note `distance_to_target`. A final descent that starts high still moving across the ground brakes more slowly at 20 degrees without climbing: the simulated Minmus Flats approach now lands about 200 m long. Over 300 m from the site, copy the `phase terminal` lines into #865.

## Check 3: Run land

1. Pick `land` and press **Run**. It passes as before (the reference lander, about 7:1 at the Mun).
