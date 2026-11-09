# #884 (occlusion half): the parent's face beside a stowed part, measured

**Result: no change made.** The change in drag from keeping the extended part's occlusion on the parent's face is a bound of 0.4% to 1.8% of the craft's drag area at 20 degrees of attitude error, and 0.2% to 0.9% at 10 degrees. The bound stays under 1% up to about 11 degrees; "well under 1%" is not shown at 20 degrees, and at 10 degrees the upper bound is 0.9%.

**Measured craft.** `KSP.log` (KSP_spike, 2026-11-08 15:02:05), `REENTRYIMPACT read:` and `block call REENTRYIMPACT`: heat shield `HeatShield1` 2.23 m² at Mach 2 (the `read:` line has no "bare" label, unlike the 14:59 lines), the craft's total 2.24 m² at Mach 2 (as the lines log them, in the Diagnosis's units), 1 Ship System read stowed (`longAntenna`, 0.01 m²). The shield is 99.6% of the craft's drag area, so the shield's face term is the denominator.

**Bound.** As in `docs/in-game/stowed-for-entry.md`: the parent face under a `solarPanels2` footprint changes by at most 0.15 m² (extended 0.45 minus retracted 0.30, from the part's cube data, not from the log). The share of the shield's face term is 0.15 × curve(20°) / (1.27 × curve(shield)) ≈ 4.3% before Cd, with the curve values there (1.06 for the parent, 2.9 for the shield). The Cd factor (`DRAG_CD_POWER` applied to the Cd after `DRAG_CD`'s curve) is 0.10 to 0.41 of the shield's, for the parent Cd of 0.72 (Mk1 pod) to 0.79 (Octo). So the share is 0.44% to 1.77% at 20 degrees. At 10 degrees the curve value of the parent face is about half (0.54), and the share is 0.22% to 0.90% (the shield's curve is held at 2.9, which is slightly conservative at 10 degrees).

**Not measured in game.** No KSP run was made. The 20 and 10 degree figures are bounds under the stated assumptions (attitude error, `DRAG_TIP` 3.1 at Mach 2, `DRAG_CD`'s curve); the flight's actual attitude is not in the log.

**Needs in-game check:** the flight's attitude during `reentry` (the `REENTRYIMPACT` line's craft drag area against a flown attitude), to decide whether the 10 degree or the 20 degree figure applies.
