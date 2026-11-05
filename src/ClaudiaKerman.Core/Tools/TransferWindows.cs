// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// The grid over departure and arrival and its costing follow MechJeb2's TransferCalculator.cs (GPL-3,
// https://github.com/MuMech/MechJeb2, commit a958383) without its UI or threads; the ejection from the parking orbit is the
// single-impulse hyperbolic burn of MechJebLib/Maneuvers/SingleImpulseHyperbolicBurn.cs (MIT) at the parking orbit's periapsis.

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
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
        /// <summary>A moon's window (#647): the plane change on the way, where the arc is slow, m/s; 0 to a planet.</summary>
        public double PlaneDv;
        /// <summary>From the ground (#727): the ascent's loss to the body's turning, launching into this row's plane, m/s; else 0.</summary>
        public double AscentLossDv;
        /// <summary>Ejection plus capture, or the ejection alone without capture (and a moon's plane change, and the ascent's loss).</summary>
        public double TotalDv;
        /// <summary>
        /// From the ground (#727): the case the row took (<see cref="DeparturePlane"/>'s notes) and the launch it assumes; null
        /// from orbit. Set on Soonest and Optimal only.
        /// </summary>
        public string Plane;
        public LaunchWindow Launch;
    }

    /// <summary>The Soonest and Optimal windows to a planet or a moon of the craft's planet, or why there are none (<see cref="Error"/>, with the rest unset).</summary>
    internal sealed class WindowSearch
    {
        /// <summary>Null when windows were found; otherwise "found no transfer window: why".</summary>
        public string Error;
        /// <summary>The target's name as the body table has it.</summary>
        public string Target;
        /// <summary>The earliest window within the Craft Check margin of <see cref="Optimal"/>'s total; Optimal itself when none is earlier.</summary>
        public TransferWindow Soonest;
        /// <summary>The cheapest window departing within the horizon.</summary>
        public TransferWindow Optimal;
        /// <summary>The horizon searched: from <see cref="From"/> (UT s) for <see cref="Synodic"/>, one synodic period (s) of the two planets, or a moon's period.</summary>
        public double From;
        public double Synodic;
        /// <summary>The parking orbit's periapsis altitude (m) the ejection is costed from, and whether it was assumed (no closed orbit round the planet).</summary>
        public double ParkingPe;
        public bool ParkingAssumed;
        /// <summary>
        /// From the ground to an out-of-plane moon (#690, #727): the launch the windows assume, null otherwise; they depart
        /// <see cref="WindowWaits.LaunchLeadS"/> after its UT or later. Into the moon's plane, no plane change; due east from a site
        /// past it, with the residual (<see cref="LaunchWindow.Residual"/>) fixed on the way. A planet's rows each carry their own.
        /// </summary>
        public LaunchWindow Launch;
        /// <summary>Whether the target is a moon of the craft's planet (#647).</summary>
        public bool Moon;
        /// <summary>The craft's delta-v from the Game Snapshot's stage table, m/s; null when the stock simulator gives none (#648).</summary>
        public double? CraftDv;
        /// <summary>Whether the craft can afford a window (#648): <see cref="Optimal"/> is the cheapest in the horizon, so no window fits when it exceeds <see cref="CraftDv"/>. True when the craft's figure is unknown.</summary>
        public bool Fits
        {
            get { return !CraftDv.HasValue || Optimal.TotalDv <= CraftDv.Value - (LaterLegsDv ?? 0); }
        }
        /// <summary>The Mission Plan's later legs' delta-v, m/s (#694); null with no Mission Plan. <see cref="Fits"/> is judged against <see cref="CraftDv"/> less this.</summary>
        public double? LaterLegsDv;
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
    /// A moon of the craft's planet (#647) is the same search round the planet over one of the moon's orbits: see
    /// <see cref="Problem.Cost"/>. A moon out of the parking orbit's plane (Minmus) is cheaper to reach near its nodes.
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
        /// A moon's Optimal saving less than this over Soonest, m/s, is no saving and Optimal is Soonest: a moon in the parking
        /// plane costs the same at any departure, and a few m/s (the Mun from a parking orbit tilted a few degrees) is not worth a wait.
        /// </summary>
        public const double NoSavingDv = 5;
        /// <summary>The points on a moon's arc the plane change is tried at.</summary>
        private const int PlanePoints = 32;
        /// <summary>A moon this far (degrees) out of the equator's plane is launched into from the pad (#690); the Mun, in it, is not.</summary>
        private const double OutOfPlaneDeg = 1;

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

        /// <summary>
        /// Whether <paramref name="target"/> shares the parent of the body the craft orbits (#459): another planet of its star (Duna
        /// from Kerbin), or another moon of its planet (Minmus from the Mun).
        /// </summary>
        public static bool IsSiblingTransfer(GameSnapshot snapshot, string target)
        {
            var system = new BodySystem(snapshot.Bodies);
            var home = system.Find(HomeName(snapshot));
            var body = system.Find(target);
            if (home == null || body == null || home == body || system.Parent(home) == null) return false;
            return string.Equals(body.Parent, home.Parent, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether <paramref name="target"/> is a moon of the craft's planet (#647): a body orbiting the body the craft orbits or stands on.</summary>
        public static bool IsMoonTransfer(GameSnapshot snapshot, string target)
        {
            var system = new BodySystem(snapshot.Bodies);
            var home = system.Find(HomeName(snapshot));
            var body = system.Find(target);
            return home != null && body != null && body != home && string.Equals(body.Parent, home.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The window the setting takes: Fastest Soonest, Most efficient Optimal, Balanced Soonest unless Optimal saves <see cref="BalancedSaving"/>.</summary>
        public static TransferWindow Pick(WindowSearch search, TimeVersusDeltaV setting)
        {
            return TakesOptimal(search.Soonest.TotalDv, search.Optimal.TotalDv, setting) ? search.Optimal : search.Soonest;
        }

        /// <summary>The setting's rule on two totals: whether it takes the cheaper Optimal over Soonest (#760: for routes as for windows).</summary>
        internal static bool TakesOptimal(double soonestDv, double optimalDv, TimeVersusDeltaV setting)
        {
            switch (setting)
            {
                case TimeVersusDeltaV.Fastest: return false;
                case TimeVersusDeltaV.MostEfficient: return true;
                default: return soonestDv - optimalDv >= BalancedSaving * soonestDv;
            }
        }

        /// <summary>
        /// The search over a copied <paramref name="problem"/>, Soonest within <paramref name="marginPercent"/> % of Optimal: it reads
        /// nothing else. <paramref name="span"/> (s) searches that long from <see cref="Problem.From"/> in place of a synodic period:
        /// transfer_to's re-solve at fly time round the planned window (#459).
        /// </summary>
        internal static WindowSearch Search(Problem problem, int marginPercent, double? span = null)
        {
            if (marginPercent < 0) return Fail("the Craft Check margin must be 0 % or more");
            var from = problem.From;
            var synodic = span ?? (problem.Moon ? problem.MoonHorizon() : Transfer.Synodic(problem.Home.Period, problem.Target.Period));
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
            // A moon's two nodes an orbit cost about the same: the earliest dip within NoSavingDv of the cheapest is Optimal, not a later twin.
            if (problem.Moon)
                for (var i = 0; i < best; i++)
                    if (rowCost[i] <= rowCost[best] + NoSavingDv && (i == 0 || rowCost[i] <= rowCost[i - 1]) && rowCost[i] <= rowCost[i + 1])
                    {
                        best = i;
                        break;
                    }

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
            // Launched into its plane (#690) the moon is in the parking plane at every departure: no wait saves anything, so none is taken.
            if (problem.Moon && (problem.Launch != null && problem.Launch.Residual == 0 || soonest.TotalDv - optimal.TotalDv < NoSavingDv)) optimal = soonest;
            problem.Describe(soonest);
            if (optimal != soonest) problem.Describe(optimal);

            return new WindowSearch
            {
                Target = problem.Name, Soonest = soonest, Optimal = optimal, From = from, Synodic = synodic,
                ParkingPe = problem.ParkingPe, ParkingAssumed = problem.ParkingAssumed, Launch = problem.Launch, Moon = problem.Moon, CraftDv = problem.CraftDv, LaterLegsDv = problem.LaterLegsDv,
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

        /// <summary>The cheapest flight time leaving at <paramref name="t"/>, from a flight of about <paramref name="seedTof"/> s: a window moved a little from the one a search found (#760).</summary>
        internal static TransferWindow CheapestAt(Problem problem, double t, double seedTof)
        {
            var hohmann = problem.HohmannTime();
            return CheapestFlight(problem, t, seedTof, (FlightHigh - FlightLow) * hohmann / (Flights - 1), FlightLow * hohmann);
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
        /// One window problem, copied from the Game Snapshot: the two planets' orbits round the star (or a moon's round the craft's
        /// planet), the parking orbit's periapsis and speed there, and the capture's periapsis. The search writes only <see cref="LambertError"/>.
        /// </summary>
        internal sealed class Problem
        {
            /// <summary>The target's name.</summary>
            public string Name;
            /// <summary>The craft's planet's orbit and the target's, round the star; to a moon, Home is null and Target is the moon's orbit round the planet.</summary>
            public Conic Home;
            public Conic Target;
            /// <summary>Whether the target is a moon of the craft's planet, and then the parking orbit's plane: its normal, turned to the moon's sense of motion.</summary>
            public bool Moon;
            public Vec3 Plane;
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
            /// <summary>From the ground to a moon out of the site's plane (#690, #727): the launch window; null otherwise.</summary>
            public LaunchWindow Launch;
            /// <summary>
            /// From the ground (#727): the craft's body, the site (degrees, at <see cref="SiteUt"/>) and the ascent's loss a moon's
            /// launch costs every row (m/s); a planet's is costed per departure from its asymptote. <see cref="PlaneNote"/> is the
            /// rows' case when no launch could be found. All unset from orbit.
            /// </summary>
            public bool Grounded;
            public SnapshotBody HomeBody;
            public double Latitude;
            public double CelestialLongitude;
            public double SiteUt;
            public double AscentLossDv;
            public string PlaneNote;
            /// <summary>The Mission's Time versus delta-v: how long a ground departure's residual may wait for the moon's node (#727).</summary>
            public TimeVersusDeltaV Setting = TimeVersusDeltaV.Balanced;
            /// <summary>The capture's periapsis radius round the target.</summary>
            public double CaptureR;
            /// <summary>Whether the capture counts in the total.</summary>
            public bool Capture;
            /// <summary>The craft's delta-v from the stage table, m/s; null when unknown (#648).</summary>
            public double? CraftDv;
            /// <summary>The Mission Plan's later legs' delta-v, m/s; null with none (#694).</summary>
            public double? LaterLegsDv;
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
                else if (!IsSiblingTransfer(snapshot, target) && !IsMoonTransfer(snapshot, target))
                    error = body.Name + " shares no parent with " + home.Name + ", nor a moon of " + home.Name;
                else if (!(targetPe >= 0 && targetPe < body.SoiRadius - body.Radius))
                    error = "target_pe must be from 0 to " + Num(body.SoiRadius - body.Radius) + " m, inside " + body.Name + "'s sphere of influence";
                else if (!Finite(snapshot.Ut)) error = "the Game Snapshot has no time";
                if (error != null) return null;

                var moon = IsMoonTransfer(snapshot, target);
                var p = new Problem
                {
                    Name = body.Name, Home = moon ? null : system.OrbitOf(home), Target = system.OrbitOf(body), From = snapshot.Ut, HomeGm = home.Gm,
                    TargetGm = body.Gm, CaptureR = body.Radius + targetPe, Capture = capture, Moon = moon,
                };
                if (moon ? !p.Target.Closed : !p.Home.Closed || !p.Target.Closed) { error = moon ? "the moon's orbit must be closed" : "the planets' orbits must be closed"; return null; }
                // The craft's parking orbit when it is in a closed one round the planet clear of its atmosphere; else the Delta-v Map's low orbit.
                var o = snapshot.Orbit;
                var floor = home.Radius + home.AtmosphereHeight;
                if (o != null && o.Eccentricity < 1 && o.SemiMajorAxis > 0 && o.SemiMajorAxis * (1 - o.Eccentricity) > floor
                    && string.Equals(HomeName(snapshot), home.Name, StringComparison.OrdinalIgnoreCase))
                {
                    p.ParkingR = o.SemiMajorAxis * (1 - o.Eccentricity);
                    p.ParkingV = Math.Sqrt(home.Gm * (2 / p.ParkingR - 1 / o.SemiMajorAxis));
                    p.Plane = Conic.FromElements(home.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, 0, 0).W;
                }
                else
                {
                    p.ParkingR = DeltaVMap.LowOrbit(home);
                    p.ParkingV = Math.Sqrt(home.Gm / p.ParkingR);
                    p.ParkingAssumed = true;
                    // From the pad, the parking orbit is the equator's: a launch due east.
                    p.Plane = new Vec3(0, 0, 1);
                }
                p.ParkingPe = p.ParkingR - home.Radius;
                if (snapshot.DeltaVSimulator && snapshot.Stages.Count > 0 && !snapshot.DeltaVUnreliable) p.CraftDv = snapshot.Stages.Sum(st => st.DeltaVVac);
                p.LaterLegsDv = snapshot.LaterLegsDv;
                if (Vec3.Dot(p.Plane, p.Target.W) < 0) p.Plane = p.Plane * -1;
                p.Setting = snapshot.TimeVersusDeltaV;
                if (p.ParkingAssumed && snapshot.IsGrounded) Ground(p, snapshot, home, body);
                return p;
            }

            /// <summary>
            /// From the ground (#690, #727), any site on any body: to a moon out of the site's plane, the launch the rows assume (as
            /// plan_ascent's launch_at): into the moon's plane at the next window, or due east past it with the residual fixed on
            /// the way, the windows starting after it. To a planet, each row's launch contains its own asymptote (<see cref="Describe"/>),
            /// so the windows start a turn of the body and the launch lead on, leaving a launch before any of them.
            /// </summary>
            private static void Ground(Problem p, GameSnapshot snapshot, SnapshotBody home, SnapshotBody body)
            {
                p.Grounded = true;
                p.HomeBody = home;
                p.Latitude = snapshot.Latitude;
                p.CelestialLongitude = snapshot.CelestialLongitude;
                p.SiteUt = snapshot.Ut;
                if (!p.Moon)
                {
                    if (home.RotationPeriod == 0 || !Finite(home.RotationPeriod)) p.PlaneNote = DeparturePlane.Assumed("the body has no rotation period to time a launch by");
                    else p.From = snapshot.Ut + WindowWaits.LaunchLeadS + Math.Abs(home.RotationPeriod);
                    return;
                }
                // The Mun from near the equator is in the parking plane already: the rows are as from orbit.
                if (Math.Abs(body.Inclination) < OutOfPlaneDeg && Math.Abs(snapshot.Latitude) < OutOfPlaneDeg) return;
                var launch = LaunchWindow.Find(home, snapshot.Ut, snapshot.Latitude, snapshot.CelestialLongitude, body.Inclination, body.Lan, p.ParkingPe);
                if (launch.Error != null)
                {
                    p.PlaneNote = DeparturePlane.Assumed(launch.Error);
                    return;
                }
                p.Launch = launch;
                p.From = launch.Ut + WindowWaits.LaunchLeadS;
                if (launch.Residual == 0)
                {
                    p.Plane = p.Target.W;
                    p.AscentLossDv = LaunchWindow.AscentLoss(home, snapshot.Latitude, body.Inclination);
                    return;
                }
                // Due east, its ascending node the moon's: no ascent loss, and the moon's sense of motion.
                p.Plane = Conic.FromElements(home.Gm, p.ParkingR, 0, launch.Inclination, body.Lan, 0, 0, 0).W;
                if (Vec3.Dot(p.Plane, p.Target.W) < 0) p.Plane = p.Plane * -1;
            }

            /// <summary>
            /// A moon's horizon (#727): one of its orbits, but from the ground with a residual the Mission's Time versus delta-v bounds
            /// the wait for a node arrival: Fastest one parking orbit after the launch, Balanced a quarter of the moon's orbit.
            /// </summary>
            public double MoonHorizon()
            {
                if (Launch == null || Launch.Residual == 0) return Target.Period;
                switch (Setting)
                {
                    case TimeVersusDeltaV.Fastest: return 2 * Math.PI * Math.Sqrt(ParkingR * ParkingR * ParkingR / HomeGm);
                    case TimeVersusDeltaV.MostEfficient: return Target.Period;
                    default: return Target.Period / 4;
                }
            }

            /// <summary>
            /// From the ground (#727), the case <paramref name="w"/> took and the launch it assumes: a moon's is the problem's; a
            /// planet's contains this row's asymptote, the latest launch the launch lead before its departure.
            /// </summary>
            public void Describe(TransferWindow w)
            {
                if (!Grounded) return;
                if (PlaneNote != null)
                {
                    w.Plane = PlaneNote;
                    return;
                }
                if (Moon)
                {
                    if (Launch == null) return;
                    w.Launch = Launch;
                    w.Plane = Launch.Residual == 0 ? DeparturePlane.LaunchedInto : w.PlaneDv < DeparturePlane.NodeFreeDv ? DeparturePlane.AtNode : DeparturePlane.MidCourse(Launch.Residual);
                    return;
                }
                var leaving = Leaving(w.DepartureUt, w.ArrivalUt - w.DepartureUt);
                var launch = leaving.HasValue
                    ? DeparturePlane.ForAsymptote(HomeBody, SiteUt, Latitude, CelestialLongitude, leaving.Value, w.DepartureUt - WindowWaits.LaunchLeadS, ParkingPe)
                    : LaunchWindow.Fail("the window's arc has no Lambert path");
                if (launch.Error != null)
                {
                    w.Plane = DeparturePlane.Assumed(launch.Error);
                    return;
                }
                w.Launch = launch;
                w.Plane = DeparturePlane.LaunchedInto;
            }

            /// <summary>A planet's departure asymptote leaving at <paramref name="t"/> on a <paramref name="tof"/> s arc: v-infinity round the craft's planet; null with no path.</summary>
            internal Vec3? Leaving(double t, double tof)
            {
                Vec3 r1, v1, r2, v2;
                Home.State(t, out r1, out v1);
                Target.State(t + tof, out r2, out v2);
                var path = Lambert.Solve(Home.Mu, r1, r2, tof, Home.W);
                return path.Error == null ? path.V1 - v1 : (Vec3?)null;
            }

            /// <summary>The Hohmann time between the planets' semi-major axes round the star, or from the parking orbit to the moon's.</summary>
            public double HohmannTime()
            {
                var a = Moon ? (ParkingR + Target.A) / 2 : (Home.A + Target.A) / 2;
                return Math.PI * Math.Sqrt(a * a * a / Target.Mu);
            }

            /// <summary>
            /// The window leaving at <paramref name="t"/> and taking <paramref name="tof"/> s; its total is infinite when the arc has no path.
            /// To a moon, built round the planet as #619's broken plane is: the moon's orbit turned into the parking orbit's plane about
            /// its line of nodes; a 180° Lambert arc in that plane from the parking orbit (where the craft is within one orbit of
            /// <paramref name="t"/>) to where the turned moon is at arrival; the plane change on the way at the point of the arc where
            /// it costs least, tilting the arc's remainder to reach the moon's height out of the plane; and the capture from the
            /// arrival speed relative to the turned moon. A moon at a node at arrival needs no plane change.
            /// </summary>
            public TransferWindow Cost(double t, double tof)
            {
                if (Moon) return MoonCost(t, tof);
                Vec3 r1, v1, r2, v2;
                Home.State(t, out r1, out v1);
                Target.State(t + tof, out r2, out v2);
                var path = Lambert.Solve(Home.Mu, r1, r2, tof, Home.W);
                if (path.Error != null)
                {
                    LambertError = path.Error;
                    return new TransferWindow { DepartureUt = t, ArrivalUt = t + tof, TotalDv = double.PositiveInfinity };
                }
                var away = path.V1 - v1;
                var leaving = away.Length;
                var arriving = (path.V2 - v2).Length;
                var ejection = Math.Sqrt(leaving * leaving + 2 * HomeGm / ParkingR) - ParkingV;
                var captureDv = Capture ? Math.Sqrt(arriving * arriving + 2 * TargetGm / CaptureR) - Math.Sqrt(TargetGm / CaptureR) : 0;
                // From the ground (#727): the launch into an orbit holding the asymptote, inclined at least its declination.
                var loss = 0.0;
                if (Grounded && PlaneNote == null && leaving > 0)
                {
                    var declination = Math.Asin(Math.Max(-1, Math.Min(1, away.Z / leaving))) * 180 / Math.PI;
                    loss = LaunchWindow.AscentLoss(HomeBody, Latitude, Math.Max(Math.Abs(Latitude), Math.Abs(declination)));
                }
                return new TransferWindow
                {
                    DepartureUt = t, ArrivalUt = t + tof, EjectionDv = ejection, ArrivalVInf = arriving, CaptureDv = captureDv, AscentLossDv = loss,
                    TotalDv = ejection + captureDv + loss,
                };
            }

            private TransferWindow MoonCost(double t, double tof)
            {
                var mu = Target.Mu;
                Vec3 r2, v2;
                Target.State(t + tof, out r2, out v2);
                // The moon's orbit turned into the parking plane about the line of nodes (Transfer.Turned's rotation).
                var axis = Vec3.Cross(Target.W, Plane);
                double sin = axis.Length, cos = Vec3.Dot(Target.W, Plane);
                Func<Vec3, Vec3> turn = x => x;
                if (sin > 1e-12)
                {
                    var k = axis / sin;
                    turn = x => x * cos + Vec3.Cross(k, x) * sin + k * (Vec3.Dot(k, x) * (1 - cos));
                }
                var meet = turn(r2);
                var r1 = meet.Normalized * -ParkingR;
                var path = Lambert.Solve(mu, r1, meet, tof, Plane);
                if (path.Error != null)
                {
                    LambertError = path.Error;
                    return new TransferWindow { DepartureUt = t, ArrivalUt = t + tof, TotalDv = double.PositiveInfinity };
                }
                var ejection = (path.V1 - Vec3.Cross(Plane, r1).Normalized * ParkingV).Length;
                var arriving = (path.V2 - turn(v2)).Length;
                var captureDv = Capture ? Math.Sqrt(arriving * arriving + 2 * TargetGm / CaptureR) - Math.Sqrt(TargetGm / CaptureR) : 0;

                // The plane change: at a point phi round the arc from the departure, tilting the rest by j lifts the arrival, 180° - phi
                // further on, to the moon's height delta out of the plane (sin delta = sin j sin(180° - phi)); it turns the horizontal
                // speed there, h / r, through j.
                var lift = Math.Abs(Vec3.Dot(r2, Plane)) / r2.Length;
                var planeDv = 0.0;
                if (lift > 1e-9)
                {
                    var h = Vec3.Cross(r1, path.V1).Length;
                    var semiLatus = h * h / mu;
                    double eCos = semiLatus / ParkingR - 1, eSin = Vec3.Dot(r1, path.V1) * h / (mu * ParkingR);
                    planeDv = double.PositiveInfinity;
                    for (var n = 1; n < PlanePoints; n++)
                    {
                        var phi = Math.PI * n / PlanePoints;
                        var sinJ = lift / Math.Sin(phi);
                        if (sinJ >= 1) continue;
                        var r = semiLatus / (1 + eCos * Math.Cos(phi) - eSin * Math.Sin(phi));
                        if (!(r > 0)) continue;
                        planeDv = Math.Min(planeDv, 2 * h / r * Math.Sin(Math.Asin(sinJ) / 2));
                    }
                }
                return new TransferWindow
                {
                    DepartureUt = t, ArrivalUt = t + tof, EjectionDv = ejection, ArrivalVInf = arriving, CaptureDv = captureDv, PlaneDv = planeDv,
                    AscentLossDv = AscentLossDv, TotalDv = ejection + planeDv + captureDv + AscentLossDv,
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
