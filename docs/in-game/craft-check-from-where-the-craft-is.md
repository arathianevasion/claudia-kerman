# The Craft Check prices the route from where the craft is (#998)

In craft-checks/20261111-175744-913, Mun Shot v2 was landed on the Mun with the goal "land in the greater flats on minmus". The Craft Check priced it from Kerbin's pad (launch:Kerbin 3,418 m/s, the trip from low Kerbin orbit 346, land:Minmus 177), and judged a liftoff from Kerbin at TWR 0.3. It wanted 4,532 m/s with the margin against the craft's 1,518, and said the Mission couldn't start.

Now the route starts where the craft is (the player's design, 2026-11-11):

- **Ascent:** from the surface, plan_ascent on that body. plan_ascent plans launches through air only, so on an airless body like the Mun it refuses. The ascent is then the Delta-v Map's `ascend:` row with this craft's gravity loss, marked "estimate, the Map's: plan_ascent: Mun has no air…". Kerbin's launch is as before.
- **Trips:** a planet to its own moon (Kerbin to the Mun), or a moon back to its planet, is the Delta-v Map's trip. Any other trip from the craft's body is plan_transfer's, at the player's Transfer Window pick. The row says `(plan_transfer, Optimal pick)`. If plan_transfer refuses, the row is the Map's trip, marked an estimate. That alone never fails the check.
- **Landings** stay the Map's.
- `launch:Kerbin` and the trips out of low Kerbin orbit are gone when the craft isn't at Kerbin.
- **TWR:** the table is for the body the craft lifts off from and the goal's bodies, not Kerbin's system.
- **Only the first hop** is planned: with "Land on Minmus and return to Kerbin", the trip is the Mun to Minmus, and Minmus back to Kerbin is the Map's `return:Minmus`. There's no `return:Mun` line.
- **The Mission Plan's per-leg check** (`leg_under_map`) judges the ascent and the first trip by the same figures. The planners run only when the route is priced: the Craft Check, its recheck and a proposed plan. In flight, the Margin check and the drop guard read the figure priced then (or the Map's), and never search.
- **From Kerbin** with a Transfer Window picked, the Craft Check's trip row is plan_transfer's at that pick even when no window row is passed (a recheck), as the per-leg check judges it.

The report's "257 m/s" was the Optimal row's ejection. With the 88 m/s capture into low Minmus orbit, the trip is 346. The route is then about 680 (Mun ascent) + 346 + 198 (Minmus landing at TWR 24.8) = 1,224 m/s, or 1,408 with the 15% margin. That passes against 1,518.

**To check in game:**

1. Load the save with Mun Shot v2 landed on the Mun (or land any craft with about 1,500 m/s on the Mun). Open the start form, enter "land in the greater flats on minmus", and pick the **Optimal** Transfer Window.
2. **Run the Craft Check.** It passes, or passes with Warnings that aren't `delta_v_short` or `twr_below_one`. Claudia's summary judges roughly 1,200 to 1,450 m/s against the craft's figure, not 4,000+.
3. **Its request** is in `saves/<save>/ClaudiaKerman/craft-checks/<newest>/request-*.json`, in the first user message. Check four things:
   - The Delta-v Map lines start with `ascend:Mun` (estimate) and `transfer:Mun>Minmus` with `(plan_transfer, Optimal pick)`.
   - The transfer's figure equals the Optimal row's "total" on the start form, and it says "includes the capture" with the row's capture.
   - There's no `launch:Kerbin` line and no `transfer:Minmus:` line.
   - The thrust-over-weight line names "the body the craft lifts off from (Mun)", and its JSON has no Kerbin column.
4. **Start a pad Mission to the Mun** ("Land on the Mun and return to Kerbin"). Its Craft Check request has the same Delta-v Map lines as before: launch:Kerbin, land:Kerbin, transfer:Mun, polar:Mun, land:Mun, ascend:Mun, return:Mun. None says plan_transfer or estimate.
5. **From the Mun again, goal "Land on Minmus and return to Kerbin".** Its request has `transfer:Mun>Minmus` and `return:Minmus`, and no `return:Mun` or `launch:Kerbin` line.
6. **Report on #998:** the Craft Check verdict and summary, the `ascend:Mun` and `transfer:Mun>Minmus` lines, and whether the first Mission Plan's card shows a `leg_under_map` Warning (with what figures).
