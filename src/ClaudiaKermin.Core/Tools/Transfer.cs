/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// The two-impulse formulation (the Lambert arc's two burns, the cost with and without capture, the offset held at 0 for a
// rendezvous and both sides of the target orbit tried for an intercept) is ported from MechJeb2's
// MechJebLib/Maneuvers/TwoImpulseTransfer.cs (https://github.com/MuMech/MechJeb2, commit a958383), without its alglib call.

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
        /// <summary>False: only the one-burn plan, as before #619 (the after-a-miss correction, which flies one burn).</summary>
        public bool BrokenPlane = true;
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
        /// <summary>The cheapest window's departure UT when the patched conics couldn't aim it and this is a later one; null when this is the cheapest.</summary>
        public double? CheapestUt;
        /// <summary>
        /// A broken-plane plan's plane change (#619): its UT, null for a one-burn plan; its node components and delta-v, m/s.
        /// The departure is then in the craft's plane and reaches the target only with it.
        /// </summary>
        public double? PlaneUt;
        public double PlanePrograde;
        public double PlaneNormal;
        public double PlaneRadial;
        public double PlaneDv;

        /// <summary>What the plan costs: the departure (and a broken plane's plane change), plus with capture the arrival burn.</summary>
        public double Total(bool capture)
        {
            return DeltaV + PlaneDv + (capture ? ArrivalDv : 0);
        }
    }

    /// <summary>
    /// The cheapest two-impulse transfer within the craft's sphere of influence (spec #435 Phase 2, #448), generalised from
    /// <c>transfer_to</c>'s ejection burn (spec #314), with the formulation of MechJebLib's <c>Maneuvers/TwoImpulseTransfer.cs</c>
    /// (MechJeb2 a958383): a Lambert arc from the craft at the departure time to the target after the flight time, costed as
    /// the departure burn alone or, with capture, plus the arrival burn. A rendezvous meets the target where it is (the offset
    /// held at 0); an intercept reaches its orbit anywhere and tries both sides of it, so the descending node is not taken for
    /// the ascending one. In place of MechJeb's alglib call, a grid over departure time and flight time finds the window and
    /// <see cref="BrentMin"/> refines it, over the departure time with the flight time (and an intercept's arrival point)
    /// minimised inside; an arc that dips into the atmosphere or the ground has no path. To a body, <see cref="PredictBurn"/>'s
    /// propagation, as <c>solve_burn</c> uses it, then sizes the prograde part of the burn (moving the normal part when that
    /// alone can't) so the patched conics arrive at the periapsis asked for, on a prograde orbit round the target where one
    /// does; when it can't, the grid's next windows are tried, cheapest first (#611). A body's capture is costed as RSVP
    /// costs it, a circular orbit at that periapsis. It works on the Game
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
        /// <summary>
        /// An intercept's refinement scans, before BrentMin narrows the cheapest: departures across its span, arrival points round
        /// the whole target orbit, and flight times to each.
        /// </summary>
        private const int DepartureScans = 13;
        private const int ArrivalScans = 24;
        private const int FlightScans = 5;
        /// <summary>The cost of a point with no Lambert path: finite, so BrentMin steps away from it instead of stopping.</summary>
        private const double NoPath = 1e30;
        /// <summary>The bisection on the prograde part: it stops within <see cref="PeClose"/> m of target_pe, and fails beyond <see cref="PeNear"/> m.</summary>
        private const double PeClose = 10;
        private const double PeNear = 100;
        private const int Bisections = 60;
        /// <summary>
        /// When no prograde part passes inside the goal, the normal part is moved <see cref="NormalStep"/> m/s each way, doubled
        /// <see cref="NormalScans"/> times, and the fine prograde scan run again at each, <see cref="NormalScanSteps"/> each way.
        /// </summary>
        private const double NormalStep = 1;
        private const int NormalScans = 3;
        private const int NormalScanSteps = 20;
        /// <summary>A body's windows tried, cheapest first, before the search gives up.</summary>
        private const int Windows = 3;
        /// <summary>From this relative inclination, deg, a body no window reaches is put down to its orbit's tilt.</summary>
        private const double InclinedDegrees = 1;
        /// <summary>The plane change's time on the in-plane arc, as a fraction of its flight time: searched between these.</summary>
        private const double PlaneLow = 0.1;
        private const double PlaneHigh = 0.99;
        private const double PlaneTolerance = 1e-4;

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
            Conic targetOrbit;
            double peR = 0;
            if (body != null)
            {
                if (!string.Equals(body.Parent, home.Name, StringComparison.OrdinalIgnoreCase))
                    return Fail(body.Name + " does not orbit " + home.Name + ", the body the craft orbits");
                if (ask.Mode != TransferMode.Rendezvous) return Fail("intercept is for a target orbit; a body is met where it is");
                var ceiling = body.SoiRadius - body.Radius;
                if (!(ask.TargetPe >= 0 && ask.TargetPe < ceiling))
                    return Fail("target_pe must be from 0 to " + Num(ceiling) + " m, inside " + body.Name + "'s sphere of influence");
                targetOrbit = setup.System.OrbitOf(body);
                peR = body.Radius + ask.TargetPe;
            }
            else
            {
                var orbitError = OrbitProblem(ask.TargetOrbit, home);
                if (orbitError != null) return Fail(orbitError);
                var o = ask.TargetOrbit;
                targetOrbit =Conic.FromElements(home.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
            }

            var craft = setup.Start;
            var intercept = ask.Mode == TransferMode.Intercept;
            var synodic = Synodic(craft.Period, targetOrbit.Period);
            var from = Math.Max(setup.Now, Math.Max(ask.EarliestUt, ask.WindowUt ?? double.NegativeInfinity));
            // A latest UT shortens the span, and no window past it may be taken.
            var until = ask.LatestUt ?? double.PositiveInfinity;
            if (until <= from)
                return Fail("the burn must come by UT " + Num(until) + ", before the earliest it can be, UT " + Num(from));
            // With the arrival point free, every departure point comes round once an orbit of the craft.
            var span = Math.Min(intercept ? craft.Period : synodic, until - from);
            var transfer = new Problem(craft, targetOrbit, body, peR, ask.Capture, intercept, Math.Min(home.Radius + home.AtmosphereHeight, craft.PeR));

            // The grid (DepartureGrid), shared with BrokenPlane.
            var grid = DepartureGrid.Build(transfer, from, span);
            var step = grid.Step;
            var count = grid.Count;
            double[] cheapest = grid.Cheapest, flight = grid.Flight, hohmann = grid.Hohmann;
            var picks = intercept ? InterceptPicks(cheapest, step, span, craft.Period) : RendezvousPicks(cheapest, step, span, ask.LatestUt.HasValue);
            if (picks.Count == 0)
                return Fail("no departure in the " + Num(span) + " s from UT " + Num(from) + " has a path to " + transfer.Name
                    + (transfer.LambertError != null ? " (Lambert: " + transfer.LambertError + ")" : ""));

            // BrentMin from a pick, never before the start or past the grid's end (or the latest UT): a step either side, or
            // for an intercept a quarter orbit, so its two sides cover the orbit (the grid's arrival opposite the departure finds
            // only the nodes of an inclined target, where the cheapest departure may be up to a quarter orbit away).
            var last = Math.Min(from + (count - 2) * step, until);
            var departureReach = intercept ? craft.Period / 4 : step;
            var scale = Scale.Create(home.Gm, Math.Sqrt(craft.A * targetOrbit.A));
            Func<int, Point> refine = pick =>
            {
                var seed = new Point { T = from + (pick - 1) * step, Tof = flight[pick], Cost = cheapest[pick] };
                return transfer.Refine(seed, Math.Max(from, seed.T - departureReach), Math.Min(last, seed.T + departureReach),
                    FlightReach * hohmann[pick] * (FlightHigh - FlightLow) / (Flights - 1), scale.TimeScale);
            };
            // An intercept's two sides: the cheaper wins. A body's windows, cheapest first: the first the patched conics can aim wins.
            Point cheapestWindow = null;
            if (intercept)
                foreach (var pick in picks)
                {
                    var found = refine(pick);
                    if (cheapestWindow == null || found.Cost < cheapestWindow.Cost) cheapestWindow = found;
                }
            TransferPlan plan = null, failed = null;
            var reached = false;
            for (var n = 0; n < (intercept ? 1 : picks.Count) && plan == null; n++)
            {
                var window = intercept ? cheapestWindow : refine(picks[n]);
                if (n == 0) cheapestWindow = window;
                bool got;
                var tried = Depart(setup, transfer, body, targetOrbit, window, ask.TargetPe, out got);
                reached |= got;
                if (tried.Error == null) plan = tried;
                else if (failed == null) failed = tried;
                if (plan != null && n > 0) plan.CheapestUt = cheapestWindow.T;
            }
            // To a body, the broken-plane plan is costed beside the one-burn one (#619), near the craft's plane too (#653): there
            // the one-burn arc, near 180°, can take a large normal and radial part, and the cheaper total wins.
            if (body != null && ask.BrokenPlane)
            {
                var broken = BrokenPlane(setup, craft, targetOrbit, body, peR, ask, from, span, transfer.Floor);
                if (broken != null && (plan == null || broken.Total(ask.Capture) < plan.Total(ask.Capture))) plan = broken;
            }
            if (plan == null)
            {
                var inclined = Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(craft.W, targetOrbit.W)))) * 180 / Math.PI;
                return body != null && !reached && inclined >= InclinedDegrees
                    ? Fail(body.Name + "'s orbit is inclined " + inclined.ToString("0.0", CultureInfo.InvariantCulture) + "° to yours; match_plane first, or try the next window")
                    : failed;
            }
            if (plan.DeltaV + plan.PlaneDv > ask.MaxDv)
                return Fail((plan.CheapestUt.HasValue ? "the burn" : "the cheapest burn") + ", at UT " + Num(plan.Ut) + ", needs " + Num(plan.DeltaV + plan.PlaneDv) + " m/s, more than the craft's " + Num(ask.MaxDv.Value) + " m/s");
            plan.SearchFrom = from;
            plan.Synodic = synodic;
            return plan;
        }

        /// <summary>
        /// The departure grid both <see cref="Find"/> and <see cref="BrokenPlane"/> search: a departure every
        /// <paramref name="span"/> / Seeds from one step before <paramref name="from"/> (so a window just before it is a falling
        /// edge, not a minimum) to a quarter span past the end, each at its cheapest time of flight. Build it for any Problem.
        /// </summary>
        private sealed class DepartureGrid
        {
            public double Step;
            public int Count;
            public double[] Cheapest, Flight, Hohmann;

            public static DepartureGrid Build(Problem problem, double from, double span)
            {
                var g = new DepartureGrid { Step = span / Seeds, Count = Seeds + Seeds / 4 + 2 };
                g.Cheapest = new double[g.Count];
                g.Flight = new double[g.Count];
                g.Hohmann = new double[g.Count];
                for (var i = 0; i < g.Count; i++)
                {
                    var t = from + (i - 1) * g.Step;
                    g.Hohmann[i] = problem.HohmannTime(t);
                    g.Cheapest[i] = double.PositiveInfinity;
                    for (var j = 0; j < Flights; j++)
                    {
                        var tof = g.Hohmann[i] * (FlightLow + (FlightHigh - FlightLow) * j / (Flights - 1));
                        var c = problem.Cost(t, tof, 0);
                        if (c < g.Cheapest[i])
                        {
                            g.Cheapest[i] = c;
                            g.Flight[i] = tof;
                        }
                    }
                }
                return g;
            }
        }

        /// <summary>
        /// The departure node at <paramref name="window"/>: the Lambert burn, aimed on the patched conics to a body;
        /// <paramref name="reached"/> is whether any burn tried there met it.
        /// </summary>
        private static TransferPlan Depart(PredictBurn.Setup setup, Problem transfer, SnapshotBody body, Conic targetOrbit, Point window, double targetPe, out bool reached)
        {
            reached = false;
            var leg = transfer.Leg(window.T, window.Tof, window.Shift);
            if (leg.Error != null) return Fail("the cheapest departure, at UT " + Num(window.T) + ", has no Lambert path: " + leg.Error);
            Vec3 r, v;
            setup.Start.State(window.T, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            double prograde = Vec3.Dot(leg.Departure, along), normal = Vec3.Dot(leg.Departure, up), radial = Vec3.Dot(leg.Departure, outward);
            if (body != null) return Aim(setup, body, targetOrbit, window.T, prograde, normal, radial, targetPe, out reached);
            return new TransferPlan
            {
                Ut = window.T, Prograde = prograde, Normal = normal, Radial = radial, DeltaV = leg.Departure.Length,
                ArrivalDv = leg.Arrival.Length, EncounterUt = window.T + window.Tof, ArrivalUt = window.T + window.Tof,
            };
        }

        /// <summary>
        /// The broken-plane plan to a body (#619), or null when there is none: the departure in the craft's plane, to where the
        /// body would arrive with its orbit turned into that plane about the line of nodes; then, where the arc is slow, the
        /// plane change to the body itself; then the arrival burn. The grid and refinement are the one-burn plan's, on the
        /// turned orbit, and only its cheapest window is tried; the plane change's time is the one that costs least on two
        /// Lambert arcs; and <see cref="CorrectionBurn"/>, from the Lambert burn to the body's centre, sizes it on the patched
        /// conics to arrive at target_pe, as <c>transfer_to</c> flies it.
        /// </summary>
        private static TransferPlan BrokenPlane(PredictBurn.Setup setup, Conic craft, Conic targetOrbit, SnapshotBody body, double peR, TransferAsk ask,
            double from, double span, double floor)
        {
            var mu = craft.Mu;
            var flat = new Problem(craft, Turned(targetOrbit, craft.W, from), body, peR, ask.Capture, false, floor);
            var grid = DepartureGrid.Build(flat, from, span);
            var step = grid.Step;
            var count = grid.Count;
            double[] cheapest = grid.Cheapest, flight = grid.Flight, hohmann = grid.Hohmann;
            var picks = RendezvousPicks(cheapest, step, span, ask.LatestUt.HasValue);
            if (picks.Count == 0) return null;
            var pick = picks[0];
            var last = Math.Min(from + (count - 2) * step, ask.LatestUt ?? double.PositiveInfinity);
            var seed = new Point { T = from + (pick - 1) * step, Tof = flight[pick], Cost = cheapest[pick] };
            var window = flat.Refine(seed, Math.Max(from, seed.T - step), Math.Min(last, seed.T + step),
                FlightReach * hohmann[pick] * (FlightHigh - FlightLow) / (Flights - 1), Scale.Create(mu, Math.Sqrt(craft.A * targetOrbit.A)).TimeScale);
            var leg = flat.Leg(window.T, window.Tof, 0);
            if (leg.Error != null) return null;

            // The arc out, and where on it the plane change costs least (with capture, with the arrival burn it leaves).
            Vec3 r1, v1;
            craft.State(window.T, out r1, out v1);
            var arc = Conic.FromState(mu, r1, v1 + leg.Departure, window.T);
            var arriveUt = window.T + window.Tof;
            Vec3 meet, vt;
            targetOrbit.State(arriveUt, out meet, out vt);
            Func<double, double> planeCost = f =>
            {
                Vec3 rm, vm;
                var ut = window.T + f * window.Tof;
                arc.State(ut, out rm, out vm);
                var second = Lambert.Solve(mu, rm, meet, arriveUt - ut, arc.W);
                if (second.Error != null) return NoPath;
                var arriving = (vt - second.V2).Length;
                return (second.V1 - vm).Length + (ask.Capture ? Math.Sqrt(arriving * arriving + 2 * body.Gm / peR) - Math.Sqrt(body.Gm / peR) : 0);
            };
            var found = BrentMin.Solve(planeCost, PlaneLow, PlaneHigh, PlaneTolerance, RefineIterations);
            var planeUt = window.T + (Finite(found.X) ? found.X : (PlaneLow + PlaneHigh) / 2) * window.Tof;

            var change = CorrectionBurn.Find(setup.System, setup.Body, arc, window.T, new CorrectionAsk
            {
                Target = body.Name, TargetPe = ask.TargetPe, EarliestUt = planeUt, LatestUt = planeUt, PeTolerance = PeNear, AimUt = arriveUt,
            });
            if (!change.HasBurn || change.Error != null) return null;

            double prograde, normal, radial;
            SingleShooting.NodeFrame(craft, window.T, leg.Departure, out prograde, out normal, out radial);
            var reached = Reach(setup, body, targetOrbit, new List<PredictBurn.BurnRequest>
            {
                new PredictBurn.BurnRequest { At = At(window.T), Prograde = prograde, Normal = normal, Radial = radial },
                new PredictBurn.BurnRequest { At = At(planeUt), Prograde = change.Prograde, Normal = change.Normal, Radial = change.Radial },
            });
            if (reached == null || Math.Abs(reached.Pe - ask.TargetPe) >= PeNear) return null;
            return new TransferPlan
            {
                Ut = window.T, Prograde = prograde, Normal = normal, Radial = radial, DeltaV = leg.Departure.Length,
                ArrivalDv = reached.CaptureDv, EncounterUt = reached.EncounterUt, ArrivalUt = reached.PeUt, PeActual = reached.Pe,
                PlaneUt = planeUt, PlanePrograde = change.Prograde, PlaneNormal = change.Normal, PlaneRadial = change.Radial, PlaneDv = change.DeltaV,
            };
        }

        /// <summary><paramref name="orbit"/> turned about its line of nodes with the plane of normal <paramref name="normal"/> into that plane (its state at <paramref name="ut"/>, rotated).</summary>
        private static Conic Turned(Conic orbit, Vec3 normal, double ut)
        {
            var axis = Vec3.Cross(orbit.W, normal);
            var sin = axis.Length;
            if (sin < 1e-12) return orbit;
            var cos = Vec3.Dot(orbit.W, normal);
            var k = axis / sin;
            Func<Vec3, Vec3> turn = x => x * cos + Vec3.Cross(k, x) * sin + k * (Vec3.Dot(k, x) * (1 - cos));
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            return Conic.FromState(orbit.Mu, turn(r), turn(v), ut);
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
        /// A rendezvous's windows: first a dip in the grid, the cheapest one inside the span, or else the one just past it (the
        /// span's start cut a window); with a latest UT the span may hold no dip (the cost still falling at its end): its cheapest
        /// departure, then. After it, the grid's other dips, cheapest first (none past a latest UT), up to <see cref="Windows"/> in all.
        /// </summary>
        private static List<int> RendezvousPicks(double[] cheapest, double step, double span, bool latest)
        {
            var count = cheapest.Length;
            int pick = -1, late = -1;
            var dips = new List<int>();
            for (var i = 1; i < count - 1; i++)
            {
                if (double.IsInfinity(cheapest[i]) || !(cheapest[i] < cheapest[i - 1] && cheapest[i] <= cheapest[i + 1])) continue;
                if ((i - 1) * step <= span)
                {
                    if (pick < 0 || cheapest[i] < cheapest[pick]) pick = i;
                }
                else if (late < 0 || cheapest[i] < cheapest[late]) late = i;
                if (!latest || (i - 1) * step <= span) dips.Add(i);
            }
            if (pick < 0 && !latest) pick = late;
            if (pick < 0 && latest) pick = CheapestInSpan(cheapest, step, span, i => true);
            if (pick < 0) return new List<int>();
            dips.Remove(pick);
            dips.Sort((a, b) => cheapest[a].CompareTo(cheapest[b]));
            dips.Insert(0, pick);
            return dips.GetRange(0, Math.Min(Windows, dips.Count));
        }

        /// <summary>
        /// An intercept's starts: the cheapest departure in the span, and the cheapest on the other side of the craft's orbit
        /// from it (a quarter to three quarters of an orbit away), which arrives on the other side of the target orbit: the
        /// two sides TwoImpulseTransfer tries, so one node is not taken for the other.
        /// </summary>
        private static List<int> InterceptPicks(double[] cheapest, double step, double span, double period)
        {
            var pick = CheapestInSpan(cheapest, step, span, i => true);
            if (pick < 0) return new List<int>();
            var other = CheapestInSpan(cheapest, step, span, i => Math.Abs(Math.IEEERemainder((i - pick) * step / period, 1.0)) >= 0.25);
            return other < 0 ? new List<int> { pick } : new List<int> { pick, other };
        }

        /// <summary>The grid's cheapest departure with a path inside the span, of those <paramref name="allowed"/>; -1 for none.</summary>
        private static int CheapestInSpan(double[] cheapest, double step, double span, Func<int, bool> allowed)
        {
            var pick = -1;
            for (var i = 1; i < cheapest.Length - 1 && (i - 1) * step <= span; i++)
                if (!double.IsInfinity(cheapest[i]) && allowed(i) && (pick < 0 || cheapest[i] < cheapest[pick])) pick = i;
            return pick;
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
            /// <summary>The lowest radius an arc may pass periapsis at: the top of the atmosphere, or the craft's periapsis if lower.</summary>
            private readonly double _floor;

            /// <summary>Why the last Lambert solve that failed had no path, for the error when no departure has one.</summary>
            public string LambertError;

            /// <summary>The lowest radius an arc may pass periapsis at, for a second problem over the same craft orbit (<see cref="BrokenPlane"/>).</summary>
            public double Floor
            {
                get { return _floor; }
            }

            public Problem(Conic craft, Conic target, SnapshotBody body, double peR, bool capture, bool intercept, double floor)
            {
                _craft = craft;
                _target = target;
                _body = body;
                _peR = peR;
                _capture = capture;
                _intercept = intercept;
                _floor = floor;
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

            /// <summary>Where an arc departing at <paramref name="t"/> with no flight time would end (<see cref="ArrivalState"/>): for the Hohmann time's radius.</summary>
            private Vec3 ArrivalPoint(double t, double shift)
            {
                Vec3 r, v;
                ArrivalState(t, 0, shift, out r, out v);
                return r;
            }

            /// <summary>
            /// Where the arc ends: a rendezvous at the target at <paramref name="t"/> + <paramref name="tof"/>; an intercept on the
            /// target orbit, <paramref name="shift"/> rad on from the point opposite the craft at <paramref name="t"/>, with its velocity there.
            /// </summary>
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
                if (Dips(r1, path.V1, t, tof)) return new TwoBurns { Error = "the arc dips into the atmosphere or the ground" };
                return new TwoBurns { Departure = path.V1 - vc, Arrival = vt - path.V2 };
            }

            /// <summary>Whether the arc leaving <paramref name="r"/> at <paramref name="v"/> passes its periapsis below the floor within <paramref name="tof"/> s.</summary>
            private bool Dips(Vec3 r, Vec3 v, double t, double tof)
            {
                var mu = _craft.Mu;
                var h = Vec3.Cross(r, v).Length;
                // Straight up and down (an intercept's arrival point over the departure): it falls back through the floor.
                if (h < 1e-9 * r.Length * v.Length) return true;
                var energy = Vec3.Dot(v, v) / 2 - mu / r.Length;
                var e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h * h / (mu * mu)));
                var pe = h * h / mu / (1 + e);
                if (pe >= _floor) return false;
                var arc = Conic.FromState(mu, r, v, t);
                var at = arc.NextPeriapsis(t);
                return at.HasValue && at.Value <= t + tof;
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
            /// flight time within <paramref name="reach"/> s of the seed's: the seed itself when that finds nothing cheaper. An
            /// intercept instead scans that span and the whole target orbit for its cheapest pair of departure and arrival point
            /// (<see cref="InterceptScan"/>), then searches round it, each arrival point at its own flight times. Times are
            /// searched in units of <paramref name="timeScale"/> s.
            /// </summary>
            public Point Refine(Point seed, double lo, double hi, double reach, double timeScale)
            {
                if (!(hi > lo)) lo = hi = seed.T;
                var centre = seed.T;
                Func<double, Point> atTime;
                if (!_intercept)
                {
                    var tofLo = Math.Max(seed.Tof - reach, seed.Tof * 0.1);
                    var tofHi = seed.Tof + reach;
                    atTime = t => Flight(t, 0, tofLo, tofHi, timeScale);
                }
                else
                {
                    // BrentMin is a local search: it starts from the scan, not from the grid's arrival opposite the departure.
                    var start = InterceptScan(lo, hi);
                    if (start == null) return seed;
                    var dt = (hi - lo) / (DepartureScans - 1);
                    // The cheapest arrival point moves with the departure, up to as far: two arrival steps hold it a departure step away.
                    var ds = 2 * 2 * Math.PI / ArrivalScans;
                    centre = start.T;
                    lo = Math.Max(lo, centre - dt);
                    hi = Math.Min(hi, centre + dt);
                    atTime = t => InterceptFlight(t, Minimise(s => InterceptFlight(t, s, timeScale).Cost, start.Shift - ds, start.Shift + ds, AngleTolerance), timeScale);
                }
                var departure = centre;
                if (hi > lo) departure += timeScale * Minimise(x => atTime(centre + timeScale * x).Cost, (lo - centre) / timeScale, (hi - centre) / timeScale, TimeTolerance);
                var found = atTime(departure);
                return found.Cost < seed.Cost ? found : seed;
            }

            /// <summary>
            /// An intercept's coarse scan: <see cref="DepartureScans"/> departures across [<paramref name="lo"/>, <paramref name="hi"/>],
            /// each to <see cref="ArrivalScans"/> arrival points round the target orbit at <see cref="FlightScans"/> flight times;
            /// the cheapest, or null when none has a path.
            /// </summary>
            private Point InterceptScan(double lo, double hi)
            {
                Point best = null;
                for (var i = 0; i < DepartureScans; i++)
                {
                    var t = lo + (hi - lo) * i / (DepartureScans - 1);
                    for (var k = 0; k < ArrivalScans; k++)
                    {
                        var shift = 2 * Math.PI * k / ArrivalScans;
                        double tofLo, tofHi;
                        InterceptFlights(t, shift, out tofLo, out tofHi);
                        for (var j = 0; j < FlightScans; j++)
                        {
                            var tof = tofLo + (tofHi - tofLo) * j / (FlightScans - 1);
                            var c = Cost(t, tof, shift);
                            if (best == null || c < best.Cost) best = new Point { T = t, Tof = tof, Shift = shift, Cost = c };
                        }
                    }
                    if (!(hi > lo)) break;
                }
                return best != null && Finite(best.Cost) ? best : null;
            }

            /// <summary>An intercept's cheapest flight time from <paramref name="t"/> to the arrival point <paramref name="shift"/> rad on, within <see cref="InterceptFlights"/>.</summary>
            private Point InterceptFlight(double t, double shift, double timeScale)
            {
                double tofLo, tofHi;
                InterceptFlights(t, shift, out tofLo, out tofHi);
                return Flight(t, shift, tofLo, tofHi, timeScale);
            }

            /// <summary>
            /// The flight times an intercept tries from <paramref name="t"/> to the arrival point <paramref name="shift"/> rad on: the
            /// grid's range of the Hohmann time to that point's radius, scaled by the angle the arc sweeps (half a turn opposite).
            /// </summary>
            private void InterceptFlights(double t, double shift, out double tofLo, out double tofHi)
            {
                var r1 = _craft.Position(t);
                var r2 = ArrivalPoint(t, shift);
                var swept = Math.Atan2(Vec3.Dot(Vec3.Cross(r1, r2), _craft.W), Vec3.Dot(r1, r2));
                if (swept <= 0) swept += 2 * Math.PI;
                var a = (r1.Length + r2.Length) / 2;
                var hohmann = Math.Sqrt(a * a * a / _craft.Mu) * swept;
                tofLo = FlightLow * hohmann;
                tofHi = FlightHigh * hohmann;
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
        /// orbits the target prograde (else the smaller burn). When no prograde part passes inside that periapsis, the normal
        /// part is moved a little each way and the scan run again (#611). <paramref name="reached"/> is whether any burn tried met the target.
        /// </summary>
        private static TransferPlan Aim(PredictBurn.Setup setup, SnapshotBody target, Conic targetOrbit, double ut, double prograde, double normal, double radial, double goal, out bool reached)
        {
            var at = At(ut);
            Func<double, Arrival> arrive = p =>
            {
                var got = Reach(setup, target, targetOrbit, new List<PredictBurn.BurnRequest> { new PredictBurn.BurnRequest { At = at, Prograde = p, Normal = normal, Radial = radial } });
                if (got != null) got.ProgradeDv = p;
                return got;
            };

            // A burn that passes inside the periapsis asked for: the Lambert burn itself, or one a little either side of it; else
            // the same with the normal part moved, nearest first, keeping the normal part it was found at.
            var lambertNormal = normal;
            reached = false;
            double? inside = null;
            for (var j = 0; j <= 2 * NormalScans && !inside.HasValue; j++)
            {
                normal = lambertNormal + (j == 0 ? 0 : (j % 2 == 1 ? 1 : -1) * NormalStep * Math.Pow(2, (j - 1) / 2));
                var start = arrive(prograde);
                reached |= start != null;
                if (start != null && start.Pe < goal) inside = prograde;
                for (var k = 1; k <= (j == 0 ? ScanSteps : NormalScanSteps) && !inside.HasValue; k++)
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

        /// <summary>Where the burns take the craft on the patched conics when the last goes straight into <paramref name="target"/>'s sphere of influence; else null.</summary>
        private static Arrival Reach(PredictBurn.Setup setup, SnapshotBody target, Conic targetOrbit, List<PredictBurn.BurnRequest> requests)
        {
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
                Pe = there.PeR - target.Radius,
                OrbitsPrograde = Vec3.Dot(there.W, targetOrbit.W) > 0,
                EncounterUt = after[0].EndUt,
                PeUt = peUt,
                CaptureDv = Math.Abs(vPe.Length - Math.Sqrt(target.Gm / rPe.Length)),
            };
        }

        /// <summary>A UT as predict_burn's <c>at</c> takes it.</summary>
        private static JsonNode At(double ut)
        {
            return JsonReader.Parse(ut.ToString("R", CultureInfo.InvariantCulture));
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
