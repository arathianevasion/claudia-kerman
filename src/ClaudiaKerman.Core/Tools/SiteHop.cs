// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// <c>site_hop</c>'s flight logic (#1310, ADR 0014), through the block-internal <c>ADDONS:CLAUDIA:SITEHOP</c> (<see cref="Call"/>): a Site Hop's
    /// arc from where the craft is to a target site on the same body, the reach check before liftoff, and each tick of the one burn onto the arc.
    /// The arc is the least-speed ballistic arc (two-body, the body a sphere) from the craft's radius to the target's, aimed where the body will
    /// have turned the site by the time the arc meets the ground. Pure; kOS reads the state, steers, throttles and stages, and <c>land</c> flies the descent.
    /// </summary>
    public static class SiteHop
    {
        /// <summary>The nearest target (m) a Site Hop flies to: nearer, the arc is all climb and fall.</summary>
        public const double MinRangeM = 1000;

        /// <summary>Height above the ground (m) the craft rises straight up to before it turns onto the arc, so it doesn't tip over on its legs.</summary>
        public const double RiseM = 20;

        /// <summary>The burn ends when the velocity still to gain is under this (m/s).</summary>
        public const double CutMps = 0.5;

        /// <summary>The reach check's margin on the burn for steering and gravity losses on the way up.</summary>
        public const double BurnMargin = 1.1;

        /// <summary>
        /// #1322: the touchdown's estimate (liftoff, the burn at full thrust, then the arc's time of flight) may be off by <c>land</c>'s descent;
        /// a liftoff counts as a daylight touchdown only with Daylight at the site this long (s) either side of it.
        /// </summary>
        public const double TouchdownSlackS = 300;

        /// <summary>
        /// One look. From <c>mu</c>, <c>day</c> (s, the body's rotation period; 0 still), the craft's <c>lat</c>, <c>lng</c>, <c>r</c> (m from the body's centre), <c>agl</c> (m),
        /// its orbital velocity as <c>vel_up</c>, <c>vel_north</c>, <c>vel_east</c> (m/s), <c>accel</c> (m/s^2 at full thrust), the site's <c>target_lat</c>, <c>target_lng</c> and
        /// <c>target_r</c> (m, its ground's radius), and, for the check before liftoff only, <c>dv</c> (m/s, the craft's).
        /// Gives <c>why</c> ("" or why the hop can't be flown: under <see cref="MinRangeM"/>, the far side of the body, too little thrust to lift off or, with <c>dv</c>,
        /// too little delta-v), <c>km</c> (along the ground to the site), <c>need</c> (m/s: the burn times <see cref="BurnMargin"/> plus the descent from the arc by
        /// <c>land</c>'s rule, 1.2 times the speed at the ground plus 100), the wanted orbital velocity as <c>speed</c>, <c>heading</c> and <c>pitch</c> (degrees),
        /// its <c>tof</c> (s), and the tick: <c>steer_heading</c>, <c>steer_pitch</c>, <c>throttle</c> (straight up at full under <see cref="RiseM"/>, then along the velocity
        /// still to gain, easing off to end it in half a second), <c>err</c> (m/s still to gain) and <c>done</c> (under <see cref="CutMps"/> to gain, or past the wanted velocity).
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            return Call(input, null);
        }

        /// <summary>
        /// The same, and (#1322, ADR 0011's daylight touchdown) for the look before liftoff (with <c>dv</c> and <c>now</c>, UT) the liftoff: <paramref name="sunAt"/>
        /// gives the sun's elevation at a point of the body (latitude, longitude, UT; degrees, NaN where it can't be read, which holds nothing up), asked at the site. Adds <c>wait</c> (s) and <c>liftoff_ut</c>:
        /// the first liftoff, now or within <see cref="LandingPass.DaylightTurns"/> of the body's day, whose touchdown has Daylight
        /// (<see cref="LandingSiteSearch.MinSunElevationDeg"/>) <see cref="TouchdownSlackS"/> either side; with none, <c>why</c> says so. The ticks of the burn
        /// (no <c>dv</c>) never ask the sun: <c>wait</c> 0.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input, Func<double, double, double, double> sunAt)
        {
            var mu = Number(input, "mu", 0);
            var day = Number(input, "day", 0);
            var lat = Number(input, "lat", 0);
            var lng = Number(input, "lng", 0);
            var r0 = Number(input, "r", 1);
            var r1 = Number(input, "target_r", r0);
            var tLat = Number(input, "target_lat", 0);
            var tLng = Number(input, "target_lng", 0);
            var accel = Number(input, "accel", 0);
            var vel = new[] { Number(input, "vel_up", 0), Number(input, "vel_north", 0), Number(input, "vel_east", 0) };

            var up = Unit(lat, lng);
            var north = new[] { -Sin(lat) * Cos(lng), -Sin(lat) * Sin(lng), Cos(lat) };
            var east = new[] { -Sin(lng), Cos(lng), 0.0 };
            var km = Math.Acos(Clamp(Dot(up, Unit(tLat, tLng)))) * r1 / 1000;

            // The arc, aimed where the site will be when it comes down: a few passes settle the time of flight.
            double tof = 0, speed = 0, gamma = 0, heading = 0, theta = 0;
            for (var pass = 0; pass < 6; pass++)
            {
                var aim = Unit(tLat, tLng + (day > 0 ? tof * 360 / day : 0));
                theta = Math.Acos(Clamp(Dot(up, aim)));
                if (theta < 1e-9 || theta > Math.PI - 1e-3) break;
                var along = Sub(aim, Scale(up, Dot(up, aim)));
                heading = Math.Atan2(Dot(along, east), Dot(along, north));
                gamma = LeastSpeedPitch(mu, r0, r1, theta, out speed);
                tof = FlightTime(mu, r0, speed, gamma, theta);
            }
            var wantH = speed * Math.Cos(gamma);
            var want = new[] { speed * Math.Sin(gamma), wantH * Math.Cos(heading), wantH * Math.Sin(heading) };
            var gain = Sub(want, vel);
            var err = Math.Sqrt(Dot(gain, gain));
            var g = mu / (r0 * r0);
            var vEnd = Math.Sqrt(Math.Max(0, speed * speed + 2 * mu * (1 / r1 - 1 / r0)));
            var need = BurnMargin * err + 1.2 * vEnd + 100;

            var why = "";
            if (km * 1000 < MinRangeM)
                why = string.Format(CultureInfo.InvariantCulture, "the target is {0:0.0} km away: a Site Hop needs at least {1:0} km", km, MinRangeM / 1000);
            else if (speed <= 0)
                why = string.Format(CultureInfo.InvariantCulture, "the target is {0:0.0} km away, on the far side of the body: no one arc reaches it", km);
            else if (input != null && input.ContainsKey("dv") && accel <= g)
                why = string.Format(CultureInfo.InvariantCulture, "the craft can't lift off: its engines give {0:0.00} m/s^2 against {1:0.00} m/s^2 of gravity here", accel, g);
            else if (input != null && input.ContainsKey("dv") && Number(input, "dv", 0) < need)
                why = string.Format(CultureInfo.InvariantCulture, "the target is {0:0.0} km away: a Site Hop there needs about {1:0} m/s and the craft has {2:0} m/s",
                    km, need, Number(input, "dv", 0));

            var now = Number(input, "now", 0);
            var wait = 0.0;
            if (why == "" && sunAt != null && input != null && input.ContainsKey("dv") && input.ContainsKey("now"))
            {
                wait = DaylightWait(ut => sunAt(tLat, tLng, ut), now + speed / Math.Max(accel, 0.1) + tof, day);
                if (wait < 0)
                    why = string.Format(CultureInfo.InvariantCulture, "no daylight touchdown at the site in the next {0:0.0} h: the sun never gets over {1:0} degrees there in that time",
                        LandingPass.DaylightTurns * Math.Abs(day) / 3600, LandingSiteSearch.MinSunElevationDeg);
            }

            double steerHeading, steerPitch, throttle;
            var done = false;
            if (why != "") { steerHeading = Compass(heading); steerPitch = 90; throttle = 0; }
            else if (Number(input, "agl", 0) < RiseM) { steerHeading = Compass(heading); steerPitch = 90; throttle = 1; }
            else
            {
                steerHeading = Compass(Math.Atan2(gain[2], gain[1]));
                steerPitch = Deg(Math.Atan2(gain[0], Math.Sqrt(gain[1] * gain[1] + gain[2] * gain[2])));
                // Under CutMps, or past the wanted velocity (what is left points back against it): ends, never turning back to chase a residual.
                done = err < CutMps || Dot(gain, want) < 0;
                throttle = done ? 0 : Math.Max(0.05, Math.Min(1, err / (Math.Max(accel, 0.1) * 0.5)));
            }
            return new Dictionary<string, object>
            {
                { "why", why }, { "km", km }, { "need", need }, { "speed", speed }, { "heading", Compass(heading) }, { "pitch", Deg(gamma) }, { "tof", tof },
                { "steer_heading", steerHeading }, { "steer_pitch", steerPitch }, { "throttle", throttle }, { "err", err }, { "done", done },
                { "wait", Math.Max(0, wait) }, { "liftoff_ut", now + Math.Max(0, wait) },
            };
        }

        /// <summary>
        /// The least wait (s) before liftoff that puts a touchdown, <paramref name="touchdown"/> UT with no wait, in Daylight <see cref="TouchdownSlackS"/> either side:
        /// stepped a degree of the body's turn at a time over <see cref="LandingPass.DaylightTurns"/> days, then halved down to the second. -1 when none does.
        /// </summary>
        private static double DaylightWait(Func<double, double> sunAt, double touchdown, double day)
        {
            Func<double, bool> lit = w =>
            {
                double before = sunAt(touchdown + w - TouchdownSlackS), after = sunAt(touchdown + w + TouchdownSlackS);
                return (double.IsNaN(before) || before > LandingSiteSearch.MinSunElevationDeg) && (double.IsNaN(after) || after > LandingSiteSearch.MinSunElevationDeg);
            };
            if (lit(0)) return 0;
            var step = Math.Abs(day) / 360;
            if (step <= 0) return -1;
            for (var w = step; w <= LandingPass.DaylightTurns * Math.Abs(day); w += step)
            {
                if (!lit(w)) continue;
                double dark = w - step, light = w;
                while (light - dark > 1)
                {
                    var mid = (dark + light) / 2;
                    if (lit(mid)) light = mid; else dark = mid;
                }
                return light;
            }
            return -1;
        }

        /// <summary>The flight path angle (rad) of the least-speed arc from r0 to r1 across theta, and that speed (0 when none reaches).</summary>
        private static double LeastSpeedPitch(double mu, double r0, double r1, double theta, out double speed)
        {
            speed = 0;
            var best = 0.0;
            double lo = 0.001, hi = Math.PI / 2 - 0.001;
            for (var round = 0; round < 3; round++)
            {
                var step = (hi - lo) / 90;
                for (var gm = lo; gm <= hi + 1e-12; gm += step)
                {
                    var v = ArcSpeed(mu, r0, r1, theta, gm);
                    if (v > 0 && (speed == 0 || v < speed)) { speed = v; best = gm; }
                }
                lo = Math.Max(0.001, best - step);
                hi = Math.Min(Math.PI / 2 - 0.001, best + step);
            }
            return best;
        }

        /// <summary>The speed (m/s) at r0 that, at flight path angle gamma, meets r1 theta further round; 0 when none does.</summary>
        private static double ArcSpeed(double mu, double r0, double r1, double theta, double gamma)
        {
            var d = r0 / r1 * Math.Cos(gamma) - Math.Cos(theta + gamma);
            if (d <= 1e-12) return 0;
            return Math.Sqrt(mu * (1 - Math.Cos(theta)) / (r0 * Math.Cos(gamma) * d));
        }

        /// <summary>Seconds from r0 (speed v, flight path angle gamma) to theta further round, on the ellipse they make.</summary>
        private static double FlightTime(double mu, double r0, double v, double gamma, double theta)
        {
            if (v <= 0) return 0;
            var h = r0 * v * Math.Cos(gamma);
            var p = h * h / mu;
            var eCos = p / r0 - 1;
            var eSin = v * Math.Sin(gamma) * h / mu;
            var e = Math.Sqrt(eCos * eCos + eSin * eSin);
            if (e >= 1) return 0;
            var f0 = Math.Atan2(eSin, eCos);
            var a = p / (1 - e * e);
            var dm = Mean(f0 + theta, e) - Mean(f0, e);
            while (dm < 0) dm += 2 * Math.PI;
            return dm / Math.Sqrt(mu / (a * a * a));
        }

        private static double Mean(double f, double e)
        {
            var big = Math.Atan2(Math.Sqrt(1 - e * e) * Math.Sin(f), e + Math.Cos(f));
            return big - e * Math.Sin(big);
        }

        private static double[] Unit(double lat, double lng)
        {
            return new[] { Cos(lat) * Cos(lng), Cos(lat) * Sin(lng), Sin(lat) };
        }

        private static double Sin(double deg) { return Math.Sin(deg * Math.PI / 180); }

        private static double Cos(double deg) { return Math.Cos(deg * Math.PI / 180); }

        private static double Deg(double rad) { return rad * 180 / Math.PI; }

        /// <summary>A compass heading (degrees, 0 to 360) from an angle east of north (rad).</summary>
        private static double Compass(double rad) { return ((Deg(rad) % 360) + 360) % 360; }

        private static double Clamp(double c) { return Math.Max(-1, Math.Min(1, c)); }

        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }

        private static double[] Scale(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
