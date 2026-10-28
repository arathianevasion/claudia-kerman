// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary><c>solve_burn</c>'s <c>intercept_at</c> goal (#451): one <see cref="Lambert"/> arc from the craft to a body.</summary>
    internal sealed partial class SolveBurn
    {
        /// <summary>The most whole revolutions an arc may make: far past any useful transfer, and a bound on the solver's work.</summary>
        private const int MaxRevolutions = 20;

        /// <summary>The sine of the angle between the craft and the target, below which a half-turn arc has no plane of its own.</summary>
        private const double HalfTurnSine = 1e-3;

        /// <summary>The sine of the angle between the target and the craft's orbit plane, at or under which the target is in it.</summary>
        private const double CoplanarSine = 1e-6;

        /// <summary>Why the fields of an <c>intercept_at</c> don't fit it; null when they do.</summary>
        private static string InterceptInputs(JsonNode input, string target, double? arriveUt, double? tof, bool? retrograde, long? revolutions)
        {
            if (target == null) return "intercept_at needs target, the body to reach";
            if (input["value"] != null) return "intercept_at takes no value";
            if (arriveUt.HasValue == tof.HasValue) return "intercept_at needs arrive_ut (the UT of arrival) or tof (the flight time in seconds), one of them";
            if (input["arrive_ut"] != null && !arriveUt.HasValue) return "arrive_ut must be a number, a UT in seconds";
            if (input["tof"] != null && !tof.HasValue) return "tof must be a number of seconds";
            if (arriveUt.HasValue && (double.IsNaN(arriveUt.Value) || double.IsInfinity(arriveUt.Value))) return "arrive_ut must be a finite number";
            if (tof.HasValue && !(tof > 0 && !double.IsInfinity(tof.Value))) return "tof must be a finite number of seconds above 0";
            if (input["retrograde"] != null && !retrograde.HasValue) return "retrograde must be true or false";
            if (input["revolutions"] != null && !revolutions.HasValue) return "revolutions must be a whole number of orbits";
            if (revolutions.HasValue && (revolutions < 0 || revolutions > MaxRevolutions))
                return "revolutions is from 0 to " + MaxRevolutions;
            return null;
        }

        /// <summary>
        /// The burn at <paramref name="at"/> (default in 60 s) onto the Lambert arc that reaches the target's centre at the arrival
        /// time, going round the way the craft's orbit does (or against it when <paramref name="retrograde"/>). The target must
        /// orbit the body the craft is round at the burn. The arrival is reported with the speed relative to the target and the
        /// burn that would match its velocity there.
        /// </summary>
        private static ToolOutcome SolveIntercept(PredictBurn.Setup setup, List<Patch> path, JsonNode at, double? arriveUt, double? tof, bool retrograde, int revolutions)
        {
            string problem;
            Patch patch;
            Vec3 r, v;
            var ut = Place(setup, path, at ?? Word(DefaultAt), out patch, out r, out v, out problem);
            if (!ut.HasValue) return PlanningJson.Error(problem);
            var target = setup.Target;
            if (!string.Equals(target.Parent, patch.Body.Name, StringComparison.OrdinalIgnoreCase))
                return PlanningJson.Error(target.Name + " is not in orbit round " + patch.Body.Name + ", the body the craft is round at that time; "
                    + "intercept_at reaches the bodies of that sphere of influence");
            var flight = tof ?? arriveUt.Value - ut.Value;
            if (!(flight > 0))
                return PlanningJson.Error("arrive_ut of " + Num(arriveUt.Value) + " is not after the burn at UT " + Num(ut.Value) + ", so there is no flight time");
            var arrival = ut.Value + flight;
            Vec3 rt, vt;
            setup.System.OrbitOf(target).State(arrival, out rt, out vt);

            // Near 180 degrees the arc's plane is set by how far the target is off the line through the body, and the burn to it grows
            // without bound as that shrinks. Exactly across in the craft's own plane (a Hohmann arc) is fine: the plane is the craft's.
            var turn = Vec3.Cross(r.Normalized, rt.Normalized).Length;
            var outOfPlane = Math.Abs(Vec3.Dot(rt.Normalized, patch.Orbit.W.Normalized));
            if (turn < HalfTurnSine && Vec3.Dot(r, rt) < 0 && outOfPlane > CoplanarSine)
                return PlanningJson.Error("the arc is nearly 180 degrees: " + target.Name + " is almost straight across " + patch.Body.Name + " from the craft at UT "
                    + Num(arrival) + " but off the craft's orbit plane, so the transfer's plane is set by a sliver and the burn would be enormous; arrive earlier or later, or burn at another time");

            var solution = Lambert.Solve(patch.Body.Gm, r, rt, flight, patch.Orbit.W, revolutions, retrograde);
            if (solution.Error != null) return PlanningJson.Error("no arc to " + target.Name + ": " + solution.Error);

            var match = vt - solution.V2;
            double mp, mn, mr;
            OrbitChange.NodeComponents(rt, solution.V2, match, out mp, out mn, out mr);
            return Answer(setup, "intercept_at", ut.Value, r, v, solution.V1 - v, json =>
            {
                PlanningJson.Time(json.Name("arrival").BeginObject(), "at", arrival, setup.Now);
                json.Field("flight_time", PlanningJson.Round(flight, 1))
                    .Field("relative_speed", PlanningJson.Round((solution.V2 - vt).Length, 2))
                    .Name("match_dv").BeginObject()
                    .Field("prograde", PlanningJson.Round(mp, 2) + 0.0).Field("normal", PlanningJson.Round(mn, 2) + 0.0).Field("radial", PlanningJson.Round(mr, 2) + 0.0)
                    .Field("magnitude", PlanningJson.Round(match.Length, 2))
                    .EndObject().EndObject();
            });
        }
    }
}
