# Ship Systems: what to watch

#756: on the craft Claudia flies, solar panels, deployable antennas, deployable radiators and lights extend, retract and switch at flight transitions. The rule is the mod's (Core's `ShipSystemsRule`), with no prompt tokens and no decision by Claudia. It runs only in a Mission: the `tests/blocks` block tests have none, so `launch_orbit`, `ascend`, `land`, `deorbit_kerbin` and `reentry_orbit` should fly as before, and the checks below need Mission flights.

Each transition that moves something writes one row in the Mission log, one `ship_systems` line in the Mission's `events.jsonl`, and the same line in `KSP.log`, for example `Ship Systems: retracted 4 panels, 1 antenna; entering Duna's atmosphere in 58 s`. A transition with nothing to move writes nothing.

Use a craft with at least one retractable panel, one deployable antenna, one radiator and one light. Before starting, open Settings and check that "Claudia manages ship systems" is on.

## A Kerbin launch

1. Start with everything stowed. Nothing moves while the plan is written and approved, nor during any wait on the pad.
2. Just before throttle-up the lift-off stow runs. If you extended a panel on the pad, it retracts and the engines wait for it, at most about 10 s. Past that cap a `phase ship_systems` note in `KSP.log` says so, and the block goes on.
3. On the climb and the coast nothing moves.
4. When the orbit is complete (periapsis above 70 km, engines off), the panels, antenna and radiator extend: `extended ...; in orbit of Kerbin`. Not during the burn, and not on an orbit whose periapsis is inside the air.
5. Retract a panel by hand. It stays in through the next vacuum burn: nothing undoes it until the next transition.

## A Mun landing

1. Parts stay out through the deorbit and the descent, and nothing retracts on an airless body.
2. The lights come on at `land`'s terminal descent or below 1,000 m, whichever comes first: `lights on; final descent to Mun` or `lights on; below 1000 m over Mun`. Day or night.
3. After touchdown the lights stay on and the panels stay out.
4. At `ascend`, before throttle-up, the lights go off (`lights off; lifting off from Mun`). The panels stay out, and there is no wait.

## A Duna or Kerbin entry

1. Coming in, the parts retract before the air, whichever comes first: within 60 s or 10 km of the atmosphere's top on a trajectory that enters it, or at the start of `deorbit` or `reentry`. A periapsis tuned into the air for an aerobrake counts as a trajectory that enters it: there is no aerobrake block to send a cue. After `deorbit`'s cue they stay in, though the craft is still in orbit.
2. Try this arrival at high warp too: the line must come in time, and the parts must really move on rails.
3. After landing or splashdown, once stationary for 3 s, they extend again: `extended ...; landed on Duna`.
4. Lights as on the Mun.
5. Before lift-off they stow and the block waits, as on the pad. In orbit they extend again.

## A part that can't retract

1. Fit a panel that can't retract (the SP-L 1x6, say) on the pod. The Craft Check shows an info line naming it, which isn't a Warning and needs no acknowledgement.
2. In a Mission whose plan comes back to Kerbin, it stays stowed in orbit, and the line says why: `left 1 panel stowed: can't retract, and the Mission Plan enters Kerbin's atmosphere (...)`.
3. Fit it on a stage the staging plan separates instead, or fly a plan with no entry ahead: it extends in orbit.

## The setting and the edges

1. Switch "Claudia manages ship systems" off and Apply: nothing moves, no line, no stow wait, and the Craft Check's line goes. It is still off after a restart (`manage_ship_systems` in `config.cfg`).
2. In Manual Control nothing moves. Switch to another vessel: nothing moves on either until you switch back.
3. Quickload in orbit with a panel retracted: it stays retracted, since an orbit the craft is already in when the rule starts is no transition.
4. A remote Mission on a probe with only a deployable antenna: note what the link does when that antenna retracts for an entry (see the ticket's discovered work).
