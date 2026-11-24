# land starts from a suborbital arc (#1309)

`land` used to start only from a low, near-circular orbit; a craft already on a suborbital path was refused with "land needs a near-circular orbit". Now Core's `LANDSTART` (ADR 0014) decides where it starts. A low, near-circular orbit flies as before: the pass search, the deorbit burn, the correction burn, then the powered descent. A craft that is SUB_ORBITAL, on an arc that meets the ground within 5 km of the site, flies no pass search and no burn before braking. Its descent (slope checks, diverts, Descent Shape, touchdown speed) is the same one an orbit start flies, aimed at the site, or for "any" at where the arc meets the ground. A biome target from an arc is refused: pass the site `find_landing_site` gave. Steering, throttle, staging and warp are still kOS's. The agent run that wrote this did not launch KSP.

**Block test to fly:** `tests/blocks/land_suborbital`, plus `tests/blocks/land` to see that an orbit start is unchanged.

1. **Fly `land_suborbital`.** The lander spawns in a 30 km equatorial Mun orbit. A minute on, `exec_node` burns about 59 m/s retrograde. Periapsis drops to about 50 km under the surface, and the status is SUB_ORBITAL.
2. **land starts from the arc.** In `KSP.log`, the `block call LANDSTART` line reads `arc ending <n> m from the aim`. No `phase pass`, `phase plan` or `phase resolve` trace line follows, and no deorbit or correction node is flown.
3. **It lands.** `PASS land_suborbital`, every `ok` line: `success` true, LANDED, `started_suborbital` true, within 5 km of the spot where the arc met the ground, touchdown under 3 m/s, `dv_used` under 880 m/s.
4. **An orbit start is unchanged.** Run `land`. It passes as before, and its `block call LANDSTART` line reads `orbit`.
5. **Report on #1309:** the `LANDSTART` line, the `phase repredict` trace line (its `verdict` and `shape`), `distance_to_target`, `touchdown_speed_actual` and `dv_used` from `land_suborbital`.
