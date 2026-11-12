// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// The ejection from the parking orbit onto the escape hyperbola follows the single-impulse hyperbolic burn of MechJeb2's
// MechJebLib/Maneuvers/SingleImpulseHyperbolicBurn.cs (MIT, https://github.com/MuMech/MechJeb2, commit a958383): the burn
// point is where the hyperbola's periapsis puts its outgoing asymptote along the departure's v-infinity; then it is refined on
// the patched conics instead of with MechJeb's solver.

using System;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>The ejection node to a body sharing the craft's parent, and its window, or why there is none (<see cref="Error"/>).</summary>
    internal sealed class SiblingPlan
    {
        public string Error;
        /// <summary>The node's UT and components, m/s, in stock's maneuver-node frame, and its delta-v.</summary>
        public double Ut;
        public double Prograde;
        public double Normal;
        public double Radial;
        public double DeltaV;
        /// <summary>The window: its departure and arrival (UT), and the speed relative to the target as the craft arrives, m/s.</summary>
        public double DepartureUt;
        public double ArrivalUt;
        public double VInf;
        /// <summary>How far from the target's centre the ejection's path round the parent passes at the arrival, m.</summary>
        public double AimMiss;
    }

    /// <summary>
    /// <c>transfer_to</c>'s ejection to a body sharing the craft's parent (#459): Duna from Kerbin, Minmus from the Mun. The window
    /// is <see cref="TransferWindows"/>' (#458): with a window UT, re-solved over a narrow span round it (the cheapest there); with
    /// "next", the one the Mission Setting Time versus delta-v picks in the next synodic period, as plan_transfer picks it. The
    /// node is the Oberth burn from the parking orbit whose asymptote leaves along the window's v-infinity, at the pass nearest
    /// the departure; then Newton's method on its three components, on the patched conics (out of the craft's sphere of
    /// influence, then round the parent), puts the path through the target's centre at the window's arrival. The Course
    /// Correction on the parent's patch then sets the periapsis. It works on the Game Snapshot's detached orbits and only reads.
    /// </summary>
    internal static class SiblingEjection
    {
        private const string Failure = "found no transfer";
        /// <summary>The re-solve round a window UT: this share of the synodic period each way, or two parking orbits if longer.</summary>
        private const double NarrowShare = 0.01;
        private const int NarrowOrbits = 2;
        /// <summary>
        /// From this eccentricity the craft is on a fall, not in a parking orbit (#760): its first periapsis after the window UT is the
        /// ejection. Its periapsis is lined up for that time, and each later pass goes back out to the moon's height, so the re-solve stays
        /// within half an orbit of the window UT, where the burn point's next pass is that first periapsis.
        /// </summary>
        private const double FallEccentricity = 0.5;
        /// <summary>Newton's method: its iteration cap, the finite-difference step (m/s), and the miss (m) it stops within.</summary>
        private const int Iterations = 20;
        private const double Step = 0.01;
        private const double AimClose = 1000;
        /// <summary>A refined burn that misses the centre by more than this share of the target's sphere of influence is no ejection.</summary>
        private const double AimNear = 0.25;

        public static SiblingPlan Find(GameSnapshot snapshot, PredictBurn.Setup setup, string target, double targetPe, double earliest, double? window, bool capture = true)
        {
            var home = setup.Body;
            var craft = setup.Start;
            if (!craft.Closed) return Fail("the craft is not in a closed orbit round " + home.Name);
            string error;
            var problem = TransferWindows.Problem.Of(snapshot, target, targetPe, capture, out error);
            if (problem == null) return Fail(error);
            if (problem.ParkingAssumed) return Fail("the craft's orbit round " + home.Name + " must be closed and clear of its atmosphere");
            var synodic = Transfer.Synodic(problem.Home.Period, problem.Target.Period);
            WindowSearch found;
            TransferWindow pick;
            if (window.HasValue)
            {
                var reach = craft.E >= FallEccentricity ? craft.Period / 4 : Math.Max(NarrowOrbits * craft.Period, NarrowShare * synodic);
                problem.From = Math.Max(earliest, window.Value - reach);
                found = TransferWindows.Search(problem, snapshot.MarginPercent, Math.Max(window.Value + reach, problem.From + 2 * reach) - problem.From);
                pick = found.Optimal;
            }
            else
            {
                problem.From = earliest;
                found = TransferWindows.Search(problem, snapshot.MarginPercent);
                pick = found.Error == null ? TransferWindows.Pick(found, snapshot) : null;
            }
            if (found.Error != null) return Fail(found.Error.Replace("found no transfer window: ", ""));
            // #966: a held window that has gone is never traded for a later one, here or in the wait for it.
            if (found.HeldMissed) return Fail(TransferWindows.HeldMissedWhy(target));

            // The window's v-infinity: the Lambert arc round the parent less the craft's planet (or moon) there.
            var tof = pick.ArrivalUt - pick.DepartureUt;
            Vec3 r1, v1, r2, v2;
            problem.Home.State(pick.DepartureUt, out r1, out v1);
            problem.Target.State(pick.ArrivalUt, out r2, out v2);
            var arc = Lambert.Solve(problem.Home.Mu, r1, r2, tof, problem.Home.W);
            if (arc.Error != null) return Fail("the window's arc has no Lambert path: " + arc.Error);
            var vInf = arc.V1 - v1;

            double ut;
            var burn = Oberth(craft, home.Gm, vInf, pick.DepartureUt, earliest, out ut);
            var homeOrbit = setup.System.OrbitOf(home);
            var targetOrbit = setup.System.OrbitOf(setup.Target);
            var parentGm = problem.Home.Mu;
            Func<Vec3, Vec3?> miss = x => Miss(craft, home, homeOrbit, parentGm, targetOrbit, ut, x, pick.ArrivalUt);
            var aimed = Aim(miss, burn);
            if (!aimed.HasValue) return Fail("the ejection at UT " + Num(ut) + " does not leave " + home.Name + "'s sphere of influence");
            var off = miss(aimed.Value);
            var missBy = off.HasValue ? off.Value.Length : double.PositiveInfinity;
            if (!(missBy <= AimNear * setup.Target.SoiRadius))
                return Fail("the ejection at UT " + Num(ut) + " passes " + Num(missBy / 1000) + " km from " + setup.Target.Name + " at the arrival, outside its sphere of influence");

            double prograde, normal, radial;
            // The aim follows one conic out of the craft's sphere of influence; on the patched conics the way out must not meet one
            // of its moons (Kerbin's Mun or Minmus) first, which the block could not correct after the burn.
            var first = SingleShooting.Follow(setup.System, home, AfterBurn(craft, home, ut, aimed.Value), ut, 1);
            if (first != null && first.Count > 0 && first[0].End == PatchEnd.Encounter)
                return Fail(MeetsMoon(setup, ut, first[0].Next));
            SingleShooting.NodeFrame(craft, ut, aimed.Value, out prograde, out normal, out radial);
            return new SiblingPlan
            {
                Ut = ut, Prograde = prograde, Normal = normal, Radial = radial, DeltaV = aimed.Value.Length,
                DepartureUt = pick.DepartureUt, ArrivalUt = pick.ArrivalUt, VInf = pick.ArrivalVInf, AimMiss = missBy,
            };
        }

        /// <summary>
        /// The refusal of an ejection at <paramref name="ut"/> that meets <paramref name="moon"/> on its way out. When the orbit waited in crosses that
        /// moon too (#867), it says so with the fix, or the same window is planned again from the same orbit.
        /// </summary>
        internal static string MeetsMoon(PredictBurn.Setup setup, double ut, SnapshotBody moon)
        {
            var home = setup.Body;
            var parked = MoonCrossings.Judge(setup.System, home, setup.Start, setup.Now, ut);
            return "the ejection at UT " + Num(ut) + " meets " + moon.Name + " on its way out of " + home.Name + "'s sphere of influence"
                + (parked != null && parked.Moon == moon ? " " + parked.RefusalClause("the orbit you're in") : "");
        }

        /// <summary>
        /// The Oberth burn (inertial, m/s) from <paramref name="craft"/> onto the hyperbola whose outgoing asymptote is along
        /// <paramref name="vInf"/>, at the pass of its periapsis point nearest <paramref name="departure"/> and not before
        /// <paramref name="earliest"/> (<paramref name="ut"/>). The periapsis point is in the craft's plane, θ∞ back from the asymptote's
        /// projection (θ∞ widened by the asymptote's tilt out of the plane, so the hyperbola's own plane holds both).
        /// </summary>
        internal static Vec3 Oberth(Conic craft, double mu, Vec3 vInf, double departure, double earliest, out double ut)
        {
            var speed = vInf.Length;
            var dir = vInf / speed;
            var h = craft.W;
            var r0 = craft.A;
            ut = departure;
            for (var pass = 0; pass < 2; pass++)
            {
                var point = BurnPoint(h, vInf, r0, mu);
                var nu = Math.Atan2(Vec3.Dot(point, craft.Q), Vec3.Dot(point, craft.P));
                ut = craft.TimeAtTrue(nu, Math.Max(earliest, departure - craft.Period / 2)) ?? departure;
                r0 = craft.Radius(ut);
            }
            Vec3 r, v;
            craft.State(ut, out r, out v);
            var up = Vec3.Cross(r, dir);
            if (up.Length < 1e-9 * r.Length) up = h;
            var along = Vec3.Cross(up.Normalized, r).Normalized;
            return along * Math.Sqrt(speed * speed + 2 * mu / r.Length) - v;
        }

        /// <summary>The direction, in the plane with normal <paramref name="h"/>, of the periapsis of the hyperbola at radius <paramref name="r0"/> leaving along <paramref name="vInf"/> (see <see cref="Oberth"/>).</summary>
        internal static Vec3 BurnPoint(Vec3 h, Vec3 vInf, double r0, double mu)
        {
            var speed = vInf.Length;
            var dir = vInf / speed;
            var sinTilt = Vec3.Dot(dir, h);
            var cosTilt = Math.Sqrt(Math.Max(1e-12, 1 - sinTilt * sinTilt));
            var u = (dir - h * sinTilt).Normalized;
            var e = 1 + r0 * speed * speed / mu;
            var c = Math.Max(-1, Math.Min(1, -1 / e / cosTilt));
            return u * c - Vec3.Cross(h, u) * Math.Sqrt(1 - c * c);
        }

        /// <summary>
        /// Where the path after <paramref name="burn"/> at <paramref name="ut"/> is at <paramref name="arrival"/> on the parent's
        /// patch, less the target's position then; null when the burn doesn't leave the craft's sphere of influence.
        /// </summary>
        private static Vec3? Miss(Conic craft, SnapshotBody home, Conic homeOrbit, double parentGm, Conic targetOrbit, double ut, Vec3 burn, double arrival)
        {
            Vec3 r, v;
            craft.State(ut, out r, out v);
            try
            {
                var after = Conic.FromState(home.Gm, r, v + burn, ut);
                var leave = after.TimeAtRadius(home.SoiRadius, ut, true);
                if (!leave.HasValue) return null;
                Vec3 re, ve, hr, hv;
                after.State(leave.Value, out re, out ve);
                homeOrbit.State(leave.Value, out hr, out hv);
                var round = Conic.FromState(parentGm, re + hr, ve + hv, leave.Value);
                return round.Position(arrival) - targetOrbit.Position(arrival);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Newton's method from <paramref name="start"/> on the miss's three components, each step halved until the miss shrinks; null when the start doesn't escape.</summary>
        private static Vec3? Aim(Func<Vec3, Vec3?> miss, Vec3 start)
        {
            var x = start;
            var f = miss(x);
            if (!f.HasValue) return null;
            for (var i = 0; i < Iterations && f.Value.Length > AimClose; i++)
            {
                var columns = new Vec3[3];
                var ok = true;
                for (var k = 0; k < 3 && ok; k++)
                {
                    var g = miss(x + Axis(k) * Step);
                    ok = g.HasValue;
                    if (ok) columns[k] = (g.Value - f.Value) / Step;
                }
                Vec3 d;
                if (!ok || !Solve(columns, f.Value * -1, out d)) break;
                var lambda = 1.0;
                var moved = false;
                for (var halve = 0; halve < 8 && !moved; halve++, lambda /= 2)
                {
                    var trial = x + d * lambda;
                    var g = miss(trial);
                    if (g.HasValue && g.Value.Length < f.Value.Length)
                    {
                        x = trial;
                        f = g;
                        moved = true;
                    }
                }
                if (!moved) break;
            }
            return x;
        }

        internal static Conic AfterBurn(Conic craft, SnapshotBody home, double ut, Vec3 burn)
        {
            Vec3 r, v;
            craft.State(ut, out r, out v);
            return Conic.FromState(home.Gm, r, v + burn, ut);
        }

        private static Vec3 Axis(int k)
        {
            return k == 0 ? new Vec3(1, 0, 0) : k == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
        }

        /// <summary>Solves [a b c] d = <paramref name="rhs"/> by Cramer's rule; false when the columns are (nearly) dependent.</summary>
        private static bool Solve(Vec3[] columns, Vec3 rhs, out Vec3 d)
        {
            var det = Vec3.Dot(columns[0], Vec3.Cross(columns[1], columns[2]));
            var scale = columns[0].Length * columns[1].Length * columns[2].Length;
            d = default(Vec3);
            if (!(Math.Abs(det) > 1e-12 * scale)) return false;
            d = new Vec3(
                Vec3.Dot(rhs, Vec3.Cross(columns[1], columns[2])) / det,
                Vec3.Dot(columns[0], Vec3.Cross(rhs, columns[2])) / det,
                Vec3.Dot(columns[0], Vec3.Cross(columns[1], rhs)) / det);
            return true;
        }

        private static SiblingPlan Fail(string why)
        {
            return new SiblingPlan { Error = Failure + ": " + why };
        }

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
