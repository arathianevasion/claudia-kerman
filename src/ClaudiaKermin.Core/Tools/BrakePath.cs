// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>The craft <see cref="BrakePath"/> flies: the braking stage, and the lander once any Underhang drops.</summary>
    internal sealed class BrakeCraft
    {
        /// <summary>Thrust, N.</summary>
        public double ThrustN;
        /// <summary>Mass, kg.</summary>
        public double MassKg;
        /// <summary>Vacuum Isp, s.</summary>
        public double Isp = 300;
        /// <summary>The mass at which the stage runs dry, kg; 0 for never.</summary>
        public double DryMassKg;
        /// <summary>How the craft lags its steering, s: a first-order lag on the acceleration, from TURNTIME (<see cref="BrakePath.TurnLag"/>).</summary>
        public double LagS = 1;
        /// <summary>m above the ground the Underhang drops at while braking; negative for no Underhang.</summary>
        public double DropAgl = -1;
        /// <summary>The lander alone, once the Underhang drops: thrust (N), mass (kg, at most what is left) and Isp (s).</summary>
        public double LanderThrustN;
        public double LanderMassKg;
        public double LanderIsp = 300;
        public double LanderDryMassKg;

        public bool CanFly
        {
            get { return ThrustN > 0 && MassKg > 0; }
        }
    }

    /// <summary>The terrain under the approach: heights above sea level at evenly spaced distances before the site along the track.</summary>
    internal sealed class TerrainProfile
    {
        /// <summary>The distance before the site of the first sample, m (negative: past it).</summary>
        public readonly double From;
        public readonly double Step;
        public readonly double[] Heights;

        public TerrainProfile(double from, double step, double[] heights)
        {
            From = from;
            Step = step;
            Heights = heights;
        }

        /// <summary>Samples <paramref name="ground"/> (by m before the site) every <paramref name="step"/> m from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static TerrainProfile Sample(Func<double, double> ground, double from, double to, double step)
        {
            var count = (int)Math.Ceiling((to - from) / step) + 1;
            var heights = new double[count];
            for (var i = 0; i < count; i++) heights[i] = ground(from + i * step);
            return new TerrainProfile(from, step, heights);
        }

        /// <summary>The height <paramref name="s"/> m before the site, between the samples either side (the end samples beyond them).</summary>
        public double At(double s)
        {
            var k = (s - From) / Step;
            if (k <= 0) return Heights[0];
            if (k >= Heights.Length - 1) return Heights[Heights.Length - 1];
            var i = (int)k;
            return Heights[i] + (Heights[i + 1] - Heights[i]) * (k - i);
        }

        /// <summary>The highest ground from <paramref name="near"/> to <paramref name="far"/> m before the site.</summary>
        public double Max(double near, double far)
        {
            var most = Math.Max(At(near), At(far));
            var first = (int)Math.Ceiling((near - From) / Step);
            var last = (int)Math.Floor((far - From) / Step);
            for (var i = Math.Max(0, first); i <= Math.Min(Heights.Length - 1, last); i++) most = Math.Max(most, Heights[i]);
            return most;
        }

        public double FarthestS
        {
            get { return From + Step * (Heights.Length - 1); }
        }
    }

    /// <summary>Where a simulated descent starts: the site <see cref="S"/> m ahead along the track, at <see cref="Alt"/> m above sea level.</summary>
    internal sealed class BrakeStart
    {
        public double S;
        public double Alt;
        /// <summary>Horizontal speed across the ground, m/s, along the track.</summary>
        public double VH;
        /// <summary>Vertical speed, m/s, up positive.</summary>
        public double VUp;
        /// <summary>True to brake from the first tick (the craft is braking now); false to fall until braking comes due.</summary>
        public bool Braking;
    }

    /// <summary>The body and the rules a descent is simulated under.</summary>
    internal sealed class BrakeWorld
    {
        public double Mu;
        public double Radius;
        public TerrainProfile Ground;
        public double Touchdown = 2;
        public double Margin = 1.1;
        /// <summary>The integration step, s.</summary>
        public double Dt = 0.2;
        /// <summary>m between the path's recorded points.</summary>
        public double PathSpacing = 500;
    }

    /// <summary>A point of the predicted path.</summary>
    internal struct BrakePoint
    {
        /// <summary>m to the site along the track (negative past it).</summary>
        public double S;
        /// <summary>m above sea level.</summary>
        public double Alt;
        /// <summary>The terrain's height there, m above sea level.</summary>
        public double Ground;
        /// <summary>m/s across the ground.</summary>
        public double VH;
    }

    internal sealed class BrakeResult
    {
        public double Shape;
        public readonly List<BrakePoint> Path = new List<BrakePoint>();
        /// <summary>Delta-v the descent used from where it started to touchdown, m/s.</summary>
        public double DvUsed;
        /// <summary>m to the site where braking began; NaN if it never did.</summary>
        public double BrakeStartS = double.NaN;
        /// <summary>m to the site and m above the ground where braking ended (under 1 m/s across the ground); NaN if it never did.</summary>
        public double BrakeEndS = double.NaN;
        public double BrakeEndAgl = double.NaN;
        /// <summary>The least height over the terrain before braking ended, m.</summary>
        public double LowestClearance = double.PositiveInfinity;
        /// <summary>The least height over the terrain less its margin (<see cref="BrakeGuidance.ClearMargin"/>) before braking ended, m.</summary>
        public double LeastSpare = double.PositiveInfinity;
        /// <summary>m to the site at touchdown (negative: long).</summary>
        public double StopS = double.NaN;
        public double TouchdownSpeed = double.NaN;
        public double Seconds;
        /// <summary>The fastest descent, m/s down.</summary>
        public double FastestDown;
        /// <summary>Why it didn't come down softly near the site, or null.</summary>
        public string Failure;

        /// <summary>The path stays the margin over the terrain while braking and touches down softly.</summary>
        public bool Clears
        {
            get { return Failure == null && LeastSpare >= 0; }
        }
    }

    /// <summary>One (periapsis, shape) pair the plan-time search tried.</summary>
    internal sealed class DescentTry
    {
        /// <summary>The periapsis, m above sea level.</summary>
        public double PeAlt;
        public double Shape;
        /// <summary>The deorbit burn, m/s.</summary>
        public double DeorbitDv;
        public BrakeResult Result;
        /// <summary>The approach's heading across the ground at the site, degrees; NaN where not planned over a pass.</summary>
        public double Heading = double.NaN;

        /// <summary>The landing's delta-v: the deorbit burn and the descent.</summary>
        public double Dv
        {
            get { return DeorbitDv + Result.DvUsed; }
        }
    }

    /// <summary>The search's answer: the cheapest clearing pair, and the range of shapes that cleared anywhere.</summary>
    internal sealed class DescentChoice
    {
        /// <summary>Null when a pair clears; otherwise why none does.</summary>
        public string Error;
        public DescentTry Best;
        /// <summary>The smallest and largest shapes that cleared at some periapsis; NaN if none did.</summary>
        public double ShapeLo = double.NaN;
        public double ShapeHi = double.NaN;
        public int Tried;
        public double Ms;
    }

    /// <summary>
    /// <c>ADDONS:CLAUDIA:BRAKEPATH</c>'s simulation (#511, ADR 0011): <c>land</c>'s braking, flown with <see cref="BrakeGuidance"/> (the
    /// kOS laws, ported) on a point mass along the track over the terrain under it: thrust and mass falling as fuel burns, the
    /// Underhang's drop to the lander, a first-order lag for the turn, and the Descent Shape's height curve. It also searches
    /// (periapsis, shape) pairs for the cheapest whose path clears the terrain by the margin at every point.
    /// </summary>
    internal static class BrakePath
    {
        private const double G0 = 9.80665;
        /// <summary>The Descent Shapes searched: 12 log-spaced steps from 0.2 to 3.</summary>
        public static readonly double[] Shapes = LogSpaced(0.2, 3, 12);
        /// <summary>The periapsis search's step, m.</summary>
        public const double PeStep = 500;
        /// <summary>The highest periapsis searched, m over the site's terrain.</summary>
        public const double PeMostOverSite = 10000;
        /// <summary>About the search's budget, ms.</summary>
        public const double BudgetMs = 200;

        private static double[] LogSpaced(double lo, double hi, int n)
        {
            var shapes = new double[n];
            for (var i = 0; i < n; i++) shapes[i] = Math.Round(lo * Math.Pow(hi / lo, i / (double)(n - 1)), 3);
            return shapes;
        }

        /// <summary>The turn lag the simulation flies with, s, from TURNTIME's top rate (deg/s) and 190° turn (s): the time to turn 20°, at least 1 s.</summary>
        public static double TurnLag(double topRate, double turnS)
        {
            return Math.Max(1, BrakeGuidance.TurnSeconds(20, topRate, turnS));
        }

        /// <summary>
        /// Flies the descent from <paramref name="start"/> to touchdown (or 1500 s) on <paramref name="shape"/>: falling until
        /// <see cref="BrakeGuidance.BrakeDue"/> fires unless already braking, then braking as land_descend does, steering by its
        /// clearance over the terrain at the look-ahead points and the highest ground on to where it stops.
        /// </summary>
        public static BrakeResult Simulate(BrakeCraft craft, BrakeWorld world, BrakeStart start, double shape)
        {
            var result = new BrakeResult { Shape = shape };
            var dt = world.Dt;
            var margin = world.Margin;
            var ground = world.Ground;
            double s = start.S, y = start.Alt, vx = start.VH, vy = start.VUp;
            double m = craft.MassKg, thrust = craft.ThrustN, isp = craft.Isp, dry = craft.DryMassKg;
            double ax = 0, ay = 0, dirAl = -1, dirUp = 0;
            var braking = false;
            double hStart = 0, v0 = 0, vTop = 0;
            var toGo = Math.Max(0, s);
            var dropPending = craft.DropAgl >= 0;
            var gap = 0.0;
            var t = 0.0;
            var nextPoint = start.S;
            var radar = y - ground.At(s);
            var siteGround = ground.At(0);
            var lastSpeed = Math.Sqrt(vx * vx + vy * vy);
            while (radar > 0 && t <= 1500)
            {
                var g = world.Mu / Math.Pow(world.Radius + y, 2);
                var aMax = thrust > 0 && (dry <= 0 || m > dry) ? thrust / m : 0;
                var h = radar;
                foreach (var d in BrakeGuidance.Lookahead(Math.Abs(vx), toGo)) h = Math.Min(h, y - ground.At(s - d));
                h = Math.Min(h, y - ground.Max(s - toGo, s));
                if (!braking && (start.Braking || BrakeGuidance.BrakeDue(s, Math.Abs(vx), -vy, h, BrakeGuidance.BrakeAccel(aMax, g), margin)))
                {
                    braking = true;
                    hStart = Math.Max(BrakeGuidance.HEnd, h);
                    v0 = vx;
                    vTop = Math.Max(50, vx);
                    result.BrakeStartS = s;
                }
                double wantAl = 0, wantUp = 0;
                if (braking)
                {
                    var w = BrakeGuidance.Guide(s, vx, vy, h, hStart, v0, shape, vTop, Math.Max(aMax, 1e-6), g, world.Radius + y, world.Touchdown, margin);
                    toGo = w.ToGo;
                    double mag;
                    BrakeGuidance.Steer(w.Along, w.Up, dirAl, dirUp, vx, vy, aMax, out dirAl, out dirUp, out mag);
                    mag = Math.Min(aMax, mag);
                    wantAl = dirAl * mag;
                    wantUp = dirUp * mag;
                }
                else
                {
                    toGo = Math.Max(0, s);
                }
                if (gap > 0)
                {
                    wantAl = 0;
                    wantUp = 0;
                    gap -= dt;
                }
                var k = Math.Min(1, dt / Math.Max(dt, craft.LagS));
                ax += (wantAl - ax) * k;
                ay += (wantUp - ay) * k;
                var aMag = Math.Sqrt(ax * ax + ay * ay);
                if (aMag > aMax)
                {
                    ax = aMax > 0 ? ax * aMax / aMag : 0;
                    ay = aMax > 0 ? ay * aMax / aMag : 0;
                    aMag = aMax;
                }
                if (braking)
                {
                    result.LowestClearance = double.IsNaN(result.BrakeEndS) ? Math.Min(result.LowestClearance, radar) : result.LowestClearance;
                }
                if (double.IsNaN(result.BrakeEndS))
                {
                    result.LeastSpare = Math.Min(result.LeastSpare, radar - BrakeGuidance.PathMargin(-vy, ground.At(s) - siteGround));
                    if (!braking) result.LowestClearance = Math.Min(result.LowestClearance, radar);
                }
                if (braking && double.IsNaN(result.BrakeEndS) && Math.Abs(vx) < 1)
                {
                    result.BrakeEndS = s;
                    result.BrakeEndAgl = radar;
                }
                if (s <= nextPoint)
                {
                    result.Path.Add(new BrakePoint { S = s, Alt = y, Ground = ground.At(s), VH = vx });
                    nextPoint -= world.PathSpacing;
                }
                lastSpeed = Math.Sqrt(vx * vx + vy * vy);
                vx += ax * dt;
                vy += (ay - g + vx * vx / (world.Radius + y)) * dt;
                s -= vx * dt;
                y += vy * dt;
                if (aMag > 0) m -= m * aMag / (isp * G0) * dt;
                result.DvUsed += aMag * dt;
                result.FastestDown = Math.Max(result.FastestDown, -vy);
                t += dt;
                radar = y - ground.At(s);
                if (dropPending && braking && (radar < craft.DropAgl || (dry > 0 && m <= dry)))
                {
                    dropPending = false;
                    thrust = craft.LanderThrustN;
                    m = Math.Min(m, craft.LanderMassKg);
                    isp = craft.LanderIsp;
                    dry = craft.LanderDryMassKg;
                    // The throttle's gap at the drop: a tick for the cut and the 1 s wait for the parts to fall clear.
                    gap = 1;
                }
            }
            result.Seconds = t;
            result.StopS = s;
            result.TouchdownSpeed = lastSpeed;
            result.Path.Add(new BrakePoint { S = s, Alt = y, Ground = ground.At(s) });
            if (radar > 0) result.Failure = "still flying after 1500 s";
            else if (lastSpeed > 5) result.Failure = "hit the ground at " + lastSpeed.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
            return result;
        }

        /// <summary>
        /// The craft's descent orbit near periapsis: the transfer from <paramref name="r0"/> (m from the body's centre, the deorbit
        /// burn) to a periapsis of <paramref name="rPe"/> over the site, where the craft is <paramref name="before"/> m of the surface
        /// before the site, moving <paramref name="vRotAlong"/> m/s slower across the ground than through space for the body's turn.
        /// </summary>
        public static BrakeStart OnTransfer(double mu, double bodyRadius, double r0, double rPe, double before, double vRotAlong)
        {
            var e = (r0 - rPe) / (r0 + rPe);
            var p = rPe * (1 + e);
            var nu = -before / bodyRadius;
            var r = p / (1 + e * Math.Cos(nu));
            var k = Math.Sqrt(mu / p);
            return new BrakeStart
            {
                S = before,
                Alt = r - bodyRadius,
                VH = k * (1 + e * Math.Cos(nu)) - vRotAlong,
                VUp = k * e * Math.Sin(nu),
            };
        }

        /// <summary>The prograde burn (m/s, positive) from a circular orbit of <paramref name="r0"/> that lowers periapsis to <paramref name="rPe"/>.</summary>
        public static double DeorbitDv(double mu, double r0, double rPe)
        {
            return Math.Abs(Math.Sqrt(mu * (2 / r0 - 2 / (r0 + rPe))) - Math.Sqrt(mu / r0));
        }

        /// <summary>How far before the site the simulation starts falling from, m: past where braking starts for this craft, and 5 km more.</summary>
        public static double FallFrom(BrakeCraft craft, double mu, double bodyRadius, double vPe, double margin)
        {
            var g = mu / (bodyRadius * bodyRadius);
            var aBrake = BrakeGuidance.BrakeAccel(craft.ThrustN / craft.MassKg, g);
            return 1.3 * BrakeGuidance.BrakeDistance(vPe, aBrake, margin, 0) + 5000;
        }

        /// <summary>
        /// The plan-time search: periapsis heights from <paramref name="peLowest"/> (m above sea level) in 500 m steps to
        /// <paramref name="peHighest"/>, each against <paramref name="shapes"/> (all of <see cref="Shapes"/> if null), flown
        /// from the transfer off an orbit of radius <paramref name="r0"/>; it keeps the clearing pair with the least delta-v and
        /// stops once clearing pairs stop getting cheaper, or at <paramref name="budgetMs"/>.
        /// </summary>
        public static DescentChoice Search(BrakeCraft craft, BrakeWorld world, double r0, double vRotAlong, double peLowest, double peHighest,
            IList<double> shapes = null, double budgetMs = BudgetMs)
        {
            var watch = Stopwatch.StartNew();
            var choice = new DescentChoice();
            shapes = shapes ?? Shapes;
            double? lastBest = null;
            for (var pe = peLowest; pe <= peHighest + 1e-6; pe += PeStep)
            {
                var rPe = world.Radius + pe;
                var vPe = Math.Sqrt(world.Mu * (2 / rPe - 2 / (r0 + rPe)));
                var before = Math.Min(world.Ground.FarthestS, FallFrom(craft, world.Mu, world.Radius, vPe, world.Margin));
                var start = OnTransfer(world.Mu, world.Radius, r0, rPe, before, vRotAlong);
                var deorbit = DeorbitDv(world.Mu, r0, rPe);
                DescentTry bestHere = null;
                foreach (var shape in shapes)
                {
                    var flown = Simulate(craft, world, start, shape);
                    choice.Tried++;
                    if (!flown.Clears) continue;
                    choice.ShapeLo = double.IsNaN(choice.ShapeLo) ? shape : Math.Min(choice.ShapeLo, shape);
                    choice.ShapeHi = double.IsNaN(choice.ShapeHi) ? shape : Math.Max(choice.ShapeHi, shape);
                    var tried = new DescentTry { PeAlt = pe, Shape = shape, DeorbitDv = deorbit, Result = flown };
                    if (bestHere == null || tried.Dv < bestHere.Dv) bestHere = tried;
                }
                if (bestHere != null)
                {
                    if (choice.Best == null || bestHere.Dv < choice.Best.Dv) choice.Best = bestHere;
                    if (lastBest.HasValue && bestHere.Dv > lastBest.Value) break;
                    lastBest = bestHere.Dv;
                }
                if (watch.Elapsed.TotalMilliseconds > budgetMs) break;
            }
            choice.Ms = watch.Elapsed.TotalMilliseconds;
            if (choice.Best == null)
                choice.Error = "no Descent Shape clears the terrain on the approach by " + BrakeGuidance.ClearMargin(0).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " m from a periapsis of " + Whole(peLowest) + " to " + Whole(peHighest) + " m";
            return choice;
        }

        /// <summary>From the craft's state now (braking begins): <paramref name="shapes"/> each flown, the cheapest clearing one chosen.</summary>
        public static DescentChoice FromState(BrakeCraft craft, BrakeWorld world, BrakeStart start, IList<double> shapes)
        {
            var choice = new DescentChoice();
            foreach (var shape in shapes)
            {
                var flown = Simulate(craft, world, start, shape);
                choice.Tried++;
                if (!flown.Clears) continue;
                choice.ShapeLo = double.IsNaN(choice.ShapeLo) ? shape : Math.Min(choice.ShapeLo, shape);
                choice.ShapeHi = double.IsNaN(choice.ShapeHi) ? shape : Math.Max(choice.ShapeHi, shape);
                var tried = new DescentTry { PeAlt = double.NaN, Shape = shape, Result = flown };
                if (choice.Best == null || tried.Dv < choice.Best.Dv) choice.Best = tried;
            }
            if (choice.Best == null) choice.Error = "no Descent Shape clears the terrain on the approach from here";
            return choice;
        }

        private static string Whole(double x)
        {
            return Math.Round(x).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
