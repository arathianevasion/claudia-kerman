# Rebuilt block-test craft (#920): watch one long run's EC, fly `launch_orbit` and `return_target`

The craft of 16 block tests changed. Core's `BlockTestCraftTests` parses each part tree (links, nodes, no orphans) and reads `return_target`'s craft through `EntryStack`. These checks need flight. All craft figures in the `test.cfg` comments (delta-v, Mk16 speed, EC drain, entry heat) are rough estimates: write the real ones down.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## 1. One long run keeps its EC

1. Run `transfer_to_minmus_from_mun` (it carries four OX-STAT panels on the kOS part). Watch the electric charge on the Resources panel through the long waits: do the panels add charge in sunlight, and does the charge stay above `min_ec_pct` (10%)?
2. **Write down** the charge at the start, the lowest charge, and the charge at the end. The last failure of this kind was `FAIL transfer_to: abort min_ec_pct: electric charge 10% is below 10%` in `block-tests.log` on 2026-10-25.
3. The four panels sit at 0, 90, 180 and 270 degrees on the kOS part, so one or two face the sun at a time. If the charge still falls too far, say so on #920: the panel count or place is the knob.
4. Check that the panels did not retract or break: no `Ship Systems` line retracts them (static panels have no retract).

## 2. `launch_orbit` reaches orbit with the 1.25 m stabilizer

1. Run `launch_orbit`. The craft now has the Advanced Inline Stabilizer (`advSasModule`) above the probe core. Open the VAB view of the craft once if you like: the stabilizer sits between the probe core and the Mk16 chute, flush with both.
2. After the first stage separates (about 9-13 km, 6-13 kPa) the craft no longer loses control. It reaches orbit and the test passes.
3. **Write down** where control was weak if it still was: altitude, dynamic pressure, error angle. Say on #920 whether the craft or `launch` is the cause.
4. `launch_lan` and `drop_empty` use the same swap. Fly them if there is time: they should climb as before.

## 3. `return_target` drops its stack and lands

1. Run `return_target`. The craft is probe, kOS, two batteries, SAS, Mk16 chute, a 1.25 m heat shield, Decoupler.1, tank and LV-T45.
2. `KSP.log` reads `ENTRYCRAFT` with a heat shield (not `no Entry Shield`), then `shield number 1: part <id>` and the stack drops about 600 s before the air.
3. The capsule lands or splashes under its chute. The test passes `shield_dropped == true`, `parts_dropped >= 3` and `landed_or_splashed == true`.
4. **Write down** the touchdown speed and `max_temp_pct`. The 3 R8 winglets stay on the dropped stack. #921 changes `return`'s entry prediction: a miss in `target_km` is #921's, not this craft's.
