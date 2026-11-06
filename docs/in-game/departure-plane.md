# The departure plane from any launch site or landed craft in KSP (#727, #732)

#727 moved the choice of the departure plane into Core (ADR 0014): `LaunchWindow` (into a moon's plane when the site passes under it, else due east at the moon's node with the residual named), `DeparturePlane` (a planet's escape: an orbit holding the asymptote), `TransferWindows` (each row's ascent loss, plane change and case note) and `TransferFlight` (#732: transfer_to fixes a residual plane mid-course). `launch` and `transfer_to` are unchanged Kerboscript: they fly the inclination, time and nodes Core gives. No block option, `//|` line or tool description changed. One deliberate behaviour change: `launch` with `launch_at` a body from a site past its plane (Minmus from 20° N) no longer fails with "the pad never passes under a plane of inclination 6"; it launches due east (target_inc the site's latitude) when its orbit's node is the body's. A retrograde plane still fails as before. The agent run that wrote this did not launch KSP; the Core tests and the gate are green in the repo.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look").

## Check 0: the block tests, unchanged

1. In the block test runner run `launch_lan` (launch_at "Minmus" from KSC, into its plane) and `transfer_to`: both `PASS` as before #727.

## Check 1: the Minmus validation flight's rows show the ascent loss

1. On the pad at KSC, start the Minmus validation Mission (goal "Land on Minmus", or "Orbit Minmus").
2. In the Mission's trace (or `KSP.log`), find Claudia's `plan_transfer {"target":"Minmus",...}` result (or the stage table row for it).
3. Each of `soonest` and `optimal` reads `"ascent_loss_dv":1` (0.9 to 1.0), `"plane_dv":0`, `"plane":"plane: launched into"`; `total_dv` is the ejection plus the capture plus that 1 m/s. The `launch` object gives `at_ut` and `target_inc` about ±6.
4. The flight launches at `at_ut` into about 6° and reaches Minmus as before (no plane change burn in transfer_to).

## Check 2: a high-latitude site launches due east and fixes the plane during the transfer

1. Put a craft that can reach Minmus on the ground at 20° N or more: the Woomerang site (Making History, about 45° N), or any craft landed there (Alt+F12, Cheats, Set Position; or flown there). Set Time versus delta-v to **Fastest**.
2. Ask Claudia for "Orbit Minmus". Her `plan_transfer` result reads `"plane":"plane: residual N° fixed mid-course"` with N the site's latitude less 6, `ascent_loss_dv` 0, and `plane_dv` tens of m/s (about 78 at 20° N, more further north), and a `launch` object with `target_inc` equal to the latitude and a note "launch due east at this time".
3. Watch the launch: it waits for `at_ut` (under one Kerbin day) and flies due east (heading 90 at lift-off), ending in an orbit inclined about the site's latitude.
4. Watch transfer_to: the ejection in the parking orbit, then a `plane` burn on the way to Minmus (the broken-plane plan's, or, after an ejection that left no encounter, #732's about halfway there; tens of m/s, mostly normal), then the finer correction; the block ends with `target_pe_actual` within pe_tolerance. `KSP.log` shows `block call TRANSFERSTEP took ... ms: burn plane at UT ...`. No large burn in the parking orbit.
5. Repeat with Time versus delta-v **Most efficient**: `plan_transfer`'s pick departs days later, reads `"plane":"plane: residual fixed at node arrival"` and `plane_dv` under 1 m/s.

## Check 3 (optional): an escape from a landed craft

1. A craft landed on Eve (or Duna) with the delta-v to reach Kerbin. Ask Claudia to plan the return.
2. `plan_transfer {"target":"Kerbin",...}` gives each row a `launch` (`at_ut` before the departure, `target_inc` at least the site's latitude), `"plane":"plane: launched into"` and an `ascent_loss_dv` of a few m/s at most.

## Report

Post on #727: pass or fail for each check, the `plan_transfer` rows (soonest, optimal, launch), the `plane` burn's m/s and `target_pe_actual` from Check 2, and the Mission ids.
