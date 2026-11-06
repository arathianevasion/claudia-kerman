// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// The grid over departure and arrival and its costing follow MechJeb2's TransferCalculator.cs (GPL-3,
// https://github.com/MuMech/MechJeb2, commit a958383) without its UI or threads; the ejection from the parking orbit is the
// single-impulse hyperbolic burn of MechJebLib/Maneuvers/SingleImpulseHyperbolicBurn.cs (MIT) at the parking orbit's periapsis.

using System;
using System.Globalization;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>One Transfer Window: when to leave and arrive, and what it costs.</summary>
    internal sealed class TransferWindow
    {
        /// <summary>When the ejection burn is made, and when the craft reaches the target, UT s.</summary>
        public double DepartureUt;
        public double ArrivalUt;
        /// <summary>The burn from the parking orbit onto the escape hyperbola, m/s.</summary>
        public double EjectionDv;
        /// <summary>The speed relative to the target as the craft arrives in its sphere of influence, m/s.</summary>
        public double ArrivalVInf;
        /// <summary>The burn at target_pe from the arrival hyperbola into a circular orbit there, m/s; 0 without capture.</summary>
        public double CaptureDv;
        /// <summary>Ejection plus capture, or the ejection alone without capture.</summary>
        public double TotalDv;
    }

    /// <summary>The Soonest and Optimal windows to a planet, or why there are none (<see cref="Error"/>, with the rest unset).</summary>
    internal sealed class WindowSearch
    {
        /// <summary>Null when windows were found; otherwise "found no transfer window: why".</summary>
        public string Error;
        /// <summary>The target planet's name as the body table has it.</summary>
        public string Target;
        /// <summary>The earliest window within the Craft Check margin of <see cref="Optimal"/>'s total; Optimal itself when none is earlier.</summary>
        public TransferWindow Soonest;
        /// <summary>The cheapest window departing within the horizon.</summary>
        public TransferWindow Optimal;
        /// <summary>The horizon searched: from <see cref="From"/> (UT s) for <see cref="Synodic"/>, one synodic period (s) of the two planets.</summary>
        public double From;
        public double Synodic;
        /// <summary>The parking orbit's periapsis altitude (m) the ejection is costed from, and whether it was assumed (no closed orbit round the planet).</summary>
        public double ParkingPe;
        public bool ParkingAssumed;
    }

    /// <summary>
    /// The Transfer Windows from the planet the craft orbits to another planet of the same star (spec #435 Phase 4, #458): a
    /// heliocentric Lambert grid over departure time and flight time, each cell costed as the ejection from the parking orbit plus,
    /// with capture, the capture into a circular orbit at target_pe; <see cref="BrentMin"/> refines the cheapest. Two answers:
    /// <b>Optimal</b>, the cheapest window within one synodic period; <b>Soonest</b>, the earliest departure whose total fits the
    /// Craft Check's margin over Optimal's (the Craft Check budgets a leg at its best figure plus the margin). The Mission Setting
    /// Time versus delta-v picks one (<see cref="Pick"/>). Patched conics at the window level: the planets are points, the
    /// ejection is the Oberth burn at the parking orbit's periapsis in the escape direction's plane, so a parking orbit tilted
    /// from that direction costs more than this says. <see cref="Problem.Of"/> copies what it needs from the Game Snapshot
    /// before the search starts, so the search never reads the snapshot (it runs on a tool thread, ToolStep.cs).
    /// </summary>
    internal static class TransferWindows
    {
        private const string Failure = "found no transfer window";

        /// <summary>
        /// The wall-clock budget of one search, ms, well inside ToolStep's 5 s cap (the Mission request runs it for each planet
        /// its goal names). The grid is sized from it: <see cref="Departures"/> x <see cref="Flights"/> Lambert solves, at about
        /// 2 µs each, plus the refinements' few thousand, is about a tenth of it, leaving the rest for a slow or loaded machine.
        /// </summary>
        public const double BudgetMs = 250;

        /// <summary>Departures tried across the synodic period: a few days apart Kerbin to Duna, where a window is weeks wide.</summary>
        private const int Departures = 180;
        /// <summary>Flight times tried per departure, from <see cref="FlightLow"/> to <see cref="FlightHigh"/> of the Hohmann time.</summary>
        private const int Flights = 41;
        private const double FlightLow = 0.5;
        private const double FlightHigh = 1.5;
        /// <summary>BrentMin's tolerance in grid steps, its iteration cap, and the bisection count for Soonest's edge.</summary>
        private const double StepTolerance = 1e-4;
        private const int Iterations = 60;
        private const int Bisections = 40;
        /// <summary>The cost of a cell with no Lambert path: finite, so BrentMin steps away from it.</summary>
        private const double NoPath = 1e30;
        /// <summary>Balanced takes Optimal only when it saves at least this fraction of Soonest's total.</summary>
        public const double BalancedSaving = 0.10;

        /// <summary>
        /// The windows from the craft's planet to <paramref name="target"/>, arriving at <paramref name="targetPe"/> (m above its sea
        /// level), with or without the capture, Soonest within <paramref name="marginPercent"/> % of Optimal.
        /// </summary>
        public static WindowSearch Find(GameSnapshot snapshot, string target, double targetPe, bool capture, int marginPercent)
        {
            string error;
            var problem = Problem.Of(snapshot, target, targetPe, capture, out error);
            return problem == null ? Fail(error) : Search(problem, marginPercent);
        }

        /// <summary>Whether <paramref name="target"/> is a planet the craft's planet can transfer to: another body orbiting the same star.</summary>
        public static bool IsPlanetTransfer(GameSnapshot snapshot, string target)
        {
            var system = new BodySystem(snapshot.Bodies);
            var home = system.Find(HomeName(snapshot));
            var body = system.Find(target);
            if (home == null || body == null || home == body) return false;
            var star = system.Parent(home);
            return star != null && string.IsNullOrEmpty(star.Parent) && string.Equals(body.Parent, star.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The window the setting takes: Fastest Soonest, Most efficient Optimal, Balanced Soonest unless Optimal saves <see cref="BalancedSaving"/>.</summary>
        public static TransferWindow Pick(WindowSearch search, TimeVersusDeltaV setting)
        {
            switch (setting)
            {
                case TimeVersusDeltaV.Fastest: return search.Soonest;
                case TimeVersusDeltaV.MostEfficient: return search.Optimal;
                default:
                    return search.Soonest.TotalDv - search.Optimal.TotalDv >= BalancedSaving * search.Soonest.TotalDv ? search.Optimal : search.Soonest;
            }
        }

        /// <summary>The search over a copied <paramref name="problem"/>, Soonest within <paramref name="marginPercent"/> % of Optimal: it reads nothing else.</summary>
        internal static WindowSearch Search(Problem problem, int marginPercent)
        {
            if (marginPercent < 0) return Fail("the Craft Check margin must be 0 % or more");
            var from = problem.From;
            var synodic = Transfer.Synodic(problem.Home.Period, problem.Target.Period);
            var step = synodic / (Departures - 1);
            var hohmann = problem.HohmannTime();
            var tofLo = FlightLow * hohmann;
            var tofStep = (FlightHigh - FlightLow) * hohmann / (Flights - 1);

            // The grid: each departure at its cheapest flight time.
            var rowCost = new double[Departures];
            var rowTof = new double[Departures];
            var best = -1;
            for (var i = 0; i < Departures; i++)
            {
                var t = from + i * step;
                rowCost[i] = double.PositiveInfinity;
                for (var j = 0; j < Flights; j++)
                {
                    var tof = tofLo + j * tofStep;
                    var c = problem.Cost(t, tof).TotalDv;
                    if (c < rowCost[i])
                    {
                        rowCost[i] = c;
                        rowTof[i] = tof;
                    }
                }
                if (!double.IsInfinity(rowCost[i]) && (best < 0 || rowCost[i] < rowCost[best])) best = i;
            }
            if (best < 0)
                return Fail("no departure in the " + Num(synodic) + " s from UT " + Num(from) + " has a Lambert path to " + problem.Name
                    + (problem.LambertError != null ? " (Lambert: " + problem.LambertError + ")" : ""));

            // Optimal: BrentMin over the departure a grid step either side of the cheapest, each at its own cheapest flight time.
            var end = from + synodic;
            Func<double, double, TransferWindow> atTime = (t, seedTof) => CheapestFlight(problem, t, seedTof, tofStep, tofLo);
            var bestT = from + best * step;
            var lo = Math.Max(from, bestT - step);
            var hi = Math.Min(end, bestT + step);
            var x = Minimise(u => atTime(bestT + u * step, rowTof[best]).TotalDv, (lo - bestT) / step, (hi - bestT) / step);
            var optimal = atTime(bestT + x * step, rowTof[best]);
            var gridBest = atTime(bestT, rowTof[best]);
            if (!(optimal.TotalDv <= gridBest.TotalDv)) optimal = gridBest;

            // Soonest: the first departure whose total fits the margin over Optimal's; between it and the grid point before, the edge.
            var budget = optimal.TotalDv * (1 + marginPercent / 100.0);
            var first = -1;
            for (var i = 0; i < Departures && first < 0; i++)
                if (rowCost[i] <= budget) first = i;
            TransferWindow soonest;
            if (first < 0 || from + first * step >= optimal.DepartureUt) soonest = optimal;
            else if (first == 0) soonest = atTime(from, rowTof[0]);
            else
            {
                double late = from + first * step, early = late - step;
                var lateTof = rowTof[first];
                soonest = atTime(late, lateTof);
                for (var k = 0; k < Bisections && late - early > 1; k++)
                {
                    var mid = (early + late) / 2;
                    var got = atTime(mid, lateTof);
                    if (got.TotalDv <= budget)
                    {
                        late = mid;
                        soonest = got;
                        lateTof = got.ArrivalUt - got.DepartureUt;
                    }
                    else early = mid;
                }
            }

            return new WindowSearch
            {
                Target = problem.Name, Soonest = soonest, Optimal = optimal, From = from, Synodic = synodic,
                ParkingPe = problem.ParkingPe, ParkingAssumed = problem.ParkingAssumed,
            };
        }

        /// <summary>The cheapest flight time from <paramref name="t"/>: the grid's flight times scanned, then BrentMin a step either side of the cheapest.</summary>
        private static TransferWindow CheapestFlight(Problem problem, double t, double seedTof, double tofStep, double tofLo)
        {
            var tof0 = seedTof;
            var c0 = problem.Cost(t, seedTof).TotalDv;
            for (var j = 0; j < Flights; j++)
            {
                var tof = tofLo + j * tofStep;
                var c = problem.Cost(t, tof).TotalDv;
                if (c < c0)
                {
                    c0 = c;
                    tof0 = tof;
                }
            }
            var y = Minimise(u => problem.Cost(t, tof0 + u * tofStep).TotalDv, Math.Max(-1, (tofStep * 0.1 - tof0) / tofStep), 1);
            var found = problem.Cost(t, tof0 + y * tofStep);
            return found.TotalDv <= c0 ? found : problem.Cost(t, tof0);
        }

        /// <summary>BrentMin's minimiser of <paramref name="f"/> in [<paramref name="lo"/>, <paramref name="hi"/>] (grid steps), a cell with no path costed <see cref="NoPath"/>.</summary>
        private static double Minimise(Func<double, double> f, double lo, double hi)
        {
            if (!(hi > lo)) return lo;
            var found = BrentMin.Solve(u =>
            {
                var c = f(u);
                return Finite(c) ? c : NoPath;
            }, lo, hi, StepTolerance, Iterations);
            return Finite(found.X) ? found.X : (lo + hi) / 2;
        }

        private static string HomeName(GameSnapshot snapshot)
        {
            return snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
        }

        /// <summary>
        /// One window problem, copied from the Game Snapshot: the two planets' orbits round the star, the parking orbit's periapsis
        /// and speed there, and the capture's periapsis. The search writes only <see cref="LambertError"/>.
        /// </summary>
        internal sealed class Problem
        {
            /// <summary>The target planet's name.</summary>
            public string Name;
            /// <summary>The craft's planet's orbit and the target's, round the star.</summary>
            public Conic Home;
            public Conic Target;
            /// <summary>The snapshot's UT: the search starts here.</summary>
            public double From;
            /// <summary>The gravitational parameters of the craft's planet and the target, m³/s².</summary>
            public double HomeGm;
            public double TargetGm;
            /// <summary>The parking orbit's periapsis radius and speed there.</summary>
            public double ParkingR;
            public double ParkingV;
            /// <summary>The parking orbit's periapsis altitude, m, and whether it is the Delta-v Map's low orbit (the craft has no closed one).</summary>
            public double ParkingPe;
            public bool ParkingAssumed;
            /// <summary>The capture's periapsis radius round the target.</summary>
            public double CaptureR;
            /// <summary>Whether the capture counts in the total.</summary>
            public bool Capture;
            /// <summary>Why the last Lambert solve that failed had no path.</summary>
            public string LambertError;

            /// <summary>The problem from <paramref name="snapshot"/>, or null with <paramref name="error"/> saying why there is none.</summary>
            public static Problem Of(GameSnapshot snapshot, string target, double targetPe, bool capture, out string error)
            {
                error = null;
                var system = new BodySystem(snapshot.Bodies);
                var home = system.Find(HomeName(snapshot));
                var body = system.Find(target ?? "");
                if (home == null) error = "the body table has no body for the craft's orbit";
                else if (body == null) error = "no body named " + target + " in the body table";
                else if (!IsPlanetTransfer(snapshot, target)) error = body.Name + " is not a planet of the star " + home.Name + " orbits";
                else if (!(targetPe >= 0 && targetPe < body.SoiRadius - body.Radius))
                    error = "target_pe must be from 0 to " + Num(body.SoiRadius - body.Radius) + " m, inside " + body.Name + "'s sphere of influence";
                else if (!Finite(snapshot.Ut)) error = "the Game Snapshot has no time";
                if (error != null) return null;

                var p = new Problem
                {
                    Name = body.Name, Home = system.OrbitOf(home), Target = system.OrbitOf(body), From = snapshot.Ut, HomeGm = home.Gm,
                    TargetGm = body.Gm, CaptureR = body.Radius + targetPe, Capture = capture,
                };
                if (!p.Home.Closed || !p.Target.Closed) { error = "the planets' orbits must be closed"; return null; }
                // The craft's parking orbit when it is in a closed one round the planet clear of its atmosphere; else the Delta-v Map's low orbit.
                var o = snapshot.Orbit;
                var floor = home.Radius + home.AtmosphereHeight;
                if (o != null && o.Eccentricity < 1 && o.SemiMajorAxis > 0 && o.SemiMajorAxis * (1 - o.Eccentricity) > floor
                    && string.Equals(HomeName(snapshot), home.Name, StringComparison.OrdinalIgnoreCase))
                {
                    p.ParkingR = o.SemiMajorAxis * (1 - o.Eccentricity);
                    p.ParkingV = Math.Sqrt(home.Gm * (2 / p.ParkingR - 1 / o.SemiMajorAxis));
                }
                else
                {
                    p.ParkingR = DeltaVMap.LowOrbit(home);
                    p.ParkingV = Math.Sqrt(home.Gm / p.ParkingR);
                    p.ParkingAssumed = true;
                }
                p.ParkingPe = p.ParkingR - home.Radius;
                return p;
            }

            /// <summary>The Hohmann time between the planets' semi-major axes round the star.</summary>
            public double HohmannTime()
            {
                var a = (Home.A + Target.A) / 2;
                return Math.PI * Math.Sqrt(a * a * a / Home.Mu);
            }

            /// <summary>The window leaving at <paramref name="t"/> and taking <paramref name="tof"/> s; its total is infinite when the arc has no path.</summary>
            public TransferWindow Cost(double t, double tof)
            {
                Vec3 r1, v1, r2, v2;
                Home.State(t, out r1, out v1);
                Target.State(t + tof, out r2, out v2);
                var path = Lambert.Solve(Home.Mu, r1, r2, tof, Home.W);
                if (path.Error != null)
                {
                    LambertError = path.Error;
                    return new TransferWindow { DepartureUt = t, ArrivalUt = t + tof, TotalDv = double.PositiveInfinity };
                }
                var leaving = (path.V1 - v1).Length;
                var arriving = (path.V2 - v2).Length;
                var ejection = Math.Sqrt(leaving * leaving + 2 * HomeGm / ParkingR) - ParkingV;
                var captureDv = Capture ? Math.Sqrt(arriving * arriving + 2 * TargetGm / CaptureR) - Math.Sqrt(TargetGm / CaptureR) : 0;
                return new TransferWindow
                {
                    DepartureUt = t, ArrivalUt = t + tof, EjectionDv = ejection, ArrivalVInf = arriving, CaptureDv = captureDv,
                    TotalDv = ejection + captureDv,
                };
            }
        }

        private static WindowSearch Fail(string why)
        {
            return new WindowSearch { Error = Failure + ": " + why };
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
