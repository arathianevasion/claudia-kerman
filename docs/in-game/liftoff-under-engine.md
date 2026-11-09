# launch and ascend clear what is still under a firing engine (#945)

In-game check for #945. From the ground, `launch` and `ascend` fire the decoupler nearest each engine that gives thrust, when one sits under it, before the throttle comes up, and let the parts leave. Core names the decoupler (`ADDONS:CLAUDIA:LIFTOFFCLEAR`, `LiftoffClear`); the firing is `claudia_stage_into_engines`'s, shared by both blocks. Engines in later stages never count, a launch clamp is not a decoupler, and `ascend` from flight (its abort to orbit) does nothing. The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: run liftoff_under_engine

The craft is the `launch_hop` craft with a TD-12 and a heat shield hung under its LV-T45, and the test lights the engine first, as a powered descent leaves it. It was hand-built without the VAB: if it loads with those two parts misplaced, open it in the VAB, fix and re-save it.

1. Pick `liftoff_under_engine` and press **Run** (up to 300 s).
2. It passes (`PASS liftoff_under_engine`, every `ok` line), including `test_cleared_before_climb` true: the shield was gone by the time the throttle first rose.
3. In `KSP.log`, the block's `liftoff_clear` note reads `fired under the engines: Decoupler.1, HeatShield1`. The craft climbs clear of both and hops to 8 km as in `launch_hop`.

## Check 2: a normal launch shows no such note

Run `launch_orbit` and `launch_hop`. Neither writes a `liftoff_clear` note in `KSP.log`, and both pass as before (the throttle now starts at 0 for the staging step and is set to 1 once the engines give thrust).

## Check 3: the Duna ship

Fly the Duna ship to a powered landing, then to liftoff with `launch` (Duna has air) with the TD-12 and heat shield 2 still under the aerospike. They separate before the throttle comes up and the `liftoff_clear` note names them. If the note says `nothing separated`, bring its `drop` and `liftoff_clear` lines to the player.
