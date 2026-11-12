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
    }

    /// <summary>
    /// #998, the player's design for pricing a route from where the craft is (the Craft Check's text and <see cref="PlanLegs"/> alike): the
    /// ascent from the surface the craft is on is plan_ascent's; a trip from the craft's body that isn't a planet to its own moon (or a moon
    /// back to its planet) is plan_transfer's, at the player's Transfer Window pick; landings, and those Map trips, are the Delta-v Map's.
    /// When a planner refuses, the caller uses the Map's figure for the leg and calls it an estimate; a refusal alone never fails a check.
    /// The planners run through their own Core entry points (<see cref="PlanAscent"/>, <see cref="TransferWindows"/>, <see cref="DirectRoute"/>),
    /// and the last answers are kept for a game day, as <see cref="PlanLegs"/> is asked again at every Margin check and drop.
    /// </summary>
    internal static class RoutePlanners
    {
        /// <summary>How long an answer is kept, game s (a Kerbin day, as <see cref="WindowChoice"/> reworks its offer).</summary>
        private const double KeepS = 21600;
        private const int Kept = 16;
        private static readonly Dictionary<string, Tuple<PlannedFigure, string>> Answers = new Dictionary<string, Tuple<PlannedFigure, string>>();

        /// <summary>The body the craft is on or orbits; null when the body table lacks it.</summary>
        public static SnapshotBody Start(GameSnapshot snapshot)
        {
            if (snapshot == null) return null;
            var name = snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) && !snapshot.IsGrounded ? snapshot.Orbit.Body : snapshot.Body;
            return snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The player's rule: a planet to its own moon, or a moon back to its planet, is the Delta-v Map's trip; any other is plan_transfer's.</summary>
        public static bool IsMapTrip(SnapshotBody from, SnapshotBody to)
        {
            return string.Equals(to.Parent, from.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(from.Parent, to.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The ascent from the surface the craft is on to orbit, by plan_ascent (its dv_budget); null with <paramref name="refusal"/> when it gives none.</summary>
        public static PlannedFigure Ascent(GameSnapshot snapshot, out string refusal)
        {
            var key = "ascent|" + snapshot.Craft.Name + "|" + snapshot.Bodies.Count + "|" + snapshot.Body + "|" + Math.Round(snapshot.Altitude / 100) + "|" + snapshot.Craft.NoseDrag.Count + "|"
                + string.Join(",", snapshot.Stages.Select(s => s.Number + ":" + Math.Round(s.DeltaVVac) + ":" + Math.Round(s.ThrustVacKn)).ToArray());
            return Remembered(key, snapshot.Ut, out refusal, () =>
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
        /// </summary>
        public static PlannedFigure Transfer(GameSnapshot snapshot, SnapshotBody to, WindowRow window, int marginPercent, out string refusal)
        {
            var from = Start(snapshot);
            refusal = null;
            if (from == null || to == null) return null;
            if (window != null && string.Equals(window.From, from.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(window.Body, to.Name, StringComparison.OrdinalIgnoreCase))
                return new PlannedFigure { DeltaV = window.TotalDv, Capture = window.CaptureDv, Source = "plan_transfer, " + window.Kind + " pick" };
            var key = "transfer|" + snapshot.Craft.Name + "|" + snapshot.Bodies.Count + "|" + from.Name + "|" + snapshot.IsGrounded + "|" + to.Name + "|" + snapshot.WindowPick.Key + "|" + snapshot.TimeVersusDeltaV + "|" + marginPercent
                + "|" + (snapshot.Orbit == null ? "" : Math.Round(snapshot.Orbit.SemiMajorAxis / 1000).ToString(CultureInfo.InvariantCulture)) + "|" + Math.Round(snapshot.Stages.Sum(s => s.DeltaVVac) / 10);
            return Remembered(key, snapshot.Ut, out refusal, () => Search(snapshot, from, to, marginPercent));
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
                return Tuple.Create(new PlannedFigure { DeltaV = routes.Pick.TotalDv, Capture = routes.Pick.CaptureDv, Source = "plan_transfer, " + routes.WindowName + picked }, (string)null);
            }
            if (!TransferWindows.IsPlanetTransfer(snapshot, to.Name) && !TransferWindows.IsSiblingTransfer(snapshot, to.Name))
                return Tuple.Create<PlannedFigure, string>(null, "plan_transfer plans no trip from " + from.Name + (snapshot.IsGrounded ? "'s surface" : "") + " to " + to.Name);
            var found = TransferWindows.Find(snapshot, to.Name, pe, capture, marginPercent);
            if (found.Error != null) return Tuple.Create<PlannedFigure, string>(null, "plan_transfer: " + found.Error);
            var pick = TransferWindows.Pick(found, snapshot);
            var kind = pick == found.Optimal && (pick != found.Soonest || (snapshot.WindowPick.IsFor(to.Name) && snapshot.WindowPick.Kind == WindowKind.Optimal)) ? "Optimal" : "Soonest";
            return Tuple.Create(new PlannedFigure { DeltaV = pick.TotalDv, Capture = pick.CaptureDv, Source = "plan_transfer, " + kind + picked }, (string)null);
        }

        /// <summary>The kept answer for <paramref name="key"/> from this game day, or a new one.</summary>
        private static PlannedFigure Remembered(string key, double ut, out string refusal, Func<Tuple<PlannedFigure, string>> work)
        {
            key += "|" + Math.Floor(ut / KeepS).ToString(CultureInfo.InvariantCulture);
            Tuple<PlannedFigure, string> answer;
            lock (Answers)
            {
                if (!Answers.TryGetValue(key, out answer))
                {
                    answer = work();
                    if (Answers.Count >= Kept) Answers.Clear();
                    Answers[key] = answer;
                }
            }
            refusal = answer.Item2;
            return answer.Item1;
        }
    }
}
