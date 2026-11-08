// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What a <c>return</c> or <c>deorbit</c> left at "auto" should aim for, and the option values that say otherwise.</summary>
    internal sealed class LandingTargetAsk
    {
        /// <summary>The planet the craft comes down on.</summary>
        public string Planet = "";
        /// <summary>The periapsis asked of a return, m; null for none given or a value not read yet.</summary>
        public double? TargetPe;
        /// <summary>A return's target_inc, degrees; null for "any".</summary>
        public double? TargetInc;
        /// <summary>A return's window, UT; null for "next".</summary>
        public double? Window;
    }

    /// <summary>What <see cref="HomeLanding.Arrival"/> found for a trip home from another planet, and why.</summary>
    internal sealed class HomeArrivalPlan
    {
        /// <summary>The delta-v left and covers capture and deorbit over the reserve: fly <c>capture</c>, <c>deorbit</c>, <c>reentry</c>.</summary>
        public bool CaptureFirst;
        /// <summary>The craft's delta-v on arrival, m/s: what it has now less any ascent and the departure; null when stock can't say.</summary>
        public double? DvLeft;
        public double CaptureDv;
        public double DeorbitDv;
        /// <summary>What the rule leaves to spend on the two, m/s (<see cref="DeorbitBurn.AutoMaxDv"/>).</summary>
        public double SpareDv;
        /// <summary>The periapsis altitude to arrive at, m: low orbit's to capture, else the entry's.</summary>
        public double ArrivalPe;
        /// <summary>Both burns' m/s, against <see cref="SpareDv"/>.</summary>
        public double Need
        {
            get { return CaptureDv + DeorbitDv; }
        }

        /// <summary>The choice in words, for plan_transfer's result.</summary>
        public string Note = "";
    }

    /// <summary>
    /// The landing target default (GLOSSARY, #790): every return to Kerbin aims for KSC unless the Mission Goal names another place, a value the mod sets itself as it sets <c>transfer_to</c>'s
    /// plane (#788), with no prompt text. A target Claudia gives wins. So does a choice that says the trip is not a landing or is timed for something else: a periapsis over the air (a moon's
    /// two-step route out), a window (a direct route's ejection) or a target_inc (the correction aimed at a landing leaves that out). Pure.
    /// </summary>
    internal static class HomeLanding
    {
        public const string Ksc = "ksc";
        public const string None = "none";

        /// <summary>Words that say where to come down that aren't a biome or a Point of Interest, in the player's own Mission Goal only: Claudia's legs say "splashdown" of any return.</summary>
        private static readonly Regex PlaceWords = new Regex(@"\b(splash(es|ing)?\s*down|splash|ocean|sea|water|island|airfield|runway|pole|anywhere|wherever)\b", RegexOptions.IgnoreCase);
        /// <summary>A pair of signed numbers in brackets, "(3.1, 24.6)": a site's latitude and longitude.</summary>
        private static readonly Regex LatLng = new Regex(@"\(\s*-?\d+(\.\d+)?\s*°?\s*,\s*-?\d+(\.\d+)?\s*°?\s*\)");

        /// <summary>Where one clause of a Mission Goal or a leg ends. Not at a comma or a bare point, which a latitude and longitude have.</summary>
        private static readonly Regex Clauses = new Regex(@"\.\s|;|\band\b|\bthen\b", RegexOptions.IgnoreCase);

        /// <summary>The target an "auto" stands for: <see cref="Ksc"/> or <see cref="None"/>.</summary>
        public static string DefaultTarget(GameSnapshot snapshot, LandingTargetAsk ask)
        {
            if (snapshot == null || !string.Equals(ask.Planet, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase)) return None;
            var home = snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase));
            if (home == null || home.AtmosphereHeight <= 0) return None;
            if (ask.TargetInc.HasValue || ask.Window.HasValue) return None;
            if (ask.TargetPe.HasValue && ask.TargetPe.Value >= home.AtmosphereHeight) return None;
            return NamesAPlace(snapshot, home) ? None : Ksc;
        }

        /// <summary>#880: the Mission's landing target (#892) that an "auto" stands for, when it was set on this planet and the ask is one that lands; null otherwise. A return aimed at "none" has cleared it by then.</summary>
        public static LandingTarget Recorded(GameSnapshot snapshot, LandingTargetAsk ask)
        {
            var target = snapshot == null ? null : snapshot.LandingTarget;
            if (target == null || !string.Equals(target.Body, ask.Planet, StringComparison.OrdinalIgnoreCase) || !string.Equals(ask.Planet, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase) || ask.TargetInc.HasValue || ask.Window.HasValue) return null;
            var home = snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, ask.Planet, StringComparison.OrdinalIgnoreCase));
            return home == null || (ask.TargetPe.HasValue && ask.TargetPe.Value >= home.AtmosphereHeight) ? null : target;
        }

        /// <summary>The craft's delta-v by stock's simulator, m/s; null when stock can't say.</summary>
        public static double? StockDv(GameSnapshot snapshot)
        {
            return snapshot.DeltaVSimulator && snapshot.Stages.Count > 0 && !snapshot.DeltaVUnreliable ? snapshot.Stages.Sum(s => s.DeltaVVac) : (double?)null;
        }

        /// <summary>
        /// #880: the landing a <c>transfer_to</c> home from another planet comes in for: KSC by the default rule, or null for any other trip or a Mission that names another place. The Transfer Window
        /// is unchanged by it; the Late Trim (#892) aims at it within the spare delta-v.
        /// </summary>
        public static LandingTarget ArrivalTarget(GameSnapshot snapshot, string target, double targetPe)
        {
            if (snapshot == null || !string.Equals(target, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase)) return null;
            var system = new BodySystem(snapshot.Bodies);
            var at = system.Find(snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body);
            var home = system.Find(target);
            if (at == null || home == null || InSystemOf(system, at, home)) return null;
            if (DefaultTarget(snapshot, new LandingTargetAsk { Planet = home.Name, TargetPe = targetPe }) != Ksc) return null;
            return new LandingTarget { Body = home.Name, Latitude = ReturnAim.KscLatitude, Longitude = ReturnAim.KscLongitude };
        }

        /// <summary>Whether the Mission Goal, or a leg ahead or a site picked for it, names a place on <paramref name="home"/> to come down that isn't KSC.</summary>
        private static bool NamesAPlace(GameSnapshot snapshot, SnapshotBody home)
        {
            var site = snapshot.PickedSite;
            if (site != null && string.Equals(site.Body, home.Name, StringComparison.OrdinalIgnoreCase)) return true;
            var goal = snapshot.MissionGoal ?? "";
            if (AboutHome(snapshot, home, goal).Any(clause => PlaceWords.IsMatch(clause))) return true;
            if (MissionPrompts.GoalBiomes(snapshot, goal).Any(b => string.Equals(b.Body, home.Name, StringComparison.OrdinalIgnoreCase))) return true;
            var words = AboutHome(snapshot, home, goal).ToList();
            if (snapshot.LegsAhead != null) words.AddRange(snapshot.LegsAhead.SelectMany(leg => AboutHome(snapshot, home, SequenceWords.Of(leg))));
            foreach (var text in words)
            {
                if (LatLng.IsMatch(text)) return true;
                foreach (var poi in PointsOfInterest.Merge(home.Name, new SurfaceObject[0]))
                    if (Regex.IsMatch(text, @"(?<![\w-])" + Regex.Escape(poi.Name) + @"(?![\w-])", RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The clauses of <paramref name="words"/> that name no other body: "land at the Mun's north pole and come home" says nothing of where at home.</summary>
        private static IEnumerable<string> AboutHome(GameSnapshot snapshot, SnapshotBody home, string words)
        {
            return Clauses.Split(words ?? "").Where(clause =>
            {
                var named = SequenceWords.NamedBody(snapshot, clause);
                return named == null || named == home;
            });
        }

        /// <summary>
        /// From another planet (or its moon) to Kerbin, at the point the arrival is planned: capture into low orbit then deorbit toward KSC when the delta-v left on arrival, less the reserve,
        /// covers both by the Delta-v Map's capture figure and the least deorbit burn from low orbit; else come in directly and let the corrections get as close as they can. Null for any other trip,
        /// or with a Mission Goal that names another place, or when the map has no figure.
        /// </summary>
        public static HomeArrivalPlan Arrival(GameSnapshot snapshot, string target)
        {
            if (snapshot == null || !ArrivalRule.EndsInReentry(snapshot, target)) return null;
            var system = new BodySystem(snapshot.Bodies);
            var at = system.Find(snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body);
            var home = system.Find(target);
            if (at == null || home == null || InSystemOf(system, at, home) || system.Parent(at) == null) return null;
            if (DefaultTarget(snapshot, new LandingTargetAsk { Planet = home.Name }) != Ksc) return null;
            var map = DeltaVMap.Compute(snapshot.Bodies, CraftCheckFlow.HomeBody);
            var trip = map.Trip(at.Name, home.Name);
            if (trip == null) return null;
            var ascent = snapshot.IsGrounded ? map.Legs.Where(leg => leg.Id == "ascend:" + at.Name).Select(leg => leg.DeltaV).FirstOrDefault() : 0;
            var plan = new HomeArrivalPlan { CaptureDv = trip.Arrival, DeorbitDv = DeorbitBurn.FromLowOrbit(home), ArrivalPe = ArrivalRule.EntryPe(home) };
            var stockDv = StockDv(snapshot);
            if (stockDv.HasValue) plan.DvLeft = stockDv.Value - ascent - trip.Departure - trip.PlaneChange;
            var need = plan.Need;
            if (plan.DvLeft.HasValue)
            {
                plan.SpareDv = DeorbitBurn.AutoMaxDv(plan.DvLeft.Value, PoweredLanding(snapshot, home), snapshot.LaterLegsDv ?? 0);
                plan.CaptureFirst = need <= plan.SpareDv;
            }
            if (plan.CaptureFirst) plan.ArrivalPe = DeltaVMap.LowOrbit(home) - home.Radius;
            plan.Note = plan.CaptureFirst
                ? "arrival: " + Ms(need) + " of " + Ms(plan.SpareDv) + " m/s: target_pe " + Ms(plan.ArrivalPe) + ", capture(), deorbit(), reentry()"
                : ArrivalRule.Note(snapshot, target, false);
            return plan;
        }

        private static bool InSystemOf(BodySystem system, SnapshotBody body, SnapshotBody planet)
        {
            for (var at = body; at != null; at = system.Parent(at))
                if (at == planet) return true;
            return false;
        }

        /// <summary>The craft's speed at the ground with no chutes, m/s, which its engines must take out; 0 with chutes.</summary>
        public static double PoweredLanding(GameSnapshot snapshot, SnapshotBody home)
        {
            if (snapshot.Craft == null || snapshot.Craft.Parts.Any(p => p.Chute != null)) return 0;
            return ChuteGroups.TouchdownWithoutChutes(home, snapshot.Craft.MassTonnes * 1000);
        }

        private static string Ms(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
