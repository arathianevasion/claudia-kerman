# dock_hold drops a settled or near level when its port is knocked (#722)

dock_hold's level (`settled` within 2° and 0.1°/s for 2 s, `near` within 5° and 0.5°/s for 2 s) now drops after the port has been outside its band for 1 s: settled to near while still inside the loose band, else to active. `dock` then holds still again. Install and `KSP.log` location are as in `phase-3-gate.md`. The rule is Core's `DockGuidance.HoldLevel`; its unit tests cover the bands and clocks, so this is one flight.

You need a crewed or probe-controlled craft with RCS, and a Target Vessel station with a kOS processor, power and a docking port, as in `tests/blocks/dock.ks`. Set `debug_tools = true` so `KSP.log` carries the `dock_hold on part ... settled on its aim`, `loosely settled` and `re-aimed, not settled` lines (an empty level logs the last).

1. Start a Mission that runs dock from 100 to 150 m out. Wait until the hold reports settled (`KSP.log`: `dock_hold on part <id> settled on its aim`) and the craft is closing.
2. Knock the station: from a second vessel or by hand, give the station a spin of about 1°/s (a short RCS pulse from the craft's plume at close range, or a tap on the station with a docked probe's RCS). It must be a spin or knock, not a re-aim of over 15°.
3. Within about a second of the station leaving 2° or 0.1°/s, and 1 s after leaving the band: `KSP.log` shows the hold report a drop (`loosely settled` when the spin is under 0.5°/s and the aim within 5°, else `re-aimed, not settled`). The craft stops closing and holds still (its relative velocity goes to 0).
4. A knock that lasts under 1 s (a tap that settles at once) changes nothing: no drop is logged and the craft keeps closing.
5. Let the station re-settle. `KSP.log` shows `settled on its aim` again about 2 s after it is back inside 2° and 0.1°/s, and the craft closes again. If it only reached near, the craft waits `settle_timeout` (300 s by default) from the drop before it closes on a loose settle.
