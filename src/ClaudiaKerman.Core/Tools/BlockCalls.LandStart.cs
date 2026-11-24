// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    public static partial class BlockCalls
    {
        /// <summary>How near the site (m) a suborbital arc must end for <c>land</c> to start from it (#1309).</summary>
        public const double LandArcReachM = 5000;

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDSTART</c>, for <c>land</c> (#1309, ADR 0014; the eccentricity check was land's own): where it starts from.
        /// From <c>situation</c> (kOS's status), <c>ecc</c>, <c>sma</c> (m), <c>mu</c>, <c>r_fall</c> (m, the radius of the ground the arc meets), <c>radius</c> (m, the body's),
        /// <c>day</c> (s, its rotation period), <c>fall_s</c> (s until the path meets r_fall, FALLETA's; -1 never), <c>end_lat</c>/<c>end_lng</c> (the arc's end as kOS reads
        /// that point under the body's turn now), <c>now</c> (UT), <c>descent_shape</c> (a number, or "auto") and <c>target</c>: the site (a lexicon of lat and lng), "" for "any" or "biome".
        /// Gives <c>start</c>: "orbit" (a low, near-circular orbit: ORBITING, eccentricity at most 0.1; it flies as before), "arc" (a SUB_ORBITAL path that meets the ground
        /// within <see cref="LandArcReachM"/> of the site, or anywhere for "any") or "fail" with <c>why</c>. An arc gives <c>end_m</c>, how far it ends from the aim, and
        /// <c>plan</c>, shaped as land's pass plan: <c>site</c> is the descent's aim (the site, or for "any" the ground the arc meets, once the body has turned under it),
        /// burn_ut now, arrive_ut when it meets the ground, r_pe r_fall, no path, and <c>figures</c>: deorbit_dv 0, v_pe (m/s at r_fall) and needed, 1.2 times that plus 100,
        /// the rule the orbit start's braking estimate uses. Pure.
        /// </summary>
        public static IDictionary<string, object> LandStart(IDictionary<string, object> input)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            object raw;
            var situation = ask.TryGetValue("situation", out raw) ? raw as string ?? "" : "";
            var site = ask.TryGetValue("target", out raw) ? raw as IDictionary<string, object> : null;
            var target = site != null ? "site" : raw as string ?? "";
            var ecc = Number(ask, "ecc", 0);
            if (situation == "ORBITING" && ecc <= 0.1) return new Dictionary<string, object> { { "start", "orbit" }, { "why", "" } };
            var fallS = Number(ask, "fall_s", -1);
            if (situation != "SUB_ORBITAL" || fallS < 0)
                return LandStartFails(string.Format(CultureInfo.InvariantCulture,
                    "land needs a near-circular orbit (eccentricity under 0.1) or a suborbital arc ending within {0:0} km of its site; situation {1}, eccentricity {2}",
                    LandArcReachM / 1000, situation, Math.Round(ecc, 3)));
            if (target == "biome")
                return LandStartFails("from a suborbital arc land needs a site, not a biome: pass the site find_landing_site gave as the target");

            var radius = Number(ask, "radius", 0);
            var day = Number(ask, "day", 0);
            var endLat = Number(ask, "end_lat", 0);
            // The body turns east under the arc while it falls, so the ground it meets lies west of where kOS reads its end now.
            var endLng = LandingSiteSearch.Wrap180(Number(ask, "end_lng", 0) - (day > 0 ? fallS * 360 / day : 0));
            var aimLat = site == null ? endLat : Number(site, "lat", 0);
            var aimLng = site == null ? endLng : Number(site, "lng", 0);
            var endM = GreatCircleM(endLat, endLng, aimLat, aimLng, radius);
            if (endM > LandArcReachM)
                return LandStartFails(string.Format(CultureInfo.InvariantCulture,
                    "land's suborbital arc ends {0:0.0} km from the site; it starts from an arc only when that is within {1:0} km (otherwise from a low, near-circular orbit)",
                    endM / 1000, LandArcReachM / 1000));

            var rFall = Number(ask, "r_fall", radius);
            var sma = Number(ask, "sma", 0);
            var vEnd = Math.Sqrt(Math.Max(0, Number(ask, "mu", 0) * (2 / rFall - 1 / sma)));
            var now = Number(ask, "now", 0);
            // The plan land flies in place of a pass's: no burn before braking, which predicts its path from the real state (no path here).
            var plan = new Dictionary<string, object>
            {
                { "error", "" }, { "burn_ut", now }, { "arrive_ut", now + fallS }, { "site", new Dictionary<string, object> { { "lat", aimLat }, { "lng", aimLng } } },
                { "r_pe", rFall }, { "next_pass_ut", -1.0 }, { "descent_shape", Number(ask, "descent_shape", -1) }, { "predicted_dv", -1.0 }, { "brake_dv", -1.0 },
                { "predicted_clearance", -1.0 }, { "moved", false }, { "path", new List<object>() },
                { "figures", new Dictionary<string, object> { { "deorbit_dv", 0.0 }, { "v_pe", vEnd }, { "needed", 1.2 * vEnd + 100 } } },
            };
            return new Dictionary<string, object> { { "start", "arc" }, { "why", "" }, { "end_m", endM }, { "plan", plan } };
        }

        private static IDictionary<string, object> LandStartFails(string why)
        {
            return new Dictionary<string, object> { { "start", "fail" }, { "why", why } };
        }

        /// <summary>Metres along the surface of a sphere of <paramref name="radius"/> between two points, degrees (haversine).</summary>
        private static double GreatCircleM(double lat1, double lng1, double lat2, double lng2, double radius)
        {
            const double Rad = Math.PI / 180;
            var dLat = (lat2 - lat1) * Rad;
            var dLng = (lng2 - lng1) * Rad;
            var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Pow(Math.Sin(dLng / 2), 2);
            return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }
    }
}
