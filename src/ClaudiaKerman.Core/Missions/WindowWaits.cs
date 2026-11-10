// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// A departure from the surface (#774): the planet whose window it waits for, then, once <see cref="WindowWaits.Solve"/> has
    /// run, that window and the ascent's Due Time.
    /// </summary>
    internal sealed class SurfaceDeparture
    {
        public SnapshotBody Planet;
        /// <summary>From a moon, the plan names a capture at its planet: the two-step route (<see cref="DirectRoute"/>), not the route's pick.</summary>
        public bool TwoStep;
        /// <summary>The window, leaving at the burn out of the craft's own body's orbit; null when the search found none.</summary>
        public TransferWindow Window;
        /// <summary>#966: <see cref="Window"/> is null because the held window has gone (<see cref="Tools.WindowSearch.HeldMissed"/>).</summary>
        public bool HeldMissed;
        /// <summary>From a moon, the route <see cref="Window"/> is read from; null from a planet.</summary>
        public RouteRow Route;
        /// <summary>The ascent lead, s (<see cref="WindowWaits.AscentLeadS"/>).</summary>
        public double LeadS;
        /// <summary>When the ascent is due; null when the wait is no more than <see cref="WindowWaits.LongWaitS"/>.</summary>
        public double? DueUt;
    }

    /// <summary>
    /// A wait for a Transfer Window longer than one Kerbin day is a Due Time (#616, the ADR 0004 amendment): the craft waits on
    /// the pad and the first Maneuver Sequence carries a Due Time at the window less the launch lead, so the player can fly other
    /// Missions meanwhile. A shorter wait stays an in-script warp. The window is #458's (<see cref="TransferWindows"/>), picked by
    /// the Mission Setting Time versus delta-v as the Mission request's plan_transfer rows pick it; a moon's is the same (#647).
    /// A craft on the surface of another body waits there too, before the ascent that starts its departure (#774): <see cref="AfterAscent"/>.
    /// </summary>
    internal static class WindowWaits
    {
        /// <summary>The longest window wait the script warps through itself, s: one Kerbin day. Fixed, not a Mission Setting.</summary>
        public const double LongWaitS = 6 * 3600;

        /// <summary>
        /// The launch lead, s: how long before the window's ejection burn the launch starts. An ascent to a low parking orbit
        /// (about 10 minutes) and up to one low Kerbin orbit (about 35 minutes) to reach the ejection point, with room to spare.
        /// </summary>
        public const double LaunchLeadS = 3600;

        /// <summary>
        /// What an ascent from the surface takes before its departure burn is <see cref="AscentAllowanceS"/>, one low orbit of the
        /// body to reach the burn point, and <see cref="AscentMarginS"/>. The launch lead's own figures, by body (<see cref="AscentLeadS"/>).
        /// </summary>
        public const double AscentAllowanceS = 600;
        /// <summary>The ascent lead's room to spare, s.</summary>
        public const double AscentMarginS = 900;

        /// <summary>The ascent lead from <paramref name="body"/>, s.</summary>
        public static double AscentLeadS(SnapshotBody body)
        {
            var low = DeltaVMap.LowOrbit(body);
            return AscentAllowanceS + 2 * Math.PI * Math.Sqrt(low * low * low / body.Gm) + AscentMarginS;
        }

        /// <summary>
        /// The first planet the Mission Goal names that the craft's planet transfers to, or moon of the craft's planet (#647), or the
        /// planet of a moon of another planet (#774: Ike gives Duna's window); null for none.
        /// </summary>
        public static SnapshotBody Planet(GameSnapshot snapshot, string goal)
        {
            if (snapshot == null) return null;
            var system = new BodySystem(snapshot.Bodies);
            return MissionPrompts.GoalBodies(snapshot, goal)
                .Select(b => TransferWindows.IsMoonTransfer(snapshot, b.Name) ? b : PlanetTransfer(snapshot, system, b))
                .FirstOrDefault(b => b != null);
        }

        /// <summary><paramref name="body"/> or the first of its parents the craft's planet transfers to; null for none.</summary>
        private static SnapshotBody PlanetTransfer(GameSnapshot snapshot, BodySystem system, SnapshotBody body)
        {
            while (body != null && !TransferWindows.IsPlanetTransfer(snapshot, body.Name)) body = system.Parent(body);
            return body;
        }

        /// <summary>
        /// #774: the departure the craft on the surface waits for, when <paramref name="plan"/> is an ascent and the plan's next leg
        /// leaves for another planet, or a manual stretch with those two next (<paramref name="step"/>, 1-based, is the step it flies);
        /// null otherwise, as for a return to the craft's own planet, which has no window. The ascent is the script's first block, or
        /// its words; the departure is the script's own transfer_to, or the words of the legs after it (<see cref="SequenceWords"/>).
        /// </summary>
        public static SurfaceDeparture AfterAscent(GameSnapshot snapshot, SequencePlan plan, IList<PlannedSequence> legs, int step)
        {
            if (snapshot == null || plan == null || (snapshot.Situation != "LANDED" && snapshot.Situation != "SPLASHED")) return null;
            var body = SequenceWords.Body(snapshot, snapshot.Body);
            if (body == null) return null;
            legs = legs ?? new List<PlannedSequence>();
            var from = step;
            if (plan.Manual)
            {
                if (step < 0 || step >= legs.Count || legs[step].Manual || !SequenceWords.Ascent.IsMatch(SequenceWords.ActionWords(legs[step]))) return null;
                from = step + 1;
            }
            else
            {
                var calls = BlockOptionCheck.Calls(plan.Script).ToList();
                var first = calls.FirstOrDefault();
                if ((first == null || (first.Block != "launch" && first.Block != "ascend")) && !SequenceWords.Ascent.IsMatch(plan.Name)) return null;
                var named = calls.Where(c => c.Block == "transfer_to").Select(c => SequenceWords.Body(snapshot, (c.Option("target") ?? "").Trim('"'))).FirstOrDefault(b => b != null);
                var flown = Elsewhere(snapshot, body, named);
                if (flown != null) return new SurfaceDeparture { Planet = flown };
                // "Ascend from Duna and return to Kerbin": the step in hand goes too.
                if (step >= 1 && step <= legs.Count && IsTrip(SequenceWords.ActionWords(legs[step - 1]))) from = step - 1;
            }
            return Departure(snapshot, body, legs, from);
        }

        /// <summary>
        /// The legs from <paramref name="from"/>, while they lead on from the ascent: a trip to another planet is the departure; a
        /// return to the craft's own planet and a capture there are the way to it (from a moon, by either route of #760).
        /// </summary>
        internal static SurfaceDeparture Departure(GameSnapshot snapshot, SnapshotBody body, IList<PlannedSequence> legs, int from)
        {
            var atPlanet = false;
            var twoStep = false;
            for (var i = Math.Max(0, from); i < legs.Count; i++)
            {
                var leg = legs[i];
                var words = SequenceWords.ActionWords(leg);
                if (leg.Manual || !MissionCards.FliesCraft(snapshot.Craft.Name, leg.Vessel)) return null;
                if (IsTrip(words))
                {
                    var target = SequenceWords.TripTarget(snapshot, words, body, CraftCheckFlow.HomeBody);
                    var planet = Elsewhere(snapshot, body, target);
                    if (planet != null) return new SurfaceDeparture { Planet = planet, TwoStep = twoStep };
                    if (target == null || target == body) return null;
                    atPlanet = true;
                }
                else if (SequenceWords.Capture.IsMatch(words) && !SequenceWords.Surface.IsMatch(words)) twoStep = atPlanet;
                else return null;
            }
            return null;
        }

        private static bool IsTrip(string words)
        {
            return SequenceWords.Trip.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words);
        }

        /// <summary>The planet <paramref name="target"/> is or orbits, when it isn't the one <paramref name="home"/> is or orbits; null otherwise.</summary>
        private static SnapshotBody Elsewhere(GameSnapshot snapshot, SnapshotBody home, SnapshotBody target)
        {
            if (target == null) return null;
            var system = new BodySystem(snapshot.Bodies);
            var planet = PlanetOf(system, target);
            return planet == PlanetOf(system, home) ? null : planet;
        }

        private static SnapshotBody PlanetOf(BodySystem system, SnapshotBody body)
        {
            var parent = system.Parent(body);
            while (parent != null && !string.IsNullOrEmpty(parent.Parent))
            {
                body = parent;
                parent = system.Parent(body);
            }
            return body;
        }

        /// <summary>
        /// The window for <paramref name="departure"/> from <paramref name="now"/>, and the ascent's Due Time. The window is the one the
        /// craft can take once it is up, so it is searched as from the body's low orbit; a shorter wait than <see cref="LongWaitS"/>
        /// is no Due Time. From a planet the Due Time is the departure less the ascent lead, or DeparturePlane's launch into an orbit
        /// holding the asymptote (plan_transfer's <c>at_ut</c>) when that is earlier. From a moon no launch plane applies: the
        /// departure is the burn out of its orbit (<see cref="RouteRow.LatestDepartUt"/>), less the ascent lead.
        /// </summary>
        public static void Solve(GameSnapshot snapshot, SurfaceDeparture departure, TimeVersusDeltaV setting, int marginPercent, double now, WindowPick chosen = null)
        {
            var home = SequenceWords.Body(snapshot, snapshot.Body);
            var planet = departure.Planet;
            var at = snapshot.At(now);
            at.TimeVersusDeltaV = setting;
            at.MarginPercent = marginPercent;
            at.WindowPick = chosen ?? WindowPick.None;
            var view = DirectRoute.FromLowOrbit(at, home, now);
            // As the departure flies and the rows show it (#782): a trip home ends in reentry, with no capture to price.
            var capture = ArrivalRule.Capture(at, planet.Name, null);
            departure.LeadS = AscentLeadS(home);
            double due;
            if (DirectRoute.Applies(view, planet.Name))
            {
                var routes = DirectRoute.Find(view, planet.Name, MissionPrompts.ArrivalPe(planet), capture);
                if (routes.Error != null) return;
                var row = departure.TwoStep ? routes.TwoStep : routes.Pick;
                departure.Route = row;
                departure.Window = AsWindow(row);
                due = row.LatestDepartUt - departure.LeadS;
            }
            else
            {
                string error;
                var problem = TransferWindows.Problem.Of(view, planet.Name, MissionPrompts.ArrivalPe(planet), capture, out error);
                if (problem == null) return;
                var found = TransferWindows.Search(problem, marginPercent);
                // #966: a held window that has gone is waited for no more, nor traded for a later one.
                departure.HeldMissed = found.Error == null && found.HeldMissed;
                if (found.Error != null || departure.HeldMissed) return;
                departure.Window = TransferWindows.Pick(found, setting, chosen);
                due = departure.Window.DepartureUt - departure.LeadS;
                var launch = LaunchFor(snapshot, home, problem, departure.Window, due, now);
                if (launch != null && launch.Error == null) due = Math.Min(due, launch.Ut);
            }
            if (due - now > LongWaitS) departure.DueUt = due;
        }

        /// <summary>A route's figures as a window, leaving at the latest burn out of the moon's orbit that makes its ejection.</summary>
        public static TransferWindow AsWindow(RouteRow row)
        {
            return new TransferWindow { DepartureUt = row.LatestDepartUt, ArrivalUt = row.ArrivalUt, EjectionDv = row.EjectionDv, CaptureDv = row.CaptureDv, TotalDv = row.TotalDv };
        }

        private static LaunchWindow LaunchFor(GameSnapshot snapshot, SnapshotBody home, TransferWindows.Problem problem, TransferWindow window, double byUt, double now)
        {
            var leaving = problem.Leaving(window.DepartureUt, window.ArrivalUt - window.DepartureUt);
            if (!leaving.HasValue) return null;
            // The site has turned since the snapshot, which may be older (the player is back from another Mission).
            var turned = home.RotationPeriod == 0 ? 0 : 360 * (now - snapshot.Ut) / home.RotationPeriod;
            return DeparturePlane.ForAsymptote(home, now, snapshot.Latitude, snapshot.CelestialLongitude + turned, leaving.Value, byUt, problem.ParkingPe);
        }

        /// <summary>The window to <paramref name="planet"/> (a planet or a moon) from <paramref name="snapshot"/>'s UT, the player's pick for it (#686) or else the Mission Setting's; null when the search found none, or (#966) the held window has gone.</summary>
        public static TransferWindow Window(GameSnapshot snapshot, SnapshotBody planet, TimeVersusDeltaV setting, int marginPercent, WindowPick chosen = null)
        {
            bool heldMissed;
            return Window(snapshot, planet, setting, marginPercent, chosen, out heldMissed);
        }

        /// <summary><see cref="Window(GameSnapshot, SnapshotBody, TimeVersusDeltaV, int, WindowPick)"/>, saying when it is null because the held window has gone.</summary>
        public static TransferWindow Window(GameSnapshot snapshot, SnapshotBody planet, TimeVersusDeltaV setting, int marginPercent, WindowPick chosen, out bool heldMissed)
        {
            heldMissed = false;
            if (snapshot == null || planet == null) return null;
            // #966: a held pick's search keeps to its window.
            if (chosen != null && chosen.DepartureUt.HasValue)
            {
                snapshot = snapshot.At(snapshot.Ut);
                snapshot.WindowPick = chosen;
            }
            var found = TransferWindows.Find(snapshot, planet.Name, MissionPrompts.ArrivalPe(planet), true, marginPercent);
            heldMissed = found.Error == null && found.HeldMissed;
            return found.Error != null || heldMissed ? null : TransferWindows.Pick(found, setting, chosen);
        }

        /// <summary>The Due Time for a window leaving at <paramref name="departureUt"/>: the window less the launch lead, when that is more than <see cref="LongWaitS"/> from <paramref name="now"/>; otherwise null (the script warps there).</summary>
        public static double? DueTime(double departureUt, double now)
        {
            var due = departureUt - LaunchLeadS;
            return due - now > LongWaitS ? due : (double?)null;
        }

        /// <summary>
        /// The window leaving at <paramref name="departureUt"/> is missed: the player came back after it. Approving late inside the
        /// launch lead is not a miss: the window is days wide, and the script takes the ejection when it reaches orbit.
        /// </summary>
        public static bool Missed(double departureUt, double now)
        {
            return now > departureUt;
        }

        /// <summary>
        /// The next window fits the Craft Check budget: the Mission Plan's budget sum, with the next window's total in place of the
        /// planned one's, has the Craft Check's margin under the craft's delta-v (the budget sum only: the Craft Check is not run
        /// again). With no delta-v figure for the craft, the next window fits only if it costs no more than the planned one.
        /// </summary>
        public static bool Fits(double budgetSum, double plannedDv, double nextDv, double? craftDeltaV, int marginPercent)
        {
            if (!craftDeltaV.HasValue) return nextDv <= plannedDv;
            var bar = MarginBar.Compute(craftDeltaV, budgetSum - plannedDv + nextDv, marginPercent);
            return bar == null || bar.Level == MarginLevel.Enough;
        }

        /// <summary>"the Duna window", what a Due Time for a window waits for.</summary>
        public static string WaitsFor(string planet)
        {
            return "the " + planet + " window";
        }
    }
}
