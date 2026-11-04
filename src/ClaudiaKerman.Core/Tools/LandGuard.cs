// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// <c>land</c>'s path guard and its vertical fallback (#712, ADR 0014), called once a braking tick through the block-internal
    /// <c>ADDONS:CLAUDIA:LANDGUARD</c> (<see cref="Tick"/>); kOS still steers, throttles and stages. Braking more than
    /// <see cref="Margin"/> over its predicted path (BRAKEPATH's, read where the craft is along the track) either comes straight
    /// down onto the site, where <see cref="Ineligible"/> finds nothing against it, or aborts as before.
    /// </summary>
    public static class LandGuard
    {
        /// <summary>m from the site within which braking over its path may come straight down instead of aborting.</summary>
        public const double NearM = 1000;
        /// <summary>m past the site the craft may stop at, killing its speed across the ground, and still come straight down.</summary>
        public const double PastM = 100;
        /// <summary>The delta-v left must be this many times the fallback's estimate (<see cref="FallbackDv"/>).</summary>
        public const double DvMargin = 1.25;
        /// <summary>m/s across the ground over which the fallback holds its height while it stops; under it, it descends in full.</summary>
        public const double HoldOverVh = 5;

        /// <summary>
        /// land_path_guard's margin: how far over the predicted altitude <paramref name="pathAlt"/> (m above sea level, over ground
        /// at <paramref name="pathGround"/>) braking may be, m: 1 km, or a quarter of the predicted height over the ground if more.
        /// </summary>
        public static double Margin(double pathAlt, double pathGround)
        {
            return Math.Max(1000, 0.25 * Math.Max(0, pathAlt - pathGround));
        }

        /// <summary>The guard's reason, <paramref name="over"/> m over the predicted path.</summary>
        public static string Reason(double over)
        {
            return "braking is above its predicted path: " + Math.Round(over / 1000, 1).ToString(CultureInfo.InvariantCulture) + " km over it";
        }

        /// <summary>
        /// The fallback's delta-v estimate, m/s: stopping <paramref name="vH"/> m/s across the ground (at the braking rate
        /// <see cref="BrakeGuidance.BrakeAccel"/> allows over <paramref name="margin"/>) while holding against gravity
        /// <paramref name="g"/>, then stopping the fall from <paramref name="h"/> m over the ground at <paramref name="vDown"/> m/s
        /// with 0.95 of <paramref name="aMax"/> (m/s²), losing to gravity as it burns. Infinite when it can't hold against gravity.
        /// </summary>
        public static double FallbackDv(double vH, double vDown, double h, double aMax, double g, double margin)
        {
            var aMost = 0.95 * aMax;
            if (aMost <= 1.1 * g) return double.PositiveInfinity;
            var aH = BrakeGuidance.BrakeAccel(aMax, g) / margin;
            var fall = Math.Sqrt(Math.Pow(Math.Max(0, vDown), 2) + 2 * g * Math.Max(0, h));
            return vH + g * vH / aH + fall * aMost / (aMost - g);
        }

        /// <summary>
        /// Why braking over its path can't come straight down instead (the fallback's rule), or null when it can: within
        /// <see cref="NearM"/> of the site (<paramref name="s"/> m ahead along the track, <paramref name="c"/> across it), able to stop
        /// <paramref name="vH"/> m/s across the ground no more than <see cref="PastM"/> past it, and with
        /// <paramref name="dvLeft"/> m/s (0 or less: unknown) <see cref="DvMargin"/> times <see cref="FallbackDv"/>.
        /// </summary>
        public static string Ineligible(double s, double c, double vH, double vDown, double h, double aMax, double g, double margin, double dvLeft)
        {
            var dist = Math.Sqrt(s * s + c * c);
            if (dist > NearM) return Math.Round(dist).ToString(CultureInfo.InvariantCulture) + " m from the site, over " + NearM;
            var aMost = 0.95 * aMax;
            if (aMost <= 1.1 * g) return "too little thrust to hold against gravity";
            var stop = BrakeGuidance.StopDistance(vH, BrakeGuidance.BrakeAccel(aMax, g), margin);
            if (stop > Math.Max(0, s) + PastM)
                return "stops " + Math.Round(stop - s).ToString(CultureInfo.InvariantCulture) + " m past the site, over " + PastM;
            var need = DvMargin * FallbackDv(vH, vDown, h, aMax, g, margin);
            if (!(dvLeft >= need))
                return Math.Round(Math.Max(0, dvLeft)).ToString(CultureInfo.InvariantCulture) + " m/s left, under " + Math.Round(need).ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>
        /// The vertical fallback's guidance, in land_guide's braking frame (<paramref name="s"/> and <paramref name="c"/> m to the
        /// site along and across the axis, <paramref name="vAl"/>, <paramref name="vCr"/> and <paramref name="vUp"/> m/s,
        /// <paramref name="h"/> m over the ground beneath): it stops across the ground over the site (land_axis_accel along and
        /// across), holding its height until under <see cref="HoldOverVh"/>, then comes down as land's terminal descent does, no
        /// faster than it can stop from by h_end (<see cref="BrakeGuidance.VvTarget"/> with no shape left), at touchdown speed once
        /// <paramref name="hover"/>. Upright near the ground (<see cref="BrakeGuidance.MaxTilt"/>).
        /// </summary>
        public static IDictionary<string, object> Vertical(double s, double c, double vAl, double vCr, double vUp, double h, double aMax, double g,
            double radius, double touchdown, double margin, double lag, bool hover)
        {
            var vH = Math.Sqrt(vAl * vAl + vCr * vCr);
            var gEff = g - vH * vH / radius;
            var aMost = 0.95 * aMax;
            var aStop = Math.Max(0.2, 0.5 * (aMost - g));
            var down = hover ? -touchdown : BrakeGuidance.VvTarget(h, 0, aStop, touchdown, lag);
            var vvWant = down * Math.Min(1, Math.Max(0, 1 - vH / HoldOverVh));
            var up = Math.Min(aMost, Math.Max(0, gEff + 0.5 * (vvWant - vUp)));
            var horizontal = Math.Sqrt(Math.Max(0, aMost * aMost - up * up));
            var al = BrakeGuidance.AxisAccel(s, vAl, horizontal, margin);
            var rest = Math.Sqrt(Math.Max(0, horizontal * horizontal - al * al));
            var cr = c < 0 ? -BrakeGuidance.AxisAccel(-c, -vCr, rest, margin) : BrakeGuidance.AxisAccel(c, vCr, rest, margin);
            var tilt = BrakeGuidance.MaxTilt(h);
            if (tilt < 90)
            {
                var most = up * Math.Tan(tilt * Math.PI / 180);
                var sideways = Math.Sqrt(al * al + cr * cr);
                if (sideways > most)
                {
                    al = al * most / sideways;
                    cr = cr * most / sideways;
                }
            }
            return new Dictionary<string, object>
            {
                // While it holds its height to stop, that height is its h_ref, so land_phase reads "approach" and land's terminal hover
                // watch (falling under a quarter of touchdown speed for 10 s) doesn't take the hold for a hover; 0 once descending.
                { "al", al }, { "cr", cr }, { "up", up }, { "h", h }, { "h_ref", vH > HoldOverVh ? h : 0.0 }, { "vv", vvWant },
                { "vh", BrakeGuidance.VhTarget(Math.Max(0, s), horizontal, margin) }, { "to_go", Math.Max(0, s) },
                { "sink", Math.Max(touchdown, Math.Min(100, BrakeGuidance.SinkLimit(h, aStop, lag))) },
            };
        }

        /// <summary>
        /// One braking tick (<c>ADDONS:CLAUDIA:LANDGUARD</c>): <c>alt</c> (m above sea level) against the predicted path there
        /// (<c>path</c>, land_path_at's: <c>alt</c> and <c>ground</c>, m above sea level; none if not a lexicon), the frame of land_guide
        /// (<c>s</c>, <c>c</c>, <c>v_al</c>, <c>v_cr</c>, <c>v_up</c>, <c>radar</c>, <c>a_max</c>, <c>g</c>, <c>radius</c>),
        /// <c>dv_left</c> (m/s), <c>touchdown</c>, <c>margin</c> (burn_margin), <c>lag</c>, <c>hover</c>, and <c>vertical</c> (true once
        /// the fallback is flying). Gives <c>verdict</c>: "fly" (braking flies on), "vertical" (with <see cref="Vertical"/>'s
        /// demand, and <c>why</c> the first time) or "abort" (<c>error</c> the reason, <c>why</c> it couldn't come straight down);
        /// and <c>over</c>, m over the path (0 without one).
        /// </summary>
        public static IDictionary<string, object> Tick(IDictionary<string, object> input)
        {
            var s = Number(input, "s", 0);
            var c = Number(input, "c", 0);
            var vAl = Number(input, "v_al", 0);
            var vCr = Number(input, "v_cr", 0);
            var vUp = Number(input, "v_up", 0);
            var radar = Number(input, "radar", 0);
            var aMax = Number(input, "a_max", 0);
            var g = Number(input, "g", 0);
            var margin = Number(input, "margin", 1.1);
            object pathValue;
            var path = input.TryGetValue("path", out pathValue) ? pathValue as IDictionary<string, object> : null;
            var over = path != null ? Number(input, "alt", 0) - Number(path, "alt", 0) : 0;
            Func<IDictionary<string, object>> vertical = () => Vertical(s, c, vAl, vCr, vUp, radar, aMax, g, Number(input, "radius", 1),
                Number(input, "touchdown", 2), margin, Number(input, "lag", 2), Flag(input, "hover"));
            if (Flag(input, "vertical")) return With(vertical(), "vertical", "", "", over);
            if (path == null || over <= Margin(Number(path, "alt", 0), Number(path, "ground", 0)))
                return With(new Dictionary<string, object>(), "fly", "", "", over);
            var vH = Math.Sqrt(vAl * vAl + vCr * vCr);
            var why = Ineligible(s, c, vH, -vUp, radar, aMax, g, margin, Number(input, "dv_left", 0));
            if (why != null) return With(new Dictionary<string, object>(), "abort", Reason(over), why, over);
            return With(vertical(), "vertical", "", Reason(over) + ", " + Math.Round(Math.Sqrt(s * s + c * c)).ToString(CultureInfo.InvariantCulture)
                + " m from the site at " + Math.Round(vH, 1).ToString(CultureInfo.InvariantCulture) + " m/s", over);
        }

        private static IDictionary<string, object> With(IDictionary<string, object> answer, string verdict, string error, string why, double over)
        {
            answer["verdict"] = verdict;
            answer["error"] = error;
            answer["why"] = why;
            answer["over"] = over;
            return answer;
        }

        private static bool Flag(IDictionary<string, object> input, string key)
        {
            object value;
            return input.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
