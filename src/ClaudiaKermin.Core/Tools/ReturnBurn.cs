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
    /// <summary>What <c>return</c> asks of its ejection: the periapsis at the moon's planet, optionally the arrival inclination there, and the burn's window.</summary>
    internal sealed class ReturnAsk
    {
        /// <summary>The periapsis altitude at the planet, m above its sea level.</summary>
        public double TargetPe;
        /// <summary>The arrival inclination round the planet, degrees from 0 to 180, or null for any.</summary>
        public double? TargetInc;
        /// <summary>The soonest UT the burn may be: the block's start plus the turn time, or the window's UT if later; one already past is now.</summary>
        public double EarliestUt;
        /// <summary>The latest UT the burn may be, or null for one orbit of the craft after <see cref="EarliestUt"/>, so the burn point comes round once.</summary>
        public double? LatestUt;
        /// <summary>How far the answer may be from target_pe (m) and target_inc (degrees) before the solve is an error.</summary>
        public double PeTolerance = 1000;
        public double IncTolerance = 0.5;
        /// <summary>Start from the coarse grid, as when the analytic seed is undefined: for tests of that worst case.</summary>
        internal bool FromGrid;
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
        public int Iterations;
        public int Evaluations;
    }

    /// <summary>
    /// <c>return</c>'s ejection from any moon to its planet (#450, ADR 0010): the smallest burn in a window that leaves the moon
    /// on a path whose periapsis at the planet is target_pe and, when asked, whose inclination there is target_inc. After the
    /// formulation of <c>MechJebLib/Maneuvers/ReturnFromMoon.cs</c> (MechJeb2 a958383), posed as single shooting through
    /// <see cref="BodySystem.Follow"/> instead of its multiple shooting: the variables are the burn's three inertial components and
    /// its time, solved by <see cref="Sqp"/>. It starts from an analytic seed, the prograde burn whose escape asymptote leaves
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
        private const int MaxIterations = 50;
        /// <summary>Out of the moon, round the planet: a last patch round the star no longer throws (#471).</summary>
        private const int Patches = 2;
        /// <summary>The seed's search for the burn point: samples over the window.</summary>
        private const int SeedSamples = 360;
        /// <summary>The fallback grid: burn times over the window, and prograde burns from the escape speed up by these factors of it.</summary>
        private const int GridTimes = 24;
        private static readonly double[] GridEscape = { 1.0, 1.05, 1.1, 1.2, 1.35, 1.5 };
        /// <summary>The out-of-plane share of the seed tried, in steps of this, to move an inclination target's start off the equator.</summary>
        private const double SeedTilt = 0.02;
        private const int SeedTilts = 5;

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
            var free = latest > earliest;
            var withInc = ask.TargetInc.HasValue;
            var m = 1 + (withInc ? Sqp.InclinationConstraints(ask.TargetInc.Value) : 0);
            var tolerances = new double[m];
            tolerances[0] = PeResidual;
            for (var k = 1; k < m; k++) tolerances[k] = IncResidual;

            var fromGrid = false;
            double startUt;
            var seed = Seed(system, moon, planet, orbit, earliest, latest, peR, out startUt);
            if (ask.FromGrid || SingleShooting.Arrive(system, moon, orbit, startUt, seed, planet, Patches) == null)
            {
                fromGrid = true;
                if (!Grid(system, moon, planet, orbit, earliest, latest, peR, out startUt, out seed))
                    return Fail("no prograde burn in the window leaves " + moon.Name + " for " + planet.Name);
            }
            if (withInc && Sqp.InclinationConstraints(ask.TargetInc.Value) == 1) seed = OffTheEquator(system, moon, planet, orbit, startUt, seed);

            var n = free ? 4 : 3;
            var problem = new SqpProblem
            {
                Scale = Scale.Create(moon.Gm, moon.Radius),
                Variables = free
                    ? new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Time }
                    : new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity },
                Start = free ? new[] { seed.X, seed.Y, seed.Z, startUt } : new[] { seed.X, seed.Y, seed.Z },
                Lower = free ? new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, earliest } : null,
                Upper = free ? new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, latest } : null,
                Objective = x => x[0] * x[0] + x[1] * x[1] + x[2] * x[2],
                ObjectiveHessian = free
                    ? new double[,] { { 2, 0, 0, 0 }, { 0, 2, 0, 0 }, { 0, 0, 2, 0 }, { 0, 0, 0, 0 } }
                    : new double[,] { { 2, 0, 0 }, { 0, 2, 0 }, { 0, 0, 2 } },
                Constraints = m,
                Evaluate = (x, c) =>
                {
                    var arrival = SingleShooting.Arrive(system, moon, orbit, n == 4 ? x[3] : startUt, new Vec3(x[0], x[1], x[2]), planet, Patches);
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
            var reached = SingleShooting.Arrive(system, moon, orbit, ut, burn, planet, Patches);
            if (reached == null) return Fail("the solver's best burn does not leave " + moon.Name + " for " + planet.Name);
            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, ut, burn, out prograde, out normal, out radial);
            var plan = new ReturnPlan
            {
                HasBurn = true,
                Planet = planet.Name,
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = burn.Length,
                PePredicted = reached.Orbit.PeR - planet.Radius,
                IncPredicted = reached.Orbit.Inclination,
                SoiUt = reached.StartUt,
                FromGrid = fromGrid,
                Iterations = result.Iterations,
                Evaluations = result.Evaluations,
            };
            plan.Error = SingleShooting.ToleranceError(Failure, plan.DeltaV, plan.PePredicted, plan.IncPredicted, ask.TargetPe, ask.TargetInc,
                ask.PeTolerance, ask.IncTolerance, result.Reason ?? result.Status.ToString());
            return plan;
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

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
