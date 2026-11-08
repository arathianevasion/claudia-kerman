// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What a block asks of a Course Correction: the body to arrive at, the periapsis there, optionally the arrival inclination, and when to burn.</summary>
    internal sealed class CorrectionAsk
    {
        public string Target;
        /// <summary>The periapsis altitude at the target, m above its sea level.</summary>
        public double TargetPe;
        /// <summary>The arrival inclination round the target, degrees from 0 to 180, or null for any.</summary>
        public double? TargetInc;
        /// <summary>
        /// The whole arrival plane (#788), a unit vector along the angular momentum wanted, direction of travel included, in place of
        /// <see cref="TargetInc"/>; null for any. A burn inside the target's sphere of influence must be given a plane through its own position
        /// (<see cref="ArrivalPlane.Through"/>), and one from further out a plane through the arrival's way in.
        /// </summary>
        public Vec3? TargetNormal;
        /// <summary>The burn's window, UT: equal for a burn at that time, otherwise the burn time is free within it.</summary>
        public double EarliestUt;
        public double LatestUt;
        /// <summary>How far the answer may be from target_pe (m) and target_inc (degrees) before the solve is an error.</summary>
        public double PeTolerance = 1000;
        public double IncTolerance = 0.5;
        /// <summary>
        /// When the path without a burn misses the target, the UT to aim at its centre for the solve's start (a broken-plane
        /// transfer's plane change, #619): the Lambert burn there. Null: the solve starts from zero burn, which must reach it.
        /// </summary>
        public double? AimUt;
        /// <summary>
        /// The closest approach to the Target Vessel, m, in place of target_pe (#454): set, the correction is to the Game Snapshot's
        /// Target Vessel (<see cref="Target"/> its name, or empty for it) and <see cref="TargetPe"/> and <see cref="TargetInc"/> are not read.
        /// </summary>
        public double? TargetDistance;
        /// <summary>How far the answer may be from target_distance, m, before the solve is an error.</summary>
        public double DistanceTolerance = 10;
        /// <summary>
        /// Where to come down on the target (#793): the landing's miss is one more equality beside target_pe, in place of <see cref="TargetInc"/> and
        /// <see cref="TargetNormal"/>; null for no landing. What the burn can't reach is left in <see cref="CorrectionPlan.ErrorKm"/>.
        /// </summary>
        public ReturnLanding Landing;
        /// <summary>The craft's delta-v left, m/s, which the burn adds nothing for the landing beyond; negative for unknown. A burn that holds target_pe alone is not limited, as without a landing.</summary>
        public double DvLeft = -1;
        /// <summary>m/s the Mission Plan's later legs need (#832), kept back from what the landing may spend; 0 for none.</summary>
        public double LaterLegsDv;
        /// <summary>For measuring (#880): the most the landing may spend along the track, m/s, in place of the rule; null for the rule.</summary>
        public double? MostLandingDv;
        /// <summary>The periapsis the home planet's corridor is measured from, m above sea level (#892: a late trim's), in place of <see cref="TargetPe"/>; null for <see cref="TargetPe"/>. Set, the corridor is still tried when it is too late to move the landing along the track.</summary>
        public double? CorridorPe;
        /// <summary>m either way of its centre the home planet's corridor reaches (#915: <c>transfer_to</c>'s pe_tolerance, <see cref="CorrectionBurn.CorridorWithin"/>), in place of <see cref="CorrectionBurn.PeCorridor"/>; null for that. Set, the corridor spends only the spare.</summary>
        public double? CorridorHalf;
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
        /// <summary>For a correction to the Target Vessel: the burn (inertial, m/s), and the closest approach it leads to, its distance (m), the relative speed then (m/s) and its UT.</summary>
        public Vec3 Burn;
        public double DistancePredicted;
        public double RelativeSpeedPredicted;
        public double ApproachUt;
        /// <summary>For a correction aimed at a landing: where the burn comes down, null without a prediction, and km from the target to there (-1 without one).</summary>
        public ReentryPrediction Landing;
        public double ErrorKm = -1;
        /// <summary>With a miss across the track and the delta-v left known (#790): <see cref="CorrectionBurn.FixFlown"/>, <see cref="CorrectionBurn.MissAccepted"/>; "" with nothing to fix.</summary>
        public string CrossFix = "";
        /// <summary>m/s of the fix's whole burn: flown, or what it would have cost.</summary>
        public double CrossDv;
        /// <summary>The fix in words, for the Debrief's landing line; "" with nothing to fix.</summary>
        public string AimNote = "";
        /// <summary>#915: an aimed ask that moved nothing for the landing: this is the target_pe-only burn, or none, and <see cref="AimNote"/> says why.</summary>
        public bool NotAimed;
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
    internal static partial class CorrectionBurn
    {
        private const string Failure = "found no correction";
        /// <summary>The solver's own tolerances: periapsis (m, as Transfer's PeClose) and the inclination residuals.</summary>
        private const double PeResidual = 10;
        private const double IncResidual = 1e-6;
        private const int MaxIterations = 50;
        /// <summary>The most patches from the burn to the target: out of a moon, round the planet, into another moon.</summary>
        private const int MaxPatches = 4;
        /// <summary>Round the star (#459): how far the finite-difference step moves the arrival, m, and the time unit, s.</summary>
        private const double StarStepMiss = 600;
        private const double StarTimeUnit = 21600;

        /// <summary>The correction for the Game Snapshot's craft.</summary>
        public static CorrectionPlan Find(GameSnapshot snapshot, CorrectionAsk ask)
        {
            if (snapshot == null) return Fail("no Game Snapshot");
            if (ask != null && ask.TargetDistance.HasValue) return FindApproach(snapshot, ask);
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
            var seed = default(Vec3);
            var needed = PatchesTo(system, from, coast.Orbit, startUt, seed, target);
            if (needed == 0 && ask.AimUt.HasValue)
            {
                seed = Aim(system, from, coast.Orbit, startUt, ask.AimUt.Value, target);
                needed = PatchesTo(system, from, coast.Orbit, startUt, seed, target);
            }
            if (needed == 0)
                return Fail("the trajectory from the burn does not reach " + target.Name + " within " + MaxPatches + " patches: a Course Correction starts from one that does");

            var withInc = ask.TargetInc.HasValue;
            var peR = target.Radius + ask.TargetPe;
            // A plane's residual is one angle (B-plane targeting): how far the arrival is turned from the plane wanted, about the direction it comes in
            // from. Turning about that is what is cheap; the normal's share along it would need a new approach, and asking for that leaves the solve singular.
            // An angle, not a component, so it has slope at 90 degrees and no second root at the mirror.
            var planeAxis = default(Vec3);
            var withNormal = ask.TargetNormal.HasValue;
            if (withNormal)
            {
                var entry = SingleShooting.Arrive(system, from, coast.Orbit, startUt, seed, target, needed);
                planeAxis = Vec3.Cross(ask.TargetNormal.Value, from == target ? coast.Orbit.Position(startUt) : ArrivalPlanes.WayIn(entry.Orbit, entry.StartUt)).Normalized;
                if (from == target) seed = InPlaneSeed(coast.Orbit, startUt, ask.TargetNormal.Value, peR);
            }
            var m = 1 + (withInc ? Sqp.InclinationConstraints(ask.TargetInc.Value) : 0) + (withNormal ? 1 : 0);
            var tolerances = new double[m];
            tolerances[0] = PeResidual;
            for (var k = 1; k < m; k++) tolerances[k] = IncResidual;
            var n = free ? 4 : 3;
            var problem = new SqpProblem
            {
                Scale = Units(system, from, coast.Orbit, startUt, seed, target, needed),
                Variables = free
                    ? new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Time }
                    : new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity },
                Start = free ? new[] { seed.X, seed.Y, seed.Z, startUt } : new[] { seed.X, seed.Y, seed.Z },
                Lower = free ? new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, ask.EarliestUt } : null,
                Upper = free ? new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, ask.LatestUt } : null,
                Objective = x => x[0] * x[0] + x[1] * x[1] + x[2] * x[2],
                ObjectiveHessian = free
                    ? new double[,] { { 2, 0, 0, 0 }, { 0, 2, 0, 0 }, { 0, 0, 2, 0 }, { 0, 0, 0, 0 } }
                    : new double[,] { { 2, 0, 0 }, { 0, 2, 0 }, { 0, 0, 2 } },
                Constraints = m,
                Evaluate = (x, c) =>
                {
                    var arrival = SingleShooting.Arrive(system, from, coast.Orbit, n == 4 ? x[3] : startUt, new Vec3(x[0], x[1], x[2]), target, needed);
                    if (arrival == null) return false;
                    c[0] = arrival.Orbit.PeR - peR;
                    if (withInc) Sqp.Inclination(Normal(arrival), ask.TargetInc.Value, c, 1);
                    if (withNormal)
                    {
                        var unit = Normal(arrival).Normalized;
                        c[m - 1] = Math.Atan2(Vec3.Dot(unit, planeAxis), Vec3.Dot(unit, ask.TargetNormal.Value));
                    }
                    return true;
                },
                Tolerances = tolerances,
                MaxIterations = MaxIterations,
            };
            Func<double, Vec3, Patch> arrive = (when, dv) => SingleShooting.Arrive(system, from, coast.Orbit, when, dv, target, needed);
            var result = Sqp.Solve(problem);
            if (result.Status == SqpStatus.Invalid)
                return ask.Landing != null ? Unsolved(arrive, startUt, target, ask, result.Reason) : Fail(result.Reason);

            var ut = free ? result.X[3] : startUt;
            var burn = new Vec3(result.X[0], result.X[1], result.X[2]);
            var reached = SingleShooting.Arrive(system, from, coast.Orbit, ut, burn, target, needed);
            var unreached = "the solver's best burn does not reach " + target.Name;
            if (reached == null) return ask.Landing != null ? Unsolved(arrive, startUt, target, ask, unreached) : Fail(unreached);
            double prograde, normal, radial;
            SingleShooting.NodeFrame(coast.Orbit, ut, burn, out prograde, out normal, out radial);
            var plan = new CorrectionPlan
            {
                HasBurn = true,
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = burn.Length,
                PePredicted = reached.Orbit.PeR - target.Radius,
                IncPredicted = reached.Orbit.Inclination,
                Iterations = result.Iterations,
                Evaluations = result.Evaluations,
            };
            plan.Error = SingleShooting.ToleranceError(Failure, plan.DeltaV, plan.PePredicted, plan.IncPredicted, ask.TargetPe, ask.TargetInc,
                ask.PeTolerance, ask.IncTolerance, result.Reason ?? result.Status.ToString());
            if (plan.Error == null && withNormal && from == target && ArrivalPlane.Angle(reached.Orbit.W, ask.TargetNormal.Value) > ask.IncTolerance)
                plan.Error = Failure + ": the best burn (" + Num(plan.DeltaV) + " m/s) arrives " + Num(ArrivalPlane.Angle(reached.Orbit.W, ask.TargetNormal.Value)) + " degrees off the plane asked";
            if (ask.Landing == null) return plan;
            if (plan.Error == null) return EntryChecked(AimLanding(coast.Orbit, target, peR, ask, problem, arrive, plan, burn), arrive, startUt, target, ask);
            // #805: aimed at a landing, the plain burn flies if it was solved (its periapsis one the landing may be left on, PeAcceptable), and otherwise none.
            return PeAcceptable(target, ask, plan.PePredicted) ? PlainOnly(arrive, startUt, target, ask, plan, burn) : Unsolved(arrive, startUt, target, ask, plan.Error);
        }

        /// <summary>
        /// The burn at <paramref name="ut"/> on <paramref name="orbit"/>, keeping its energy, that puts the orbit in the plane of
        /// <paramref name="normal"/> (through the craft's position) with periapsis radius <paramref name="peR"/>.
        /// </summary>
        private static Vec3 InPlaneSeed(Conic orbit, double ut, Vec3 normal, double peR)
        {
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            var radius = r.Length;
            var up = r / radius;
            var energy = Vec3.Dot(v, v) / 2 - orbit.Mu / radius;
            var h = peR * Math.Sqrt(Math.Max(0, 2 * (energy + orbit.Mu / peR)));
            var along = h / radius;
            var outward = Math.Sqrt(Math.Max(0, Vec3.Dot(v, v) - along * along));
            return up * (Vec3.Dot(v, up) < 0 ? -outward : outward) + Vec3.Cross(normal, up) * along - v;
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
            if (ask.Landing != null && (ask.TargetInc.HasValue || ask.TargetNormal.HasValue)) return "a landing target takes no arrival inclination or plane";
            return null;
        }

        /// <summary>The patch of the trajectory from <paramref name="orbit"/> at <paramref name="now"/> that <paramref name="ut"/> falls on, or null.</summary>
        private static Patch PatchAt(BodySystem system, SnapshotBody body, Conic orbit, double now, double ut)
        {
            for (var count = 1; count <= MaxPatches; count++)
            {
                var path = SingleShooting.Follow(system, body, orbit, now, count);
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
                if (SingleShooting.Arrive(system, body, orbit, ut, burn, target, count) != null) return count;
            return 0;
        }

        /// <summary>
        /// The burn at <paramref name="ut"/> on <paramref name="orbit"/> whose Lambert arc reaches <paramref name="target"/>'s
        /// centre at <paramref name="aimUt"/>, without its gravity; zero when the target doesn't orbit <paramref name="body"/> or there is no arc.
        /// </summary>
        private static Vec3 Aim(BodySystem system, SnapshotBody body, Conic orbit, double ut, double aimUt, SnapshotBody target)
        {
            if (!(aimUt > ut) || !string.Equals(target.Parent, body.Name, StringComparison.OrdinalIgnoreCase)) return default(Vec3);
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            var arc = Lambert.Solve(body.Gm, r, system.OrbitOf(target).Position(aimUt), aimUt - ut, orbit.W);
            return arc.Error == null ? arc.V1 - v : default(Vec3);
        }

        /// <summary>
        /// The solver's units: the burn's body's, as before #459, except on a patch round the star (no parent), whose radius makes
        /// the finite-difference step about 0.9 m/s, which over a coast of months moves the arrival by thousands of kilometres. There
        /// the velocity unit is set so the step moves the arrival by <see cref="StarStepMiss"/>, as 3 cm/s does across a Mun coast,
        /// and the time unit is a day.
        /// </summary>
        private static Scale Units(BodySystem system, SnapshotBody from, Conic orbit, double ut, Vec3 seed, SnapshotBody target, int needed)
        {
            var body = Scale.Create(from.Gm, from.Radius);
            if (system.Parent(from) != null) return body;
            var arrival = SingleShooting.Arrive(system, from, orbit, ut, seed, target, needed);
            var coast = arrival != null ? arrival.StartUt - ut : 0;
            if (!(coast > 0)) return body;
            return Scale.Units(Math.Min(body.VelocityScale, StarStepMiss / coast / Sqp.VelocityStep), StarTimeUnit);
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
    }
}
