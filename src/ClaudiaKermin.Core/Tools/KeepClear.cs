// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>land</c>'s keep-clear decision (#713, ADR 0014; land_clear_of and land_clear_shift before it), through the block-internal
    /// <c>ADDONS:CLAUDIA:KEEPCLEAR</c> (<see cref="Call"/>): a touchdown point too near something it must stay clear of (where the
    /// Underhang hits, #427; the landed Target Vessel, #662; a Point of Interest, #713) moves straight away from it to
    /// <c>clear_m</c>, by no more than <c>max_divert</c>. Pure; kOS still steers onto the site.
    /// </summary>
    public static class KeepClear
    {
        /// <summary>How far the Underhang's impact and a "vessel" target's Target Vessel are kept from the site, m.</summary>
        public const double DefaultClearM = 50;

        /// <summary>m to move a touchdown point <paramref name="dist"/> m from what it keeps clear of: 0 at <paramref name="clearM"/> or more, else the rest of the way, never over <paramref name="maxDivert"/>.</summary>
        public static double Shift(double dist, double maxDivert, double clearM = DefaultClearM)
        {
            if (dist >= clearM) return 0;
            return Math.Min(maxDivert, clearM - dist);
        }

        /// <summary>
        /// The touchdown point (<c>lat</c>, <c>lng</c>, degrees) moved clear of <c>keep_lat</c>, <c>keep_lng</c> on a body of
        /// <c>radius</c> m: <see cref="Shift"/> m straight away from it (on the bearing from it to the point), or, with the two on
        /// the same spot, toward <c>ahead_lat</c>, <c>ahead_lng</c> (a point along the track), else north. Gives <c>lat</c>,
        /// <c>lng</c>, <c>dist</c> (m from what it keeps clear of, before) and <c>moved</c> (m; 0 when already clear).
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var lat = Number(input, "lat", 0);
            var lng = Number(input, "lng", 0);
            var keepLat = Number(input, "keep_lat", 0);
            var keepLng = Number(input, "keep_lng", 0);
            var radius = Math.Max(1, Number(input, "radius", 1));
            var toM = Math.PI / 180 * radius;
            var dist = LandingSiteSearch.ArcDegrees(lat, lng, keepLat, keepLng) * toM;
            var shift = Shift(dist, Number(input, "max_divert", 0), Number(input, "clear_m", DefaultClearM));
            var answer = new Dictionary<string, object> { { "lat", lat }, { "lng", lng }, { "dist", dist }, { "moved", 0.0 } };
            if (shift <= 0) return answer;
            double bearing;
            if (dist >= 1) bearing = Bearing(keepLat, keepLng, lat, lng);
            else
            {
                var aheadLat = Number(input, "ahead_lat", lat);
                var aheadLng = Number(input, "ahead_lng", lng);
                bearing = LandingSiteSearch.ArcDegrees(lat, lng, aheadLat, aheadLng) * toM >= 0.001 ? Bearing(lat, lng, aheadLat, aheadLng) : 0;
            }
            double toLat, toLng;
            LandingSiteSearch.Destination(lat, lng, bearing, shift / toM, out toLat, out toLng);
            answer["lat"] = toLat;
            answer["lng"] = toLng;
            answer["moved"] = shift;
            return answer;
        }

        /// <summary>Degrees clockwise from north at point 1 of the way along the surface to point 2 (land_bearing's).</summary>
        internal static double Bearing(double lat1, double lng1, double lat2, double lng2)
        {
            const double Rad = Math.PI / 180;
            var dl = (lng2 - lng1) * Rad;
            var b = Math.Atan2(Math.Sin(dl) * Math.Cos(lat2 * Rad), Math.Cos(lat1 * Rad) * Math.Sin(lat2 * Rad) - Math.Sin(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Cos(dl)) / Rad;
            return (b + 360) % 360;
        }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
