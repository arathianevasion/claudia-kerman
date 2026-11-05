# The Encounter Watch: what to watch

#770: during `transfer_to`'s long coasts to a far target (Duna from Kerbin, Minmus from the Mun), the mod checks the encounter while the warp runs, reading KSP's own orbit, so with no physics and at any rate. It drops the warp only when the encounter is lost, KSP has the craft round a body the coast doesn't go round, or the periapsis has moved more than 10 % of the target's sphere of influence (4.8 Mm at Duna) from what the coast was planned on. The block then plans the way back. The checks go by game time: a twentieth of the time to the nearer end of the coast, from 1 minute to 6 hours.

Each drop writes one row in the Mission log, one `encounter_watch` line in the Mission's `events.jsonl`, one `transfer_to trace` line and the same line in `KSP.log`, for example `Encounter Watch: Duna encounter lost at UT 44786120; warp dropped to restore it`. The block tests have no Mission, so there only the two `KSP.log` lines appear.

Also new: the correction after the escape waits until the craft is 5 % of the sphere of influence's radius past its edge (4.2 Mm at Kerbin, about an hour at 1 km/s), and in `"auto"` a next-window recovery must burn within 30 Kerbin days. Past that the block fails with the figures so Claudia can replan with the player.

Install and the block test runner are as in `phase-3-gate.md` ("Install", "Where to look"). `ClaudiaKerman.Adapter` and `ClaudiaKerman.KOS` both changed, so install both.

## Block tests to fly

`transfer_to_duna` and `transfer_to_minmus_from_mun`, as in `transfer-to.md`, Checks 2 and 3.

## Check 1: nothing wrong, no drops (Kerbin to Duna at high warp)

1. Pick `transfer_to_duna` and press **Run**. Don't touch anything.
2. After the ejection, the block warps out of Kerbin's sphere of influence. In `KSP.log`, `setting new dominant body: Sun` comes first; the `block call TRANSFERSTEP ... burn correction` line comes only after a further warp (`wait ... next escaped` planned it), not within a few minutes of game time as before #770.
3. Through the coast the warp runs up to KSP's limit and stays there, apart from KSP's own "Orbit event imminent" cap near a sphere of influence's edge. There is no `Encounter Watch:` line anywhere.
4. Every 10 real seconds or so during a watched wait, a `block call ENCOUNTERWATCHDROPPED took ... ms ...: N checks` line. N climbs by a few hundred over the whole coast. The time should stay under a few ms: note it if it passes 20 ms (one physics tick).
5. `KSP.log` reads `PASS transfer_to_duna`, with `correction_dv` under 20 m/s as before.

## Check 2: forcing a loss

1. Run `transfer_to_duna` again, up to the long coast on the Sun's patch (`block call TRANSFERSTEP ... wait ... next near` or `next mid`).
2. Stop the warp by hand. The script then leaves the warp to you for the rest of this wait (ADR 0008).
3. Point retrograde and burn about 10 m/s by hand (8 m/s or more along the track loses a 50 km Duna encounter early in the coast). The map shows no Duna encounter.
4. Raise the warp again by hand.
5. Within a few warp ticks: the warp drops to 1x, and `KSP.log` has `Encounter Watch: Duna encounter lost at UT ...; warp dropped to restore it` and a `transfer_to trace` line with the same text.
6. Next, `block call TRANSFERSTEP ... burn correction ... next restore_check; { recovered: "no encounter with Duna after the correction" }`, about the size of your burn, as soon as the craft can turn to it. After it the map shows the encounter again, and the flight goes on with its next wait watched.
7. The block ends as usual, with `recovered` in its result and `correction_dv` raised by the restore. The test's own `correction_dv` check may fail for that reason: that's expected here.

## Check 3: a small drift (optional)

As Check 2, but burn about 2 m/s. The encounter stays on the map with its periapsis about 14 Mm off. The warp drops with `Encounter Watch: Duna periapsis off by ... km at UT ...`, and the next step is a burn of about 2 m/s back to `target_pe`, with no `recovered`.

## Check 4: handing back

As Check 2, but first drain the craft's fuel by hand to under the restore's cost (or burn about 40 m/s by hand with little fuel left). The block fails with `no encounter with Duna after the correction, and no correction fits: ...; a restore costs ... m/s of ... m/s left; the next window costs ... m/s at UT ..., ... s from now`. It must not start a burn years away.

## What to report

- Any `Encounter Watch:` line in a flight where nothing was wrong (Check 1): the time, the line and what the map showed.
- A warp that drops and rises repeatedly: there should be at most three drops in one flight.
- A re-entry into the home body's sphere of influence after the correction, as in Mission m20261105-174251-8f20: `setting new dominant body: Kerbin` during the coast. The margin should have made this go away, and if it happens the watch should drop the warp and the next step should be `wait ... next escaped`.
