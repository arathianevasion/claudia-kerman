// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// <c>land</c>'s steering and throttle for a braking tick (#865, ADR 0014), called once a tick through the block-internal
    /// <c>ADDONS:CLAUDIA:LANDSTEER</c> (<see cref="Tick"/>); kOS still locks the steering and throttle to what it gives. It takes
    /// the demand land_guide (or LandGuard's fallback) asks for, limits its lean, points the thrust (<see cref="Steer"/>, the
    /// old land_steer) and sets the throttle. In the final descent (the demand's <c>h_ref</c> 0) it caps the lean at
    /// <see cref="TerminalLeanDeg"/> below <see cref="CapBelowM"/> or slower across the ground than <see cref="CapSlowerMs"/>
    /// (the player's call, 2026-11-07: capped from 1 km up at 12 m/s across, a Minmus landing hovered 300 s and landed 230 m
    /// long), turns the commanded direction no faster than the craft turns, thrusts only for the
    /// vertical need while more than <see cref="OffTargetDeg"/> off, and below <see cref="SettleBelowM"/> holds its height
    /// while the craft swings (<see cref="SettleErrorDeg"/>, <see cref="SettleRateDegS"/>) for up to <see cref="SettleMaxS"/>
    /// or <see cref="SettleDvShare"/> of its delta-v. On Ike, at about 15:1 thrust, the craft chased a swinging direction to
    /// touchdown 43° off at 21°/s and tipped to 70° (m20261107-055424-908b).
    /// </summary>
    public static class LandSteer
    {
        /// <summary>The most the thrust may lean from vertical in the final descent, degrees.</summary>
        public const double TerminalLeanDeg = 20;
        /// <summary>m over the ground under which the final descent's lean is capped.</summary>
        public const double CapBelowM = 50;
        /// <summary>m/s across the ground under which the final descent's lean is capped, at any height.</summary>
        public const double CapSlowerMs = 2;
        /// <summary>Degrees off its target over which, in the final descent, the throttle gives only the vertical need.</summary>
        public const double OffTargetDeg = 15;
        /// <summary>m over the ground under which the final descent settles before touchdown.</summary>
        public const double SettleBelowM = 10;
        /// <summary>Degrees off target over which the craft is swinging, for the settle.</summary>
        public const double SettleErrorDeg = 10;
        /// <summary>Turn rate (degrees a second) over which the craft is swinging, for the settle.</summary>
        public const double SettleRateDegS = 5;
        /// <summary>Seconds the settle may hold, in all.</summary>
        public const double SettleMaxS = 10;
        /// <summary>The share of the delta-v left when the settle began that it may spend.</summary>
        public const double SettleDvShare = 0.02;

        private const double Deg = Math.PI / 180;

        /// <summary>
        /// The most the thrust may lean from vertical (degrees) <paramref name="h"/> m over the ground: upright for the last few
        /// metres, free from about 40 m up (<see cref="BrakeGuidance.MaxTilt"/>); where the final descent is
        /// <paramref name="capped"/>, never over <see cref="TerminalLeanDeg"/>.
        /// </summary>
        public static double MaxLean(double h, bool capped)
        {
            var tilt = BrakeGuidance.MaxTilt(h);
            return capped ? Math.Min(TerminalLeanDeg, tilt) : tilt;
        }

        /// <summary>
        /// The demand's sideways part (<paramref name="al"/>, <paramref name="cr"/>, m/s²) cut so the demand leans no more than
        /// <paramref name="tilt"/> degrees from its upward part <paramref name="up"/>. Its vertical part stands.
        /// </summary>
        public static void LimitLean(ref double al, ref double cr, double up, double tilt)
        {
            if (tilt >= 90) return;
            var most = up * Math.Tan(tilt * Deg);
            var sideways = Math.Sqrt(al * al + cr * cr);
            if (sideways > most)
            {
                al = al * most / sideways;
                cr = cr * most / sideways;
            }
        }

        /// <summary>
        /// land's lean limit on a demand (al, cr, up m/s², <paramref name="h"/> m over the ground), for the flight (BRAKEPATH calls it too, #886). Uncapped, and in the last few metres, the sideways part is cut to <see cref="BrakeGuidance.MaxTilt"/>
        /// as before. Where the final descent is <paramref name="capped"/> and <see cref="TerminalLeanDeg"/> binds, the upward part is raised so the demand
        /// leans that far and still brakes across the ground, but never past what holds its height (gravity <paramref name="g"/>
        /// less half its vertical speed <paramref name="vUp"/>, m/s, as the settle holds it; nor 0.95 of <paramref name="aMax"/>):
        /// falling fast it brakes hard, and it never climbs. Cutting the sideways part alone left a craft falling with no upward
        /// demand drifting 40-145 m past the site (#886: the bound is land_guide's, gravity plus half the gap from its wanted
        /// vertical speed <paramref name="vvWant"/>, m/s, so lift stops a fall faster than that but never holds height: slow at
        /// 260 m over Minmus Flats it hovered 40 s); raising it without a bound held one climbing over Minmus Flats for 900 s.
        /// </summary>
        public static void Lean(ref double al, ref double cr, ref double up, double h, bool capped, double aMax, double g, double vUp, double vvWant = 0)
        {
            if (capped && BrakeGuidance.MaxTilt(h) > TerminalLeanDeg)
                up = Math.Max(up, Math.Min(Math.Min(0.95 * aMax, g + 0.5 * (vvWant - vUp)), Math.Sqrt(al * al + cr * cr) / Math.Tan(TerminalLeanDeg * Deg)));
            LimitLean(ref al, ref cr, up, MaxLean(h, capped));
        }

        /// <summary>
        /// The steering for a demanded acceleration (#511), in the braking frame: the last direction (<paramref name="last"/>)
        /// while the demand is under 5% of <paramref name="aMax"/>, so a tiny demand flipping each tick doesn't swing the craft;
        /// otherwise the demand, held within 45 degrees of surface retrograde (from <paramref name="vel"/>, m/s) while over
        /// 50 m/s across the ground. Gives a unit direction and the m/s² to thrust along it: the demand's, or where the cone
        /// turned it, the demand's part along the turned direction (none for a demand ahead, #530), or enough to keep its
        /// upward part, whichever is more. No thrust, or a demand zero or not a number, holds the last direction with none (#595).
        /// </summary>
        public static double[] Steer(double[] want, double[] last, double[] vel, double aMax, out double mag)
        {
            mag = Math.Sqrt(want[0] * want[0] + want[1] * want[1] + want[2] * want[2]);
            if (!(aMax > 0) || !(mag > 0) || mag > 1e15)
            {
                mag = 0;
                return last;
            }
            if (mag < 0.05 * aMax) return last;
            var d = new[] { want[0] / mag, want[1] / mag, want[2] / mag };
            if (Math.Sqrt(vel[0] * vel[0] + vel[1] * vel[1]) <= 50) return d;
            var speed = Math.Sqrt(vel[0] * vel[0] + vel[1] * vel[1] + vel[2] * vel[2]);
            var back = new[] { -vel[0] / speed, -vel[1] / speed, -vel[2] / speed };
            var c = Dot(d, back);
            var cos45 = Math.Cos(Math.PI / 4);
            if (c >= cos45) return d;
            // The part of the demand square to retrograde, laid 45 degrees off it.
            var side = new[] { d[0] - c * back[0], d[1] - c * back[1], d[2] - c * back[2] };
            var sideMag = Math.Sqrt(Dot(side, side));
            var turned = back;
            if (sideMag >= 0.000001)
            {
                var k = Math.Sin(Math.PI / 4) / sideMag;
                turned = new[] { cos45 * back[0] + k * side[0], cos45 * back[1] + k * side[1], cos45 * back[2] + k * side[2] };
            }
            mag = Math.Max(0, Dot(want, turned));
            if (turned[2] > 0.01) mag = Math.Max(mag, want[2] / turned[2]);
            return turned;
        }

        /// <summary>
        /// One braking tick (<c>ADDONS:CLAUDIA:LANDSTEER</c>): <c>want</c> (land_guide's or LandGuard's demand: <c>al</c>,
        /// <c>cr</c>, <c>up</c> m/s², <c>h</c> m over the ground, <c>h_ref</c>, 0 in the final descent), <c>last</c> (the last
        /// tick's answer), the velocity <c>v_al</c>, <c>v_cr</c>, <c>v_up</c> (m/s), <c>a_max</c> and <c>g</c> (m/s²),
        /// <c>facing</c> (the craft's nose in the frame: <c>al</c>, <c>cr</c>, <c>up</c>; none: on target), <c>rate</c> (its
        /// turn rate, °/s), <c>top_rate</c> (TURNTIME's, °/s; 0: no limit), <c>dv_left</c> (m/s) and <c>ut</c>. Gives the
        /// direction (<c>al</c>, <c>cr</c>, <c>up</c>), <c>mag</c> (m/s²), <c>throttle</c>, <c>error</c> and <c>lean</c>
        /// (degrees), <c>settling</c>, <c>settle_s</c>, and the state the next tick reads back from <c>last</c>.
        /// </summary>
        public static IDictionary<string, object> Tick(IDictionary<string, object> input)
        {
            var want = Map(input, "want");
            var last = Map(input, "last");
            var h = Number(want, "h", 1e9);
            var terminal = Number(want, "h_ref", 1) <= 0;
            var aMax = Number(input, "a_max", 0);
            var vUp = Number(input, "v_up", 0);
            var ut = Number(input, "ut", 0);
            var al = Number(want, "al", 0);
            var cr = Number(want, "cr", 0);
            var up = Number(want, "up", 0);
            var vel = new[] { Number(input, "v_al", 0), Number(input, "v_cr", 0), vUp };
            // The lean cap's zone: near the ground, or slow across it (the player's call, #865).
            var capped = LeanCapApplies(terminal, h, Math.Sqrt(vel[0] * vel[0] + vel[1] * vel[1]));
            // The vertical need, before the lean limit adds lift to brake sideways: what the throttle gives while off target.
            var need = up;
            var tilt = MaxLean(h, capped);
            Lean(ref al, ref cr, ref up, h, capped, aMax, Number(input, "g", 0), vUp, Number(want, "vv", 0));
            var lastDir = new[] { Number(last, "al", -1), Number(last, "cr", 0), Number(last, "up", 0) };
            var facingMap = Map(input, "facing");
            double mag;
            var dir = Steer(new[] { al, cr, up }, lastDir, vel, aMax, out mag);
            if (capped) dir = LeanCapped(dir, tilt);
            var facing = facingMap.Count > 0 ? new[] { Number(facingMap, "al", 0), Number(facingMap, "cr", 0), Number(facingMap, "up", 1) } : null;
            var lean = facing != null ? Angle(facing, new[] { 0.0, 0, 1 }) : Angle(dir, new[] { 0.0, 0, 1 });

            // The settle (below SettleBelowM in the final descent): its time and delta-v count from the last tick's state.
            var settleS = Number(last, "settle_s", 0);
            var settleUt = Number(last, "settle_ut", -1);
            var dv0 = Number(last, "settle_dv0", -1);
            var done = Flag(last, "settle_done");
            var settling = false;
            // Holding its height, and stopping any drift over 2 s within the lean limit, rather than chasing the site.
            var holdAl = -vel[0] / 2;
            var holdCr = -vel[1] / 2;
            var holdUp = Math.Min(0.95 * aMax, Math.Max(0, Number(input, "g", 0) - 0.5 * vUp));
            LimitLean(ref holdAl, ref holdCr, holdUp, tilt);
            var holdMag = Math.Sqrt(holdAl * holdAl + holdCr * holdCr + holdUp * holdUp);
            var holdDir = holdMag > 0 ? new[] { holdAl / holdMag, holdCr / holdMag, holdUp / holdMag } : new[] { 0.0, 0, 1 };
            if (terminal && h < SettleBelowM && !done)
            {
                // Off target: where it would steer, or once holding, where the hold steers.
                var off = facing != null ? Angle(facing, settleUt >= 0 ? holdDir : dir) : 0;
                if (off > SettleErrorDeg || Number(input, "rate", 0) > SettleRateDegS)
                {
                    var dvLeft = Number(input, "dv_left", 0);
                    if (dv0 < 0) dv0 = dvLeft;
                    if (settleUt >= 0) settleS += Math.Max(0, ut - settleUt);
                    if (settleS >= SettleMaxS || (dv0 > 0 && dv0 - dvLeft >= SettleDvShare * dv0)) done = true;
                    else settling = true;
                }
            }
            if (settling)
            {
                need = holdUp;
                mag = holdMag;
                dir = holdDir;
            }
            // Turning no faster than the craft, even onto the cap as it enters the zone: the cap binds once it is there.
            if (terminal) dir = Slewed(Number(last, "ut", -1), ut, lastDir, dir, Number(input, "top_rate", 0));

            var pointing = Dot(dir, dir) > 0.000001 ? dir : new[] { 0.0, 0, 1 };
            var error = facing != null ? Angle(facing, pointing) : 0;
            double throttle;
            if (terminal && error > OffTargetDeg)
            {
                // Off target: only the vertical need, along where it points now; nothing sideways until it is back on. No thrust
                // or a need not finite gives none (#595).
                var cosLean = Math.Cos(lean * Deg);
                throttle = cosLean > 0 && aMax > 0 && !double.IsNaN(need) && !double.IsInfinity(need) ? Math.Min(1, Math.Max(0, need) / cosLean / aMax) : 0;
            }
            else
            {
                throttle = Math.Min(1, mag / Math.Max(0.001, aMax)) * Math.Max(0, Math.Cos(error * Deg));
            }
            return new Dictionary<string, object>
            {
                { "al", dir[0] }, { "cr", dir[1] }, { "up", dir[2] }, { "mag", mag }, { "throttle", throttle }, { "error", error }, { "lean", lean },
                { "settling", settling }, { "settle_s", settleS }, { "settle_ut", settling ? ut : -1.0 }, { "settle_dv0", dv0 }, { "settle_done", done },
                { "ut", ut },
            };
        }

        /// <summary>Whether land's lean cap (and the want:vv-bound lift) applies: the final descent, below <see cref="CapBelowM"/> or slower than <see cref="CapSlowerMs"/> across the ground. LandSteer.Tick and BrakeGuidance.Steer both ask this.</summary>
        internal static bool LeanCapApplies(bool terminal, double h, double acrossMs)
        {
            return terminal && (h < CapBelowM || acrossMs < CapSlowerMs);
        }

        /// <summary>The direction <paramref name="dir"/> leaning no more than <paramref name="tilt"/> degrees from upright, on its own side.</summary>
        internal static double[] LeanCapped(double[] dir, double tilt)
        {
            var side = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1]);
            var norm = Math.Sqrt(side * side + dir[2] * dir[2]);
            if (norm < 0.000001) return dir;
            if (Math.Atan2(side, dir[2]) <= tilt * Deg) return dir;
            if (side < 0.000001) return new[] { 0.0, 0, 1 };
            var k = Math.Sin(tilt * Deg) / side;
            return new[] { dir[0] * k, dir[1] * k, Math.Cos(tilt * Deg) };
        }

        /// <summary>
        /// <paramref name="to"/>, or the way from <paramref name="from"/> to it that a craft turning at <paramref name="topRate"/>
        /// °/s covers between <paramref name="lastUt"/> and <paramref name="ut"/>: none for a second tick at the same UT. No last
        /// tick or rate: <paramref name="to"/>.
        /// </summary>
        private static double[] Slewed(double lastUt, double ut, double[] from, double[] to, double topRate)
        {
            var dt = ut - lastUt;
            if (lastUt < 0 || !(topRate > 0)) return to;
            if (!(dt > 0)) return from;
            var angle = Angle(from, to);
            var most = topRate * dt;
            if (angle <= most) return to;
            // Spherical interpolation from the last direction, most degrees of the way.
            var a = angle * Deg;
            if (!(Math.Abs(Math.Sin(a)) > 1e-9)) return to;
            var t = most / angle;
            var fa = Math.Sin((1 - t) * a) / Math.Sin(a);
            var fb = Math.Sin(t * a) / Math.Sin(a);
            var f = Unit(from);
            var g = Unit(to);
            return new[] { fa * f[0] + fb * g[0], fa * f[1] + fb * g[1], fa * f[2] + fb * g[2] };
        }

        private static double[] Unit(double[] v)
        {
            var n = Math.Sqrt(Dot(v, v));
            return n > 0 ? new[] { v[0] / n, v[1] / n, v[2] / n } : v;
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        /// <summary>Degrees between two vectors, as kOS's VANG gives them; 0 if either is zero.</summary>
        private static double Angle(double[] a, double[] b)
        {
            var n = Math.Sqrt(Dot(a, a) * Dot(b, b));
            if (!(n > 0)) return 0;
            return Math.Acos(Math.Max(-1, Math.Min(1, Dot(a, b) / n))) / Deg;
        }

        private static IDictionary<string, object> Map(IDictionary<string, object> input, string key)
        {
            object value;
            return input.TryGetValue(key, out value) && value is IDictionary<string, object> ? (IDictionary<string, object>)value : new Dictionary<string, object>();
        }

        private static bool Flag(IDictionary<string, object> input, string key)
        {
            object value;
            return input.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        /// <summary>The number at key, or fallback: land reads the answer's keys unguarded, so nothing here may throw.</summary>
        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value) || value == null || value is string || value is bool || !(value is IConvertible)) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
