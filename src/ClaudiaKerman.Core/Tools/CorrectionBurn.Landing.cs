// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// A Course Correction aimed at a landing (#793, spike #482): the landing's miss along the track is one more equality beside
    /// target_pe, with the reentry predictor in the loop. Holding the periapsis keeps the entry steep and its authority honest (about 2.6 km per
    /// m/s halfway home from the Mun, against 56 km per m/s through the periapsis, on a slope where the burn's own error costs the most). What
    /// the burn can't reach for <see cref="MostLandingDv"/> stays in <see cref="CorrectionPlan.ErrorKm"/>; that is no failure.
    /// </summary>
    internal static partial class CorrectionBurn
    {
        /// <summary>The most an aimed correction burns, m/s: about 130 km of the landing halfway home from the Mun, more than an ejection's error leaves (0.5 m/s: about 95 km).</summary>
        public const double MostLandingDv = 50;
        /// <summary>The share of the delta-v left it plans for, as the Craft Check's 15% margin: the landing doesn't move quite in line with the burn, so the burn can come out a little over.</summary>
        private const double DvLeftShare = 0.85;
        /// <summary>Below this the landing is too late to move along the track, km per m/s: the burn would spend its most for a few km (3,000 s from periapsis: 0.04, or 56 m/s for 2 km).</summary>
        private const double LeastKmPerMs = 0.1;
        /// <summary>The solver's own tolerance on the landing, km: under the 2 km a return counts as there.</summary>
        private const double LandingResidual = 0.2;
        /// <summary>Each iteration flies the predictor about 7 times, so the solve stops at what it has by then (an iteration's best still holds the periapsis).</summary>
        private const int LandingIterations = 8;
        /// <summary>The most times one solve flies the predictor, a line search's included: past this every point is undefined and the solve ends on its best. About 630 ms in the game at 10.5 ms each, in one call on the main thread until #463.</summary>
        private const int LandingPredictions = 60;

        /// <summary>The landing's way along the track, from one burn: <see cref="Along"/> east and north, and <see cref="KmPerMs"/> km it moves per m/s with the periapsis held.</summary>
        private sealed class LandingLever
        {
            public double[] Along;
            public double KmPerMs;
        }

        /// <summary>
        /// <paramref name="plain"/>, the burn at <paramref name="ut"/> that only holds target_pe, re-solved to move the landing toward the target. With no
        /// landing to read, or nothing it can reach, <paramref name="plain"/> stands: the periapsis comes before the landing.
        /// </summary>
        private static CorrectionPlan AimLanding(Conic orbit, SnapshotBody target, double peR, CorrectionAsk ask, SqpProblem solved, Func<double, Vec3, Patch> arrive, CorrectionPlan plain, Vec3 burn)
        {
            var landing = ask.Landing;
            Func<Patch, Vec3, ReentryPrediction> fly = (arrival, dv) =>
                arrival == null ? null : ReentryPredictor.Predict(target, arrival.Orbit, arrival.StartUt, landing.GroundAltitude, landing.MassAfter(dv.Length), landing.Air, landing.Craft, landing.Frame);
            Func<double, Vec3, ReentryPrediction> predict = (when, dv) => fly(arrive(when, dv), dv);
            var first = predict(plain.Ut, burn);
            if (first == null || first.Error != null) return plain;
            plain.Landing = first;
            plain.ErrorKm = landing.ErrorKm(target, first);

            var lever = Lever(target, landing, predict, arrive, orbit, plain.Ut, burn, solved.Scale.VelocityScale * Sqp.VelocityStep);
            if (lever == null || lever.KmPerMs < LeastKmPerMs) return plain;
            var off = landing.OffKm(target, first);
            var miss = off[0] * lever.Along[0] + off[1] * lever.Along[1];
            var most = ask.DvLeft >= 0 ? Math.Min(MostLandingDv, ask.DvLeft * DvLeftShare) : MostLandingDv;
            var reach = lever.KmPerMs * Math.Max(0, most - plain.DeltaV);
            var moved = Math.Max(-reach, Math.Min(reach, miss));
            if (Math.Abs(moved) <= LandingResidual) return plain;
            var goal = miss - moved;

            var free = solved.Variables.Length == 4;
            var fixedUt = plain.Ut;
            var flights = 0;
            var result = Sqp.Solve(new SqpProblem
            {
                Scale = solved.Scale,
                Variables = solved.Variables,
                Lower = solved.Lower,
                Upper = solved.Upper,
                Objective = solved.Objective,
                ObjectiveHessian = solved.ObjectiveHessian,
                Start = free ? new[] { burn.X, burn.Y, burn.Z, plain.Ut } : new[] { burn.X, burn.Y, burn.Z },
                Constraints = 2,
                Tolerances = new[] { PeResidual, LandingResidual },
                MaxIterations = LandingIterations,
                Evaluate = (x, c) =>
                {
                    if (++flights > LandingPredictions) return false;
                    var dv = new Vec3(x[0], x[1], x[2]);
                    var arrival = arrive(free ? x[3] : fixedUt, dv);
                    var landed = fly(arrival, dv);
                    if (landed == null || landed.Error != null) return false;
                    var at = landing.OffKm(target, landed);
                    c[0] = arrival.Orbit.PeR - peR;
                    c[1] = at[0] * lever.Along[0] + at[1] * lever.Along[1] - goal;
                    return true;
                },
            });
            if (result.Status == SqpStatus.Invalid) return plain;

            var ut = free ? result.X[3] : fixedUt;
            var aimed = new Vec3(result.X[0], result.X[1], result.X[2]);
            var reached = arrive(ut, aimed);
            var down = fly(reached, aimed);
            if (down == null || down.Error != null) return plain;
            var pe = reached.Orbit.PeR - target.Radius;
            var km = landing.ErrorKm(target, down);
            // The periapsis first, and never more than the craft has for the landing's sake.
            if (Math.Abs(pe - ask.TargetPe) > ask.PeTolerance || !(km < plain.ErrorKm) || (ask.DvLeft >= 0 && aimed.Length > ask.DvLeft)) return plain;

            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, ut, aimed, out prograde, out normal, out radial);
            return new CorrectionPlan
            {
                HasBurn = true,
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = aimed.Length,
                PePredicted = pe,
                IncPredicted = reached.Orbit.Inclination,
                Landing = down,
                ErrorKm = km,
                Iterations = plain.Iterations + result.Iterations,
                Evaluations = plain.Evaluations + result.Evaluations,
            };
        }

        /// <summary>
        /// How the landing moves along the track with the periapsis held, by central differences at the SQP's own step: the one burn direction in the path's plane
        /// that leaves the periapsis alone, and where it takes the landing. Not the way the landing moves furthest: near the planet that is across the track, by
        /// tilting the plane. Halfway home it is mostly the time of arrival against the ground turning, so about east. Null when a point around the burn has no landing.
        /// </summary>
        private static LandingLever Lever(SnapshotBody target, ReturnLanding landing, Func<double, Vec3, ReentryPrediction> predict, Func<double, Vec3, Patch> arrive, Conic orbit, double ut, Vec3 burn, double step)
        {
            Func<Vec3, double?> peR = dv =>
            {
                var arrival = arrive(ut, dv);
                return arrival == null ? (double?)null : arrival.Orbit.PeR;
            };
            var slope = new double[3];
            for (var i = 0; i < 3; i++)
            {
                var axis = new Vec3(i == 0 ? step : 0, i == 1 ? step : 0, i == 2 ? step : 0);
                var high = peR(burn + axis);
                var low = peR(burn - axis);
                if (!high.HasValue || !low.HasValue) return null;
                slope[i] = (high.Value - low.Value) / (2 * step);
            }
            var inPlane = Vec3.Cross(orbit.W, new Vec3(slope[0], slope[1], slope[2]));
            if (!(inPlane.Length > 0)) return null;
            var move = inPlane.Normalized * step;
            var ahead = predict(ut, burn + move);
            var behind = predict(ut, burn - move);
            if (ahead == null || behind == null || ahead.Error != null || behind.Error != null) return null;
            var to = landing.OffKm(target, ahead);
            var from = landing.OffKm(target, behind);
            double east = (to[0] - from[0]) / (2 * step), north = (to[1] - from[1]) / (2 * step);
            var size = Math.Sqrt(east * east + north * north);
            if (!(size > 0)) return null;
            return new LandingLever { Along = new[] { east / size, north / size }, KmPerMs = size };
        }
    }
}
