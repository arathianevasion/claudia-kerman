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
        /// <summary>The terrain under the whole fall from the deorbit burn, more coarsely (<see cref="BrakePath.FallDue"/>); null for <see cref="Ground"/>.</summary>
        public TerrainProfile FallGround;
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
        /// <summary>
        /// m above sea level as the craft passes <see cref="S"/>. Past where braking stopped it comes straight down, so the last
        /// point keeps the height braking stopped at: the highest it is there, which is what <c>land</c>'s path guard reads (#712).
        /// </summary>
        public double Alt;
        /// <summary>The terrain's height there, m above sea level.</summary>
        public double Ground;
        /// <summary>m/s across the ground.</summary>
        public double VH;
        /// <summary>m/s up, and s since the simulation started.</summary>
        public double VUp;
        public double T;
    }

    internal sealed class BrakeResult
    {
        public double Shape;
        public readonly List<BrakePoint> Path = new List<BrakePoint>();
        /// <summary>Delta-v the descent used from where it started to touchdown, m/s.</summary>
        public double DvUsed;
        /// <summary>m to the site where braking began; NaN if it never did.</summary>
        public double BrakeStartS = double.NaN;
        /// <summary>The craft's state where braking began (braking from there), for BRAKEPATH's prediction at braking start; null if it never did.</summary>
        public BrakeStart BrakingFrom;
        /// <summary>m to the site and m above the ground where braking ended (under 1 m/s across the ground); NaN if it never did.</summary>
        public double BrakeEndS = double.NaN;
        public double BrakeEndAgl = double.NaN;
        /// <summary>m above sea level where braking ended; NaN if it never did.</summary>
        public double BrakeEndAlt = double.NaN;
        /// <summary>m to the site where the braking descent first came down to <see cref="BrakeGuidance.HEnd"/> over the ground; NaN if it never did.</summary>
        public double HEndS = double.NaN;
        /// <summary>
        /// The least height over the ground the braking descent would stop at, falling, if it thrust fully from now after its turn
        /// lag, m, while over <see cref="BrakeGuidance.HEnd"/> (#547); infinite if it never fell there.
        /// </summary>
        public double LeastStopRoom = double.PositiveInfinity;
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

        /// <summary>The path stays the margin over the terrain while braking, could always stop its fall over the ground (#547), and touches down softly.</summary>
        public bool Clears
        {
            get { return Failure == null && LeastSpare >= 0 && LeastStopRoom >= 0; }
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
        /// <summary>
        /// The periapses passed over because the fall to them came inside the margin over the ground before braking was due
        /// (<see cref="BrakePath.FallDue"/>), and the farthest from the site that happened, m (NaN if none).
        /// </summary>
        public int FallBlocked;
        public double FallBlockedS = double.NaN;
        /// <summary>The highest periapsis passed over for its fall, m above sea level (NaN if none).</summary>
        public double FallBlockedPe = double.NaN;
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
        /// clearance over the terrain at the look-ahead points and the highest ground on to where it stops. The path is a point
        /// every <see cref="BrakeWorld.PathSpacing"/> m along the track, one where braking stops, and where it came down
        /// (<see cref="BrakePoint.Alt"/>); a cleared one keeps <see cref="FlyableFailure"/>'s rule. <paramref name="ticks"/>, if given,
        /// gets every tick's state.
        /// </summary>
        public static BrakeResult Simulate(BrakeCraft craft, BrakeWorld world, BrakeStart start, double shape, List<BrakePoint> ticks = null)
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
                // Braking also steers by the highest ground on to where it stops (land_path_ground_ahead); the fall, as land_descend's,
                // only by the look-ahead, so the safety net fires where the block's would.
                if (braking || start.Braking) h = Math.Min(h, y - ground.Max(s - toGo, s));
                if (!braking && (start.Braking || BrakeGuidance.BrakeDue(s, Math.Abs(vx), -vy, h, BrakeGuidance.BrakeAccel(aMax, g), margin)))
                {
                    braking = true;
                    hStart = Math.Max(BrakeGuidance.HEnd, h);
                    v0 = vx;
                    vTop = Math.Max(50, vx);
                    result.BrakeStartS = s;
                    result.BrakingFrom = new BrakeStart { S = s, Alt = y, VH = vx, VUp = vy, Braking = true };
                }
                double wantAl = 0, wantUp = 0;
                if (braking)
                {
                    vTop = BrakeGuidance.VTop(vTop, Math.Abs(vx));
                    var w = BrakeGuidance.Guide(s, vx, vy, h, hStart, v0, shape, vTop, Math.Max(aMax, 1e-6), g, world.Radius + y, world.Touchdown, margin, craft.LagS);
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
                    result.BrakeEndAlt = y;
                    // The top of the straight-down descent: without it the line from the last point to touchdown read as a dive
                    // across the last few hundred m, 1 km under the craft 190 m from Minmus Flats (#712).
                    result.Path.Add(new BrakePoint { S = s, Alt = y, Ground = ground.At(s), VH = vx, VUp = vy, T = t });
                }
                // Where a fall stopped at full thrust after the turn lag would end, over the ground (#547).
                if (braking && vy < 0 && aMax > g && radar > BrakeGuidance.HEnd)
                    result.LeastStopRoom = Math.Min(result.LeastStopRoom, radar + vy * craft.LagS - vy * vy / (2 * (aMax - g)));
                if (braking && double.IsNaN(result.HEndS) && radar <= BrakeGuidance.HEnd) result.HEndS = s;
                if (s <= nextPoint && double.IsNaN(result.BrakeEndS))
                {
                    result.Path.Add(new BrakePoint { S = s, Alt = y, Ground = ground.At(s), VH = vx, VUp = vy, T = t });
                    nextPoint -= world.PathSpacing;
                }
                if (ticks != null) ticks.Add(new BrakePoint { S = s, Alt = y, Ground = ground.At(s), VH = vx, VUp = vy, T = t });
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
            var ended = !double.IsNaN(result.BrakeEndS);
            result.Path.Add(new BrakePoint { S = s, Alt = ended ? result.BrakeEndAlt : y, Ground = ground.At(s), VUp = vy, T = t });
            if (radar > 0) result.Failure = "still flying after 1500 s";
            else if (lastSpeed > 5) result.Failure = "hit the ground at " + lastSpeed.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
            else result.Failure = FlyableFailure(result.Path, world, ended ? result.Path.Count - 1 : result.Path.Count);
            return result;
        }

        /// <summary>
        /// The rule a cleared path keeps (#712): <c>land</c>'s braking asks for no push below the horizon (<see cref="BrakeGuidance.Guide"/>),
        /// so gravity is the only pull down, and between any two of the first <paramref name="count"/> points the path may drop no more
        /// than a fall from the first's vertical speed under the gravity there gives over the time between them (1 m spare). A path
        /// that drops faster asks for a fall the craft can't fly, so it doesn't clear: why, or null.
        /// </summary>
        public static string FlyableFailure(IList<BrakePoint> path, BrakeWorld world, int count)
        {
            for (var i = 1; i < Math.Min(count, path.Count); i++)
            {
                var a = path[i - 1];
                var b = path[i];
                var t = b.T - a.T;
                var g = world.Mu / Math.Pow(world.Radius + Math.Min(a.Alt, b.Alt), 2);
                if (a.Alt - b.Alt > -a.VUp * t + 0.5 * g * t * t + 1)
                    return "its path falls " + Whole(a.Alt - b.Alt) + " m in " + t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + " s " + Whole(a.S) + " m out, faster than gravity alone";
            }
            return null;
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

        /// <summary>m between the points <see cref="FallDue"/> checks along the fall.</summary>
        public const double FallStep = 100;
        /// <summary>
        /// The clearance over the margin the fall must keep before braking is due, m: the deorbit burn may leave periapsis a little
        /// low (25 m on Minmus Flats, #530), and the block reads the look-ahead along a straight line off the curving ground.
        /// </summary>
        public const double FallSlack = 100;

        /// <summary>
        /// Where <c>land_brake_due</c> first fires on the unbraked fall from <paramref name="from"/> m before the site down the
        /// transfer to a periapsis of <paramref name="rPe"/> (as <see cref="OnTransfer"/>), the block's own check: the stopping
        /// distance, or the ground-close safety net, the look-ahead (<see cref="BrakeGuidance.Lookahead"/>) inside the margin
        /// (plus <see cref="FallSlack"/>). The state there, braking from it; <paramref name="early"/> when the safety net fired
        /// first, so braking would begin at orbital speed short of where the simulation plans it (#530). Analytic along the
        /// conic, so it holds over the whole fall, where <see cref="Simulate"/>'s flat frame would drift.
        /// </summary>
        public static BrakeStart FallDue(BrakeCraft craft, BrakeWorld world, double r0, double rPe, double vRotAlong, double from, out bool early)
        {
            var ground = world.FallGround ?? world.Ground;
            var aMax = craft.ThrustN / craft.MassKg;
            early = false;
            BrakeStart at = null;
            for (var before = from; before > -DescentApproach.PastSiteM; before -= FallStep)
            {
                at = OnTransfer(world.Mu, world.Radius, r0, rPe, before, vRotAlong);
                var g = world.Mu / Math.Pow(world.Radius + at.Alt, 2);
                var h = at.Alt - ground.At(before);
                foreach (var d in BrakeGuidance.Lookahead(at.VH, before)) h = Math.Min(h, at.Alt - ground.At(before - d));
                if (before <= BrakeGuidance.BrakeDistance(at.VH, BrakeGuidance.BrakeAccel(aMax, g), world.Margin, 0)) break;
                if (h < BrakeGuidance.ClearMargin(-at.VUp) + FallSlack)
                {
                    early = true;
                    break;
                }
            }
            if (at != null) at.Braking = true;
            return at;
        }

        /// <summary>
        /// How far before the site the fall to a periapsis of <paramref name="rPe"/> off an orbit of radius <paramref name="r0"/>
        /// is under <paramref name="ceilingAlt"/> m above sea level, m along the surface: the stretch whose terrain <see cref="FallDue"/>
        /// must see; at most half the orbit, from the deorbit burn.
        /// </summary>
        public static double FallReach(double bodyRadius, double r0, double rPe, double ceilingAlt)
        {
            var e = (r0 - rPe) / (r0 + rPe);
            if (e <= 0) return 0;
            var p = rPe * (1 + e);
            var cos = (p / (bodyRadius + ceilingAlt) - 1) / e;
            if (cos >= 1) return 0;
            return Math.Acos(Math.Max(-1, cos)) * bodyRadius;
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
        /// <paramref name="peHighest"/>, each against <paramref name="shapes"/> (all of <see cref="Shapes"/> if null), braking flown
        /// from where the fall down the transfer off an orbit of radius <paramref name="r0"/> brings it due (<see cref="FallDue"/>); it keeps the clearing pair with the least delta-v and
        /// stops once clearing pairs stop getting cheaper, or at <paramref name="budgetMs"/>.
        /// </summary>
        public static DescentChoice Search(BrakeCraft craft, BrakeWorld world, double r0, double vRotAlong, double peLowest, double peHighest,
            IList<double> shapes = null, double budgetMs = BudgetMs)
        {
            var watch = Stopwatch.StartNew();
            if (SearchBudget.Unlimited) budgetMs = double.PositiveInfinity;
            var choice = new DescentChoice();
            shapes = shapes ?? Shapes;
            double? lastBest = null;
            for (var pe = peLowest; pe <= peHighest + 1e-6; pe += PeStep)
            {
                var rPe = world.Radius + pe;
                // The whole fall first: one that comes inside the margin over the ground before braking is due would start braking
                // there at orbital speed, far short of the path simulated below (#530).
                bool early;
                var due = FallDue(craft, world, r0, rPe, vRotAlong, (world.FallGround ?? world.Ground).FarthestS, out early);
                if (early)
                {
                    choice.FallBlocked++;
                    choice.FallBlockedS = double.IsNaN(choice.FallBlockedS) ? due.S : Math.Max(choice.FallBlockedS, due.S);
                    choice.FallBlockedPe = double.IsNaN(choice.FallBlockedPe) ? pe : Math.Max(choice.FallBlockedPe, pe);
                    if (watch.Elapsed.TotalMilliseconds > budgetMs) break;
                    continue;
                }
                // Braking flown from where the fall brings it due, on the conic: the state the block predicts again from as braking
                // begins (BRAKEPATH). The flat frame's fall drifted about 1 m/s off it over 6 km, enough to move the braking's
                // figure by a third on a rough approach, so the re-check refused what the plan had chosen (#546).
                var deorbit = DeorbitDv(world.Mu, r0, rPe);
                DescentTry bestHere = null;
                foreach (var shape in shapes)
                {
                    var flown = Simulate(craft, world, due, shape);
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
                    + " m from a periapsis of " + Whole(peLowest) + " to " + Whole(peHighest) + " m"
                    // #546: which periapses the fall ruled out, so a refusal over higher ones isn't read as the fall's.
                    + (choice.FallBlocked > 0 ? "; the fall from the deorbit burn passes too low over the ground up to " + Whole(choice.FallBlockedS / 1000)
                        + " km before the site from a periapsis under " + Whole(choice.FallBlockedPe + PeStep) + " m" : "");
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
