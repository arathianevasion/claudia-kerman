// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
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
        /// <summary>The most an aimed correction burns along the track, m/s: about 130 km of the landing halfway home from the Mun, more than an ejection's error leaves (0.5 m/s: about 95 km). A fix across the track (#790) has <see cref="MostFixDv"/> and the reserve in its place.</summary>
        public const double MostLandingDv = 50;
        /// <summary>Below this the landing is too late to move along the track, km per m/s: the burn would spend its most for a few km (3,000 s from periapsis: 0.04, or 56 m/s for 2 km).</summary>
        private const double LeastKmPerMs = 0.1;
        /// <summary>The solver's own tolerance on the landing, km: under the 2 km a return counts as there.</summary>
        private const double LandingResidual = 0.2;
        /// <summary>Each iteration flies the predictor about 7 times, so the solve stops at what it has by then (an iteration's best still holds the periapsis).</summary>
        private const int LandingIterations = 8;
        /// <summary>The most times one solve flies the predictor, a line search's included: past this every point is undefined and the solve ends on its best. About 630 ms in the game at 10.5 ms each, in one call on the main thread until #463.</summary>
        private const int LandingPredictions = 60;
        /// <summary>
        /// The most a fix across the track burns, m/s (#790): a Minmus return's worst, about 60 km, costs 40. It stands instead of <see cref="MostLandingDv"/>: the delta-v left less the reserve is what
        /// limits it, and this stops an explicit target far off the track from spending all of that.
        /// </summary>
        public const double MostFixDv = 150;
        /// <summary>A miss across the track under this isn't worth a fix (#790), km: the burn's own error is of that size.</summary>
        private const double WorthFixingKm = 5;
        /// <summary>The fix's tolerance on the landing, km.</summary>
        private const double WholeResidualKm = 1;
        private const int WholeSteps = 10;
        private const int WholeHalvings = 3;
        /// <summary>The probe for the landing's slopes, and the longest of Newton's steps, m/s.</summary>
        private const double WholeProbeMs = 1;
        private const double WholeLongestStepMs = 20;
        /// <summary>The most times one fix flies the predictor: about 630 ms in the game at 10.5 ms each, in one call on the main thread until #463.</summary>
        private const int WholePredictions = 60;
        /// <summary>The fix's word when flown and when the miss is accepted (<see cref="CorrectionPlan.CrossFix"/>).</summary>
        public const string FixFlown = "flown";
        public const string MissAccepted = "accepted";

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
            var acrossKm = Math.Abs(off[0] * lever.Along[1] - off[1] * lever.Along[0]);
            string acrossNote = "";
            double acrossDv = 0;
            if (ask.DvLeft < 0 && acrossKm > WorthFixingKm) acrossNote = "; stock can't say the delta-v left";
            else if (acrossKm > WorthFixingKm)
            {
                var whole = WholeMiss(orbit, target, peR, ask, fly, arrive, plain, burn, solved.Scale.VelocityScale * Sqp.VelocityStep);
                if (whole != null)
                {
                    acrossDv = whole.DeltaV;
                    var spare = Math.Min(MostFixDv, DeorbitBurn.AutoMaxDv(ask.DvLeft, DeorbitBurn.PoweredLandingDv(landing.Craft, true, whole.Landing.LandingSpeed)));
                    if (whole.DeltaV <= spare)
                    {
                        whole.CrossFix = FixFlown;
                        whole.CrossDv = whole.DeltaV;
                        whole.AimNote = "fix flown: " + Ms(whole.DeltaV) + " m/s, " + DeorbitBurn.Km(plain.ErrorKm) + " km down to " + DeorbitBurn.Km(whole.ErrorKm) + " km";
                        return whole;
                    }
                    acrossNote = "; the fix costs " + Ms(whole.DeltaV) + " m/s, over the " + Ms(Math.Max(0, spare)) + " m/s it may spend above the reserve";
                }
                else acrossNote = "; no burn found to take it out";
            }
            Func<CorrectionPlan, CorrectionPlan> reported = along =>
            {
                if (acrossNote == "") return along;
                along.CrossFix = MissAccepted;
                along.CrossDv = acrossDv;
                along.AimNote = "miss accepted: " + DeorbitBurn.Km(along.ErrorKm) + " km from the target" + acrossNote;
                return along;
            };
            var most = ask.DvLeft >= 0
                ? Math.Min(MostLandingDv, DeorbitBurn.AutoMaxDv(ask.DvLeft, DeorbitBurn.PoweredLandingDv(landing.Craft, true, first.LandingSpeed)))
                : MostLandingDv;
            var reach = lever.KmPerMs * Math.Max(0, most - plain.DeltaV);
            var moved = Math.Max(-reach, Math.Min(reach, miss));
            if (Math.Abs(moved) <= LandingResidual) return reported(plain);
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
            if (result.Status == SqpStatus.Invalid) return reported(plain);

            var ut = free ? result.X[3] : fixedUt;
            var aimed = new Vec3(result.X[0], result.X[1], result.X[2]);
            var reached = arrive(ut, aimed);
            var down = fly(reached, aimed);
            if (down == null || down.Error != null) return reported(plain);
            var pe = reached.Orbit.PeR - target.Radius;
            var km = landing.ErrorKm(target, down);
            // The periapsis first, and never more than the craft has for the landing's sake.
            if (Math.Abs(pe - ask.TargetPe) > ask.PeTolerance || !(km < plain.ErrorKm) || (ask.DvLeft >= 0 && aimed.Length > ask.DvLeft)) return reported(plain);

            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, ut, aimed, out prograde, out normal, out radial);
            return reported(new CorrectionPlan
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
            });
        }

        /// <summary>
        /// The burn that takes out the landing's whole miss with target_pe held (#790): what an inclined moon's path needs. Newton on two parts of the burn, one normal to the path, which tilts
        /// the plane and moves the landing across the track (Minmus, halfway home: about 1.5 km per m/s), and one in the plane that leaves the periapsis alone, which moves it along; the third
        /// part of each try is solved for the periapsis. Probes of whole m/s ride over the predictor's fine grain, where an SQP on the same equalities creeps. Null when it doesn't get there.
        /// </summary>
        private static CorrectionPlan WholeMiss(Conic orbit, SnapshotBody target, double peR, CorrectionAsk ask, Func<Patch, Vec3, ReentryPrediction> fly, Func<double, Vec3, Patch> arrive, CorrectionPlan plain, Vec3 burn, double step)
        {
            var landing = ask.Landing;
            var ut = plain.Ut;
            var peDirection = PeSlope(arrive, ut, burn, step);
            if (peDirection == null) return null;
            var toPe = peDirection.Value.Normalized;
            var across = orbit.W.Normalized;
            var along = Vec3.Cross(across, toPe).Normalized;
            var flights = 0;
            var pePart = 0.0;
            Func<double, double, Trial> tryAt = (normal, inPlane) =>
            {
                if (++flights > WholePredictions) return null;
                var aside = burn + across * normal + along * inPlane;
                var part = Crossing(p => OffPe(arrive, ut, aside + toPe * p, peR), pePart);
                if (!part.HasValue) return null;
                pePart = part.Value;
                var dv = aside + toPe * pePart;
                var arrival = arrive(ut, dv);
                var landed = fly(arrival, dv);
                if (landed == null || landed.Error != null) return null;
                return new Trial { Normal = normal, InPlane = inPlane, Burn = dv, Arrival = arrival, Landed = landed, Off = landing.OffKm(target, landed), Km = landing.ErrorKm(target, landed) };
            };
            var here = tryAt(0, 0);
            for (var i = 0; here != null && i < WholeSteps && here.Km > WholeResidualKm; i++)
            {
                var byNormal = tryAt(here.Normal + WholeProbeMs, here.InPlane);
                var byPlane = tryAt(here.Normal, here.InPlane + WholeProbeMs);
                if (byNormal == null || byPlane == null) break;
                double en = (byNormal.Off[0] - here.Off[0]) / WholeProbeMs, nn = (byNormal.Off[1] - here.Off[1]) / WholeProbeMs;
                double ea = (byPlane.Off[0] - here.Off[0]) / WholeProbeMs, na = (byPlane.Off[1] - here.Off[1]) / WholeProbeMs;
                var det = en * na - ea * nn;
                if (Math.Abs(det) < 1e-12) break;
                var moveN = (-here.Off[0] * na + ea * here.Off[1]) / det;
                var moveA = (-en * here.Off[1] + nn * here.Off[0]) / det;
                var share = Math.Min(1, WholeLongestStepMs / Math.Sqrt(moveN * moveN + moveA * moveA));
                Trial better = null;
                for (var halving = 0; halving <= WholeHalvings && better == null; halving++, share /= 2)
                {
                    var tried = tryAt(here.Normal + share * moveN, here.InPlane + share * moveA);
                    if (tried != null && tried.Km < here.Km) better = tried;
                }
                if (better == null) break;
                here = better;
            }
            if (here == null) return null;
            var pe = here.Arrival.Orbit.PeR - target.Radius;
            if (Math.Abs(pe - ask.TargetPe) > ask.PeTolerance || here.Km > plain.ErrorKm - WorthFixingKm) return null;
            double prograde, normalPart, radial;
            SingleShooting.NodeFrame(orbit, ut, here.Burn, out prograde, out normalPart, out radial);
            return new CorrectionPlan
            {
                HasBurn = true,
                Ut = ut,
                Prograde = prograde,
                Normal = normalPart,
                Radial = radial,
                DeltaV = here.Burn.Length,
                PePredicted = pe,
                IncPredicted = here.Arrival.Orbit.Inclination,
                Landing = here.Landed,
                ErrorKm = here.Km,
                Iterations = plain.Iterations,
                Evaluations = plain.Evaluations + flights,
            };
        }

        /// <summary>One try of <see cref="WholeMiss"/>.</summary>
        private sealed class Trial
        {
            public double Normal;
            public double InPlane;
            public Vec3 Burn;
            public Patch Arrival;
            public ReentryPrediction Landed;
            public double[] Off;
            public double Km;
        }

        /// <summary>How the periapsis changes with the burn, m per m/s, by central differences; null when a point around it doesn't reach the planet.</summary>
        private static Vec3? PeSlope(Func<double, Vec3, Patch> arrive, double ut, Vec3 burn, double step)
        {
            var slope = new double[3];
            for (var i = 0; i < 3; i++)
            {
                var axis = new Vec3(i == 0 ? step : 0, i == 1 ? step : 0, i == 2 ? step : 0);
                var high = arrive(ut, burn + axis);
                var low = arrive(ut, burn - axis);
                if (high == null || low == null) return null;
                slope[i] = (high.Orbit.PeR - low.Orbit.PeR) / (2 * step);
            }
            var found = new Vec3(slope[0], slope[1], slope[2]);
            return found.Length > 0 ? found : (Vec3?)null;
        }

        private static double OffPe(Func<double, Vec3, Patch> arrive, double ut, Vec3 burn, double peR)
        {
            var arrival = arrive(ut, burn);
            return arrival == null ? double.NaN : arrival.Orbit.PeR - peR;
        }

        /// <summary>The part nearest <paramref name="start"/> where <paramref name="off"/> crosses zero; null when none is in reach.</summary>
        private static double? Crossing(Func<double, double> off, double start)
        {
            var atStart = off(start);
            if (double.IsNaN(atStart)) return null;
            if (Math.Abs(atStart) <= PeResidual) return start;
            for (var reach = 0.25; reach <= 64; reach *= 2)
                foreach (var side in new[] { 1.0, -1.0 })
                {
                    var end = start + side * reach;
                    var atEnd = off(end);
                    if (double.IsNaN(atEnd) || (atEnd > 0) == (atStart > 0)) continue;
                    var root = BrentRoot.Solve(off, Math.Min(start, end), Math.Max(start, end), 1e-5, 40);
                    return root.Converged ? root.X : (double?)null;
                }
            return null;
        }

        private static string Ms(double dv)
        {
            return Math.Round(dv, 1).ToString("0.#", CultureInfo.InvariantCulture);
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
