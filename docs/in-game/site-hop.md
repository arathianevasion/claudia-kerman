# site_hop: a Site Hop between two Mun sites (#1310)

`site_hop` is a new Building Block. From a landed craft it flies one burn onto the least-speed arc that comes down on a target site on the same body, then hands the touchdown to `land`'s descent from that arc (#1309). Core's `SITEHOP` (ADR 0014) computes the arc, the reach check before liftoff and each tick of the burn: it rises straight up for 20 m, then steers along the velocity still to gain and cuts within 0.5 m/s. kOS steers, throttles and stages. A site under 1 km away, a craft that can't lift off, or a hop needing more delta-v than the craft has fails before liftoff, with the reason. Since #1322 it waits on the ground, warping, until a liftoff gives a touchdown with Daylight at the site (no wait if one now would), and fails before liftoff when the sun never lights the site within 1.2 days of the body. The agent run that wrote this did not launch KSP.

**Block test to fly:** `tests/blocks/site_hop_mun`.

1. **The first landing.** The lander spawns in a 30 km equatorial Mun orbit, burns about 59 m/s retrograde a minute on and lands from the arc, as `land_suborbital` does. `site_hop test: first landing True, LANDED` prints.
2. **The near site is refused.** `site_hop test: near hop False, the target is 0.5 km away: a Site Hop needs at least 1 km` prints. The engine never lights and the craft stays where it is.
3. **The hop.** In `KSP.log`, `block call SITEHOP` lines read `20.x km, <n> m/s at <n> deg, throttle …`; with the site dark at touchdown, the first ends `; waits <n> s on the ground for a daylight touchdown` and the craft warps on the ground before liftoff (#1322; the day/night line itself is #1312's scenario). The craft rises a few metres straight up, pitches over to about 43 degrees east and burns for some seconds, then cuts and coasts on an arc of about two minutes.
4. **The landing.** `land`'s `block call LANDSTART` line reads `arc ending <n> m from the aim`, and its powered descent brings the craft down near the site.
5. **It passes.** `PASS site_hop_mun`, every `ok` line: `success` true, LANDED, `hop_km` about 20, `hop_burn_dv` 150 to 300 m/s, `test_hop_m` under 5000, touchdown under 3 m/s.
6. **Report on #1310:** the first and last `SITEHOP` lines, the `LANDSTART` line, `hop_burn_dv`, `hop_need_dv`, `test_hop_m`, `touchdown_speed_actual` and `dv_used`.
