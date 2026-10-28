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
    /// <summary>How a transfer meets its target, as MechJebLib's <c>TwoImpulseTransfer</c> poses it.</summary>
    internal enum TransferMode
    {
        /// <summary>Meet the target where it is at arrival: the phase offset is held at 0. The only mode for a body.</summary>
        Rendezvous,
        /// <summary>Reach the target orbit anywhere on it: the arrival point is free.</summary>
        Intercept,
    }

    /// <summary>
    /// What a transfer asks: the target (a body in the craft's sphere of influence, or an orbit round the craft's body), how to
    /// meet it, whether the arrival burn counts, and from when.
    /// </summary>
    internal sealed class TransferAsk
    {
        /// <summary>A body orbiting the body the craft orbits; null when <see cref="TargetOrbit"/> is given instead.</summary>
        public string Target;
        /// <summary>An orbit round the body the craft orbits, as elements (<see cref="SnapshotOrbit.MeanAnomalyAtEpoch"/> in radians at its <see cref="SnapshotOrbit.Epoch"/>); null for a body.</summary>
        public SnapshotOrbit TargetOrbit;
        public TransferMode Mode = TransferMode.Rendezvous;
        /// <summary>True: the cheapest departure plus arrival burns. False: the cheapest departure alone.</summary>
        public bool Capture = true;
        /// <summary>The periapsis altitude at a body, m above its sea level; unused for an orbit.</summary>
        public double TargetPe;
        /// <summary>The soonest UT the burn may be: the block's start plus the turn time.</summary>
        public double EarliestUt;
        /// <summary>The window's UT, or null for the next window.</summary>
        public double? WindowUt;
        /// <summary>The latest UT the burn may be (the block's max_duration), or null for none: the search ends there.</summary>
        public double? LatestUt;
        /// <summary>The most delta-v the departure burn may take (the craft's), m/s, or null for no limit.</summary>
        public double? MaxDv;
    }

    /// <summary>The departure node found and the arrival burn after it, or why there is none (<see cref="Error"/>, with the rest unset).</summary>
    internal sealed class TransferPlan
    {
        public string Error;
        public double Ut;
        /// <summary>The departure node's components, m/s, in stock's maneuver-node frame (as <see cref="PredictBurn"/> takes them).</summary>
        public double Prograde;
        public double Normal;
        public double Radial;
        public double DeltaV;
        /// <summary>
        /// The arrival burn, m/s: at a body, the burn that makes the orbit circular at its periapsis there; at an orbit, the
        /// burn that matches the target's velocity at arrival.
        /// </summary>
        public double ArrivalDv;
        /// <summary>When the craft enters the target's sphere of influence, and when it reaches periapsis there; both the arrival at an orbit.</summary>
        public double EncounterUt;
        public double ArrivalUt;
        /// <summary>The periapsis predicted at a body, m above its sea level; NaN for an orbit.</summary>
        public double PeActual = double.NaN;
        /// <summary>The search: from this UT, the synodic period (s) between the craft's orbit and the target's.</summary>
        public double SearchFrom;
        public double Synodic;
    }

    /// <summary>
    /// The cheapest two-impulse transfer within the craft's sphere of influence (spec #435 Phase 2, #448), generalised from
    /// <c>transfer_to</c>'s ejection burn (spec #314), with the formulation of MechJebLib's <c>Maneuvers/TwoImpulseTransfer.cs</c>
    /// (MechJeb2 a958383): a Lambert arc from the craft at the departure time to the target after the flight time, costed as
    /// the departure burn alone or, with capture, plus the arrival burn. A rendezvous meets the target where it is (the offset
    /// held at 0); an intercept reaches its orbit anywhere and tries both sides of it, so the descending node is not taken for
    /// the ascending one. In place of MechJeb's alglib call, a grid over departure time and flight time finds the window and
    /// <see cref="BrentMin"/> refines it, over the departure time with the flight time (and an intercept's arrival point)
    /// minimised inside. To a body, <see cref="PredictBurn"/>'s propagation, as <c>solve_burn</c> uses it, then sizes the
    /// prograde part of the burn so the patched conics arrive at the periapsis asked for, on a prograde orbit round the target
    /// where one does; a body's capture is costed as RSVP costs it, a circular orbit at that periapsis. It works on the Game
    /// Snapshot's detached orbits and only reads. Only the departure node is given; the capture is <c>circularize</c>'s. After
    /// an ejection that misses (#363), <c>transfer_to</c> asks the same search for its correction from the orbit it is on,
    /// within the block's limits (<see cref="TransferAsk.LatestUt"/>, <see cref="TransferAsk.MaxDv"/>).
    /// </summary>
    internal static class Transfer
    {
        private const string Failure = "found no transfer";
        /// <summary>Departures tried per span (a synodic period, or one orbit of the craft for an intercept), as <c>transfer_to</c> seeded RSVP (span / 100).</summary>
        private const int Seeds = 100;
        /// <summary>Times of flight tried per departure, from 0.6 to 1.4 of the Hohmann time.</summary>
        private const int Flights = 21;
        private const double FlightLow = 0.6;
        private const double FlightHigh = 1.4;
        /// <summary>
        /// The steps, m/s, the prograde part is scanned in round the Lambert burn before it is bisected, and how many each way:
        /// fine to find a burn that passes inside the goal periapsis (a Minmus encounter is lost 2 m/s off), coarse out from it.
        /// </summary>
        private const double FineStep = 0.1;
        private const double ScanStep = 2;
        private const int ScanSteps = 60;
        /// <summary>
        /// The refinement: BrentMin's tolerance on a time in units of the problem's <see cref="Scale.TimeScale"/> (about 0.003 s
        /// from low Kerbin orbit to the Mun) and on an arrival angle (rad), its iteration cap, and how far the flight time may
        /// move from the grid's, in grid steps.
        /// </summary>
        private const double TimeTolerance = 1e-6;
        private const double AngleTolerance = 1e-6;
        private const int RefineIterations = 100;
        private const double FlightReach = 2;
        /// <summary>How far an intercept's arrival point may move from the point opposite the departure, rad.</summary>
        private const double ArrivalReach = Math.PI / 3;
        /// <summary>The cost of a point with no Lambert path: finite, so BrentMin steps away from it instead of stopping.</summary>
        private const double NoPath = 1e30;
        /// <summary>The bisection on the prograde part: it stops within <see cref="PeClose"/> m of target_pe, and fails beyond <see cref="PeNear"/> m.</summary>
        private const double PeClose = 10;
        private const double PeNear = 100;
        private const int Bisections = 60;

        public static TransferPlan Find(GameSnapshot snapshot, TransferAsk ask)
        {
            var toOrbit = ask.TargetOrbit != null;
            if (toOrbit == !string.IsNullOrEmpty(ask.Target)) return Fail(toOrbit ? "give a target body or a target orbit, not both" : "no target given");
            if (!Finite(ask.EarliestUt) || ask.WindowUt.HasValue && !Finite(ask.WindowUt.Value) || ask.LatestUt.HasValue && double.IsNaN(ask.LatestUt.Value))
                return Fail("earliest_ut, window and latest_ut must be numbers");
            if (ask.MaxDv.HasValue && !(ask.MaxDv.Value >= 0)) return Fail("max_dv must be a number of m/s, 0 or more");
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, ask.Target, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            var home = setup.Body;
            if (!setup.Start.Closed) return Fail("the craft is not in a closed orbit round " + home.Name);

            var body = setup.Target;
            Conic aim;
            double peR = 0;
            if (body != null)
            {
                if (!string.Equals(body.Parent, home.Name, StringComparison.OrdinalIgnoreCase))
                    return Fail(body.Name + " does not orbit " + home.Name + ", the body the craft orbits");
                if (ask.Mode != TransferMode.Rendezvous) return Fail("intercept is for a target orbit; a body is met where it is");
                var ceiling = body.SoiRadius - body.Radius;
                if (!(ask.TargetPe >= 0 && ask.TargetPe < ceiling))
                    return Fail("target_pe must be from 0 to " + Num(ceiling) + " m, inside " + body.Name + "'s sphere of influence");
                aim = setup.System.OrbitOf(body);
                peR = body.Radius + ask.TargetPe;
            }
            else
            {
                var problem = OrbitProblem(ask.TargetOrbit, home);
                if (problem != null) return Fail(problem);
                var o = ask.TargetOrbit;
                aim = Conic.FromElements(home.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
            }

            var craft = setup.Start;
            var intercept = ask.Mode == TransferMode.Intercept;
            var synodic = Synodic(craft.Period, aim.Period);
            var from = Math.Max(setup.Now, Math.Max(ask.EarliestUt, ask.WindowUt ?? double.NegativeInfinity));
            // A latest UT shortens the span, and no window past it may be taken.
            var until = ask.LatestUt ?? double.PositiveInfinity;
            if (until <= from)
                return Fail("the burn must come by UT " + Num(until) + ", before the earliest it can be, UT " + Num(from));
            // With the arrival point free, every departure point comes round once an orbit of the craft.
            var span = Math.Min(intercept ? craft.Period : synodic, until - from);
            var problemOf = new Problem(craft, aim, body, peR, ask.Capture, intercept);

            // The grid: a departure every span / Seeds from one step before the start (so a window just before it is a
            // falling edge, not a minimum) to a quarter span past the end, each at its cheapest time of flight.
            var step = span / Seeds;
            var count = Seeds + Seeds / 4 + 2;
            var cheapest = new double[count];
            var flight = new double[count];
            var hohmann = new double[count];
            for (var i = 0; i < count; i++)
            {
                var t = from + (i - 1) * step;
                hohmann[i] = problemOf.HohmannTime(t);
                cheapest[i] = double.PositiveInfinity;
                for (var j = 0; j < Flights; j++)
                {
                    var tof = hohmann[i] * (FlightLow + (FlightHigh - FlightLow) * j / (Flights - 1));
                    var c = problemOf.Cost(t, tof, 0);
                    if (c < cheapest[i])
                    {
                        cheapest[i] = c;
                        flight[i] = tof;
                    }
                }
            }
            var picks = intercept ? InterceptPicks(cheapest, from, step, span, craft.Period) : RendezvousPick(cheapest, step, span, ask.LatestUt.HasValue);
            if (picks.Count == 0)
                return Fail("no departure in the " + Num(span) + " s from UT " + Num(from) + " has a path to " + problemOf.Name
                    + (problemOf.LambertError != null ? " (Lambert: " + problemOf.LambertError + ")" : ""));

            // BrentMin from each pick, never before the start or past the grid's end (or the latest UT); the cheapest wins.
            var last = Math.Min(from + (count - 2) * step, until);
            var scale = Scale.Create(home.Gm, Math.Sqrt(craft.A * aim.A));
            Point best = null;
            foreach (var pick in picks)
            {
                var seed = new Point { T = from + (pick - 1) * step, Tof = flight[pick], Cost = cheapest[pick] };
                var found = problemOf.Refine(seed, Math.Max(from, seed.T - step), Math.Min(last, seed.T + step),
                    FlightReach * hohmann[pick] * (FlightHigh - FlightLow) / (Flights - 1), scale.TimeScale);
                if (best == null || found.Cost < best.Cost) best = found;
            }

            var leg = problemOf.Leg(best.T, best.Tof, best.Shift);
            if (leg.Error != null) return Fail("the cheapest departure, at UT " + Num(best.T) + ", has no Lambert path: " + leg.Error);
            Vec3 r, v;
            craft.State(best.T, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            double prograde = Vec3.Dot(leg.Departure, along), normal = Vec3.Dot(leg.Departure, up), radial = Vec3.Dot(leg.Departure, outward);

            var plan = body != null
                ? Aim(setup, body, aim, best.T, prograde, normal, radial, ask.TargetPe)
                : new TransferPlan
                {
                    Ut = best.T, Prograde = prograde, Normal = normal, Radial = radial, DeltaV = leg.Departure.Length,
                    ArrivalDv = leg.Arrival.Length, EncounterUt = best.T + best.Tof, ArrivalUt = best.T + best.Tof,
                };
            if (plan.Error != null) return plan;
            if (plan.DeltaV > ask.MaxDv)
                return Fail("the cheapest burn, at UT " + Num(best.T) + ", needs " + Num(plan.DeltaV) + " m/s, more than the craft's " + Num(ask.MaxDv.Value) + " m/s");
            plan.SearchFrom = from;
            plan.Synodic = synodic;
            return plan;
        }

        /// <summary>Seconds between one departure window and the next for orbits of these periods; the longer period if they are the same.</summary>
        public static double Synodic(double a, double b)
        {
            var beat = Math.Abs(1 / a - 1 / b);
            return beat < 1e-12 ? Math.Max(a, b) : 1 / beat;
        }

        /// <summary>Why a target orbit round <paramref name="home"/> can't be transferred to, or null.</summary>
        private static string OrbitProblem(SnapshotOrbit o, SnapshotBody home)
        {
            if (!Finite(o.SemiMajorAxis) || !Finite(o.Eccentricity) || !Finite(o.Inclination) || !Finite(o.Lan) || !Finite(o.ArgumentOfPeriapsis)
                || !Finite(o.MeanAnomalyAtEpoch) || !Finite(o.Epoch))
                return "the target orbit's elements must be numbers";
            if (!(o.Eccentricity >= 0 && o.Eccentricity < 1) || !(o.SemiMajorAxis > 0)) return "the target orbit must be closed: a semi-major axis over 0 and an eccentricity from 0 to under 1";
            if (o.SemiMajorAxis * (1 - o.Eccentricity) <= home.Radius) return "the target orbit's periapsis is below " + home.Name + "'s surface";
            if (home.SoiRadius > 0 && o.SemiMajorAxis * (1 + o.Eccentricity) >= home.SoiRadius) return "the target orbit's apoapsis is outside " + home.Name + "'s sphere of influence";
            return null;
        }

        /// <summary>
        /// A rendezvous's window: a dip in the grid, the cheapest one inside the span, or else the one just past it (the span's
        /// start cut a window); with a latest UT the span may hold no dip (the cost still falling at its end): its cheapest departure, then.
        /// </summary>
        private static List<int> RendezvousPick(double[] cheapest, double step, double span, bool latest)
        {
            var count = cheapest.Length;
            int pick = -1, late = -1;
            for (var i = 1; i < count - 1; i++)
            {
                if (double.IsInfinity(cheapest[i]) || !(cheapest[i] < cheapest[i - 1] && cheapest[i] <= cheapest[i + 1])) continue;
                if ((i - 1) * step <= span)
                {
                    if (pick < 0 || cheapest[i] < cheapest[pick]) pick = i;
                }
                else if (late < 0 || cheapest[i] < cheapest[late]) late = i;
            }
            if (pick < 0 && !latest) pick = late;
            if (pick < 0 && latest)
                for (var i = 1; i < count - 1 && (i - 1) * step <= span; i++)
                    if (!double.IsInfinity(cheapest[i]) && (pick < 0 || cheapest[i] < cheapest[pick])) pick = i;
            return pick < 0 ? new List<int>() : new List<int> { pick };
        }

        /// <summary>
        /// An intercept's starts: the cheapest departure in the span, and the cheapest on the other side of the craft's orbit
        /// from it (a quarter to three quarters of an orbit away), which arrives on the other side of the target orbit: the
        /// two sides TwoImpulseTransfer tries, so one node is not taken for the other.
        /// </summary>
        private static List<int> InterceptPicks(double[] cheapest, double from, double step, double span, double period)
        {
            int pick = -1;
            for (var i = 1; i < cheapest.Length - 1 && (i - 1) * step <= span; i++)
                if (!double.IsInfinity(cheapest[i]) && (pick < 0 || cheapest[i] < cheapest[pick])) pick = i;
            if (pick < 0) return new List<int>();
            int other = -1;
            for (var i = 1; i < cheapest.Length - 1 && (i - 1) * step <= span; i++)
            {
                var phase = Math.Abs(Math.IEEERemainder((i - pick) * step / period, 1.0));
                if (phase >= 0.25 && !double.IsInfinity(cheapest[i]) && (other < 0 || cheapest[i] < cheapest[other])) other = i;
            }
            return other < 0 ? new List<int> { pick } : new List<int> { pick, other };
        }

        /// <summary>A departure time and flight time, with an intercept's arrival shift (rad from the point opposite the departure), and its cost.</summary>
        private sealed class Point
        {
            public double T;
            public double Tof;
            public double Shift;
            public double Cost;
        }

        /// <summary>The two burns of one Lambert arc, in the craft's body's frame, or why the arc has no path.</summary>
        private struct TwoBurns
        {
            public Vec3 Departure;
            /// <summary>The target's velocity at arrival less the arc's: for a body, its length is the speed the craft arrives at.</summary>
            public Vec3 Arrival;
            public string Error;
        }

        /// <summary>One transfer problem: the craft's orbit, the target's, how the arrival is costed, and the Lambert arc between them.</summary>
        private sealed class Problem
        {
            private readonly Conic _craft;
            private readonly Conic _target;
            private readonly SnapshotBody _body;
            private readonly double _peR;
            private readonly bool _capture;
            private readonly bool _intercept;

            /// <summary>Why the last Lambert solve that failed had no path, for the error when no departure has one.</summary>
            public string LambertError;

            public Problem(Conic craft, Conic target, SnapshotBody body, double peR, bool capture, bool intercept)
            {
                _craft = craft;
                _target = target;
                _body = body;
                _peR = peR;
                _capture = capture;
                _intercept = intercept;
            }

            public string Name
            {
                get { return _body != null ? _body.Name : "the target orbit"; }
            }

            /// <summary>The Hohmann time from the craft's radius at <paramref name="t"/> to the target's (an intercept's: opposite the departure).</summary>
            public double HohmannTime(double t)
            {
                var r1 = _craft.Radius(t);
                var r2 = _intercept ? ArrivalPoint(t, 0).Length : _target.A;
                var a = (r1 + r2) / 2;
                return Math.PI * Math.Sqrt(a * a * a / _craft.Mu);
            }

            /// <summary>
            /// Where the arc ends: a rendezvous at the target at <paramref name="t"/> + <paramref name="tof"/>; an intercept on the
            /// target orbit, <paramref name="shift"/> rad on from the point opposite the craft at <paramref name="t"/>, with its velocity there.
            /// </summary>
            private Vec3 ArrivalPoint(double t, double shift)
            {
                Vec3 r, v;
                ArrivalState(t, 0, shift, out r, out v);
                return r;
            }

            private void ArrivalState(double t, double tof, double shift, out Vec3 r, out Vec3 v)
            {
                if (!_intercept)
                {
                    _target.State(t + tof, out r, out v);
                    return;
                }
                var away = -1 * _craft.Position(t);
                var nu = Math.Atan2(Vec3.Dot(away, _target.Q), Vec3.Dot(away, _target.P));
                _target.StateAtTrue(nu + shift, out r, out v);
            }

            public TwoBurns Leg(double t, double tof, double shift)
            {
                Vec3 r1, vc, r2, vt;
                _craft.State(t, out r1, out vc);
                ArrivalState(t, tof, shift, out r2, out vt);
                var path = Lambert.Solve(_craft.Mu, r1, r2, tof, _craft.W);
                if (path.Error != null) return new TwoBurns { Error = path.Error };
                return new TwoBurns { Departure = path.V1 - vc, Arrival = vt - path.V2 };
            }

            /// <summary>
            /// The cost of a departure at <paramref name="t"/> taking <paramref name="tof"/> s: the departure burn, plus with capture
            /// the arrival burn (to a body, RSVP's circular capture at the target periapsis); infinity when there is no path.
            /// </summary>
            public double Cost(double t, double tof, double shift)
            {
                var leg = Leg(t, tof, shift);
                if (leg.Error != null)
                {
                    LambertError = leg.Error;
                    return double.PositiveInfinity;
                }
                var cost = leg.Departure.Length;
                if (!_capture) return cost;
                var arriving = leg.Arrival.Length;
                return _body == null
                    ? cost + arriving
                    : cost + Math.Sqrt(arriving * arriving + 2 * _body.Gm / _peR) - Math.Sqrt(_body.Gm / _peR);
            }

            /// <summary>
            /// BrentMin over the departure time in [<paramref name="lo"/>, <paramref name="hi"/>], each departure at its cheapest
            /// flight time within <paramref name="reach"/> s of the seed's (and an intercept's at its cheapest arrival point): the
            /// seed itself when that finds nothing cheaper. Times are searched in units of <paramref name="timeScale"/> s.
            /// </summary>
            public Point Refine(Point seed, double lo, double hi, double reach, double timeScale)
            {
                if (!(hi > lo)) lo = hi = seed.T;
                var tofLo = Math.Max(seed.Tof - reach, seed.Tof * 0.1);
                var tofHi = seed.Tof + reach;
                Func<double, Point> atTime = t =>
                {
                    if (!_intercept) return Flight(t, 0, tofLo, tofHi, timeScale);
                    var shift = Minimise(s => Flight(t, s, tofLo, tofHi, timeScale).Cost, -ArrivalReach, ArrivalReach, AngleTolerance);
                    return Flight(t, shift, tofLo, tofHi, timeScale);
                };
                var departure = seed.T;
                if (hi > lo) departure += timeScale * Minimise(x => atTime(seed.T + timeScale * x).Cost, (lo - seed.T) / timeScale, (hi - seed.T) / timeScale, TimeTolerance);
                var found = atTime(departure);
                return found.Cost < seed.Cost ? found : seed;
            }

            /// <summary>The cheapest flight time from <paramref name="t"/> in [<paramref name="tofLo"/>, <paramref name="tofHi"/>].</summary>
            private Point Flight(double t, double shift, double tofLo, double tofHi, double timeScale)
            {
                var mid = (tofLo + tofHi) / 2;
                var x = Minimise(y => Cost(t, mid + timeScale * y, shift), (tofLo - mid) / timeScale, (tofHi - mid) / timeScale, TimeTolerance);
                var tof = mid + timeScale * x;
                return new Point { T = t, Tof = tof, Shift = shift, Cost = Cost(t, tof, shift) };
            }

            /// <summary>BrentMin's minimiser of <paramref name="f"/> in [<paramref name="lo"/>, <paramref name="hi"/>], a point with no path costed <see cref="NoPath"/>.</summary>
            private static double Minimise(Func<double, double> f, double lo, double hi, double tol)
            {
                var found = BrentMin.Solve(x =>
                {
                    var c = f(x);
                    return Finite(c) ? c : NoPath;
                }, lo, hi, tol, RefineIterations);
                // Only a cap on iterations can stop it with a point (the bounds are checked and the cost is finite): its best so far.
                return Finite(found.X) ? found.X : (lo + hi) / 2;
            }
        }

        /// <summary>One trial of the burn on the patched conics: where it meets the target, or null when it doesn't go straight there.</summary>
        private sealed class Arrival
        {
            /// <summary>The prograde part of the burn tried, m/s.</summary>
            public double ProgradeDv;
            public double Pe;
            /// <summary>Whether the orbit round the target goes the way the target orbits.</summary>
            public bool OrbitsPrograde;
            public double EncounterUt;
            public double PeUt;
            /// <summary>The burn at periapsis that makes the orbit round the target circular, m/s.</summary>
            public double CaptureDv;
        }

        /// <summary>
        /// The Lambert burn aims at the target's centre, without its gravity: scans its prograde part each way on the patched
        /// conics until the periapsis at the target crosses the one asked for, bisects each crossing, and keeps the one that
        /// orbits the target prograde (else the smaller burn).
        /// </summary>
        private static TransferPlan Aim(PredictBurn.Setup setup, SnapshotBody target, Conic moon, double ut, double prograde, double normal, double radial, double goal)
        {
            var at = JsonReader.Parse(ut.ToString("R", CultureInfo.InvariantCulture));
            Func<double, Arrival> arrive = p =>
            {
                var requests = new List<PredictBurn.BurnRequest> { new PredictBurn.BurnRequest { At = at, Prograde = p, Normal = normal, Radial = radial } };
                List<PredictBurn.BurnDone> done;
                List<Patch> after;
                if (PredictBurn.Simulate(setup, requests, out done, out after, false) != null || after.Count < 2) return null;
                if (after[0].End != PatchEnd.Encounter || !string.Equals(after[0].Next.Name, target.Name, StringComparison.OrdinalIgnoreCase)) return null;
                var there = after[1].Orbit;
                var peUt = there.NextPeriapsis(after[1].StartUt) ?? after[1].StartUt;
                Vec3 rPe, vPe;
                there.State(peUt, out rPe, out vPe);
                return new Arrival
                {
                    ProgradeDv = p,
                    Pe = there.PeR - target.Radius,
                    OrbitsPrograde = Vec3.Dot(there.W, moon.W) > 0,
                    EncounterUt = after[0].EndUt,
                    PeUt = peUt,
                    CaptureDv = Math.Abs(vPe.Length - Math.Sqrt(target.Gm / rPe.Length)),
                };
            };

            // A burn that passes inside the periapsis asked for: the Lambert burn itself, or one a little either side of it.
            var start = arrive(prograde);
            var reached = start != null;
            double? inside = start != null && start.Pe < goal ? prograde : (double?)null;
            for (var k = 1; k <= ScanSteps && !inside.HasValue; k++)
            {
                foreach (var way in new[] { 1.0, -1.0 })
                {
                    var p = prograde + way * k * FineStep;
                    var got = arrive(p);
                    reached |= got != null;
                    if (got != null && got.Pe < goal)
                    {
                        inside = p;
                        break;
                    }
                }
            }
            if (!inside.HasValue)
                return Fail(reached
                    ? "the ejection at UT " + Num(ut) + " reaches " + target.Name + ", but no burn there passes as low as a periapsis of " + Num(goal) + " m"
                    : "the cheapest ejection, at UT " + Num(ut) + ", does not reach " + target.Name + "'s sphere of influence on the patched conics");

            // Out from it each way, the periapsis rises until the encounter is lost: the goal lies between.
            Arrival chosen = null;
            foreach (var way in new[] { 1.0, -1.0 })
            {
                var below = inside.Value;
                for (var k = 1; k <= ScanSteps; k++)
                {
                    var p = inside.Value + way * k * ScanStep;
                    var got = arrive(p);
                    if (got != null && got.Pe < goal)
                    {
                        below = p;
                        continue;
                    }
                    var found = Bisect(arrive, below, p, goal);
                    var better = found != null && (chosen == null || found.OrbitsPrograde && !chosen.OrbitsPrograde
                        || found.OrbitsPrograde == chosen.OrbitsPrograde && Math.Abs(found.ProgradeDv) < Math.Abs(chosen.ProgradeDv));
                    if (better) chosen = found;
                    break;
                }
            }
            return chosen != null
                ? Plan(ut, normal, radial, chosen)
                : Fail("the ejection at UT " + Num(ut) + " reaches " + target.Name + ", but no burn there arrives with a periapsis of " + Num(goal) + " m");
        }

        /// <summary>
        /// Narrows the prograde part between <paramref name="below"/> (periapsis under the goal) and <paramref name="above"/> (over
        /// it, or no encounter) to a periapsis within <see cref="PeClose"/> m of the goal; null when it can't get within <see cref="PeNear"/> m.
        /// </summary>
        private static Arrival Bisect(Func<double, Arrival> arrive, double below, double above, double goal)
        {
            Arrival nearest = null;
            for (var i = 0; i < Bisections; i++)
            {
                var mid = (below + above) / 2;
                var got = arrive(mid);
                if (got != null && (nearest == null || Math.Abs(got.Pe - goal) < Math.Abs(nearest.Pe - goal))) nearest = got;
                if (got != null && Math.Abs(got.Pe - goal) < PeClose) break;
                if (got != null && got.Pe < goal) below = mid;
                else above = mid;
            }
            return nearest != null && Math.Abs(nearest.Pe - goal) < PeNear ? nearest : null;
        }

        private static TransferPlan Plan(double ut, double normal, double radial, Arrival arrival)
        {
            var prograde = arrival.ProgradeDv;
            return new TransferPlan
            {
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = Math.Sqrt(prograde * prograde + normal * normal + radial * radial),
                ArrivalDv = arrival.CaptureDv,
                EncounterUt = arrival.EncounterUt,
                ArrivalUt = arrival.PeUt,
                PeActual = arrival.Pe,
            };
        }

        /// <summary>No node: "found no transfer: <paramref name="why"/>".</summary>
        public static TransferPlan Fail(string why)
        {
            return new TransferPlan { Error = Failure + ": " + why };
        }

        private static bool Finite(double x)
        {
            return SearchInput.Finite(x);
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
