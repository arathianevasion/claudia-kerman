// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>One tick of <see cref="BrakeGuidance.Guide"/>'s answer: m/s² wanted along the track and up, with the trace's figures.</summary>
    internal struct BrakeDemand
    {
        public double Along;
        public double Up;
        public double HRef;
        public double ToGo;
    }

    /// <summary>
    /// <c>land</c>'s braking laws, ported line for line from land.ks's pure functions (#511, ADR 0011) so <see cref="BrakePath"/>
    /// predicts the descent the block flies. Along the track only: the simulation flies a site on its track, so the across-track
    /// terms (<c>land_cross_accel</c>) are 0. LandAscendTests pins the port to the kOS laws on the FlyOver profiles; a change to
    /// either side must change both.
    /// </summary>
    internal static class BrakeGuidance
    {
        /// <summary>land_h_end: where the Descent Shape ends, m above the terrain.</summary>
        public const double HEnd = 200;

        /// <summary>land_stop_distance.</summary>
        public static double StopDistance(double speed, double accel, double margin)
        {
            return speed * speed / (2 * accel) * margin;
        }

        /// <summary>land_vh_target.</summary>
        public static double VhTarget(double dist, double accel, double margin)
        {
            var aOk = accel / margin;
            return Math.Sqrt(2 * aOk * Math.Max(0, dist) + 4 * aOk * aOk) - 2 * aOk;
        }

        /// <summary>land_shape_height: the Descent Shape's curve.</summary>
        public static double ShapeHeight(double vH, double v0, double hStart, double hEnd, double shape)
        {
            var left = Math.Min(1, Math.Max(0, vH / Math.Max(0.001, v0)));
            return hEnd + (hStart - hEnd) * Math.Pow(left, shape);
        }

        /// <summary>land_shape_ref.</summary>
        public static double ShapeRef(double toGo, double vH, double v0, double hStart, double shape)
        {
            if (toGo < 30) return 0;
            return ShapeHeight(vH, v0, Math.Max(HEnd, hStart), HEnd, shape);
        }

        /// <summary>land_clear_margin: the clearance the path keeps, m.</summary>
        public static double ClearMargin(double vDown)
        {
            return Math.Max(300, 2 * Math.Max(0, vDown));
        }

        /// <summary>
        /// The clearance the predicted path must keep over a point of the approach whose ground is <paramref name="overSite"/> m
        /// above the site's, m: <see cref="ClearMargin"/> over the terrain on the approach, but over ground at the site's own level
        /// the Descent Shape's end height (<see cref="HEnd"/>, 200 m over the site) is the plan, so 150 m is enough there, rising to
        /// 300 m over ground 150 m higher; never under 2 s of the descent rate. A rim or ridge keeps the whole margin.
        /// </summary>
        public static double PathMargin(double vDown, double overSite)
        {
            return Math.Max(2 * Math.Max(0, vDown), Math.Min(300, Math.Max(0.75 * HEnd, 0.75 * HEnd + overSite)));
        }

        /// <summary>land_lookahead: m along the track at which the descent reads the terrain ahead.</summary>
        public static IEnumerable<double> Lookahead(double vH, double toGo)
        {
            var most = Math.Max(0, toGo);
            foreach (var t in new[] { 0.5, 1, 2 }) yield return Math.Min(most, vH * t);
            for (var i = 1; i < 21; i++) yield return Math.Min(most, vH * 3 * i);
        }

        /// <summary>land_vv_target.</summary>
        public static double VvTarget(double h, double hRef, double aStop, double touchdown)
        {
            if (h < hRef) return Math.Min(20, 0.1 * (hRef - h));
            var fastest = Math.Max(touchdown, Math.Min(100, Math.Sqrt(2 * aStop * Math.Max(0, h))));
            return -Math.Min(fastest, Math.Max(touchdown, 0.1 * (h - hRef)));
        }

        /// <summary>land_max_tilt.</summary>
        public static double MaxTilt(double h)
        {
            return Math.Min(90, 5 + 2 * Math.Max(0, h));
        }

        /// <summary>land_axis_accel.</summary>
        public static double AxisAccel(double s, double vAhead, double aMax, double margin, double vTop = 1e9)
        {
            var a = -vAhead / 4;
            if (s > 0)
            {
                var vWant = VhTarget(s, aMax, margin);
                var aOk = aMax / margin;
                var slope = aOk / Math.Max(0.001, vWant + 2 * aOk);
                if (vWant > vTop)
                {
                    vWant = vTop;
                    slope = 0;
                }
                a = (vWant - vAhead) / 2 - slope * Math.Max(0, vAhead);
                if (vAhead > vWant) a = Math.Min(a, -vAhead * vAhead / (2 * s));
            }
            return Math.Max(-aMax, Math.Min(aMax, a));
        }

        /// <summary>
        /// land_guide with the site on the track (c 0, v_cr 0): <paramref name="s"/> m to the site along it, <paramref name="vAl"/>
        /// and <paramref name="vUp"/> m/s, <paramref name="h"/> m of clearance, the braking start's <paramref name="hStart"/> and
        /// <paramref name="v0"/>, the Descent Shape, <paramref name="vTop"/>, <paramref name="aMax"/> and <paramref name="g"/> m/s² and
        /// <paramref name="radius"/> m from the body's centre.
        /// </summary>
        public static BrakeDemand Guide(double s, double vAl, double vUp, double h, double hStart, double v0, double shape, double vTop,
            double aMax, double g, double radius, double touchdown, double margin)
        {
            var vH = Math.Abs(vAl);
            var gEff = g - vH * vH / radius;
            var aMost = 0.95 * aMax;
            var aHMost = Math.Sqrt(Math.Max(0.01, aMost * aMost - Math.Pow(Math.Max(0, gEff), 2)));
            var toGo = Math.Max(Math.Max(0, s), StopDistance(vH, aHMost, margin));
            var aOk = aHMost / margin;
            var brakingNeeds = aOk * vH / (vH + 2 * aOk);
            var aStop = Math.Max(0.2, 0.5 * (Math.Sqrt(Math.Max(0, aMost * aMost - brakingNeeds * brakingNeeds)) - g));
            var hRef = ShapeRef(toGo, vH, v0, hStart, shape);
            var vvWant = VvTarget(h, hRef, aStop, touchdown);
            // land_split_accel.
            var up = Math.Min(aMost, Math.Max(0, gEff + 0.5 * (vvWant - vUp)));
            var horizontal = Math.Min(aMost, Math.Sqrt(Math.Max(0, aMost * aMost - up * up)));
            var al = AxisAccel(s, vAl, horizontal, margin, vTop);
            
            if (vH > 50 && vUp > 0 && vvWant < vUp && vAl > 0) al = Math.Min(al, -Math.Min(horizontal, brakingNeeds));
            var tilt = MaxTilt(h);
            if (tilt < 90)
            {
                var most = up * Math.Tan(tilt * Math.PI / 180);
                if (Math.Abs(al) > most) al = al * most / Math.Abs(al);
            }
            return new BrakeDemand { Along = al, Up = up, HRef = hRef, ToGo = toGo };
        }

        /// <summary>land_brake_accel.</summary>
        public static double BrakeAccel(double aMax, double g)
        {
            return Math.Sqrt(Math.Max(0.01, Math.Pow(0.95 * aMax, 2) - g * g));
        }

        /// <summary>land_brake_distance.</summary>
        public static double BrakeDistance(double vH, double aBrake, double margin, double turnLeft)
        {
            var aOk = aBrake / margin;
            return vH * vH / (2 * aOk) + 2 * vH + vH * Math.Max(0, turnLeft);
        }

        /// <summary>land_brake_due, for a site on the track (dist and ahead both <paramref name="s"/>).</summary>
        public static bool BrakeDue(double s, double vH, double vDown, double h, double aBrake, double margin)
        {
            return s <= BrakeDistance(vH, aBrake, margin, 0) || s < 0 || h < ClearMargin(vDown);
        }

        /// <summary>land_turn_seconds.</summary>
        public static double TurnSeconds(double angle, double topRate, double turnS)
        {
            var left = Math.Max(0, angle - 5);
            if (topRate <= 0 || left <= 0) return 0;
            var ramp = Math.Max(0.001, turnS - 190 / topRate);
            var alpha = topRate / ramp;
            if (Math.Sqrt(alpha * left) <= topRate) return 2 * Math.Sqrt(left / alpha);
            return left / topRate + ramp;
        }

        /// <summary>
        /// land_steer in the track's plane: the unit direction (along, up) for the demand, the last direction under 5% of
        /// <paramref name="aMax"/>, and within 45° of surface retrograde while over 50 m/s across the ground; and the m/s² to thrust
        /// along it (<paramref name="mag"/>), which keeps the demand's upward part where the cone turned it.
        /// </summary>
        public static void Steer(double wantAl, double wantUp, double lastAl, double lastUp, double vAl, double vUp, double aMax,
            out double al, out double up, out double mag)
        {
            mag = Math.Sqrt(wantAl * wantAl + wantUp * wantUp);
            if (mag < 0.05 * aMax)
            {
                al = lastAl;
                up = lastUp;
                return;
            }
            al = wantAl / mag;
            up = wantUp / mag;
            var speed = Math.Sqrt(vAl * vAl + vUp * vUp);
            if (Math.Abs(vAl) <= 50) return;
            var rAl = -vAl / speed;
            var rUp = -vUp / speed;
            var c = al * rAl + up * rUp;
            var cos45 = Math.Cos(Math.PI / 4);
            if (c >= cos45) return;
            var pAl = al - c * rAl;
            var pUp = up - c * rUp;
            var pMag = Math.Sqrt(pAl * pAl + pUp * pUp);
            if (pMag < 1e-6)
            {
                al = rAl;
                up = rUp;
            }
            else
            {
                var k = Math.Sin(Math.PI / 4) / pMag;
                al = cos45 * rAl + k * pAl;
                up = cos45 * rUp + k * pUp;
            }
            if (up > 0.01) mag = Math.Max(mag, wantUp / up);
        }
    }
}
