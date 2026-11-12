// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>A planner's figure for one leg, m/s, with no margin: the whole leg and the capture it holds (0 for none).</summary>
    internal sealed class PlannedFigure
    {
        public double DeltaV;
        public double Capture;
        /// <summary>The leg's source as the Craft Check row names it: "plan_ascent", "plan_transfer, Optimal pick".</summary>
        public string Source = "";
        /// <summary>The UT the figure's window departs; a pricing after it searches again. Infinity for no window.</summary>
        public double DepartureUt = double.PositiveInfinity;
    }

    /// <summary>
    /// #998, the player's design for pricing a route from where the craft is (the Craft Check's text and <see cref="PlanLegs"/> alike): the
    /// ascent from the surface the craft is on is plan_ascent's; a trip from the craft's body that isn't a planet to its own moon (or a moon
    /// back to its planet) is plan_transfer's, at the player's Transfer Window pick (from home, only with a pick, as before); landings, and
    /// those Map trips, are the Delta-v Map's. When a planner refuses, the caller uses the Map's figure for the leg and calls it an estimate; a
    /// refusal alone never fails a check. The planners run through their own Core entry points (<see cref="PlanAscent"/>,
    /// <see cref="TransferWindows"/>, <see cref="DirectRoute"/>), and only where the route is priced up front (<c>price</c>: the Craft Check,
    /// its recheck, the plan proposal). What they priced is kept, and the checks in flight (a Margin check, <see cref="DropGuard"/>) read
    /// it, never search: with none kept, the caller uses the Map's.
    /// </summary>
    internal static class RoutePlanners
    {
        /// <summary>How long a priced answer serves a new pricing, game s (a Kerbin day, as <see cref="WindowChoice"/> reworks its offer).</summary>
        private const double DayS = 21600;
        private const int Kept = 64;

        private sealed class Answer
        {
            public PlannedFigure Figure;
            public string Refusal;
            /// <summary>What else the answer was worked from, which a new pricing must match: the situation, the pick's departure.</summary>
            public string Detail;
            public double Day;
        }

        /// <summary>The answers by a coarse key (the save's craft, the bodies, the pick), no live orbit or delta-v; oldest first in <see cref="Order"/>.</summary>
        private static readonly Dictionary<string, Answer> Answers = new Dictionary<string, Answer>();
        private static readonly List<string> Order = new List<string>();

        /// <summary>Test hook: the planner searches run on this thread.</summary>
        [ThreadStatic] internal static int Searches;

        /// <summary>The body the craft is on or orbits; null when the body table lacks it.</summary>
        public static SnapshotBody Start(GameSnapshot snapshot)
        {
            if (snapshot == null) return null;
            var name = snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) && !snapshot.IsGrounded ? snapshot.Orbit.Body : snapshot.Body;
            return snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The start when it is away from home and the route is priced from there; null at home, round the star or escaping, priced as before.</summary>
        public static SnapshotBody Away(GameSnapshot snapshot, string home)
        {
            var start = Start(snapshot);
            if (start == null) return null;
            var escaping = !snapshot.IsGrounded && snapshot.Orbit != null && snapshot.Orbit.Eccentricity >= 1;
            return start.Name != home && !string.IsNullOrEmpty(start.Parent) && !escaping ? start : null;
        }

        /// <summary>The player's rule: a planet to its own moon, or a moon back to its planet, is the Delta-v Map's trip; any other is plan_transfer's.</summary>
        public static bool IsMapTrip(SnapshotBody from, SnapshotBody to)
        {
            return string.Equals(to.Parent, from.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(from.Parent, to.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether the trip from <paramref name="from"/>, where the craft is, to <paramref name="to"/> is plan_transfer's: not a Map trip, and from home only at the player's pick for it.</summary>
        public static bool PlansTrip(GameSnapshot snapshot, SnapshotBody from, SnapshotBody to, string home)
        {
            return !IsMapTrip(from, to) && (from.Name != home || snapshot.WindowPick.IsFor(to.Name));
        }

        /// <summary>
        /// The ascent from the surface the craft is on to orbit, by plan_ascent (its dv_budget); null with <paramref name="refusal"/> when it
        /// gives none. With <paramref name="price"/> false, only the answer kept from a pricing.
        /// </summary>
        public static PlannedFigure Ascent(GameSnapshot snapshot, out string refusal, bool price)
        {
            // The site to 10 degrees of latitude: another landing on the body later isn't read this one's ascent.
            var key = "ascent|" + Save(snapshot) + "|" + snapshot.Body + "|" + Math.Round(snapshot.Latitude / 10);
            var detail = snapshot.Situation + "|" + Math.Round(snapshot.Latitude) + "|" + Math.Round(snapshot.Altitude / 100) + "|" + snapshot.Craft.NoseDrag.Count + "|"
                + string.Join(",", snapshot.Stages.Select(s => s.Number + ":" + Math.Round(s.DeltaVVac) + ":" + Math.Round(s.ThrustVacKn)).ToArray());
            return Find(key, detail, snapshot.Ut, price, out refusal, () =>
            {
                var outcome = new PlanAscent().Run(JsonReader.Parse("{}"), snapshot, null);
                var json = JsonReader.Parse(outcome.Json);
                var budget = JsonNode.Double(json["dv_budget"]);
                if (outcome.IsError || !budget.HasValue)
                    return Tuple.Create<PlannedFigure, string>(null, "plan_ascent: " + (JsonNode.Str(json["error"]) ?? outcome.Summary));
                return Tuple.Create(new PlannedFigure { DeltaV = budget.Value, Source = "plan_ascent" }, (string)null);
            });
        }

        /// <summary>
        /// The trip from the craft's body to <paramref name="to"/> by plan_transfer's search: <paramref name="window"/>'s figure when it is the
        /// player's row for that trip, else the snapshot's pick or setting's; null with <paramref name="refusal"/> when plan_transfer plans none.
        /// With <paramref name="price"/> false, only the answer kept from a pricing.
        /// </summary>
        public static PlannedFigure Transfer(GameSnapshot snapshot, SnapshotBody to, WindowRow window, int marginPercent, out string refusal, bool price)
        {
            var from = Start(snapshot);
            refusal = null;
            if (from == null || to == null) return null;
            var row = window != null && string.Equals(window.From, from.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(window.Body, to.Name, StringComparison.OrdinalIgnoreCase);
            var pick = row ? window.Kind.ToString() : snapshot.WindowPick.IsFor(to.Name) ? snapshot.WindowPick.Kind.ToString() : "setting " + snapshot.TimeVersusDeltaV;
            var key = "transfer|" + Save(snapshot) + "|" + from.Name + "|" + to.Name + "|" + pick + "|" + marginPercent;
            if (row)
            {
                // The player's row is plan_transfer's own answer: it is the priced figure.
                var figure = new PlannedFigure { DeltaV = window.TotalDv, Capture = window.CaptureDv, Source = "plan_transfer, " + window.Kind + " pick", DepartureUt = window.DepartureUt };
                Keep(key, new Answer { Figure = figure, Detail = "row", Day = Day(snapshot.Ut) });
                return figure;
            }
            var detail = snapshot.IsGrounded + "|" + (snapshot.WindowPick.IsFor(to.Name) && snapshot.WindowPick.DepartureUt.HasValue
                ? snapshot.WindowPick.DepartureUt.Value.ToString("0", CultureInfo.InvariantCulture) : "");
            return Find(key, detail, snapshot.Ut, price, out refusal, () => Search(snapshot, from, to, marginPercent));
        }

        private static Tuple<PlannedFigure, string> Search(GameSnapshot snapshot, SnapshotBody from, SnapshotBody to, int marginPercent)
        {
            var capture = ArrivalRule.Capture(snapshot, to.Name, null);
            var pe = MissionPrompts.ArrivalPe(to);
            var picked = snapshot.WindowPick.IsFor(to.Name) ? " pick" : "";
            if (DirectRoute.Applies(snapshot, to.Name))
            {
                var routes = DirectRoute.Find(snapshot, to.Name, pe, capture);
                if (routes.Error != null) return Tuple.Create<PlannedFigure, string>(null, "plan_transfer: " + routes.Error);
                return Tuple.Create(new PlannedFigure
                {
                    DeltaV = routes.Pick.TotalDv, Capture = routes.Pick.CaptureDv, Source = "plan_transfer, " + routes.WindowName + picked, DepartureUt = routes.Pick.DepartUt,
                }, (string)null);
            }
            if (!TransferWindows.IsPlanetTransfer(snapshot, to.Name) && !TransferWindows.IsSiblingTransfer(snapshot, to.Name))
                return Tuple.Create<PlannedFigure, string>(null, "plan_transfer plans no trip from " + from.Name + (snapshot.IsGrounded ? "'s surface" : "") + " to " + to.Name);
            var found = TransferWindows.Find(snapshot, to.Name, pe, capture, marginPercent);
            if (found.Error != null) return Tuple.Create<PlannedFigure, string>(null, "plan_transfer: " + found.Error);
            var pick = TransferWindows.Pick(found, snapshot);
            var kind = pick == found.Optimal && (pick != found.Soonest || (snapshot.WindowPick.IsFor(to.Name) && snapshot.WindowPick.Kind == WindowKind.Optimal)) ? "Optimal" : "Soonest";
            return Tuple.Create(new PlannedFigure { DeltaV = pick.TotalDv, Capture = pick.CaptureDv, Source = "plan_transfer, " + kind + picked, DepartureUt = pick.DepartureUt }, (string)null);
        }

        /// <summary>What tells one save's craft from another's: its name and persistent id, and the body table it flies in.</summary>
        private static string Save(GameSnapshot snapshot)
        {
            unchecked
            {
                var bodies = 17L;
                foreach (var b in snapshot.Bodies) bodies = bodies * 31 + (long)Math.Round(b.Sma) + (long)Math.Round(b.Radius);
                return snapshot.Craft.Name + "#" + snapshot.CraftPersistentId + "|" + snapshot.Bodies.Count + ":" + bodies.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static double Day(double ut)
        {
            return Math.Floor(ut / DayS);
        }

        /// <summary>
        /// The answer for <paramref name="key"/>. Pricing: the kept one when worked from the same <paramref name="detail"/> this game day and
        /// its window hasn't departed, else a new search (outside the lock), kept. Not pricing: the kept one whatever its age, or none.
        /// </summary>
        private static PlannedFigure Find(string key, string detail, double ut, bool price, out string refusal, Func<Tuple<PlannedFigure, string>> work)
        {
            Answer answer;
            lock (Answers) Answers.TryGetValue(key, out answer);
            if (!price)
            {
                refusal = answer == null ? "not priced" : answer.Refusal;
                return answer == null ? null : answer.Figure;
            }
            // The player's row (plan_transfer's own answer) serves any pricing at its pick until it departs.
            if (answer == null || (answer.Detail != detail && answer.Detail != "row") || answer.Day != Day(ut) || (answer.Figure != null && ut >= answer.Figure.DepartureUt))
            {
                Searches++;
                var got = work();
                answer = new Answer { Figure = got.Item1, Refusal = got.Item2, Detail = detail, Day = Day(ut) };
                Keep(key, answer);
            }
            refusal = answer.Refusal;
            return answer.Figure;
        }

        private static void Keep(string key, Answer answer)
        {
            lock (Answers)
            {
                if (Answers.ContainsKey(key)) Order.Remove(key);
                Answers[key] = answer;
                Order.Add(key);
                while (Order.Count > Kept)
                {
                    Answers.Remove(Order[0]);
                    Order.RemoveAt(0);
                }
            }
        }
    }
}
