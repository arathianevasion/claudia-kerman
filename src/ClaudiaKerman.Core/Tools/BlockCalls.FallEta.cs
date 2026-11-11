// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    public static partial class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:FALLETA</c>, for <c>reentry</c> (#994, ADR 0014; was the library's <c>claudia_fall_eta</c>): seconds until a path next falls to radius <c>r_fall</c> (m) from above, around a body
        /// of gravitational parameter <c>mu</c>, from <c>ecc</c>, <c>sma</c> (m; negative for a hyperbola) and <c>ta</c> (true anomaly, degrees, as kOS reads it). 0 at or under <c>r_fall</c> now; -1 when the path
        /// never gets that low ahead: a closed orbit whose periapsis is above it, or a hyperbolic one whose periapsis is above it or that is past periapsis and outbound. Pure.
        /// </summary>
        public static double FallEta(IDictionary<string, object> input)
        {
            if (input == null) return -1;
            var ask = new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase);
            return FallEta(Number(ask, "ecc", 0), Number(ask, "sma", 0), Number(ask, "mu", 0), Number(ask, "r_fall", 0), Number(ask, "ta", 0));
        }

        /// <summary><see cref="FallEta(IDictionary{string, object})"/> on its five figures, <paramref name="ta"/> in degrees.</summary>
        public static double FallEta(double ecc, double sma, double mu, double rFall, double ta)
        {
            if (!(mu > 0) || ecc < 0) return -1;
            var closed = ecc < 1;
            if (closed ? sma <= 0 : (ecc == 1 || sma >= 0)) return -1;
            var nu = LandingSiteSearch.Wrap180(ta) * Math.PI / 180;
            var semiLatus = sma * (1 - ecc * ecc);
            // Past a hyperbola's asymptote is no point on the path.
            if (1 + ecc * Math.Cos(nu) <= 0) return -1;
            var rNow = semiLatus / (1 + ecc * Math.Cos(nu));
            if (rNow <= rFall) return 0;
            if (ecc <= 0) return -1;
            var cosFall = (semiLatus / rFall - 1) / ecc;
            if (cosFall > 1) return -1;
            // On the way down the true anomaly is the negative of the one on the way up.
            var nuFall = -Math.Acos(Math.Max(-1, cosFall));
            if (closed)
            {
                var ahead = ShipSystemsRule.MeanAnomaly(nuFall, ecc) - ShipSystemsRule.MeanAnomaly(nu, ecc);
                ahead = ((ahead % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                return ahead * Math.Sqrt(sma * sma * sma / mu);
            }
            // A hyperbola comes down only before periapsis: outbound, it climbs away for good.
            if (nu >= 0) return -1;
            var dt = (ShipSystemsRule.MeanAnomaly(nuFall, ecc) - ShipSystemsRule.MeanAnomaly(nu, ecc)) / Math.Sqrt(mu / Math.Pow(-sma, 3));
            return dt >= 0 ? dt : -1;
        }
    }
}
