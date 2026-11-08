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
    /// <summary>What <c>return</c> asks of its ejection: the periapsis at the moon's planet, optionally the arrival inclination there, and the burn's window.</summary>
    internal sealed class ReturnAsk
    {
        /// <summary>The periapsis altitude at the planet, m above its sea level.</summary>
        public double TargetPe;
        /// <summary>The arrival inclination round the planet, degrees from 0 to 180, or null for any.</summary>
        public double? TargetInc;
        /// <summary>The soonest UT the burn may be: the block's start plus the turn time, or the window's UT if later; one already past is now.</summary>
        public double EarliestUt;
        /// <summary>The latest UT the burn may be, or null for one orbit of the craft after <see cref="EarliestUt"/>, so the burn point comes round once. With <see cref="Landing"/>, a limit on the windows beside <see cref="MaxWait"/>.</summary>
        public double? LatestUt;
        /// <summary>How far the answer may be from target_pe (m) and target_inc (degrees) before the solve is an error.</summary>
        public double PeTolerance = 1000;
        public double IncTolerance = 0.5;
        /// <summary>Where to come down (#792), for <see cref="ReturnAim"/> to pick the window and time the burn; null for the least burn in the window.</summary>
        public ReturnLanding Landing;
        /// <summary>With <see cref="Landing"/>, the seconds after <see cref="EarliestUt"/> the burn may wait; null for <see cref="ReturnAim.AutoWait"/>.</summary>
        public double? MaxWait;
        /// <summary>Without <see cref="Landing"/>: the block was given no window (#816), so from an inclined orbit a next pass over the cheapest within the auto wait plus <see cref="ReturnAim.CostCapMs"/> waits for the cheapest.</summary>
        public bool CheapWindow;
        /// <summary>Without <see cref="Landing"/> (#888): which way the planet's frame turns the landing's longitude, from <see cref="ReturnAim.HandedOn"/>; 1 when not read.</summary>
        public double Handed = 1;
        /// <summary>With the search over a costly plane (#887): the window the Mission planned, so a restart past the cheap alignment keeps that alignment's window; null for the next alignment.</summary>
        public double? PlannedUt;
        /// <summary>Start from the coarse grid, as when the analytic seed is undefined: for tests of that worst case.</summary>
        internal bool FromGrid;
        /// <summary>The cap on the SQP's iterations, and whether a best point short of the answer gets its finish: for tests of a solve that stops short.</summary>
        internal int SolverIterations = 50;
        internal bool FinishSearch = true;
    }

    /// <summary>The ejection node found, or why there is none (<see cref="Error"/>; the rest is set when a best point exists).</summary>
    internal sealed class ReturnPlan
    {
        public string Error;
        /// <summary>Whether the rest is set: a burn was found, met the tolerances (no <see cref="Error"/>) or not.</summary>
        public bool HasBurn;
        /// <summary>The planet the moon orbits, which the burn returns to.</summary>
        public string Planet;
        public double Ut;
        /// <summary>The node's components, m/s, in stock's maneuver-node frame (as <see cref="PredictBurn"/> takes them).</summary>
        public double Prograde;
        public double Normal;
        public double Radial;
        public double DeltaV;
        /// <summary>The periapsis (m above sea level) and inclination (degrees) the node arrives at round the planet, and when it leaves the moon's sphere of influence.</summary>
        public double PePredicted;
        public double IncPredicted;
        public double SoiUt;
        /// <summary>Whether the solve started from the coarse grid because the analytic seed was undefined.</summary>
        public bool FromGrid;
        /// <summary>What finished the burn when the SQP stopped short of it, in words (#759), or null when the SQP reached it.</summary>
        public string FinishedBy;
        public int Iterations;
        public int Evaluations;
        /// <summary>With a landing asked: where this burn comes down, or null when the predictor can't say (the burn is then the window's least).</summary>
        public ReentryPrediction Landing;
        /// <summary>km from the target to <see cref="Landing"/>; -1 without one.</summary>
        public double ErrorKm = -1;
        /// <summary>m/s this burn costs over the least burn of its window; from an inclined orbit (#816), over the cheapest window within max_wait.</summary>
        public double AimDv;
        /// <summary>km <see cref="ReturnAim.ResidualMs"/> of error in the burn moves the landing; -1 when it can't be said.</summary>
        public double SpreadKm = -1;
        /// <summary>From an inclined orbit (#816), the window rule's figures for the log: the cheapest, the cap and the window taken; null otherwise.</summary>
        public string WindowNote;
    }

    /// <summary>
    /// <c>return</c>'s ejection from any moon to its planet (#450, ADR 0010): the smallest burn in a window that leaves the moon
    /// on a path whose periapsis at the planet is target_pe and, when asked, whose inclination there is target_inc. After the
    /// formulation of <c>MechJebLib/Maneuvers/ReturnFromMoon.cs</c> (MechJeb2 a958383), posed as single shooting through
    /// <see cref="BodySystem.Follow"/> instead of its multiple shooting: the variables are the burn's three node components, the
    /// prograde one bounded clear of the moon's sphere of influence (<see cref="ProgradeFloor"/>), and its time, solved by
    /// <see cref="Sqp"/>, with a search along prograde or in time to finish a best point it stops short at (#759); then, if that
    /// fails, the same unbounded over inertial components. It starts from an analytic seed, the prograde burn whose escape asymptote leaves
    /// backwards along the moon's orbit at the speed a Hohmann-like descent from the moon's distance to target_pe needs, at the
    /// point where the asymptote lines up; where that seed is undefined, from the best point of a coarse grid over burn time and
    /// prograde delta-v. There is no short-way/long-way pair: the seed picks the escape side that lowers the periapsis. It
    /// works on detached orbits and only reads.
    /// </summary>
    internal static class ReturnBurn
    {
        private const string Failure = "found no return";
        /// <summary>The solver's own tolerances: periapsis (m, as <see cref="CorrectionBurn"/>'s) and the inclination residuals.</summary>
        private const double PeResidual = 10;
        private const double IncResidual = 1e-6;
        /// <summary>How far past the moon's sphere of influence the path out must reach, as a share of the sphere (see <see cref="ProgradeFloor"/>).</summary>
        private const double EdgeMargin = 0.05;
        /// <summary>The finishes' searches: how far either side of the best point each looks for the crossing (m/s; shares of the craft's orbit), each's tolerance (m/s; s), and Brent's cap.</summary>
        private static readonly double[] ProgradeReach = { 0.25, 0.5, 1, 2, 4, 8 };
        private static readonly double[] TimeReach = { 1.0 / 512, 1.0 / 256, 1.0 / 128, 1.0 / 64, 1.0 / 32, 1.0 / 16 };
        private const double ProgradeTolerance = 1e-4;
        private const double TimeTolerance = 1e-2;
        private const int FinishIterations = 30;
        private const string Prograde1D = "a search on the prograde component";
        private const string TimeShift = "a time shift";
        private const string InertialSolve = "the inertial solve, without the bound";
        /// <summary>Out of the moon, round the planet: a last patch round the star no longer throws (#471).</summary>
        internal const int Patches = 2;
        /// <summary>The seed's search for the burn point: samples over the window.</summary>
        private const int SeedSamples = 360;
        /// <summary>The fallback grid: burn times over the window, and prograde burns from the escape speed up by these factors of it.</summary>
        private const int GridTimes = 24;
        private static readonly double[] GridEscape = { 1.0, 1.05, 1.1, 1.2, 1.35, 1.5 };
        /// <summary>The out-of-plane share of the seed tried, in steps of this, to move an inclination target's start off the equator.</summary>
        private const double SeedTilt = 0.02;
        private const int SeedTilts = 5;
        /// <summary>A least burn over this many times the burn straight onto the asymptote (<see cref="Direct"/>) is solved again from that burn (#816).</summary>
        private const double OverDirect = 2;

        /// <summary>The ejection for the Game Snapshot's craft.</summary>
        public static ReturnPlan Find(GameSnapshot snapshot, ReturnAsk ask)
        {
            if (snapshot == null) return Fail("no Game Snapshot");
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            return Find(setup.System, setup.Body, setup.Start, setup.Now, ask);
        }

        /// <summary>The ejection for a craft on <paramref name="orbit"/> round <paramref name="moon"/> at <paramref name="now"/>.</summary>
        public static ReturnPlan Find(BodySystem system, SnapshotBody moon, Conic orbit, double now, ReturnAsk ask)
        {
            if (ask == null) return Fail("no ask given");
            if (ask.Landing != null || ask.CheapWindow) return ReturnAim.Find(system, moon, orbit, now, ask);
            var planet = system.Parent(moon);
            if (planet == null || system.Parent(planet) == null)
                return Fail("the craft orbits " + moon.Name + ", not a moon: return starts from orbit round a moon");
            if (!orbit.Closed) return Fail("the craft is not in a closed orbit round " + moon.Name);
            // An earliest_ut already past (the call comes a moment after the block asked) is now, as Transfer takes it for transfer_to.
            var earliest = Math.Max(now, ask.EarliestUt);
            var latest = ask.LatestUt ?? earliest + orbit.Period;
            var why = Unreadable(ask, earliest, latest);
            if (why != null) return Fail(why);
            if (planet.SoiRadius > 0 && ask.TargetPe >= planet.SoiRadius - planet.Radius)
                return Fail("target_pe must be under " + Num(planet.SoiRadius - planet.Radius) + " m, inside " + planet.Name + "'s sphere of influence");
            var stable = SingleShooting.Follow(system, moon, orbit, now, 1);
            if (stable == null || stable[0].End != PatchEnd.Stable || stable[0].Error != null)
                return Fail("the craft's orbit round " + moon.Name + " does not stay there until the burn: return starts from a stable orbit");

            var peR = planet.Radius + ask.TargetPe;
            var fromGrid = false;
            double startUt;
            var seed = Seed(system, moon, planet, orbit, earliest, latest, peR, out startUt);
            if (ask.FromGrid || SingleShooting.Arrive(system, moon, orbit, startUt, seed, planet, Patches) == null)
            {
                fromGrid = true;
                if (!Grid(system, moon, planet, orbit, earliest, latest, peR, out startUt, out seed))
                    return Fail("no prograde burn in the window leaves " + moon.Name + " for " + planet.Name);
            }
            var plan = From(system, moon, planet, orbit, ask, earliest, latest, peR, startUt, seed, fromGrid);
            // #816: far out of the craft's plane the prograde seed can lead the solve to a burn of thousands of m/s that still meets
            // target_pe (6,196 m/s at UT 1000 from a 66° Mun orbit). Over a window, a solve that fails or costs over twice the burn
            // straight onto the asymptote starts again from that burn, and the cheaper answer that meets the ask stands.
            if (latest > earliest)
            {
                double directUt;
                var direct = Direct(system, moon, planet, orbit, earliest, latest, peR, out directUt);
                if (direct.Length > 0 && (plan.Error != null || plan.DeltaV > OverDirect * direct.Length))
                {
                    var again = From(system, moon, planet, orbit, ask, earliest, latest, peR, directUt, direct, false);
                    if (again.Error == null && (plan.Error != null || again.DeltaV < plan.DeltaV)) plan = again;
                }
            }
            return plan;
        }

        /// <summary>The solve from <paramref name="seed"/> at <paramref name="startUt"/>: bounded, then inertial, each finished when it stops short.</summary>
        private static ReturnPlan From(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, ReturnAsk ask, double earliest, double latest, double peR,
            double startUt, Vec3 seed, bool fromGrid)
        {
            if (ask.TargetInc.HasValue && Sqp.InclinationConstraints(ask.TargetInc.Value) == 1) seed = OffTheEquator(system, moon, planet, orbit, startUt, seed);

            var floor = ProgradeFloor(moon, orbit, startUt);
            ReturnPlan stopped = null;
            foreach (var bounded in new[] { true, false })
            {
                var plan = Solved(system, moon, planet, orbit, ask, earliest, latest, peR, startUt, seed, floor, bounded);
                plan.FromGrid = fromGrid;
                if (plan.Error == null)
                {
                    if (!bounded) plan.FinishedBy = InertialSolve;
                    return plan;
                }
                if (stopped == null || (!stopped.HasBurn && plan.HasBurn)) stopped = plan;
                if (!plan.HasBurn || !ask.FinishSearch) continue;
                var finished = Finished(FinishAlongPrograde(system, moon, planet, orbit, plan, floor, peR), plan, Prograde1D, ask)
                    ?? Finished(FinishInTime(system, moon, planet, orbit, plan, earliest, latest, peR), plan, TimeShift, ask);
                if (finished != null) return finished;
            }
            return stopped;
        }

        /// <summary>
        /// One SQP solve. <paramref name="bounded"/>: over the burn's node components with the prograde one over <paramref name="floor"/>, the
        /// answer to take, clear of the sphere's edge. Otherwise over its inertial components, unbounded, for what that can't do: the
        /// node frame turns with the burn's time, which couples its variables when the burn is large or against the orbit.
        /// </summary>
        private static ReturnPlan Solved(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, ReturnAsk ask, double earliest, double latest, double peR,
            double startUt, Vec3 seed, double floor, bool bounded)
        {
            var free = latest > earliest;
            var withInc = ask.TargetInc.HasValue;
            var m = 1 + (withInc ? Sqp.InclinationConstraints(ask.TargetInc.Value) : 0);
            var tolerances = new double[m];
            tolerances[0] = PeResidual;
            for (var k = 1; k < m; k++) tolerances[k] = IncResidual;
            Func<double[], double, Vec3> burnOf = (x, t) => bounded ? SingleShooting.FromNodeFrame(orbit, t, x[0], x[1], x[2]) : new Vec3(x[0], x[1], x[2]);
            double first = seed.X, second = seed.Y, third = seed.Z;
            if (bounded)
            {
                SingleShooting.NodeFrame(orbit, startUt, seed, out first, out second, out third);
                first = Math.Max(first, floor);
            }
            var lowest = bounded ? floor : double.NegativeInfinity;
            var problem = new SqpProblem
            {
                Scale = Scale.Create(moon.Gm, moon.Radius),
                Variables = free
                    ? new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Time }
                    : new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity },
                Start = free ? new[] { first, second, third, startUt } : new[] { first, second, third },
                Lower = free ? new[] { lowest, double.NegativeInfinity, double.NegativeInfinity, earliest } : new[] { lowest, double.NegativeInfinity, double.NegativeInfinity },
                Upper = free
                    ? new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, latest }
                    : new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity },
                Objective = x => x[0] * x[0] + x[1] * x[1] + x[2] * x[2],
                ObjectiveHessian = free
                    ? new double[,] { { 2, 0, 0, 0 }, { 0, 2, 0, 0 }, { 0, 0, 2, 0 }, { 0, 0, 0, 0 } }
                    : new double[,] { { 2, 0, 0 }, { 0, 2, 0 }, { 0, 0, 2 } },
                Constraints = m,
                Evaluate = (x, c) =>
                {
                    var t = free ? x[3] : startUt;
                    var arrival = SingleShooting.Arrive(system, moon, orbit, t, burnOf(x, t), planet, Patches);
                    if (arrival == null) return false;
                    c[0] = arrival.Orbit.PeR - peR;
                    if (withInc) Sqp.Inclination(Normal(arrival), ask.TargetInc.Value, c, 1);
                    return true;
                },
                Tolerances = tolerances,
                MaxIterations = ask.SolverIterations,
            };
            var result = Sqp.Solve(problem);
            if (result.Status == SqpStatus.Invalid) return Fail(result.Reason);

            var ut = free ? result.X[3] : startUt;
            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, ut, burnOf(result.X, ut), out prograde, out normal, out radial);
            var plan = PlanAt(system, moon, planet, orbit, ut, prograde, normal, radial);
            if (plan == null) return Fail("the solver's best burn does not leave " + moon.Name + " for " + planet.Name);
            plan.Iterations = result.Iterations;
            plan.Evaluations = result.Evaluations;
            plan.Error = PlanError(plan, ask, result.Reason ?? result.Status.ToString());
            return plan;
        }

        /// <summary>A finish's plan, as the stopped solve's but for what the search moved, when it meets the tolerances; else null.</summary>
        private static ReturnPlan Finished(ReturnPlan finish, ReturnPlan stopped, string by, ReturnAsk ask)
        {
            if (finish == null || PlanError(finish, ask, "") != null) return null;
            finish.FromGrid = stopped.FromGrid;
            finish.Iterations = stopped.Iterations;
            finish.Evaluations = stopped.Evaluations;
            finish.FinishedBy = by;
            return finish;
        }

        /// <summary>The plan for a burn in node components at <paramref name="ut"/>, or null where it does not reach the planet.</summary>
        private static ReturnPlan PlanAt(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, double ut, double prograde, double normal, double radial)
        {
            var reached = SingleShooting.Arrive(system, moon, orbit, ut, SingleShooting.FromNodeFrame(orbit, ut, prograde, normal, radial), planet, Patches);
            if (reached == null) return null;
            return new ReturnPlan
            {
                HasBurn = true,
                Planet = planet.Name,
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = Math.Sqrt(prograde * prograde + normal * normal + radial * radial),
                PePredicted = reached.Orbit.PeR - planet.Radius,
                IncPredicted = reached.Orbit.Inclination,
                SoiUt = reached.StartUt,
            };
        }

        private static string PlanError(ReturnPlan plan, ReturnAsk ask, string solverReason)
        {
            return SingleShooting.ToleranceError(Failure, plan.DeltaV, plan.PePredicted, plan.IncPredicted, ask.TargetPe, ask.TargetInc,
                ask.PeTolerance, ask.IncTolerance, solverReason);
        }

        /// <summary>
        /// The least prograde burn whose path clears the moon's sphere of influence by <see cref="EdgeMargin"/> (#759), at <paramref name="ut"/>
        /// on <paramref name="orbit"/>: the burn is tangent to the craft's motion, so it sets the apoapsis of the path out. The least
        /// burn to a high periapsis at the planet lies at the edge itself, where the path's apoapsis meets the sphere: below it nothing
        /// leaves and every point is undefined, and above it the periapsis has an infinite slope in the burn and the time of leaving
        /// runs to half an orbit. The SQP stalls there, and a burn flown there, which ends up to 0.1 m/s out, may never leave. Bounding
        /// the prograde component keeps both away for about 1.5 m/s. The orbit's speed is that of the start's time, so on a very
        /// eccentric orbit the floor is only as good as the seed's time.
        /// </summary>
        private static double ProgradeFloor(SnapshotBody moon, Conic orbit, double ut)
        {
            if (!(moon.SoiRadius > 0)) return double.NegativeInfinity;
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            var apoapsis = moon.SoiRadius * (1 + EdgeMargin);
            return Math.Sqrt(2 * moon.Gm * apoapsis / (r.Length * (r.Length + apoapsis))) - v.Length;
        }

        /// <summary>
        /// The first finish of a best point the SQP stopped short at (#759): a search on the prograde component alone, the rest of the
        /// burn and its time held and the prograde kept over its floor. Null when the periapsis does not cross the target in reach.
        /// </summary>
        private static ReturnPlan FinishAlongPrograde(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, ReturnPlan from, double floor, double peR)
        {
            var prograde = Crossing(p => OffTarget(system, moon, planet, orbit, from.Ut, p, from.Normal, from.Radial, peR), from.Prograde, ProgradeReach, 1, floor, double.PositiveInfinity, ProgradeTolerance);
            return prograde.HasValue ? PlanAt(system, moon, planet, orbit, from.Ut, prograde.Value, from.Normal, from.Radial) : null;
        }

        /// <summary>
        /// The second finish, for a best point the floor holds: a time shift inside the window, the burn's components held.
        /// Null when the periapsis does not cross the target in reach.
        /// </summary>
        private static ReturnPlan FinishInTime(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, ReturnPlan from, double earliest, double latest, double peR)
        {
            var ut = Crossing(t => OffTarget(system, moon, planet, orbit, t, from.Prograde, from.Normal, from.Radial, peR), from.Ut, TimeReach, orbit.Period, earliest, latest, TimeTolerance);
            return ut.HasValue ? PlanAt(system, moon, planet, orbit, ut.Value, from.Prograde, from.Normal, from.Radial) : null;
        }

        /// <summary>m the burn's periapsis at the planet is over the target; NaN where it does not get there.</summary>
        private static double OffTarget(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, double ut, double prograde, double normal, double radial, double peR)
        {
            var arrival = SingleShooting.Arrive(system, moon, orbit, ut, SingleShooting.FromNodeFrame(orbit, ut, prograde, normal, radial), planet, Patches);
            return arrival == null ? double.NaN : arrival.Orbit.PeR - peR;
        }

        /// <summary>
        /// Steps out from <paramref name="start"/> either side, by each of <paramref name="reach"/> times <paramref name="unit"/> and inside the
        /// bounds, until <paramref name="off"/> changes sign, then takes Brent to within <paramref name="tolerance"/>. Null when no defined
        /// point changes it, or the crossing between has an undefined point.
        /// </summary>
        private static double? Crossing(Func<double, double> off, double start, double[] reach, double unit, double lowest, double highest, double tolerance)
        {
            var atStart = off(start);
            if (double.IsNaN(atStart)) return null;
            foreach (var step in reach)
            {
                foreach (var side in new[] { 1.0, -1.0 })
                {
                    var end = Math.Min(highest, Math.Max(lowest, start + side * step * unit));
                    if (end == start) continue;
                    var atEnd = off(end);
                    if (double.IsNaN(atEnd) || (atEnd > 0) == (atStart > 0)) continue;
                    var root = BrentRoot.Solve(off, Math.Min(start, end), Math.Max(start, end), tolerance / Math.Max(Math.Abs(start), 1), FinishIterations);
                    return root.Converged ? root.X : (double?)null;
                }
            }
            return null;
        }

        /// <summary>Why the ask can't be solved as given, or null.</summary>
        private static string Unreadable(ReturnAsk ask, double earliest, double latest)
        {
            if (!Finite(ask.TargetPe) || ask.TargetPe <= 0) return "target_pe must be metres above 0, not " + Num(ask.TargetPe);
            if (ask.TargetInc.HasValue && !(ask.TargetInc.Value >= 0 && ask.TargetInc.Value <= 180))
                return "target_inc must be from 0 to 180 degrees, not " + Num(ask.TargetInc.Value);
            if (!Finite(ask.EarliestUt) || !Finite(latest)) return "earliest_ut and latest_ut must be finite UTs";
            if (latest < earliest)
                return "the burn must come by latest_ut, UT " + Num(latest) + ", before the earliest it can be, UT " + Num(earliest);
            if (!(ask.PeTolerance > 0) || !(ask.IncTolerance > 0)) return "pe_tolerance and inc_tolerance must be above 0";
            return null;
        }

        /// <summary>
        /// The analytic seed: a prograde burn at the time in the window when an escape at the speed a Hohmann-like descent needs
        /// (from the moon's distance to <paramref name="peR"/>) has its asymptote nearest backwards along the moon's orbit, seen in
        /// the craft's plane. Returns the burn (inertial, m/s) and its time in <paramref name="ut"/>.
        /// </summary>
        private static Vec3 Seed(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, double earliest, double latest, double peR, out double ut)
        {
            var moonOrbit = system.OrbitOf(moon);
            ut = earliest;
            var best = double.PositiveInfinity;
            var bestBurn = default(Vec3);
            var samples = latest > earliest ? SeedSamples : 0;
            for (var i = 0; i <= samples; i++)
            {
                var t = samples == 0 ? earliest : earliest + (latest - earliest) * i / samples;
                Vec3 mr, mv, r, v;
                moonOrbit.State(t, out mr, out mv);
                orbit.State(t, out r, out v);
                var rm = mr.Length;
                var vApoapsis = Math.Sqrt(planet.Gm * 2 * peR / (rm * (rm + peR)));
                var vInf = Math.Max(0, mv.Length - vApoapsis);
                var e = 1 + r.Length * vInf * vInf / moon.Gm;
                var asymptote = Math.Acos(-1 / e);
                // Backwards along the moon's orbit, in the craft's plane, as an angle from the craft ahead along its motion.
                var normal = Vec3.Cross(r, v).Normalized;
                var back = mv * -1;
                back = back - normal * Vec3.Dot(back, normal);
                var angle = Math.Atan2(Vec3.Dot(Vec3.Cross(r, back), normal), Vec3.Dot(r, back));
                if (angle < 0) angle += 2 * Math.PI;
                var off = Math.Abs(angle - asymptote);
                if (off < best)
                {
                    best = off;
                    ut = t;
                    var speed = Math.Sqrt(vInf * vInf + 2 * moon.Gm / r.Length);
                    bestBurn = v.Normalized * (speed - v.Length);
                }
            }
            return bestBurn;
        }

        /// <summary>
        /// The burn straight onto the asymptote (#816): at each sample in the window, the velocity of the hyperbola from the craft's
        /// place whose asymptote leaves backwards along the moon's orbit at the speed a Hohmann-like descent to <paramref name="peR"/>
        /// needs, in the plane of the place and the asymptote; the sample whose burn to it is least. Where the prograde seed only
        /// lines the asymptote up in the craft's plane, this one holds out of it. A seed and a yardstick, not a bound: it takes the
        /// planet's pull at the moon's distance only (from a 30 km Mun orbit about 310 m/s where the least is 266; from Ike's
        /// Mission orbit 163 m/s where it is 423). Returns the burn (inertial, m/s) and its time in <paramref name="ut"/>.
        /// </summary>
        internal static Vec3 Direct(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, double earliest, double latest, double peR, out double ut)
        {
            var moonOrbit = system.OrbitOf(moon);
            ut = earliest;
            var best = double.PositiveInfinity;
            var bestBurn = default(Vec3);
            var samples = latest > earliest ? SeedSamples : 0;
            for (var i = 0; i <= samples; i++)
            {
                var t = samples == 0 ? earliest : earliest + (latest - earliest) * i / samples;
                Vec3 mr, mv, r, v;
                moonOrbit.State(t, out mr, out mv);
                orbit.State(t, out r, out v);
                var rm = mr.Length;
                var vInf = Math.Max(1e-3, mv.Length - Math.Sqrt(planet.Gm * 2 * peR / (rm * (rm + peR))));
                var back = mv.Normalized * -1;
                var radius = r.Length;
                var rHat = r * (1 / radius);
                var cosPhi = Math.Max(-1, Math.Min(1, Vec3.Dot(rHat, back)));
                var across = back - rHat * cosPhi;
                // The asymptote along the place itself, or straight back through the moon, has no plane: no hyperbola from here.
                if (across.Length < 1e-6 || cosPhi > 1 - 1e-9) continue;
                var tHat = across.Normalized;
                var sinPhi = Math.Sqrt(Math.Max(0, 1 - cosPhi * cosPhi));
                // r (1 + e cos(nu)) = a (e^2 - 1) with nu = the asymptote's anomaly less phi: a quadratic in s = sqrt(e^2 - 1).
                var a = moon.Gm / (vInf * vInf);
                var s = (radius * sinPhi + Math.Sqrt(radius * radius * sinPhi * sinPhi + 4 * a * radius * (1 - cosPhi))) / (2 * a);
                var e = Math.Sqrt(1 + s * s);
                var nu = Math.Acos(-1 / e) - Math.Acos(cosPhi);
                var h = Math.Sqrt(moon.Gm * a * s * s);
                var need = rHat * (moon.Gm / h * e * Math.Sin(nu)) + tHat * (moon.Gm / h * (1 + e * Math.Cos(nu)));
                var burn = need - v;
                if (burn.Length < best)
                {
                    best = burn.Length;
                    ut = t;
                    bestBurn = burn;
                }
            }
            return bestBurn;
        }

        /// <summary>
        /// The fallback when the seed is undefined: prograde burns over a grid of times in the window and sizes from the escape
        /// speed up; the defined point whose periapsis at the planet is nearest <paramref name="peR"/>. False when none is defined.
        /// </summary>
        private static bool Grid(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, double earliest, double latest, double peR,
            out double ut, out Vec3 burn)
        {
            ut = earliest;
            burn = default(Vec3);
            var best = double.PositiveInfinity;
            var times = latest > earliest ? GridTimes : 1;
            for (var i = 0; i < times; i++)
            {
                var t = earliest + (latest - earliest) * i / Math.Max(1, times - 1);
                Vec3 r, v;
                orbit.State(t, out r, out v);
                var escape = Math.Sqrt(2 * moon.Gm / r.Length);
                foreach (var factor in GridEscape)
                {
                    var trial = v.Normalized * (escape * factor - v.Length);
                    var arrival = SingleShooting.Arrive(system, moon, orbit, t, trial, planet, Patches);
                    if (arrival == null) continue;
                    var off = Math.Abs(arrival.Orbit.PeR - peR);
                    if (off < best)
                    {
                        best = off;
                        ut = t;
                        burn = trial;
                    }
                }
            }
            return !double.IsPositiveInfinity(best);
        }

        /// <summary>
        /// The start for an inclination target off the equator: an equatorial arrival is where cos(inc) has no slope in the
        /// out-of-plane burn (ADR 0010), so a solve from there stalls. Tilts <paramref name="seed"/> out of the craft's plane in
        /// small steps until its arrival is clear of <see cref="Sqp.EquatorialBand"/>; the seed as it was if none is.
        /// </summary>
        private static Vec3 OffTheEquator(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic orbit, double ut, Vec3 seed)
        {
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            var up = Vec3.Cross(r, v).Normalized;
            for (var tilt = 0; tilt <= SeedTilts; tilt++)
            {
                var tilted = seed + up * (seed.Length * SeedTilt * tilt);
                var arrival = SingleShooting.Arrive(system, moon, orbit, ut, tilted, planet, Patches);
                if (arrival == null) break;
                var inc = arrival.Orbit.Inclination;
                if (inc >= Sqp.EquatorialBand && inc <= 180 - Sqp.EquatorialBand) return tilted;
            }
            return seed;
        }

        private static Vec3 Normal(Patch patch)
        {
            Vec3 r, v;
            patch.Orbit.State(patch.StartUt, out r, out v);
            return Vec3.Cross(r, v);
        }

        internal static ReturnPlan Fail(string why)
        {
            return new ReturnPlan { Error = Failure + ": " + why };
        }

        private static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        /// <summary>
        /// Why the path flown is not the return asked (#759), or null: its periapsis over <paramref name="peTolerance"/> from
        /// <paramref name="targetPe"/>, or, when one was asked, its inclination over <paramref name="incTolerance"/> from
        /// <paramref name="targetInc"/>. The block's two questions: whether the correction is needed, and at the end whether it
        /// may report success.
        /// </summary>
        public static string Miss(double pe, double inc, double targetPe, double? targetInc, double peTolerance, double incTolerance)
        {
            var withInc = targetInc.HasValue;
            if (Math.Abs(pe - targetPe) <= peTolerance && (!withInc || Math.Abs(inc - targetInc.Value) <= incTolerance)) return null;
            return "the return flown arrives at periapsis " + Num(pe) + " m" + (withInc ? " and inclination " + Deg(inc) + "°" : "")
                + ", not " + Num(targetPe) + " m" + (withInc ? " and " + Deg(targetInc.Value) + "°" : "");
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
