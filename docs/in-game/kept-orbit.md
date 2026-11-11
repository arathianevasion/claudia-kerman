# A capture that keeps a high orbit for the moon next: what to watch

#866: in flight m20261107-055424-908b, `capture` at Duna burned 253.6 m/s into a 60 × 24,434 km orbit, 6.1 for the plane, then 359.3 down to a 60 km circle; `transfer_to Ike` took 289.0 and Ike's capture 120.7: 1,029 m/s from Duna's arrival to low Ike orbit. Now, when the Mission Plan's next leg goes to a moon of the planet it captures at (Ike, Gilly, Jool's moons; never Kerbin's), `capture` keeps a high ellipse whose next periapsis (else one of the next three) meets a window to the moon, and `transfer_to` leaves from that periapsis. Core's solver flies 908b's arrival for about 458 m/s (`KeptRouteTests`).

- **The kept line.** `capture`'s result has `kept`: `kept 60 x 3,193 km for Ike: window at periapsis 1, UT ...`, or `circle: ...` with the reason it flew the circle (no moon next, no window within three loops, past the next leg's Due Time).
- **Safety.** The ellipse is only kept when its path meets nothing before the window, every loop followed (#867's check). The Debrief doesn't call it an `orbit crosses a moon`: the moon is the next leg's.
- **Pricing.** For a goal at Ike, the Delta-v Map's `transfer:Duna` reads "Low Kerbin orbit to high Duna orbit" and `transfer:Duna>Ike` "High Duna orbit to low Ike orbit": the two together about 600 m/s under the low circle's.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look"). `ClaudiaKerman.Core` and `ClaudiaKerman.Adapter` changed (the block test's plan reaches `capture`), and `capture.ks`: install all three.

## Check 1: the block test

1. Pick `capture_duna_ike` and press **Run**. It flies `transfer_to Duna` (as `transfer_to_duna`, about 40 real minutes), `capture` with the test's plan naming Ike next, `transfer_to Ike` and Ike's capture.
2. It ends with `PASS capture_duna_ike`. In `KSP.log`, the `CAPTURESTEP` lines show the first burn's `kept` line, a plane burn high up if one is needed, then `done` with `kept ... for Ike: window at periapsis N, UT ...`.
3. The `transfer_to Ike` print's departure UT is within a few minutes of that window's UT, at a Duna periapsis, and its ejection is tens of m/s, not about 290.
4. The last print gives "Duna arrival to low Ike orbit": about 460 m/s (the check allows 300 to 560).

Fail: `kept` starts `circle:`, or the craft meets Ike before the window (an `On-Rails SOI Transition from Duna to Ike` in `KSP.log` before `transfer_to Ike`'s ejection), or the total is over 560.

## Check 2: a Mission

1. A craft with about 3,000 m/s in low Kerbin orbit. Start a Mission: "Land on Ike, send a crew report and then return to Kerbin".
2. The Craft Check's Delta-v Map and the Mission Plan's request show `transfer:Duna` to a high Duna orbit and `transfer:Duna>Ike` from it, together about 600 m/s under the low circle's.
3. Fly it. The Duna capture's result line has `kept ... for Ike`; the next leg's `transfer_to Ike` leaves at that window's periapsis; the Debrief after the capture has no `orbit crosses a moon` event.
4. If Claudia's next plan waits for a Due Time instead of flying `transfer_to Ike` right away, note it on the ticket: the kept window is within three loops.

## Report

Tell the ticket the `kept` line, the window UT against `transfer_to Ike`'s departure UT, and the total from Duna's arrival to low Ike orbit.
