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
    /// <summary>What a block asks of a Course Correction: the body to arrive at, the periapsis there, optionally the arrival inclination, and when to burn.</summary>
    internal sealed class CorrectionAsk
    {
        public string Target;
        /// <summary>The periapsis altitude at the target, m above its sea level.</summary>
        public double TargetPe;
        /// <summary>The arrival inclination round the target, degrees from 0 to 180, or null for any.</summary>
        public double? TargetInc;
        /// <summary>The burn's window, UT: equal for a burn at that time, otherwise the burn time is free within it.</summary>
        public double EarliestUt;
        public double LatestUt;
        /// <summary>How far the answer may be from target_pe (m) and target_inc (degrees) before the solve is an error.</summary>
        public double PeTolerance = 1000;
        public double IncTolerance = 0.5;
    }

    /// <summary>The correction node found, or why there is none (<see cref="Error"/>; the rest is set when a best point exists).</summary>
    internal sealed class CorrectionPlan
    {
        public string Error;
        /// <summary>Whether the rest is set: a burn was found, met the tolerances (no <see cref="Error"/>) or not.</summary>
        public bool HasBurn;
        public double Ut;
        /// <summary>The node's components, m/s, in stock's maneuver-node frame (as <see cref="PredictBurn"/> takes them).</summary>
        public double Prograde;
        public double Normal;
        public double Radial;
        public double DeltaV;
        /// <summary>The periapsis (m above sea level) and inclination (degrees) the node arrives at round the target.</summary>
        public double PePredicted;
        public double IncPredicted;
        public int Iterations;
        public int Evaluations;
    }

    /// <summary>
    /// A Course Correction (#449, ADR 0010): the smallest burn in a window that brings the trajectory to the target body at
    /// target_pe and, when asked, at target_inc. Single shooting through <see cref="BodySystem.Follow"/>: the variables are the
    /// burn's three inertial components, and its time when the window is open, solved by <see cref="Sqp"/> from zero burn, which
    /// must already reach the target. After <c>MechJebLib/Maneuvers/FineTuneClosestApproachToCelestial.cs</c>'s formulation, without
    /// its short-way/long-way pair: there is no Lambert step to have two ways (ADR 0010). It replaces kerboscript's
    /// <c>claudia_correct</c> for <c>transfer_to</c> and <c>return</c>. It works on detached orbits and only reads.
    /// </summary>
    internal static class CorrectionBurn
    {
        private const string Failure = "found no correction";
        /// <summary>The solver's own tolerances: periapsis (m, as Transfer's PeClose) and the inclination residuals.</summary>
        private const double PeResidual = 10;
        private const double IncResidual = 1e-6;
        private const int MaxIterations = 50;
        /// <summary>The most patches from the burn to the target: out of a moon, round the planet, into another moon.</summary>
        private const int MaxPatches = 4;

        /// <summary>The correction for the Game Snapshot's craft.</summary>
        public static CorrectionPlan Find(GameSnapshot snapshot, CorrectionAsk ask)
        {
            if (snapshot == null) return Fail("no Game Snapshot");
            if (ask == null || string.IsNullOrEmpty(ask.Target)) return Fail("target is required");
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, ask.Target, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            return Find(setup.System, setup.Body, setup.Start, setup.Now, ask);
        }

        /// <summary>The correction for a craft on <paramref name="orbit"/> round <paramref name="body"/> at <paramref name="now"/>.</summary>
        public static CorrectionPlan Find(BodySystem system, SnapshotBody body, Conic orbit, double now, CorrectionAsk ask)
        {
            var why = Unreadable(ask, now);
            if (why != null) return Fail(why);
            var target = system.Find(ask.Target);
            if (target == null) return Fail("there is no body named " + ask.Target);
            if (target.SoiRadius > 0 && ask.TargetPe >= target.SoiRadius - target.Radius)
                return Fail("target_pe must be under " + Num(target.SoiRadius - target.Radius) + " m, inside " + target.Name + "'s sphere of influence");

            // The burn is on the patch the window opens on, and the whole window must be on it.
            var coast = PatchAt(system, body, orbit, now, ask.EarliestUt);
            if (coast == null) return Fail("the trajectory does not reach UT " + Num(ask.EarliestUt) + " in flight");
            if (coast.End != PatchEnd.Stable && coast.EndUt < ask.LatestUt)
                return Fail("the burn window crosses the change of sphere of influence at UT " + Num(coast.EndUt) + ": end it before then, or start it after");
            var from = coast.Body;
            var free = ask.LatestUt > ask.EarliestUt;
            var startUt = free ? (ask.EarliestUt + ask.LatestUt) / 2 : ask.EarliestUt;

            // Follow only as many patches as the uncorrected path takes to the target (a hyperbola round the star ends the list
            // without a throw since #471), and take a throw, or a path that does not get there in as many, as an undefined point.
            var needed = PatchesTo(system, from, coast.Orbit, startUt, default(Vec3), target);
            if (needed == 0)
                return Fail("the trajectory from the burn does not reach " + target.Name + " within " + MaxPatches + " patches: a Course Correction starts from one that does");

            var withInc = ask.TargetInc.HasValue;
            var m = 1 + (withInc ? Sqp.InclinationConstraints(ask.TargetInc.Value) : 0);
            var tolerances = new double[m];
            tolerances[0] = PeResidual;
            for (var k = 1; k < m; k++) tolerances[k] = IncResidual;
            var peR = target.Radius + ask.TargetPe;
            var n = free ? 4 : 3;
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
                Constraints = m,
                Evaluate = (x, c) =>
                {
                    var arrival = Arrive(system, from, coast.Orbit, n == 4 ? x[3] : startUt, new Vec3(x[0], x[1], x[2]), target, needed);
                    if (arrival == null) return false;
                    c[0] = arrival.Orbit.PeR - peR;
                    if (withInc) Sqp.Inclination(Normal(arrival), ask.TargetInc.Value, c, 1);
                    return true;
                },
                Tolerances = tolerances,
                MaxIterations = MaxIterations,
            };
            var result = Sqp.Solve(problem);
            if (result.Status == SqpStatus.Invalid) return Fail(result.Reason);

            var ut = free ? result.X[3] : startUt;
            var burn = new Vec3(result.X[0], result.X[1], result.X[2]);
            var reached = Arrive(system, from, coast.Orbit, ut, burn, target, needed);
            if (reached == null) return Fail("the solver's best burn does not reach " + target.Name);
            Vec3 r, v;
            coast.Orbit.State(ut, out r, out v);
            // Stock's maneuver-node frame, as PredictBurn applies it.
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            var plan = new CorrectionPlan
            {
                HasBurn = true,
                Ut = ut,
                Prograde = Vec3.Dot(burn, along),
                Normal = Vec3.Dot(burn, up),
                Radial = Vec3.Dot(burn, outward),
                DeltaV = burn.Length,
                PePredicted = reached.Orbit.PeR - target.Radius,
                IncPredicted = reached.Orbit.Inclination,
                Iterations = result.Iterations,
                Evaluations = result.Evaluations,
            };
            // The solver's own status is not the test: the caller's tolerances, in metres and degrees, are.
            var peOff = Math.Abs(plan.PePredicted - ask.TargetPe);
            // In degrees, so an equatorial target is not met by arriving the other way round (the normal's pair can't tell).
            var incOff = withInc ? Math.Abs(plan.IncPredicted - ask.TargetInc.Value) : 0;
            if (peOff > ask.PeTolerance || incOff > ask.IncTolerance)
            {
                plan.Error = Failure + ": the best burn (" + Num(plan.DeltaV) + " m/s) arrives at periapsis " + Num(plan.PePredicted) + " m"
                    + (withInc ? " and inclination " + Deg(plan.IncPredicted) + "°" : "") + ", not " + Num(ask.TargetPe) + " m"
                    + (withInc ? " and " + Deg(ask.TargetInc.Value) + "°" : "") + " (" + (result.Reason ?? result.Status.ToString()) + ")";
            }
            return plan;
        }

        /// <summary>Why the ask can't be solved as given, or null.</summary>
        private static string Unreadable(CorrectionAsk ask, double now)
        {
            if (ask == null || string.IsNullOrEmpty(ask.Target)) return "target is required";
            if (!Finite(ask.TargetPe) || ask.TargetPe <= 0) return "target_pe must be metres above 0, not " + Num(ask.TargetPe);
            if (ask.TargetInc.HasValue && !(ask.TargetInc.Value >= 0 && ask.TargetInc.Value <= 180))
                return "target_inc must be from 0 to 180 degrees, not " + Num(ask.TargetInc.Value);
            if (!Finite(ask.EarliestUt) || !Finite(ask.LatestUt)) return "earliest_ut and latest_ut must be finite UTs";
            if (ask.EarliestUt < now) return "earliest_ut (UT " + Num(ask.EarliestUt) + ") is before now (UT " + Num(now) + ")";
            if (ask.LatestUt < ask.EarliestUt)
                return "the burn must come by latest_ut, UT " + Num(ask.LatestUt) + ", before earliest_ut, UT " + Num(ask.EarliestUt);
            if (!(ask.PeTolerance > 0) || !(ask.IncTolerance > 0)) return "pe_tolerance and inc_tolerance must be above 0";
            return null;
        }

        /// <summary>The patch of the trajectory from <paramref name="orbit"/> at <paramref name="now"/> that <paramref name="ut"/> falls on, or null.</summary>
        private static Patch PatchAt(BodySystem system, SnapshotBody body, Conic orbit, double now, double ut)
        {
            for (var count = 1; count <= MaxPatches; count++)
            {
                var path = Follow(system, body, orbit, now, count);
                if (path == null) return null;
                var last = path[path.Count - 1];
                if (last.Error != null) return null;
                if (last.EndUt >= ut) return last;
                if (path.Count < count) return last.End == PatchEnd.Stable ? last : null;
            }
            return null;
        }

        /// <summary>How many patches the path after <paramref name="burn"/> takes to reach <paramref name="target"/>, or 0 if it doesn't within <see cref="MaxPatches"/>.</summary>
        private static int PatchesTo(BodySystem system, SnapshotBody body, Conic orbit, double ut, Vec3 burn, SnapshotBody target)
        {
            for (var count = 1; count <= MaxPatches; count++)
                if (Arrive(system, body, orbit, ut, burn, target, count) != null) return count;
            return 0;
        }

        /// <summary>
        /// The first patch round <paramref name="target"/> after <paramref name="burn"/> (inertial, m/s) at <paramref name="ut"/>
        /// on <paramref name="orbit"/>, following at most <paramref name="patches"/> patches; null when it does not get there.
        /// </summary>
        private static Patch Arrive(BodySystem system, SnapshotBody body, Conic orbit, double ut, Vec3 burn, SnapshotBody target, int patches)
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
            var path = Follow(system, body, after, ut, patches);
            if (path == null) return null;
            foreach (var patch in path)
            {
                if (patch.Body == target) return patch;
                if (patch.Error != null) return null;
            }
            return null;
        }

        /// <summary>
        /// <see cref="BodySystem.Follow"/>, or null where it throws on a radial path (a last patch that is a hyperbola round
        /// the star no longer throws, #471). The wrapper stays until #497.
        /// </summary>
        private static List<Patch> Follow(BodySystem system, SnapshotBody body, Conic orbit, double ut, int patches)
        {
            try
            {
                return system.Follow(body, orbit, ut, patches);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static Vec3 Normal(Patch patch)
        {
            Vec3 r, v;
            patch.Orbit.State(patch.StartUt, out r, out v);
            return Vec3.Cross(r, v);
        }

        internal static CorrectionPlan Fail(string why)
        {
            return new CorrectionPlan { Error = Failure + ": " + why };
        }

        private static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Deg(double x)
        {
            return x.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
