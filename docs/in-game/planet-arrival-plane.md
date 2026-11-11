# A planet's arrival plane is set far out (#986)

Mission m20261110-203402-45d1 came into Duna at 53.9° with `plane_dv` 0. Its next leg read "Land at Duna equator". Claudia then flew a plane change to the equator for 806 m/s. Its return reached Kerbin at 57.9°. Now, in a transfer to a planet, the Course Correction on the Sun's patch aims the arrival plane as well as the periapsis, and the trim inside the sphere of influence is only a trim. "auto" finds the plane in the Mission Plan: from a landing leg that names the equator (0) or a pole (90), or from a picked site. A trip home aims for KSC's plane, the equator.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look").

**Block migration:** all of this is in Core (`ArrivalPlanes`, `TransferFlight.MidCorrection`). There is no Kerboscript, `//|` or prompt change. Fly `transfer_to_duna` as before.

## Check 0: the block test

1. In the block test runner, run `transfer_to_duna`. It should read `PASS`. It has no plane to aim for, so it flies as before.

## Check 1: Duna for an equatorial landing, with no plane-change sequence

1. Start a Mission from the pad with the goal "Fly to Duna in the optimal window. Land on the equator and send a crew report. Then return to Kerbin in the soonest window." This is the flight's own goal.
2. Approve a plan whose transfer leg names its capture ("... coast to Duna, and capture into a 60 km orbit") and whose next leg is a landing on the equator ("Land at Duna equator"). Leave `plane` out of `transfer_to`, or set it to "auto".
3. After the Course Correction on the Sun's patch, the map view's Duna encounter should show an inclination under about 5°. The burn is the `correction` kind. In `KSP.log`, a `TRANSFERSTEP` line shows it round the Sun, and it should cost at most a few m/s more than a flight with no plane.
4. In the result of `capture`: `inc` should be under about 5°. Report `inc` and the `correction_dv` and `plane_dv` from `transfer_to`'s result on the ticket.
5. The plan that follows should have no plane-change sequence before the landing. If Claudia still adds one, note its `delta_v` on the ticket.

## Check 2: the trip home comes in near the equator

1. Continue the same Mission to the soonest return, leaving `plane` out of `transfer_to`.
2. After the Course Correction on the Sun's patch, the map view's Kerbin encounter should show an inclination under about 5°, going prograde (not about 180°).
3. Inside Kerbin's sphere of influence, the landing aim (#915) flies as before: its `landing aim:` log line is unchanged in form.
