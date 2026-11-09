# reentry holds attitude only near the air; deorbit stows after its burn (#941)

In-game check for #941. While `reentry` waits above the air its steering stays unlocked (no wheel or RCS work); it locks to surface retrograde a turn time plus 30 s before the air (Core's `hold_in`, from `TURNTIME` with `to_air`). A craft already in the air, too close, or with no turn time holds at once; `hold "none"` is unchanged. `deorbit` calls `shipsystems("entry")` after a successful burn, not before. The agent run that wrote this did not launch KSP.

## Check: `reentry_duna_powered`, or the Duna ship

Fly `tests/blocks/reentry_duna_powered`, or the Duna ship (m20261108-220659-ac85 layout) from a Duna orbit through `deorbit` and `reentry`. Results are in `KSP.log`.

1. Two `blocknote` lines with `phase charge`: `at start` and `at air_top`, each with `units` and `pct`. They are not in the report.
2. Between block start and shortly before the air, no `steering` or `RCS` lines for the hold (no on/off RCS lines, no steering-error notes). The `block call TURNTIME` line before the wait shows the turn time; the hold starts that plus 30 s before the air.
3. At the air top the craft points at surface retrograde.
4. Pass: charge at `air_top` is close to charge at `start` (the old flight lost about 2.2 units/s above the air), and `reentry` no longer aborts on `min_ec_pct` before the air.
5. `deorbit`: the panels stay out through the wait for the burn and retract only after the burn ends (the `shipsystems entry` log line comes after the burn, before the impact prediction). A burn that fails (e.g. `max_dv` too small, `deorbit-max-dv.md`) leaves the panels out.
