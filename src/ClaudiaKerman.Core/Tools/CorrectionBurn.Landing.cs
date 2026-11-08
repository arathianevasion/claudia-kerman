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
        /// <summary>The most an aimed correction burns along the track when stock can't say the delta-v left, m/s: about 130 km of the landing halfway home from the Mun. Known, the delta-v left less the reserve (#790, #817) limits it, up to <see cref="MostFixDv"/>.</summary>
        public const double MostLandingDv = 50;
        /// <summary>
        /// The periapsis corridor at the home planet (#817), m either way of target_pe: 25 to 35 km for return's 30 km. The landing moves about 8 km per km of periapsis there (5 at 23 km,
        /// 11 at 35 km on the capsule model), for about 56 km of periapsis per m/s halfway home, so the corridor alone moves it about 40 km either way for a fraction of a m/s.
        /// Its deep end rests on <see cref="MostEntryG"/>, by the predictor flying the craft reentry flies (#814); the predictor has no heat model, so heat rests on return's
        /// own entries: 30 km from the Mun (about 3.2 km/s at the air) is what it has flown, and 25 km is short of deorbit's 21 km (<see cref="DeorbitBurn.EntryPeriapsisShare"/>), slower but as deep.
        /// </summary>
        public const double PeCorridor = 5000;
        /// <summary>The corridor's deep end at the home planet whatever target_pe, m (#817): the 25 km checked against <see cref="MostEntryG"/>; a target_pe under it holds the periapsis.</summary>
        public const double CorridorFloor = 25000;
        /// <summary>#805: a plain burn that missed pe_tolerance still flies when its periapsis is within this many pe_tolerances of target_pe (or in the corridor).</summary>
        private const double PlainSlack = 2;
        /// <summary>How far over what it may spend the aimed burn may come before it is brought back to it, m/s: exec_node's own residual.</summary>
        private const double SpendSlackMs = 0.5;
        /// <summary>The most g a corridor periapsis's entry may pull by the predictor (#817): the capsule model's Mun return pulls about 4.0 at 25 km, 3.4 at 30 and 2.7 at 35; past this the corridor narrows toward target_pe.</summary>
        public const double MostEntryG = 8;
        /// <summary>How far an end of the corridor moves back toward target_pe when its entry pulls too hard, m.</summary>
        private const double CorridorStepM = 1000;
        /// <summary>The corridor's search: regula falsi steps on a side that crosses, the step under which it stops, m, and the most predictions in all (about 150 ms in the game at 10.5 ms each, before the held-periapsis solve).</summary>
        private const int CorridorSecants = 4;
        private const double CorridorRootM = 50;
        private const int CorridorPredictions = 14;
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
        /// <paramref name="plain"/>, the burn at its time that only holds target_pe, re-solved to move the landing toward the target (#817): at the home planet first by moving
        /// the periapsis within <see cref="PeCorridor"/> of target_pe, then with the periapsis held, spending up to the delta-v left less the reserve. With no landing to read, or
        /// nothing it can reach, the best burn found so far stands, and <see cref="CorrectionPlan.AimNote"/> says why, with the miss and the delta-v left.
        /// </summary>
        private static CorrectionPlan AimLanding(Conic orbit, SnapshotBody target, double peR, CorrectionAsk ask, SqpProblem solved, Func<double, Vec3, Patch> arrive, CorrectionPlan plain, Vec3 burn)
        {
            var landing = ask.Landing;
            Func<Patch, Vec3, ReentryPrediction> fly = (arrival, dv) => PredictOn(target, landing, arrival, dv.Length);
            Func<double, Vec3, ReentryPrediction> predict = (when, dv) => fly(arrive(when, dv), dv);
            var first = predict(plain.Ut, burn);
            if (first == null || first.Error != null) return Kept(plain, plain, ask, "no landing to read" + (first == null ? "" : " (" + first.Error + ")"));
            plain.Landing = first;
            plain.ErrorKm = landing.ErrorKm(target, first);

            var step = solved.Scale.VelocityScale * Sqp.VelocityStep;
            var lever = Lever(target, landing, predict, arrive, orbit, plain.Ut, burn, step);
            if (lever == null) return Kept(plain, plain, ask, "found no way to move the landing along the track");
            // A correction stops here. A late trim's corridor (#892) still moves the landing for a fraction of a m/s; only spending along the track and the fix across it are out.
            var tooLate = lever.KmPerMs < LeastKmPerMs;
            if (tooLate && !ask.CorridorPe.HasValue) return Kept(plain, plain, ask, "too late to move the landing (" + Ms(lever.KmPerMs * 1000) + " m per m/s)");
            var off = landing.OffKm(target, first);
            var acrossKm = tooLate ? 0 : Math.Abs(off[0] * lever.Along[1] - off[1] * lever.Along[0]);
            string acrossNote = "";
            double acrossDv = 0;
            if (ask.DvLeft < 0 && acrossKm > WorthFixingKm) acrossNote = "; stock can't say the delta-v left";
            else if (acrossKm > WorthFixingKm)
            {
                var whole = WholeMiss(orbit, target, peR, ask, fly, arrive, plain, burn, step);
                if (whole != null)
                {
                    acrossDv = whole.DeltaV;
                    var spare = Math.Min(MostFixDv, DeorbitBurn.SpareDv(ask.DvLeft, DeorbitBurn.PoweredLandingDv(landing.Craft, true, whole.Landing.LandingSpeed), ask.LaterLegsDv, landing.Payer));
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
            Func<CorrectionPlan, string, CorrectionPlan> reported = (kept, why) =>
            {
                if (why != null) Kept(kept, plain, ask, why);
                if (acrossNote == "") return kept;
                kept.CrossFix = MissAccepted;
                kept.CrossDv = acrossDv;
                kept.AimNote = (kept.AimNote == "" ? "" : kept.AimNote + "; ") + "miss accepted: " + DeorbitBurn.Km(kept.ErrorKm) + " km from the target" + acrossNote;
                return kept;
            };
            var most = ask.MostLandingDv ?? (ask.DvLeft >= 0
                ? Math.Min(MostFixDv, DeorbitBurn.SpareDv(ask.DvLeft, DeorbitBurn.PoweredLandingDv(landing.Craft, true, first.LandingSpeed), ask.LaterLegsDv, landing.Payer))
                : MostLandingDv);
            Func<ReentryPrediction, double> along = landed =>
            {
                var at = landing.OffKm(target, landed);
                return at[0] * lever.Along[0] + at[1] * lever.Along[1];
            };

            // The corridor first (#817): the periapsis may move PeCorridor either way of target_pe, which moves the landing about 8 km per km for a fraction of a m/s.
            var held = new CorridorPoint { Pe = ask.TargetPe, Burn = burn, Arrival = arrive(plain.Ut, burn), Landed = first, Along = along(first), Km = plain.ErrorKm };
            var from = held;
            var aimed = plain;
            string corridorWhy = null;
            double low, high;
            CorridorOf(target, ask, out low, out high);
            if (high > low && Math.Abs(held.Along) > LandingResidual)
            {
                var moved = Corridor(target, ask, low, high, arrive, fly, along, landing, plain.Ut, step, held);
                // #915: a corridor set by the ask (transfer_to's) spends only the spare, so with none the target_pe-only burn stands.
                if (moved != null && moved.Burn.Length > (ask.CorridorHalf.HasValue ? most : Math.Max(plain.DeltaV, most)))
                    corridorWhy = "the periapsis corridor costs " + Ms(moved.Burn.Length) + " m/s, over the " + Ms(Math.Max(0, most)) + " m/s above the reserve";
                else if (moved != null)
                {
                    from = moved;
                    aimed = PlanAt(orbit, target, plain, moved);
                }
            }
            var peAimed = target.Radius + from.Pe;
            var miss = from.Along;
            var reach = lever.KmPerMs * Math.Max(0, most - aimed.DeltaV);
            var goal = Math.Max(-reach, Math.Min(reach, miss));
            if (Math.Abs(miss) <= LandingResidual) return reported(aimed, null);
            if (tooLate) return reported(aimed, corridorWhy ?? "too late to move the landing (" + Ms(lever.KmPerMs * 1000) + " m per m/s)");
            if (Math.Abs(goal) <= LandingResidual) return reported(aimed, corridorWhy ?? "nothing above the reserve to move the landing with the periapsis held");
            var rest = miss - goal;

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
                Start = free ? new[] { from.Burn.X, from.Burn.Y, from.Burn.Z, plain.Ut } : new[] { from.Burn.X, from.Burn.Y, from.Burn.Z },
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
                    c[0] = arrival.Orbit.PeR - peAimed;
                    c[1] = along(landed) - rest;
                    return true;
                },
            });
            if (result.Status == SqpStatus.Invalid) return reported(aimed, "the aimed solve failed (" + result.Reason + ")");

            var ut = free ? result.X[3] : fixedUt;
            var burnAimed = new Vec3(result.X[0], result.X[1], result.X[2]);
            // Never more than it may spend (#817): the SQP spends by the lever's slope, so its answer can come a few percent over. Over by more than SpendSlackMs, it is brought back along
            // its change from the burn it started from, which is about square to the periapsis's slope, to what it may spend; the checks below fly it again.
            // #915: a corridor set by the ask spends only the spare, with no slack but the rounding of Toward's answer.
            var spendable = ask.CorridorHalf.HasValue ? most : Math.Max(most, plain.DeltaV);
            var slack = ask.CorridorHalf.HasValue ? 0.001 : SpendSlackMs;
            if (burnAimed.Length > spendable + slack) burnAimed = Toward(from.Burn, burnAimed, spendable);
            var reached = arrive(ut, burnAimed);
            var down = fly(reached, burnAimed);
            if (down == null || down.Error != null) return reported(aimed, "the aimed burn has no landing to read");
            var pe = reached.Orbit.PeR - target.Radius;
            var km = landing.ErrorKm(target, down);
            // The periapsis first, and never more than the craft has for the landing's sake. #915: a corridor set by the ask keeps the flown-error half of pe_tolerance free.
            if (Math.Abs(pe - from.Pe) > ask.PeTolerance || (ask.CorridorHalf.HasValue && !LeavesFlownMargin(pe, ask.TargetPe, ask.PeTolerance)))
                return reported(aimed, "the aimed burn's periapsis, " + DeorbitBurn.Km(pe / 1000) + " km, is off the " + DeorbitBurn.Km(from.Pe / 1000) + " km aimed for");
            // The entry flown again (#817): no deeper than the corridor's end, nor harder than MostEntryG.
            var deepest = high > low ? low - PeResidual : ask.TargetPe - ask.PeTolerance;
            if (pe < deepest) return reported(aimed, "the aimed burn's periapsis, " + DeorbitBurn.Km(pe / 1000) + " km, is under the corridor's " + DeorbitBurn.Km(Math.Max(0, deepest) / 1000) + " km");
            if (down.PeakG > MostEntryG) return reported(aimed, "the aimed burn's entry pulls " + Ms(down.PeakG) + " g, over " + Ms(MostEntryG));
            if (!(km < aimed.ErrorKm)) return reported(aimed, "the aimed burn lands no nearer (" + DeorbitBurn.Km(km) + " km from the target)");
            if (ask.DvLeft >= 0 && burnAimed.Length > ask.DvLeft) return reported(aimed, "the aimed burn needs " + Ms(burnAimed.Length) + " m/s, more than is left");
            if (burnAimed.Length > spendable + slack)
                return reported(aimed, "the aimed burn needs " + Ms(burnAimed.Length) + " m/s, over the " + Ms(Math.Max(0, spendable)) + " m/s it may spend");

            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, ut, burnAimed, out prograde, out normal, out radial);
            var plan = new CorrectionPlan
            {
                HasBurn = true,
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = burnAimed.Length,
                PePredicted = pe,
                IncPredicted = reached.Orbit.Inclination,
                Landing = down,
                ErrorKm = km,
                Iterations = plain.Iterations + result.Iterations,
                Evaluations = plain.Evaluations + result.Evaluations,
            };
            // What it couldn't reach is no failure (#817): it is reported.
            return reported(plan, Math.Abs(rest) > LandingResidual ? corridorWhy ?? "spent the " + Ms(Math.Max(0, most)) + " m/s it may above the reserve" : null);
        }

        /// <summary>
        /// The periapses (m above sea level) an aimed correction may leave the path on (#817): at the home planet the corridor, <see cref="PeCorridor"/> either way of target_pe but not under
        /// <see cref="CorridorFloor"/>, and held at target_pe when that is under it; elsewhere target_pe alone, as before #817.
        /// </summary>
        internal static void CorridorOf(SnapshotBody target, CorrectionAsk ask, out double low, out double high)
        {
            low = high = ask.TargetPe;
            var centre = ask.CorridorPe ?? ask.TargetPe;
            if (!string.Equals(target.Name, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase) || !(target.AtmosphereHeight > 0) || centre < CorridorFloor) return;
            var half = ask.CorridorHalf ?? PeCorridor;
            low = Math.Max(CorridorFloor, centre - half);
            high = centre + half;
        }

        /// <summary>#915: the corridor's reach either way of target_pe that sits inside <paramref name="tolerance"/> m: half of it less the solve's own <see cref="PeResidual"/>, so a burn solved at the corridor's end still passes <see cref="LeavesFlownMargin"/>.</summary>
        internal static double CorridorWithin(double tolerance)
        {
            return Math.Min(PeCorridor, Math.Max(0, tolerance / 2 - PeResidual));
        }

        /// <summary>#915: whether an aimed periapsis of <paramref name="pe"/> m is within half of <paramref name="tolerance"/> m of <paramref name="targetPe"/>, leaving the other half for the burn's flown error.</summary>
        internal static bool LeavesFlownMargin(double pe, double targetPe, double tolerance)
        {
            return Math.Abs(pe - targetPe) <= tolerance / 2;
        }

        /// <summary>#805: whether the landing may be left on a periapsis of <paramref name="pe"/> m: inside the corridor, or within <see cref="PlainSlack"/> pe_tolerances of target_pe.</summary>
        internal static bool PeAcceptable(SnapshotBody target, CorrectionAsk ask, double pe)
        {
            double low, high;
            CorridorOf(target, ask, out low, out high);
            return (pe >= low && pe <= high) || (pe >= low && Math.Abs(pe - ask.TargetPe) <= PlainSlack * ask.PeTolerance);
        }

        /// <summary>
        /// <paramref name="kept"/>, the burn the aimed solve stopped at, with why in its <see cref="CorrectionPlan.AimNote"/> (#817): which burn, the reason, where it comes down and the delta-v left.
        /// </summary>
        private static CorrectionPlan Kept(CorrectionPlan kept, CorrectionPlan plain, CorrectionAsk ask, string why)
        {
            kept.NotAimed = kept == plain;
            kept.AimNote = (kept == plain ? "plain burn: " : "periapsis " + DeorbitBurn.Km(kept.PePredicted / 1000) + " km: ") + why
                + (kept.ErrorKm >= 0 ? "; " + DeorbitBurn.Km(kept.ErrorKm) + " km from the target" : "")
                + (ask.DvLeft >= 0 ? ", " + Ms(ask.DvLeft) + " m/s left" : ", delta-v left unknown");
            return kept;
        }

        /// <summary>A periapsis in the corridor (m above sea level), the burn that holds it, and where it comes down: <see cref="Along"/> km along the lever past the target.</summary>
        private sealed class CorridorPoint
        {
            public double Pe;
            public Vec3 Burn;
            public Patch Arrival;
            public ReentryPrediction Landed;
            public double Along;
            public double Km;
        }

        /// <summary>
        /// The periapsis in the corridor whose landing comes nearest the target along the track (#817): each end of it, then regula falsi on the side that crosses, each
        /// periapsis held by the burn's part along the periapsis's slope. The ends are <paramref name="low"/> and <paramref name="high"/> (<see cref="CorridorOf"/>); one whose entry pulls over
        /// <see cref="MostEntryG"/> moves back toward target_pe. Null with nothing better.
        /// </summary>
        private static CorridorPoint Corridor(SnapshotBody target, CorrectionAsk ask, double low, double high, Func<double, Vec3, Patch> arrive, Func<Patch, Vec3, ReentryPrediction> fly,
            Func<ReentryPrediction, double> along, ReturnLanding landing, double ut, double step, CorridorPoint held)
        {
            var slope = PeSlope(arrive, ut, held.Burn, step);
            if (slope == null) return null;
            var toPe = slope.Value.Normalized;
            var flights = 0;
            Func<double, CorridorPoint> at = pe =>
            {
                if (++flights > CorridorPredictions) return null;
                var found = Crossing(p => OffPe(arrive, ut, held.Burn + toPe * p, target.Radius + pe), 0);
                if (!found.HasValue) return null;
                var dv = held.Burn + toPe * found.Value;
                var arrival = arrive(ut, dv);
                var landed = fly(arrival, dv);
                if (landed == null || landed.Error != null) return null;
                return new CorridorPoint { Pe = pe, Burn = dv, Arrival = arrival, Landed = landed, Along = along(landed), Km = landing.ErrorKm(target, landed) };
            };
            CorridorPoint best = null;
            Action<CorridorPoint> keep = p =>
            {
                if (p != null && p.Landed.PeakG <= MostEntryG && (best == null || Math.Abs(p.Along) < Math.Abs(best.Along))) best = p;
            };
            foreach (var side in new[] { -1.0, 1.0 })
            {
                var edge = side < 0 ? low : high;
                if (!(side * (edge - ask.TargetPe) > 0)) continue;
                var end = at(edge);
                // Too hard an entry: the corridor narrows toward target_pe.
                for (var pe = edge - side * CorridorStepM; end != null && end.Landed.PeakG > MostEntryG && side * (pe - ask.TargetPe) > 0; pe -= side * CorridorStepM)
                    end = at(pe);
                if (end == null || end.Landed.PeakG > MostEntryG) continue;
                keep(end);
                if ((end.Along > 0) == (held.Along > 0)) continue;
                // Regula falsi (Illinois) between target_pe and this end: the landing moves about linearly with the periapsis.
                CorridorPoint a = held, b = end;
                double fa = a.Along, fb = b.Along;
                for (var i = 0; i < CorridorSecants && Math.Abs(fb - fa) > 0; i++)
                {
                    var x = at(b.Pe - fb * (b.Pe - a.Pe) / (fb - fa));
                    if (x == null) break;
                    keep(x);
                    if (Math.Abs(x.Along) <= LandingResidual || Math.Abs(x.Pe - b.Pe) < CorridorRootM) break;
                    if ((x.Along > 0) == (fb > 0)) fa /= 2;
                    else
                    {
                        a = b;
                        fa = fb;
                    }
                    b = x;
                    fb = x.Along;
                }
            }
            return best != null && Math.Abs(best.Along) < Math.Abs(held.Along) ? best : null;
        }

        /// <summary>The burn on the way from <paramref name="start"/> to <paramref name="end"/> whose size is <paramref name="most"/> m/s; <paramref name="start"/> when that is already over it.</summary>
        private static Vec3 Toward(Vec3 start, Vec3 end, double most)
        {
            var change = end - start;
            double cc = Vec3.Dot(change, change), sc = Vec3.Dot(start, change), ss = Vec3.Dot(start, start);
            var discriminant = sc * sc - cc * (ss - most * most);
            if (!(cc > 0) || !(ss <= most * most) || discriminant < 0) return start;
            return start + change * Math.Min(1, (-sc + Math.Sqrt(discriminant)) / cc);
        }

        /// <summary>The plan for a corridor point, its burn at <paramref name="plain"/>'s time.</summary>
        private static CorrectionPlan PlanAt(Conic orbit, SnapshotBody target, CorrectionPlan plain, CorridorPoint point)
        {
            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, plain.Ut, point.Burn, out prograde, out normal, out radial);
            return new CorrectionPlan
            {
                HasBurn = true,
                Ut = plain.Ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = point.Burn.Length,
                PePredicted = point.Arrival.Orbit.PeR - target.Radius,
                IncPredicted = point.Arrival.Orbit.Inclination,
                Landing = point.Landed,
                ErrorKm = point.Km,
                Iterations = plain.Iterations,
                Evaluations = plain.Evaluations,
            };
        }

        /// <summary>
        /// #805: the plain burn, solved but off pe_tolerance, flies when its periapsis is one the landing may be left on (<see cref="PeAcceptable"/>): no aiming from it, and why in
        /// <see cref="CorrectionPlan.AimNote"/>, with where it comes down when the predictor can say. Its entry is checked as the aimed burn's is: over <see cref="MostEntryG"/>, none flies.
        /// </summary>
        private static CorrectionPlan PlainOnly(Func<double, Vec3, Patch> arrive, double startUt, SnapshotBody target, CorrectionAsk ask, CorrectionPlan plain, Vec3 burn)
        {
            var why = plain.Error;
            if (ask.DvLeft >= 0 && burn.Length > ask.DvLeft) return Unsolved(arrive, startUt, target, ask, why + "; the plain burn needs " + Ms(burn.Length) + " m/s, more than is left");
            var landed = PredictOn(target, ask.Landing, arrive(plain.Ut, burn), burn.Length);
            if (landed != null && landed.Error == null && landed.PeakG > MostEntryG)
                return Unsolved(arrive, startUt, target, ask, why + "; the plain burn's entry pulls " + Ms(landed.PeakG) + " g, over " + Ms(MostEntryG));
            plain.Error = null;
            if (landed != null && landed.Error == null)
            {
                plain.Landing = landed;
                plain.ErrorKm = ask.Landing.ErrorKm(target, landed);
            }
            return Kept(plain, plain, ask, why);
        }

        /// <summary>Where <paramref name="arrival"/> comes down on <paramref name="target"/> for the craft after a burn of <paramref name="dv"/> m/s; null with no arrival.</summary>
        private static ReentryPrediction PredictOn(SnapshotBody target, ReturnLanding landing, Patch arrival, double dv)
        {
            return arrival == null ? null
                : ReentryPredictor.Predict(target, arrival.Orbit, arrival.StartUt, landing.GroundAltitude, landing.MassAfter(dv), landing.Air, landing.Craft, landing.Frame);
        }

        /// <summary>
        /// #805: a correction aimed at a landing with no plain burn to fly flies none, and the return carries on to reentry: the path as it is, where it comes down, and why in
        /// <see cref="CorrectionPlan.AimNote"/>. Unless that path is no landing: it doesn't reach the planet, its periapsis is outside <see cref="PeAcceptable"/>, or the predictor has no
        /// landing on it. Then it is the block's error, as before #805.
        /// </summary>
        internal static CorrectionPlan Unsolved(Func<double, Vec3, Patch> arrive, double ut, SnapshotBody target, CorrectionAsk ask, string why)
        {
            var failed = why.StartsWith(Failure, StringComparison.Ordinal) ? why : Failure + ": " + why;
            var arrival = arrive(ut, default(Vec3));
            if (arrival == null) return new CorrectionPlan { Error = failed + "; without it the path does not reach " + target.Name };
            var plan = new CorrectionPlan { HasBurn = true, Ut = ut, PePredicted = arrival.Orbit.PeR - target.Radius, IncPredicted = arrival.Orbit.Inclination };
            if (!PeAcceptable(target, ask, plan.PePredicted))
            {
                double low, high;
                CorridorOf(target, ask, out low, out high);
                plan.Error = failed + "; without it the periapsis is " + DeorbitBurn.Km(plan.PePredicted / 1000) + " km, outside " + DeorbitBurn.Km(low / 1000)
                    + (high > low ? " to " + DeorbitBurn.Km(high / 1000) : "") + " km";
                return plan;
            }
            var landing = ask.Landing;
            var landed = PredictOn(target, landing, arrival, 0);
            if (landed == null || landed.Error != null)
            {
                plan.Error = failed + "; without it the path has no landing to read" + (landed == null ? "" : " (" + landed.Error + ")");
                return plan;
            }
            if (landed.PeakG > MostEntryG)
            {
                plan.Error = failed + "; without it the entry pulls " + Ms(landed.PeakG) + " g, over " + Ms(MostEntryG);
                return plan;
            }
            plan.Landing = landed;
            plan.ErrorKm = landing.ErrorKm(target, landed);
            plan.NotAimed = true;
            plan.AimNote = "no correction flown: " + why + "; " + DeorbitBurn.Km(plan.ErrorKm) + " km from the target" + (ask.DvLeft >= 0 ? ", " + Ms(ask.DvLeft) + " m/s left" : "");
            return plan;
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
