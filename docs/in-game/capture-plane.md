# Capture into the Mission's plane and direction (#788)

`transfer_to` now aims the arrival plane on the approach (`plane`, default `"auto"`), and the new `capture` block flies the arrival burn and any leftover tilt. Core worked all of this out on detached orbits; these checks fly it in KSP. The Delta-v Map's `polar:` row is the plane aimed this way (#806); for any other plane, read the m/s below against the leg's margin.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look").

**Block migration:** the arrival plane's choice, the trim's aim and `capture`'s choice are in Core (`ArrivalPlanes`, `TransferFlight`, `CorrectionBurn`, `CaptureFlight`). Nothing moved out of Kerboscript: `capture.ks` and the `plane` option are new wiring, so there is no parity test. Fly `tests/blocks/capture_mun_polar`, `transfer_to` and `transfer_to_duna`.

## Check 0: the block tests

1. In the block test runner run `transfer_to` and `transfer_to_duna`: both `PASS`. Neither has a plane to aim for, so they fly as before #788.
2. Run `capture_mun_polar`: `PASS`. In `KSP.log`, the line `capture test:` gives `capture_method`, `capture_dv`, `plane_dv`, `inc` and `ecc`.

## Check 1: a Mun capture into a polar orbit (`plane: 90`)

1. Start a Mission from low Kerbin orbit with the goal "Polar orbit of the Mun". A script of `transfer_to(lexicon("target", "Mun", "target_pe", 30000, "plane", 90))`, `coast` to `encounter_ut + 60` and `capture()` is what you want to see, with `plane` in Claudia's call.
2. In the Script Result of `transfer_to`: `plane_off_deg` under 0.5, and `plane_dv` plus `correction_dv` about 40 to 80 m/s. The flight burns the plane in two places: halfway to the Mun, then just inside its sphere of influence.
3. In the result of `capture`: `capture_method` "plain", `inc` 89.5 to 90.5, `ecc` under 0.011. No burn after the capture burn.
4. Note the figure in step 2 on the ticket. The brief hoped for "a few m/s"; the Mun's approach is slow and its sphere of influence small, so Core predicts tens of m/s for a full 90 degrees. It is still far under the 760 m/s of the same turn in low orbit.

## Check 2: Kerbin to Duna with a next leg to Ike

1. Start a Mission with a Mission Plan whose legs read "Transfer to Duna", "Capture at Duna", "Transfer to Ike". Leave `plane` out of `transfer_to`.
2. Inside Duna's sphere of influence the trim burns a `plane_dv` of under about 25 m/s. In `KSP.log`, the `TRANSFERSTEP` lines show a "plane" burn round Duna, and the `arrival_ut` is unchanged to within a few minutes.
3. In the result of `transfer_to`, `plane_off_deg` is the tilt left: the part of Ike's plane the approach couldn't reach.
4. In the result of `capture`, `inc` is within half a degree of Ike's (about 0.2 degrees), going the same way round, unless `capture_method` reads "over_max_plane_dv": then `plane_leftover_deg` and `plane_leftover_dv` say what was left, and the block did not fail.
5. The later `transfer_to` to Ike needs little or no plane work.

## Check 3: Kerbin to Minmus with an inclined, eccentric Target Vessel in Minmus orbit

1. Put a vessel with no crew in an inclined, eccentric Minmus orbit (Alt+F12, Set Orbit: inclination about 30, periapsis 20 km, apoapsis 90 km). Make it the Mission's Target Vessel.
2. Start a Mission from low Kerbin orbit with the goal to rendezvous with it. Leave `plane` and `alt` out of Claudia's calls.
3. `transfer_to`: `target_pe_actual` within `pe_tolerance` of 20 km, whatever `target_pe` Claudia gave. `plane_off_deg` may read several degrees: a burn this size swings Minmus's slow approach.
4. `capture`: circular at 20 km (`ecc` under 0.011), in the vessel's plane and going the same way round: compare `inc` and the map view's orbit lines. If `capture_method` reads "over_max_plane_dv" the block reports the leftover instead, and Claudia decides at the Debrief.
5. `rendezvous` then phases and meets the vessel, with little or no plane match of its own.

## Check 4: Mun Shot v2 to the Mun's pole (#806)

1. In the editor load Mun Shot v2 with the goal "Land at the Mun's pole and return to Kerbin", with the 15% margin, and run the Craft Check. It passes: the card's route has about 80 m/s for polar and sums to about 6,390 m/s, about 7,350 with the margin, against the craft's 7,393 m/s.
2. Start the Mission and approve the plan. Read the Mission Script's `transfer_to`: it leaves `plane` out, or passes 90. If it passes another number or "none", note that on the ticket: Claudia's value wins, so the check's figure no longer applies.
3. In the result of `transfer_to`: `plane_off_deg` under 0.5. The aimed plane isn't logged: after the burn, the Mission's save holds it as `arrivalPlane` (inc 90, source "site", or "number" if Claudia passed 90).
4. Add up `plane_dv` and `correction_dv` from that result and report the sum on the ticket against the `polar:Mun` figure on the card. It should be under it, about 40 to 50 m/s.
5. In the result of `capture`: `inc` 89.5 to 90.5, with no burn after the capture burn. The landing reaches the pole with no plane change in low orbit (that turn costs about 760 m/s).
6. Biomes: repeat steps 2 and 3 with the goal "Land in the Mun's Poles biome" (aims 90), then with "Land in the Mun's Twin Craters" (aims the biome's median |latitude| + 0.5), reading `arrivalPlane` and `capture`'s `inc`. The first `transfer_to` step surveys the biome map once: in `KSP.log`, compare that step's `block call TRANSFERSTEP took` figure with the later ones and report it on the ticket.
