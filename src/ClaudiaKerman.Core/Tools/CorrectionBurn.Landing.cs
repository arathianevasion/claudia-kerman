// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// A Course Correction aimed at a landing (#793, spike #482): the landing's miss along the way it can move is one more equality beside
    /// target_pe, with the reentry predictor in the loop. Holding the periapsis keeps the entry steep and its authority honest (about 2.6 km per
    /// m/s halfway home from the Mun, against 56 km per m/s through the periapsis, on a slope where the burn's own error costs the most). What
    /// the burn can't reach for <see cref="MostLandingDv"/> stays in <see cref="CorrectionPlan.ErrorKm"/>; that is no failure.
    /// </summary>
    internal static partial class CorrectionBurn
    {
        /// <summary>The most an aimed correction burns, m/s: about 130 km of the landing halfway home from the Mun, more than an ejection's error leaves (0.5 m/s: about 95 km).</summary>
        public const double MostLandingDv = 50;
        /// <summary>The solver's own tolerance on the landing, km: under the 2 km a return counts as there.</summary>
        private const double LandingResidual = 0.2;
        /// <summary>Each iteration flies the predictor about 7 times, so the solve stops at what it has by then (an iteration's best still holds the periapsis).</summary>
        private const int LandingIterations = 8;

        /// <summary>The landing's way of moving, from one burn: <see cref="Along"/> east and north, and <see cref="KmPerMs"/> km it moves per m/s with the periapsis held.</summary>
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
            Func<double, Vec3, ReentryPrediction> predict = (when, dv) =>
            {
                var arrival = arrive(when, dv);
                return arrival == null ? null : ReentryPredictor.Predict(target, arrival.Orbit, arrival.StartUt, landing.GroundAltitude, landing.MassAfter(dv.Length), landing.Air, landing.Craft, landing.Frame);
            };
            var first = predict(plain.Ut, burn);
            if (first == null || first.Error != null) return plain;
            plain.Landing = first;
            plain.ErrorKm = landing.ErrorKm(target, first);

            var lever = Lever(target, landing, predict, arrive, plain.Ut, burn, solved.Scale.VelocityScale * Sqp.VelocityStep);
            if (lever == null) return plain;
            var off = landing.OffKm(target, first);
            var miss = off[0] * lever.Along[0] + off[1] * lever.Along[1];
            var most = ask.DvLeft > 0 ? Math.Min(MostLandingDv, ask.DvLeft) : MostLandingDv;
            var reach = lever.KmPerMs * Math.Max(0, most - plain.DeltaV);
            var moved = Math.Max(-reach, Math.Min(reach, miss));
            if (Math.Abs(moved) <= LandingResidual) return plain;
            var goal = miss - moved;

            var free = solved.Variables.Length == 4;
            var fixedUt = plain.Ut;
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
                    var when = free ? x[3] : fixedUt;
                    var dv = new Vec3(x[0], x[1], x[2]);
                    var arrival = arrive(when, dv);
                    if (arrival == null) return false;
                    var landed = predict(when, dv);
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
            var down = predict(ut, aimed);
            if (reached == null || down == null || down.Error != null) return plain;
            var pe = reached.Orbit.PeR - target.Radius;
            var km = landing.ErrorKm(target, down);
            if (Math.Abs(pe - ask.TargetPe) > ask.PeTolerance || !(km < plain.ErrorKm)) return plain;

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
        /// How the landing moves with the periapsis held, by central differences at the SQP's own step: the landing's slopes in the burn's three components with
        /// the periapsis's slope projected out, and their principal direction. Halfway home that is mostly the time of arrival against the ground turning, so
        /// nearly east whatever the track's heading; near the planet, the path's own line. Null when a point around the burn has no landing.
        /// </summary>
        private static LandingLever Lever(SnapshotBody target, ReturnLanding landing, Func<double, Vec3, ReentryPrediction> predict, Func<double, Vec3, Patch> arrive, double ut, Vec3 burn, double step)
        {
            var east = new double[3];
            var north = new double[3];
            var pe = new double[3];
            for (var i = 0; i < 3; i++)
            {
                var along = new Vec3(i == 0 ? step : 0, i == 1 ? step : 0, i == 2 ? step : 0);
                var above = predict(ut, burn + along);
                var below = predict(ut, burn - along);
                if (above == null || below == null || above.Error != null || below.Error != null) return null;
                var high = landing.OffKm(target, above);
                var low = landing.OffKm(target, below);
                east[i] = (high[0] - low[0]) / (2 * step);
                north[i] = (high[1] - low[1]) / (2 * step);
                pe[i] = (arrive(ut, burn + along).Orbit.PeR - arrive(ut, burn - along).Orbit.PeR) / (2 * step);
            }
            var peSize = Dot3(pe, pe);
            if (!(peSize > 0)) return null;
            var ee = Dot3(east, east) - Dot3(east, pe) * Dot3(east, pe) / peSize;
            var nn = Dot3(north, north) - Dot3(north, pe) * Dot3(north, pe) / peSize;
            var en = Dot3(east, north) - Dot3(east, pe) * Dot3(north, pe) / peSize;
            var angle = Math.Atan2(2 * en, ee - nn) / 2;
            var most = (ee + nn) / 2 + Math.Sqrt((ee - nn) * (ee - nn) / 4 + en * en);
            if (!(most > 0)) return null;
            return new LandingLever { Along = new[] { Math.Cos(angle), Math.Sin(angle) }, KmPerMs = Math.Sqrt(most) };
        }

        private static double Dot3(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }
    }
}
