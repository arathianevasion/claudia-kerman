// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The Course Correction to the Target Vessel (#454): the smallest burn in the window that brings the next closest approach
    /// to <see cref="CorrectionAsk.TargetDistance"/> metres, a distance constraint in place of the periapsis one, by the same
    /// <see cref="Sqp"/> single shooting from zero burn. After MechJeb's <c>DeltaVAndTimeForCheapestCourseCorrection</c> vessel
    /// form, which aims a Lambert arc instead. "The next closest approach" is the nearest one, over the next few orbits, of the
    /// uncorrected path after the window opens; each trial burn is measured round that time, half an orbit either way, so a burn
    /// that moves the approach a little is not taken for one that jumps to another orbit.
    /// </summary>
    internal static partial class CorrectionBurn
    {
        /// <summary>The solver's own tolerance on the approach distance, m.</summary>
        private const double DistanceResidual = 1;
        /// <summary>Orbits ahead the approach is looked for, as <see cref="BurnTime"/>'s closest_approach.</summary>
        private const int ApproachOrbits = 3;

        /// <summary>The Course Correction to the Game Snapshot's Target Vessel (its name in <see cref="CorrectionAsk.Target"/>, or any when empty).</summary>
        private static CorrectionPlan FindApproach(GameSnapshot snapshot, CorrectionAsk ask)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            string why;
            var vessel = VesselTarget.Read(snapshot, setup.System, ask.Target, out why);
            if (vessel == null) return Fail(why);
            return FindApproach(setup.System, setup.Body, setup.Start, setup.Now, vessel, ask);
        }

        /// <summary>The Course Correction to <paramref name="vessel"/> for a craft on <paramref name="orbit"/> round <paramref name="body"/> at <paramref name="now"/>.</summary>
        public static CorrectionPlan FindApproach(BodySystem system, SnapshotBody body, Conic orbit, double now, VesselTarget vessel, CorrectionAsk ask)
        {
            var distance = ask.TargetDistance ?? double.NaN;
            if (!Finite(distance) || distance <= 0) return Fail("target_distance must be metres above 0, not " + Num(distance));
            if (!Finite(ask.EarliestUt) || !Finite(ask.LatestUt)) return Fail("earliest_ut and latest_ut must be finite UTs");
            if (ask.EarliestUt < now) return Fail("earliest_ut (UT " + Num(ask.EarliestUt) + ") is before now (UT " + Num(now) + ")");
            if (ask.LatestUt < ask.EarliestUt)
                return Fail("the burn must come by latest_ut, UT " + Num(ask.LatestUt) + ", before earliest_ut, UT " + Num(ask.EarliestUt));
            if (!(ask.DistanceTolerance > 0)) return Fail("distance_tolerance must be above 0");

            var coast = PatchAt(system, body, orbit, now, ask.EarliestUt);
            if (coast == null) return Fail("the trajectory does not reach UT " + Num(ask.EarliestUt) + " in flight");
            if (coast.End != PatchEnd.Stable && coast.EndUt < ask.LatestUt)
                return Fail("the burn window crosses the change of sphere of influence at UT " + Num(coast.EndUt) + ": end it before then, or start it after");
            var elsewhere = vessel.Elsewhere(coast.Body, "a course correction to it");
            if (elsewhere != null) return Fail(elsewhere);
            var from = coast.Body;
            var craft = coast.Orbit;

            // The approach aimed at, from the uncorrected path, and the span round it every trial burn is measured over.
            var span = craft.Closed ? ApproachOrbits * craft.Period : vessel.Orbit.Period;
            double aimedDistance, aimedUt, aimedSpeed;
            vessel.Approach(system, craft, ask.EarliestUt, Math.Min(coast.EndUt, ask.EarliestUt + span), out aimedDistance, out aimedUt, out aimedSpeed);
            if (aimedUt <= ask.LatestUt)
                return Fail("the closest approach to " + vessel.Name + " is at UT " + Num(aimedUt) + ", inside the burn window: end the window before it");
            var half = craft.Closed ? craft.Period / 2 : aimedUt - ask.LatestUt;
            Func<double, Vec3, Approach> measure = (ut, burn) => Measure(system, from, craft, ut, burn, vessel, aimedUt - half, aimedUt + half);

            var free = ask.LatestUt > ask.EarliestUt;
            var startUt = free ? (ask.EarliestUt + ask.LatestUt) / 2 : ask.EarliestUt;
            var problem = new SqpProblem
            {
                Scale = Scale.Create(from.Gm, from.Radius),
                Variables = free
                    ? new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Time }
                    : new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity },
                Start = free ? new[] { 0, 0, 0, startUt } : new double[3],
                Lower = free ? new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, ask.EarliestUt } : null,
                Upper = free ? new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, ask.LatestUt } : null,
                Objective = x => x[0] * x[0] + x[1] * x[1] + x[2] * x[2],
                ObjectiveHessian = free
                    ? new double[,] { { 2, 0, 0, 0 }, { 0, 2, 0, 0 }, { 0, 0, 2, 0 }, { 0, 0, 0, 0 } }
                    : new double[,] { { 2, 0, 0 }, { 0, 2, 0 }, { 0, 0, 2 } },
                Constraints = 1,
                Evaluate = (x, c) =>
                {
                    var got = measure(free ? x[3] : startUt, new Vec3(x[0], x[1], x[2]));
                    if (got == null) return false;
                    c[0] = got.Distance - distance;
                    return true;
                },
                Tolerances = new[] { DistanceResidual },
                MaxIterations = MaxIterations,
            };
            var result = Sqp.Solve(problem);
            if (result.Status == SqpStatus.Invalid) return Fail(result.Reason);

            var burnUt = free ? result.X[3] : startUt;
            var dv = new Vec3(result.X[0], result.X[1], result.X[2]);
            var reached = measure(burnUt, dv);
            if (reached == null) return Fail("the solver's best burn leaves " + from.Name + "'s sphere of influence before the approach");
            Vec3 r, v;
            craft.State(burnUt, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            var plan = new CorrectionPlan
            {
                HasBurn = true,
                Ut = burnUt,
                Burn = dv,
                Prograde = Vec3.Dot(dv, along),
                Normal = Vec3.Dot(dv, up),
                Radial = Vec3.Dot(dv, outward),
                DeltaV = dv.Length,
                DistancePredicted = reached.Distance,
                RelativeSpeedPredicted = reached.RelativeSpeed,
                ApproachUt = reached.Ut,
                Iterations = result.Iterations,
                Evaluations = result.Evaluations,
            };
            if (Math.Abs(plan.DistancePredicted - distance) > ask.DistanceTolerance)
                plan.Error = Failure + ": the best burn (" + Num(plan.DeltaV) + " m/s) comes within " + Num(plan.DistancePredicted) + " m of "
                    + vessel.Name + ", not " + Num(distance) + " m (" + (result.Reason ?? result.Status.ToString()) + ")";
            return plan;
        }

        /// <summary>The closest approach to the vessel between <paramref name="from"/> and <paramref name="to"/> after <paramref name="burn"/> at <paramref name="ut"/>; null when the path leaves the body first.</summary>
        private static Approach Measure(BodySystem system, SnapshotBody body, Conic orbit, double ut, Vec3 burn, VesselTarget vessel, double from, double to)
        {
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            Conic after;
            try
            {
                after = Conic.FromState(body.Gm, r, v + burn, ut);
            }
            catch (ArgumentException)
            {
                return null;
            }
            var path = Follow(system, body, after, ut, 1);
            if (path == null || path[0].Error != null || path[0].Body != body) return null;
            var start = Math.Max(ut, from);
            var end = Math.Min(path[0].EndUt, to);
            if (!(end > start)) return null;
            var got = new Approach();
            vessel.Approach(system, after, start, end, out got.Distance, out got.Ut, out got.RelativeSpeed);
            return got;
        }

        private sealed class Approach
        {
            public double Distance;
            public double Ut;
            public double RelativeSpeed;
        }
    }
}
