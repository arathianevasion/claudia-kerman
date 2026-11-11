# reentry flies a direct entry from a trip home (#994)

In flight m20261111-172716-a40a the Duna return reached Kerbin ESCAPING (semi-major axis −9,670,147 m, periapsis 29,209 m), and `reentry` refused it twice with "the orbit never reaches the atmosphere": its time to the air (`claudia_fall_eta`) knew only closed orbits. Claudia then captured into a 1,000 km by 29 km ellipse first, about 1,400 m/s, and landed with 352 m/s left.

Now the time to the air is Core's `FALLETA` (ADR 0014; `claudia_fall_eta` and `claudia_mean_anomaly` left the library). It answers closed orbits as before and, by the hyperbolic Kepler equation, an inbound hyperbolic path whose periapsis is in the air. A hyperbolic path whose periapsis is above the air, or one already past periapsis and outbound, still refuses with the same reason.

Aiming toward KSC (the player's ruling) stays within the delta-v left: the Course Correction inside Kerbin's sphere and `reentry`'s Late Trim spend at most `DeorbitBurn.SpareDv` (the delta-v left less the reserve, any powered landing's need and later legs), and with nothing above that the craft enters on the path it has. A craft that can enter (`EntryCraft.CanEnter`, #981) is never captured just to aim.

**Block test to fly:** `tests/blocks/transfer_to_kerbin_from_duna` (it now also checks `reentry_success == true`).

1. **Fly the block test**, or a Duna round trip with an Entry Shield under the pod and a parachute on the pod, and a Mission Goal that names no place on Kerbin.
2. **No capture.** After `transfer_to` Kerbin, the craft stays on its arrival path (ESCAPING or a hyperbolic orbit at Kerbin). No `capture`, `solve_burn ellipticize` or other burn lowers the apoapsis before the entry. In KSP.log, `block call FALLETA` lines give the seconds to the air, never "never", while the periapsis is inside 70 km and the craft is inbound.
3. **reentry waits and enters.** `reentry` doesn't fail with "the orbit never reaches the atmosphere". It warps on rails toward the air (hours from the sphere's edge), then flies the entry: the shield takes the heat and the chutes land it. In the block test the Script Result has `reentry_success` true and `landed_or_splashed` true.
4. **The aim stays within the delta-v left.** The `block call ENTRYTRIM` line is a burn of no more than the delta-v left above the reserve (its aim note says "spent the N m/s it may above the reserve" when the cap binds), or "no trim: ..." with too little. The `late_trim` block note's `dv` is no more than the craft's delta-v at that point less the larger of 50 m/s or 10%. With too little delta-v there is no capture: the craft enters on its own path.
5. **Report on #994:** the arrival's `arrival_v_inf`, the first `block call FALLETA` time, the `ENTRYTRIM` line, the delta-v left at landing, the peak g, and the landing's distance from KSC.
