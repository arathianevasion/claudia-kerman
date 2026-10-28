// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>solve_burn</c>: the burn that reaches a goal, as maneuver-node components with its magnitude, burn time and the
    /// <c>predict_burn</c> result for it. <c>circularize</c>, <c>set_ap</c> and <c>set_pe</c> are closed-form (vis-viva, a
    /// prograde burn at an apsis); <c>next_patch_pe</c> searches the burn size with <see cref="PredictBurn"/>'s own propagation.
    /// The orbit changes ported from MechJeb (<c>set_sma</c>, <c>set_ecc</c>, <c>ellipticize</c>, <c>set_inc</c>,
    /// <c>match_plane</c>, <c>shift_lan</c>, <c>resonant</c>, <c>stationary</c>) are in <see cref="OrbitChange"/>, each at a
    /// <see cref="BurnTime"/> reference; every goal reads the element it set back from the propagated orbit.
    /// </summary>
    internal sealed class SolveBurn : IPlanningTool
    {
        /// <summary>The finest burn step the search scans with, m/s: a transfer window is tens of m/s wide.</summary>
        private const double ScanStep = 2;
        private const int MaxScanSteps = 1500;
        /// <summary>
        /// How many burn sizes the next_patch_pe scan tries before it gives up: one past the scan's own step count (it runs from 0
        /// to its end inclusive), so a normal search is never cut short. A count, not a clock, so the same input gives the same
        /// answer under any machine load. A full scan (the Pe never reaches the goal) measured about 0.3 s on a dev machine, roughly
        /// 0.2 ms a burn size, so the 5 s cap needs a burn size to cost over 3 ms: more than 10x headroom.
        /// </summary>
        internal const int DefaultMaxScanEvaluations = MaxScanSteps + 1;
        private const string DefaultAt = "in 60";
        private static readonly string[] Goals =
        {
            "circularize", "set_ap", "set_pe", "next_patch_pe", "set_sma", "set_ecc", "ellipticize", "set_inc", "match_plane", "shift_lan", "resonant", "stationary",
        };
        /// <summary>BrentRoot's tolerance on the burn size, relative to max(|burn|, 1) m/s: far finer than the metre the Pe is held to.</summary>
        private const double RefineTolerance = 1e-9;
        private const int RefineIterations = 80;

        private readonly int _maxScanEvaluations;

        public SolveBurn() : this(DefaultMaxScanEvaluations)
        {
        }

        /// <summary>A solver whose next_patch_pe scan gives up after <paramref name="maxScanEvaluations"/> burn sizes (tests inject a small one).</summary>
        internal SolveBurn(int maxScanEvaluations)
        {
            _maxScanEvaluations = maxScanEvaluations;
        }

        public string Name
        {
            get { return "solve_burn"; }
        }

        public string Description
        {
            get
            {
                return "The burn to an orbit goal, from this turn's Game Snapshot: maneuver-node components (prograde, normal, radial m/s), "
                    + "magnitude, burn time, the element reached and the predict_burn result. at: ap, pe, altitude, eq_an, eq_dn, eq_nearest, "
                    + "eq_highest, rel_an, rel_dn, closest_approach or a UT. Goals (altitudes in m above sea level): "
                    + "circularize: at ap or pe; "
                    + "set_ap, set_pe: value; "
                    + "next_patch_pe: value above the next body, at default in 60 s; "
                    + "set_sma: value m from the centre, at default pe; "
                    + "set_ecc: value, at default pe; "
                    + "ellipticize: pe and ap, at default in 60 s; "
                    + "set_inc: value degrees, at default eq_highest; "
                    + "match_plane: target's plane, at the cheaper of rel_an and rel_dn; "
                    + "shift_lan: value degrees, default where the planes cross; "
                    + "resonant: value orbits while target (default this orbit) makes of, at default pe; "
                    + "stationary: synchronous circle. "
                    + "Its blocks are change_orbit, circularize, exec_node and tune_pe: call it to budget their burn instead of working it out, then "
                    + "pass the block the goal, or exec_node the node. Don't call it to check a burn (predict_burn) or for the craft's "
                    + "delta-v (stage_performance).";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"goal\":{\"type\":\"string\",\"enum\":[\"circularize\",\"set_ap\",\"set_pe\",\"next_patch_pe\",\"set_sma\",\"set_ecc\",\"ellipticize\",\"set_inc\",\"match_plane\",\"shift_lan\",\"resonant\",\"stationary\"]},"
                    + "\"at\":{\"description\":\"A word from the description, \\\"altitude <m> [ascending|descending]\\\", a UT in seconds or \\\"in <s>\\\". rel_an, rel_dn and closest_approach use target.\"},"
                    + "\"target\":{\"type\":\"string\",\"description\":\"A body.\"},"
                    + "\"value\":{\"type\":\"number\"},"
                    + "\"pe\":{\"type\":\"number\"},\"ap\":{\"type\":\"number\"},"
                    + "\"of\":{\"type\":\"number\",\"description\":\"Default 1.\"},"
                    + "\"direction\":{\"type\":\"string\",\"enum\":[\"prograde\",\"retrograde\"],\"description\":\"next_patch_pe only. Default prograde.\"}"
                    + "},\"required\":[\"goal\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "solving the burn"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
        {
            return Solve(input, snapshot, new SolvedBurn());
        }

        /// <summary>
        /// <see cref="Run"/>'s solve, also filling <paramref name="into"/> with the burn unrounded and the figure the goal aims at,
        /// for <c>ADDONS:CLAUDIA:ORBITBURN</c> (#444). It checks the goal itself: the block call has no schema check before it.
        /// </summary>
        internal ToolOutcome Solve(JsonNode input, GameSnapshot snapshot, SolvedBurn into)
        {
            var goal = JsonNode.Str(input["goal"]);
            if (Array.IndexOf(Goals, goal) < 0) return PlanningJson.Error("goal must be one of " + string.Join(", ", Goals));
            var value = JsonNode.Double(input["value"]);
            var at = input["at"];
            var direction = JsonNode.Str(input["direction"]);
            var target = JsonNode.Str(input["target"]);
            var pe = JsonNode.Double(input["pe"]);
            var ap = JsonNode.Double(input["ap"]);
            var of = JsonNode.Double(input["of"]);
            var word = at != null && at.Kind == JsonKind.String ? at.StringValue.Trim().ToLowerInvariant() : null;
            if (goal != "next_patch_pe" && direction != null) return PlanningJson.Error("direction is for next_patch_pe only");
            if (goal != "ellipticize" && (pe.HasValue || ap.HasValue)) return PlanningJson.Error("pe and ap are for ellipticize only; set_ap and set_pe take value");
            if (goal != "resonant" && of.HasValue) return PlanningJson.Error("of is for resonant only");
            if (target != null && (goal == "circularize" || goal == "set_ap" || goal == "set_pe" || goal == "stationary"))
                return PlanningJson.Error("target is for next_patch_pe and the other goals that take at, and for match_plane and resonant; " + goal + " takes none");
            if (target != null && goal != "match_plane" && goal != "resonant" && word != "rel_an" && word != "rel_dn" && word != "closest_approach")
                return PlanningJson.Error("target is for at rel_an, rel_dn or closest_approach, or for match_plane and resonant");
            if (target == null && goal == "match_plane") return PlanningJson.Error("match_plane needs target, the body whose orbit plane to match");
            if (at != null && (goal == "set_ap" || goal == "set_pe"))
                return PlanningJson.Error(goal + " burns at the " + (goal == "set_ap" ? "periapsis" : "apoapsis") + "; leave at out");
            if (at != null && goal == "stationary") return PlanningJson.Error("stationary burns where the orbit crosses the synchronous altitude; leave at out");
            if (at != null && goal == "match_plane" && word != "rel_an" && word != "rel_dn") return PlanningJson.Error("match_plane burns at rel_an or rel_dn; leave at out for the cheaper");
            if (goal == "circularize" && word != "ap" && word != "pe") return PlanningJson.Error("circularize needs at to be \"ap\" or \"pe\"");
            var problem = Inputs(goal, value, pe, ap, of);
            if (problem != null) return PlanningJson.Error(problem);

            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, target, out error);
            if (setup == null) return error;
            var path = setup.System.Follow(setup.Body, setup.Start, setup.Now, 8);
            if (path[path.Count - 1].Error != null) return PlanningJson.Error(path[path.Count - 1].Error);
            switch (goal)
            {
                case "next_patch_pe": return SolvePatchPe(into, setup, path, value.Value, at, direction ?? "prograde");
                case "circularize":
                case "set_ap":
                case "set_pe": return SolveClosedForm(into, setup, path, goal, value, at);
                case "match_plane": return SolveMatchPlane(into, setup, path, at);
                case "shift_lan": return SolveShiftLan(into, setup, path, value.Value, at);
                case "stationary": return SolveStationary(into, setup, path);
                default: return SolveAt(into, setup, path, goal, value, pe, ap, of, at);
            }
        }

        /// <summary>Why the goal's own fields don't fit it; null when they do. Every build checks them, unlike MechJeb's Debug-only checks.</summary>
        private static string Inputs(string goal, double? value, double? pe, double? ap, double? of)
        {
            var takesValue = goal != "circularize" && goal != "ellipticize" && goal != "match_plane" && goal != "stationary";
            if (!takesValue && value.HasValue) return goal + " takes no value";
            if (takesValue && !value.HasValue) return goal + " needs a value, " + ValueMeaning(goal);
            if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value))) return "value must be a finite number";
            switch (goal)
            {
                case "set_sma":
                    if (!(value > 0)) return "set_sma's value is the semi-major axis in metres from the body's centre, so above 0";
                    break;
                case "set_ecc":
                    if (!(value >= 0)) return "set_ecc's value is an eccentricity, 0 or more";
                    break;
                case "set_inc":
                    if (!(value >= 0 && value <= 180)) return "set_inc's value is an inclination from 0 to 180 degrees";
                    break;
                case "resonant":
                    if (!(value > 0)) return "resonant's value is how many orbits the craft makes, above 0";
                    if (of.HasValue && !(of > 0 && !double.IsInfinity(of.Value))) return "resonant's of is how many orbits the reference makes meanwhile, above 0";
                    break;
                case "ellipticize":
                    if (!pe.HasValue || !ap.HasValue) return "ellipticize needs pe and ap, the altitudes in metres above sea level";
                    if (double.IsInfinity(pe.Value) || double.IsInfinity(ap.Value)) return "ellipticize's pe and ap must be finite";
                    if (pe > ap) return "ellipticize's pe of " + Num(pe.Value) + " m is above its ap of " + Num(ap.Value) + " m";
                    break;
            }
            return null;
        }

        private static string ValueMeaning(string goal)
        {
            switch (goal)
            {
                case "set_sma": return "the semi-major axis in metres from the body's centre";
                case "set_ecc": return "the eccentricity";
                case "set_inc": return "the inclination in degrees";
                case "shift_lan": return "the longitude of the ascending node in degrees";
                case "resonant": return "how many orbits the craft makes while the reference makes of";
                default: return "the altitude in metres above sea level";
            }
        }

        /// <summary>
        /// The goals solved at one time, the caller's <c>at</c> or the goal's default: set_sma and resonant by vis-viva, set_ecc by
        /// <see cref="BrentRoot"/>, ellipticize and set_inc by MechJeb's closed forms (<see cref="OrbitChange"/>).
        /// </summary>
        private static ToolOutcome SolveAt(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, string goal, double? value, double? pe, double? ap, double? of, JsonNode at)
        {
            if (at == null)
            {
                var first = path[0].Orbit;
                if (goal == "set_inc")
                    // An equatorial orbit has no equatorial nodes; every point is one, and the apoapsis is the slowest.
                    at = Vec3.Cross(new Vec3(0, 0, 1), first.W).Length < 1e-6 ? Word(first.Closed ? "ap" : DefaultAt) : Word("eq_highest");
                else
                    at = Word(goal == "ellipticize" ? DefaultAt : "pe");
            }
            string problem;
            Patch patch;
            Vec3 r, v;
            var ut = Place(setup, path, at, out patch, out r, out v, out problem);
            if (!ut.HasValue) return PlanningJson.Error(problem);
            var mu = patch.Body.Gm;
            var name = patch.Body.Name;
            Vec3 dv;
            switch (goal)
            {
                case "set_sma":
                    var toSma = OrbitChange.ProgradeForSma(mu, r, v, value.Value, out problem);
                    if (!toSma.HasValue) return PlanningJson.Error(problem);
                    dv = v.Normalized * toSma.Value;
                    into.Want("sma", value.Value);
                    break;
                case "set_ecc":
                    var toEcc = OrbitChange.ProgradeForEccentricity(mu, r, v, value.Value, out problem);
                    if (!toEcc.HasValue) return PlanningJson.Error(problem);
                    dv = v.Normalized * toEcc.Value;
                    into.Want("ecc", value.Value);
                    break;
                case "ellipticize":
                    double peR = patch.Body.Radius + pe.Value, apR = patch.Body.Radius + ap.Value, radius = r.Length;
                    if (!(peR > 0)) return PlanningJson.Error("a Pe of " + Num(pe.Value) + " m is below " + name + "'s centre");
                    // A burn leaves the craft where it is, so its altitude is on the new orbit: between the Pe and the Ap.
                    if (radius < peR - 1 || radius > apR + 1)
                        return PlanningJson.Error("the burn is at " + Num(radius - patch.Body.Radius) + " m, outside the Pe of " + Num(pe.Value) + " m and Ap of "
                            + Num(ap.Value) + " m asked for; a burn leaves the craft at its own altitude, so pick an at between them");
                    dv = OrbitChange.Ellipticize(mu, r, v, Math.Min(peR, radius), Math.Max(apR, radius));
                    into.Want("pe", pe.Value).Want("ap", ap.Value);
                    break;
                case "set_inc":
                    if (!patch.Orbit.Closed)
                        return PlanningJson.Error("the orbit round " + name + " is an escape; set_inc changes a closed orbit's inclination, so set it once in orbit at the next body");
                    var toInc = OrbitChange.ChangeInclination(r, v, value.Value, out problem);
                    if (!toInc.HasValue) return PlanningJson.Error(problem);
                    dv = toInc.Value;
                    into.Want("inc", value.Value);
                    break;
                default:
                    double period;
                    var toResonant = Resonant(setup, patch, r, v, value.Value, of ?? 1, out period, out problem);
                    if (!toResonant.HasValue) return PlanningJson.Error(problem);
                    dv = v.Normalized * toResonant.Value;
                    into.Want("period", period);
                    break;
            }
            return Answer(into, setup, goal, ut.Value, r, v, dv);
        }

        /// <summary>
        /// DeltaVToResonantOrbit: the prograde burn to the period that makes <paramref name="orbits"/> orbits while the reference
        /// (the target's orbit round the same body, or this orbit) makes <paramref name="of"/>. MechJeb keeps one apsis and moves
        /// the other; a prograde burn at the default pe does that. A Pe under the surface or an Ap past the sphere of influence is
        /// an error, not a resonant orbit.
        /// </summary>
        private static double? Resonant(PredictBurn.Setup setup, Patch patch, Vec3 r, Vec3 v, double orbits, double of, out double period, out string why)
        {
            why = null;
            period = 0;
            double reference;
            var target = setup.Target;
            if (target != null)
            {
                if (!string.Equals(target.Parent, patch.Body.Name, StringComparison.OrdinalIgnoreCase))
                {
                    why = target.Name + " is not in orbit round " + patch.Body.Name + ", the body the craft is round at that time, so there is no resonance with it";
                    return null;
                }
                reference = setup.System.OrbitOf(target).Period;
            }
            else
            {
                reference = Conic.FromState(patch.Body.Gm, r, v, 0).Period;
                if (double.IsInfinity(reference))
                {
                    why = "the orbit round " + patch.Body.Name + " is an escape with no period; give target, a body to resonate with";
                    return null;
                }
            }
            period = reference * of / orbits;
            var sma = OrbitChange.RadiusForPeriod(patch.Body.Gm, period);
            var burn = OrbitChange.ProgradeForSma(patch.Body.Gm, r, v, sma, out why);
            if (!burn.HasValue) return null;
            var after = Conic.FromState(patch.Body.Gm, r, v.Normalized * (v.Length + burn.Value), 0);
            var body = patch.Body;
            if (after.PeR < body.Radius)
            {
                why = "the resonant orbit (period " + Num(period) + " s, semi-major axis " + Num(sma) + " m) from that burn has its Pe at "
                    + Num(after.PeR - body.Radius) + " m, below " + body.Name + "'s surface";
                return null;
            }
            if (body.SoiRadius > 0 && after.ApR > body.SoiRadius)
            {
                why = "the resonant orbit (period " + Num(period) + " s, semi-major axis " + Num(sma) + " m) from that burn reaches past "
                    + body.Name + "'s sphere of influence";
                return null;
            }
            return burn;
        }

        /// <summary>
        /// DeltaVAndTimeToMatchPlanes*: the plane change into the target's orbit plane at a node with it, rel_an or rel_dn, or the
        /// slower of the two when at is left out (the same angle costs less at a lower speed).
        /// </summary>
        private static ToolOutcome SolveMatchPlane(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, JsonNode at)
        {
            string problem;
            Patch patch;
            Vec3 r, v;
            var ut = at != null ? Place(setup, path, at, out patch, out r, out v, out problem) : Slower(setup, path, "rel_an", "rel_dn", out patch, out r, out v, out problem);
            if (!ut.HasValue) return PlanningJson.Error(problem);
            var dv = OrbitChange.MatchPlane(r, v, setup.System.OrbitOf(setup.Target).W);
            into.Want("rel_inc", 0);
            return Answer(into, setup, "match_plane", ut.Value, r, v, dv);
        }

        /// <summary>
        /// DeltaVToShiftLAN at the caller's time; left out, where the orbit's plane crosses the plane with the same inclination
        /// and the new LAN (the slower crossing), so the burn turns the plane about that line and keeps the inclination.
        /// </summary>
        private static ToolOutcome SolveShiftLan(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, double lan, JsonNode at)
        {
            var first = path[0];
            if (Vec3.Cross(new Vec3(0, 0, 1), first.Orbit.W).Length < 1e-6)
                return PlanningJson.Error("the orbit round " + first.Body.Name + " is equatorial, so it has no LAN to shift; set an inclination first (set_inc)");
            string problem;
            Patch patch;
            Vec3 r, v;
            double? ut;
            if (at != null) ut = Place(setup, path, at, out patch, out r, out v, out problem);
            else
            {
                var wanted = OrbitChange.PlaneNormal(first.Orbit.Inclination, lan);
                double? best = null;
                problem = null;
                foreach (var ascending in new[] { true, false })
                {
                    string why;
                    var node = BurnTime.PlaneNode(first, wanted, ascending, setup.Now, "the orbit with that LAN", out why);
                    if (!node.HasValue)
                    {
                        problem = problem ?? why;
                        continue;
                    }
                    if (!best.HasValue || first.Orbit.Position(node.Value).Length > first.Orbit.Position(best.Value).Length) best = node;
                }
                if (!best.HasValue) return PlanningJson.Error("there is no time to shift the LAN: " + problem);
                ut = Place(setup, path, Word(best.Value.ToString("R", CultureInfo.InvariantCulture)), out patch, out r, out v, out problem);
            }
            if (!ut.HasValue) return PlanningJson.Error(problem);
            var dv = OrbitChange.ShiftLan(r, v, lan, out problem);
            if (!dv.HasValue) return PlanningJson.Error(problem);
            into.Want("lan", lan - 360 * Math.Floor(lan / 360));
            return Answer(into, setup, "shift_lan", ut.Value, r, v, dv.Value);
        }

        /// <summary>
        /// set_sma to the body's synchronous radius with zero eccentricity, in one burn: Simple.DeltaVToCircularize where the
        /// orbit crosses that radius (or at the apsis within <see cref="SynchronousSlack"/> of it).
        /// </summary>
        private static ToolOutcome SolveStationary(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path)
        {
            var patch = path[0];
            var body = patch.Body;
            if (!(body.RotationPeriod > 0)) return PlanningJson.Error(body.Name + " does not rotate, so it has no synchronous orbit");
            var radius = OrbitChange.RadiusForPeriod(body.Gm, body.RotationPeriod);
            var altitude = radius - body.Radius;
            if (radius <= body.Radius + body.AtmosphereHeight)
                return PlanningJson.Error(body.Name + "'s synchronous altitude, " + Num(altitude) + " m, is below its surface" + (body.AtmosphereHeight > 0 ? " or in its atmosphere" : ""));
            if (body.SoiRadius > 0 && radius > body.SoiRadius)
                return PlanningJson.Error(body.Name + "'s synchronous radius, " + Num(radius) + " m, is outside its sphere of influence (" + Num(body.SoiRadius) + " m), so it has no stationary orbit");
            var orbit = patch.Orbit;
            JsonNode at;
            if (Math.Abs(orbit.ApR - radius) <= SynchronousSlack) at = Word("ap");
            else if (Math.Abs(orbit.PeR - radius) <= SynchronousSlack) at = Word("pe");
            else if (radius > orbit.PeR && radius < orbit.ApR) at = Word("altitude " + altitude.ToString("R", CultureInfo.InvariantCulture));
            else
                return PlanningJson.Error("the orbit (Pe " + Num(orbit.PeR - body.Radius) + " m" + (orbit.Closed ? ", Ap " + Num(orbit.ApR - body.Radius) + " m" : "")
                    + ") never reaches " + body.Name + "'s synchronous altitude of " + Num(altitude) + " m; set_ap to it first");
            string problem;
            Vec3 r, v;
            var ut = Place(setup, path, at, out patch, out r, out v, out problem);
            if (!ut.HasValue) return PlanningJson.Error(problem);
            into.Want("period", body.RotationPeriod);
            return Answer(into, setup, "stationary", ut.Value, r, v, OrbitChange.Circularize(patch.Body.Gm, r, v));
        }

        /// <summary>How far, m, an apsis may be from the synchronous radius for stationary to circularize there: a period within about 0.05%.</summary>
        private const double SynchronousSlack = 1000;

        /// <summary>The burn's UT from <paramref name="at"/>, with the patch and the craft's state then; null with why.</summary>
        private static double? Place(PredictBurn.Setup setup, List<Patch> path, JsonNode at, out Patch patch, out Vec3 r, out Vec3 v, out string problem)
        {
            patch = null;
            r = v = default(Vec3);
            var ut = PredictBurn.BurnUt(at, path, setup.Now, setup.Now, "the burn", out problem, setup.System, setup.Target);
            if (problem != null) return null;
            patch = PredictBurn.Containing(path, ut.Value);
            if (patch == null)
            {
                problem = "the burn is after the craft reaches " + PredictBurn.Ending(path[path.Count - 1]) + "; nothing after that is predicted";
                return null;
            }
            patch.Orbit.State(ut.Value, out r, out v);
            return ut;
        }

        /// <summary>Of two references, the one where the craft is slower (a plane change there costs less); either alone when the other has no time.</summary>
        private static double? Slower(PredictBurn.Setup setup, List<Patch> path, string one, string two, out Patch patch, out Vec3 r, out Vec3 v, out string problem)
        {
            Patch patch2;
            Vec3 r2, v2;
            string problem2;
            var first = Place(setup, path, Word(one), out patch, out r, out v, out problem);
            var second = Place(setup, path, Word(two), out patch2, out r2, out v2, out problem2);
            if (!second.HasValue) return first;
            if (first.HasValue && v.Length <= v2.Length) return first;
            patch = patch2;
            r = r2;
            v = v2;
            problem = null;
            return second;
        }

        private static JsonNode Word(string text)
        {
            return JsonReader.Parse(PlanningJson.Quote(text));
        }

        /// <summary>A prograde burn at an apsis to the speed that puts the orbit's other end at the goal, by vis-viva.</summary>
        private static ToolOutcome SolveClosedForm(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, string goal, double? value, JsonNode at)
        {
            var apsis = goal == "circularize" ? at.StringValue.Trim().ToLowerInvariant() : goal == "set_ap" ? "pe" : "ap";
            string problem;
            var ut = PredictBurn.BurnUt(JsonReader.Parse(PlanningJson.Quote(apsis)), path, setup.Now, setup.Now, "the burn", out problem, setup.System, setup.Target);
            if (problem != null) return PlanningJson.Error(problem);
            var patch = PredictBurn.Containing(path, ut.Value);
            if (patch == null) return PlanningJson.Error("the burn is after the craft reaches " + PredictBurn.Ending(path[path.Count - 1]) + "; nothing after that is predicted");
            var mu = patch.Body.Gm;
            Vec3 r, v;
            patch.Orbit.State(ut.Value, out r, out v);
            var radius = r.Length;
            double wanted;
            if (goal == "circularize")
                wanted = Math.Sqrt(mu / radius);
            else
            {
                var far = value.Value + patch.Body.Radius;
                if (far <= patch.Body.Radius)
                    return PlanningJson.Error("a " + (goal == "set_ap" ? "Ap" : "Pe") + " of " + Num(value.Value) + " m is not above " + patch.Body.Name + "'s sea level");
                // A burn at radius r leaves r on the new orbit, so the far end can only be on the far side of it.
                if (goal == "set_ap" && far < radius)
                    return PlanningJson.Error("an Ap of " + Num(value.Value) + " m is below the Pe of " + Num(radius - patch.Body.Radius) + " m; a burn can't put the Ap under the point it burns at");
                if (goal == "set_pe" && far > radius)
                    return PlanningJson.Error("a Pe of " + Num(value.Value) + " m is above the Ap of " + Num(radius - patch.Body.Radius) + " m; a burn can't put the Pe over the point it burns at");
                wanted = Math.Sqrt(mu * (2 / radius - 2 / (radius + far)));
            }
            if (goal == "circularize") into.Want("ecc", 0);
            else into.Want(goal == "set_ap" ? "ap" : "pe", value.Value);
            return Answer(into, setup, goal, ut.Value, r, v, v.Normalized * (wanted - v.Length));
        }

        /// <summary>
        /// The burn at <c>at</c>, prograde or retrograde, whose next patch has the periapsis asked for: scans the burn size in steps
        /// with the predict_burn propagation, finds where the next patch's Pe crosses the goal, and refines it with <see cref="BrentRoot"/>.
        /// </summary>
        private ToolOutcome SolvePatchPe(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, double value, JsonNode at, string direction)
        {
            string problem;
            var ut = PredictBurn.BurnUt(at ?? JsonReader.Parse(PlanningJson.Quote(DefaultAt)), path, setup.Now, setup.Now, "the burn", out problem, setup.System, setup.Target);
            if (problem != null) return PlanningJson.Error(problem);
            var patch = PredictBurn.Containing(path, ut.Value);
            if (patch == null) return PlanningJson.Error("the burn is after the craft reaches " + PredictBurn.Ending(path[path.Count - 1]) + "; nothing after that is predicted");
            Vec3 r, v;
            patch.Orbit.State(ut.Value, out r, out v);
            var sign = direction == "retrograde" ? -1.0 : 1.0;
            var speed = v.Length;
            var most = sign > 0 ? 2 * Math.Sqrt(patch.Body.Gm / r.Length) : speed;
            var fixedAt = JsonReader.Parse(ut.Value.ToString("R", CultureInfo.InvariantCulture));

            // The next patch's Pe (m above its body's sea level) and body for a burn of s m/s; false when there is no next patch.
            Func<double, Tuple<double, string>> pe = s =>
            {
                var request = new PredictBurn.BurnRequest { At = fixedAt, Prograde = sign * s };
                List<PredictBurn.BurnDone> done;
                List<Patch> after;
                if (PredictBurn.Simulate(setup, new List<PredictBurn.BurnRequest> { request }, out done, out after, false) != null || after.Count < 2) return null;
                return Tuple.Create(after[1].Orbit.PeR - after[1].Body.Radius, after[1].Body.Name);
            };

            var step = Math.Max(ScanStep, most / MaxScanSteps);
            double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
            var anyNext = false;
            double? last = null;
            string lastBody = null;
            var tried = 0;
            for (var s = 0.0; s <= most; s += step)
            {
                if (tried++ >= _maxScanEvaluations)
                    return PlanningJson.Error("the search for a burn to a next patch Pe of " + Num(value) + " m gave up after " + (tried - 1).ToString(CultureInfo.InvariantCulture)
                        + " burn sizes, at " + Num(s) + " m/s; try an earlier or narrower burn time");
                var got = pe(s);
                if (got == null)
                {
                    last = null;
                    continue;
                }
                anyNext = true;
                lowest = Math.Min(lowest, got.Item1);
                highest = Math.Max(highest, got.Item1);
                if (last.HasValue && got.Item2 == lastBody && (last.Value - value) * (got.Item1 - value) <= 0)
                {
                    var found = Refine(pe, s - step, s, last.Value, got.Item1, value, got.Item2);
                    if (found == null)
                        return PlanningJson.Error("found a burn near " + Num(s) + " m/s that reaches " + got.Item2 + ", but the search could not narrow it to the Pe asked for (the encounter drops out or the Pe jumps there); try another burn time");
                    into.Want("next_patch_pe", value);
                    return Answer(into, setup, "next_patch_pe", ut.Value, r, v, v.Normalized * (sign * found.Value));
                }
                last = got.Item1;
                lastBody = got.Item2;
            }
            var where = at == null ? "in 60 s" : "that time";
            if (!anyNext)
                return PlanningJson.Error("no " + direction + " burn of up to " + Num(most) + " m/s at " + where + " takes the craft out of " + patch.Body.Name
                    + "'s sphere of influence or to an encounter, so there is no next patch; try another burn time");
            return PlanningJson.Error("the next patch's Pe only reaches " + Num(lowest) + " to " + Num(highest) + " m with " + direction + " burns of up to "
                + Num(most) + " m/s at " + where + ", not " + Num(value) + " m; try another burn time");
        }

        /// <summary>
        /// Finds the burn size between <paramref name="lo"/> and <paramref name="hi"/> (Pe errors straddle zero) with
        /// <see cref="BrentRoot"/>, to a Pe within a metre; null if the encounter drops out or the Pe jumps instead of crossing the goal.
        /// The Pe at both ends is known from the scan, so those two evaluations are not repeated.
        /// </summary>
        private static double? Refine(Func<double, Tuple<double, string>> pe, double lo, double hi, double peLo, double peHi, double value, string body)
        {
            var dropped = false;
            Func<double, double> error = s =>
            {
                if (s == lo) return peLo - value;
                if (s == hi) return peHi - value;
                var got = pe(s);
                if (got == null || got.Item2 != body)
                {
                    dropped = true;
                    return double.NaN;
                }
                return got.Item1 - value;
            };
            var found = BrentRoot.Solve(error, lo, hi, RefineTolerance, RefineIterations);
            if (dropped || !found.Converged || Math.Abs(found.Fx) >= 1) return null;
            return found.X;
        }

        /// <summary>
        /// The result: the burn's components, magnitude and time, the element the goal set (<c>achieved</c>) read back from the
        /// orbit predict_burn's propagation leaves, and the predict_burn result for it. <paramref name="dv"/> is the velocity
        /// change at (<paramref name="r"/>, <paramref name="v"/>), the craft's state at <paramref name="ut"/>.
        /// </summary>
        private static ToolOutcome Answer(SolvedBurn into, PredictBurn.Setup setup, string goal, double ut, Vec3 r, Vec3 v, Vec3 dv)
        {
            double prograde, normal, radial;
            OrbitChange.NodeComponents(r, v, dv, out prograde, out normal, out radial);
            var request = new PredictBurn.BurnRequest
            {
                At = JsonReader.Parse(ut.ToString("R", CultureInfo.InvariantCulture)), Prograde = prograde, Normal = normal, Radial = radial,
            };
            List<PredictBurn.BurnDone> burns;
            List<Patch> path;
            var problem = PredictBurn.Simulate(setup, new List<PredictBurn.BurnRequest> { request }, out burns, out path);
            if (problem != null) return PlanningJson.Error(problem);
            var predicted = PredictBurn.Result(setup.System, burns, path, null, setup.Now);
            var burn = burns[0];
            var magnitude = dv.Length;
            into.Ut = ut;
            into.Prograde = prograde;
            into.Normal = normal;
            into.Radial = radial;
            into.DeltaV = magnitude;
            into.BurnTime = burn.BurnTime;
            var json = new JsonWriter().BeginObject().Field("goal", goal);
            PlanningJson.Time(json, "at", ut, setup.Now);
            // + 0.0 turns a rounded -0 into 0.
            json.Name("dv").BeginObject()
                .Field("prograde", PlanningJson.Round(prograde, 2) + 0.0).Field("normal", PlanningJson.Round(normal, 2) + 0.0).Field("radial", PlanningJson.Round(radial, 2) + 0.0)
                .EndObject()
                .Field("magnitude", PlanningJson.Round(magnitude, 2))
                .Field("burn_time", burn.BurnTime.HasValue ? PlanningJson.Round(burn.BurnTime.Value, 1) : (double?)null);
            if (!burn.BurnTime.HasValue) json.Field("burn_time_note", "the craft's stages don't give it");
            else if (burn.BurnTime.Value > PredictBurn.LongBurnSeconds) json.Field("long_burn", true);
            Achieved(json.Name("achieved").BeginObject(), setup, goal, burn, path).EndObject();
            json.Name("predict_burn").Raw(predicted.Json).EndObject();
            var along = Math.Abs(prograde) >= magnitude - 1e-6 * Math.Max(1, magnitude);
            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = goal + ": " + Math.Round(magnitude, 1).ToString("0.#", CultureInfo.InvariantCulture) + " m/s "
                    + (along ? (prograde < 0 ? "retrograde " : "prograde ") : "") + "at UT " + Num(ut) + "; " + predicted.Summary,
            };
        }

        /// <summary>The element each goal sets, from the orbit just after the burn as predict_burn propagates it.</summary>
        private static JsonWriter Achieved(JsonWriter json, PredictBurn.Setup setup, string goal, PredictBurn.BurnDone burn, List<Patch> path)
        {
            var orbit = burn.Orbit;
            var body = burn.Body;
            switch (goal)
            {
                case "circularize":
                case "set_ecc":
                    return json.Field("eccentricity", PlanningJson.Round(orbit.E, 6));
                case "set_ap":
                    return json.Field("ap", PlanningJson.Round(orbit.ApR - body.Radius, 0));
                case "set_pe":
                    return json.Field("pe", PlanningJson.Round(orbit.PeR - body.Radius, 0));
                case "ellipticize":
                    return json.Field("pe", PlanningJson.Round(orbit.PeR - body.Radius, 0)).Field("ap", orbit.Closed ? PlanningJson.Round(orbit.ApR - body.Radius, 0) : (double?)null);
                case "next_patch_pe":
                    return path.Count > 1 ? json.Field("next_patch_pe", PlanningJson.Round(path[1].Orbit.PeR - path[1].Body.Radius, 0)).Field("body", path[1].Body.Name) : json;
                case "set_sma":
                    return json.Field("sma", PlanningJson.Round(orbit.A, 0));
                case "set_inc":
                    return json.Field("inclination", PlanningJson.Round(orbit.Inclination, 4));
                case "match_plane":
                    var w = setup.System.OrbitOf(setup.Target).W;
                    return json.Field("relative_inclination", PlanningJson.Round(Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(orbit.W, w)))) * 180 / Math.PI, 4));
                case "shift_lan":
                    var node = Vec3.Cross(new Vec3(0, 0, 1), orbit.W);
                    var lan = Math.Atan2(node.Y, node.X) * 180 / Math.PI;
                    return json.Field("lan", PlanningJson.Round(lan < 0 ? lan + 360 : lan, 4)).Field("inclination", PlanningJson.Round(orbit.Inclination, 4));
                case "resonant":
                    return json.Field("period", PlanningJson.Round(orbit.Period, 1));
                default:
                    return json.Field("sma", PlanningJson.Round(orbit.A, 0)).Field("eccentricity", PlanningJson.Round(orbit.E, 6)).Field("period", PlanningJson.Round(orbit.Period, 1));
            }
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A burn <see cref="SolveBurn"/> solved, unrounded, for a block to fly (<c>ADDONS:CLAUDIA:ORBITBURN</c>): the node's UT and
    /// components, its magnitude and burn time, and <see cref="Wanted"/>, what the burn should leave.
    /// </summary>
    internal sealed class SolvedBurn
    {
        public double Ut, Prograde, Normal, Radial, DeltaV;
        public double? BurnTime;

        /// <summary>
        /// Each element the goal sets with the figure it aims at, in the order set: <c>inc</c>, <c>rel_inc</c> (to the target's
        /// plane) and <c>lan</c> in degrees, <c>period</c> in s, <c>pe</c>, <c>ap</c> and <c>next_patch_pe</c> in m above sea level,
        /// <c>sma</c> in m from the centre, <c>ecc</c>.
        /// </summary>
        public readonly List<KeyValuePair<string, double>> Wanted = new List<KeyValuePair<string, double>>();

        public SolvedBurn Want(string element, double figure)
        {
            Wanted.Add(new KeyValuePair<string, double>(element, figure));
            return this;
        }
    }
}
