/*
 * MechJeb2 Copyright (C) 2013 and the MechJeb contributors, GNU GPL version 3
 * Ported from MechJeb2/Maneuver/TimeSelector.cs and MechJeb2/OrbitExtensions.cs (MechJeb2 a958383)
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// Where a burn goes in time: the next UT, after a starting time, for each reference <c>solve_burn</c> and <c>predict_burn</c>
    /// take in <c>at</c>. Ported from MechJeb2's <c>TimeSelector.ComputeManeuverTime</c> and the <c>OrbitExtensions</c> helpers it
    /// calls (<c>TimeOfAscendingNode</c>, <c>TimeOfDescendingNode</c>, the equatorial pair, <c>NextTimeOfRadius</c>,
    /// <c>NextClosestApproachTime</c>), on <see cref="Conic"/> rather than KSP's orbit. Unlike MechJeb's, the relative-node modes
    /// are honoured, and a reference with no answer is an error result (a reason in <c>problem</c>), never a throw or a guess.
    /// The references: <c>ap</c>, <c>pe</c>, a UT, <c>in &lt;s&gt;</c>, <c>altitude &lt;m&gt; [ascending|descending]</c>,
    /// <c>eq_an</c>, <c>eq_dn</c>, <c>eq_nearest</c>, <c>eq_highest</c>, <c>rel_an</c>, <c>rel_dn</c> and <c>closest_approach</c>.
    /// </summary>
    internal static class BurnTime
    {
        /// <summary>A step past the start, s, so a reference at the very moment of the last burn finds the next one (as ap and pe always have).</summary>
        private const double Nudge = 1e-3;
        /// <summary>Orbits a closest-approach search looks ahead on a closed orbit: "the next few".</summary>
        internal const int ApproachOrbits = 3;
        /// <summary>MechJeb's look-ahead on a hyperbola, in units of mean anomaly.</summary>
        private const double HyperbolicMeanAnomalies = 100;
        /// <summary>Plane normals closer than this (a unit cross product, about 6e-5 degrees) are the same plane: there are no nodes.</summary>
        private const double SamePlane = 1e-6;

        /// <summary>
        /// The keyword references, read by <see cref="IsNamed"/> and by the bad-<c>at</c> message. <see cref="Named"/> dispatches on the same
        /// names by hand, so a name added here needs its branch there (<c>Every_reference_resolves_within_a_budget</c> checks each one).
        /// The altitude form is the one "altitude " prefix, shown as <see cref="AltitudeForm"/>.
        /// </summary>
        internal static readonly string[] References =
        {
            "ap", "pe", "eq_an", "eq_dn", "eq_nearest", "eq_highest", "rel_an", "rel_dn", "closest_approach",
        };

        /// <summary>The altitude reference as the bad-<c>at</c> message shows it.</summary>
        internal const string AltitudeForm = "altitude <m> [ascending|descending]";

        /// <summary>Whether <paramref name="text"/> (trimmed, lower case) names a reference this class resolves, a UT or "in &lt;s&gt;" aside.</summary>
        public static bool IsNamed(string text)
        {
            return Array.IndexOf(References, text) >= 0 || text.StartsWith("altitude ", StringComparison.Ordinal);
        }

        /// <summary>The bad-<c>at</c> message: every form <c>at</c> accepts, listing <see cref="References"/> (the list <see cref="IsNamed"/> reads).</summary>
        private static string BadAt(string label)
        {
            var forms = new List<string>();
            foreach (var reference in References) forms.Add("\"" + reference + "\"");
            forms.Add("\"" + AltitudeForm + "\"");
            return label + ".at must be " + string.Join(", ", forms) + ", a UT in seconds, or \"in <s>\"";
        }

        /// <summary>
        /// The burn's UT from its <c>at</c>, or why it can't be placed (<paramref name="problem"/>). The reference is worked on the
        /// patch flown at <paramref name="after"/>; <paramref name="system"/> and <paramref name="target"/>
        /// are needed only by <c>rel_an</c>, <c>rel_dn</c> and <c>closest_approach</c>; <paramref name="target"/> is
        /// a body (in its table) or the Target Vessel.
        /// </summary>
        public static double? Resolve(JsonNode at, List<Patch> path, double after, double now, string label, BodySystem system, BurnTarget target, out string problem)
        {
            problem = null;
            double ut;
            var text = at != null && at.Kind == JsonKind.String ? at.StringValue.Trim().ToLowerInvariant() : null;
            if (text != null && IsNamed(text))
                return Named(text, path, after, label, system, target, out problem);
            if (at != null && at.Kind == JsonKind.Number)
                ut = JsonNode.Double(at).Value;
            else if (text != null && text.StartsWith("in ", StringComparison.Ordinal)
                && double.TryParse(text.Substring(3).Trim().TrimEnd('s').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ut))
                ut += now;
            else if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ut)) { }
            else
            {
                problem = BadAt(label);
                return null;
            }
            if (double.IsNaN(ut) || double.IsInfinity(ut))
            {
                problem = label + ".at is not a finite time";
                return null;
            }
            if (ut < after - 0.5)
            {
                problem = label + " is at UT " + Math.Round(ut).ToString("0", CultureInfo.InvariantCulture) + ", before "
                    + (after == now ? "now" : "the burn before it");
                return null;
            }
            return Math.Max(ut, after);
        }

        private static double? Named(string text, List<Patch> path, double after, string label, BodySystem system, BurnTarget target, out string problem)
        {
            problem = null;
            var patch = PredictBurn.Containing(path, after);
            if (patch == null)
            {
                problem = label + " is at " + Shown(text) + ", but the craft has left the predicted path by then";
                return null;
            }
            var orbit = patch.Orbit;
            var from = after + Nudge;
            double? ut;
            string why = null;
            if (text == "ap")
            {
                ut = orbit.NextApoapsis(from);
                if (!ut.HasValue) why = "the orbit round " + patch.Body.Name + " is an escape with no apoapsis";
            }
            else if (text == "pe")
            {
                ut = orbit.NextPeriapsis(from);
                if (!ut.HasValue) why = "the orbit round " + patch.Body.Name + " is past its periapsis";
            }
            else if (text.StartsWith("altitude ", StringComparison.Ordinal))
                ut = Altitude(text, patch, from, out why);
            else if (text == "closest_approach")
                ut = ClosestApproach(patch, from, system, target, out why);
            else if (text == "rel_an" || text == "rel_dn")
                ut = RelativeNode(text == "rel_an", patch, from, system, target, out why);
            else
                ut = EquatorialNode(text, patch, from, out why);
            if (why != null)
            {
                problem = label + " is at " + Shown(text) + ", but " + why;
                return null;
            }
            if (ut.Value > patch.EndUt)
            {
                problem = label + " is at " + Shown(text) + ", but the craft reaches " + PredictBurn.Ending(patch) + " first";
                return null;
            }
            return ut;
        }

        private static string Shown(string text)
        {
            return text.StartsWith("altitude ", StringComparison.Ordinal) ? "altitude" : text;
        }

        /// <summary>The next crossing of an altitude above sea level, going out or coming in; MechJeb needs the altitude strictly between the Pe and the Ap (any above the Pe on an escape).</summary>
        private static double? Altitude(string text, Patch patch, double from, out string why)
        {
            why = null;
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double altitude;
            if (words.Length < 2 || words.Length > 3 || !double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out altitude)
                || double.IsNaN(altitude) || double.IsInfinity(altitude))
            {
                why = "altitude needs the form \"altitude <m>\" with an optional \"ascending\" or \"descending\"";
                return null;
            }
            bool? outward = null;
            if (words.Length == 3)
            {
                if (words[2] == "ascending") outward = true;
                else if (words[2] == "descending") outward = false;
                else
                {
                    why = "altitude's direction is \"ascending\" or \"descending\", not \"" + words[2] + "\"";
                    return null;
                }
            }
            var orbit = patch.Orbit;
            var radius = patch.Body.Radius + altitude;
            var peAlt = orbit.PeR - patch.Body.Radius;
            if (orbit.E < 1e-12)
            {
                why = "the orbit round " + patch.Body.Name + " is circular and never crosses an altitude";
                return null;
            }
            if (!(radius > orbit.PeR) || (orbit.Closed && !(radius < orbit.ApR)))
            {
                why = "the orbit round " + patch.Body.Name + " does not cross " + Num(altitude) + " m (Pe " + Num(peAlt) + " m"
                    + (orbit.Closed ? ", Ap " + Num(orbit.ApR - patch.Body.Radius) + " m" : "") + ")";
                return null;
            }
            double? best = null;
            foreach (var up in new[] { true, false })
            {
                if (outward.HasValue && outward.Value != up) continue;
                var t = orbit.TimeAtRadius(radius, from, up);
                if (t.HasValue && (!best.HasValue || t.Value < best.Value)) best = t;
            }
            if (!best.HasValue) why = "the craft has already passed " + Num(altitude) + " m" + (outward.HasValue ? (outward.Value ? " going up" : " going down") : "") + " on this escape";
            return best;
        }

        /// <summary>The equatorial nodes: ascending where the craft crosses the equator northward, with MechJeb's nearest and highest picks.</summary>
        private static double? EquatorialNode(string text, Patch patch, double from, out string why)
        {
            why = null;
            var orbit = patch.Orbit;
            var toAn = Vec3.Cross(new Vec3(0, 0, 1), orbit.W);
            if (toAn.Length < SamePlane)
            {
                why = "the orbit round " + patch.Body.Name + " is equatorial, so it has no equatorial nodes";
                return null;
            }
            var an = NodeTime(orbit, toAn, from);
            var dn = NodeTime(orbit, toAn * -1, from);
            switch (text)
            {
                case "eq_an":
                    if (!an.HasValue) why = Missing("ascending equatorial");
                    return an;
                case "eq_dn":
                    if (!dn.HasValue) why = Missing("descending equatorial");
                    return dn;
            }
            // A node past the patch's end is no node to choose between: the other one may still be flown.
            if (an > patch.EndUt) an = null;
            if (dn > patch.EndUt) dn = null;
            if (an.HasValue && dn.HasValue)
            {
                if (text == "eq_nearest") return Math.Min(an.Value, dn.Value);
                // The node where the craft is slower is the higher one, where a plane change costs least; a tie goes to the ascending node.
                return Speed(orbit, toAn) <= Speed(orbit, toAn * -1) ? an : dn;
            }
            if (an.HasValue) return an;
            if (dn.HasValue) return dn;
            why = "no equatorial node is ahead on this orbit round " + patch.Body.Name + " (a hyperbola's nodes can be behind the craft or in its opening)";
            return null;
        }

        /// <summary>The nodes of the craft's orbit with the target's orbit round the same parent, the plane change that lines the two up.</summary>
        private static double? RelativeNode(bool ascending, Patch patch, double from, BodySystem system, BurnTarget target, out string why)
        {
            Conic aim;
            if (!Aim(patch, system, target, ascending ? "rel_an" : "rel_dn", out aim, out why)) return null;
            return PlaneNodeTime(patch, aim.W, ascending, from, target.Name + "'s", out why);
        }

        /// <summary>
        /// The next node, after <paramref name="after"/> and within the patch, of the patch's orbit with the plane whose normal is
        /// <paramref name="normal"/> (ascending: crossing it towards that normal, as <c>rel_an</c> does); null with why. solve_burn's
        /// shift_lan burns where its orbit's plane and the plane it wants cross.
        /// </summary>
        public static double? PlaneNode(Patch patch, Vec3 normal, bool ascending, double after, string plane, out string why)
        {
            var ut = PlaneNodeTime(patch, normal, ascending, after + Nudge, plane, out why);
            if (ut > patch.EndUt)
            {
                why = "the craft reaches " + PredictBurn.Ending(patch) + " before that node";
                return null;
            }
            return ut;
        }

        private static double? PlaneNodeTime(Patch patch, Vec3 normal, bool ascending, double from, string plane, out string why)
        {
            why = null;
            var toAn = Vec3.Cross(normal, patch.Orbit.W);
            if (toAn.Length < SamePlane)
            {
                why = "the orbit round " + patch.Body.Name + " is in the same plane as " + plane + ", so there are no relative nodes";
                return null;
            }
            var direction = ascending ? toAn : toAn * -1;
            var ut = NodeTime(patch.Orbit, direction, from);
            if (!ut.HasValue) why = Missing(ascending ? "ascending relative" : "descending relative");
            return ut;
        }

        /// <summary>
        /// The craft's nearest approach to the target over the next few orbits (#442 asks for a few, MechJeb searches one; a hyperbola, 100 radians of
        /// mean anomaly), found by <see cref="BodySystem.ClosestApproach"/> on the part of the patch in that window.
        /// </summary>
        private static double? ClosestApproach(Patch patch, double from, BodySystem system, BurnTarget target, out string why)
        {
            Conic aim;
            if (!Aim(patch, system, target, "closest_approach", out aim, out why)) return null;
            var orbit = patch.Orbit;
            var span = orbit.Closed ? ApproachOrbits * orbit.Period : HyperbolicMeanAnomalies / orbit.N;
            var end = Math.Min(patch.EndUt, from + span);
            if (!(end > from))
            {
                why = "the patch round " + patch.Body.Name + " ends before there is a path to search";
                return null;
            }
            double distance, ut;
            system.ClosestApproach(new Patch { Body = patch.Body, Orbit = orbit, StartUt = from, EndUt = end, End = PatchEnd.Stable }, aim, out distance, out ut);
            return ut;
        }

        /// <summary>The orbit a relative reference aims at; false with why when the target is not round the patch's body.</summary>
        private static bool Aim(Patch patch, BodySystem system, BurnTarget target, string reference, out Conic aim, out string why)
        {
            aim = null;
            if (target == null || (!target.IsVessel && system == null))
                why = "it needs target, the body or Target Vessel to measure from";
            else
                why = target.Elsewhere(patch.Body, reference);
            if (why == null) aim = target.OrbitIn(system);
            return why == null;
        }

        /// <summary>Why a node has no time: only a hyperbola can lack one, when it is in the opening angle or behind the craft.</summary>
        private static string Missing(string name)
        {
            return "the " + name + " node is not ahead on this escape orbit (it lies in the opening of the hyperbola, or the craft has passed it)";
        }

        /// <summary>The next time the craft is in <paramref name="direction"/> from the body's centre, or null if the orbit never is (a hyperbola) or has passed it.</summary>
        private static double? NodeTime(Conic orbit, Vec3 direction, double from)
        {
            var nu = Math.Atan2(Vec3.Dot(direction, orbit.Q), Vec3.Dot(direction, orbit.P));
            if (!orbit.Closed && !(Math.Abs(nu) < Math.Acos(-1 / orbit.E))) return null;
            return orbit.TimeAtTrue(nu, from);
        }

        /// <summary>The speed at the point of the orbit that lies in <paramref name="direction"/>, by vis-viva.</summary>
        private static double Speed(Conic orbit, Vec3 direction)
        {
            var nu = Math.Atan2(Vec3.Dot(direction, orbit.Q), Vec3.Dot(direction, orbit.P));
            Vec3 r, v;
            orbit.StateAtTrue(nu, out r, out v);
            return v.Length;
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
