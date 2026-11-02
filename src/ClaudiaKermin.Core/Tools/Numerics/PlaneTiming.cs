/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>
    /// How long until a launch site on the ground passes under a target plane: <c>TimeToPlane</c> and <c>MinimumTimeToPlane</c>
    /// from <c>MechJebLib/Functions/Astro.cs</c> (MechJeb2 a958383), with Napier's rules for the site's latitude against the plane's
    /// inclination, exact where the old flat approximation was not. Angles are degrees, times seconds. MechJeb's clamped arcsine
    /// quietly gives a wrong answer for a plane the latitude can't reach, and its input checks are Debug-only: here both throw
    /// <see cref="ArgumentOutOfRangeException"/> in every build (<see cref="LaunchWindow"/> turns them into reasons first).
    /// </summary>
    internal static class PlaneTiming
    {
        private const double Eps = 2.2204460492503131e-16;
        private const double Tau = 2 * Math.PI;

        /// <summary>
        /// The sooner of the northbound and southbound passes: the seconds and the inclination of the plane flown, <paramref name="inc"/>'s
        /// size positive when the pass launches northward and negative southward.
        /// </summary>
        public static void MinimumTimeToPlane(double rotationPeriod, double latitude, double celestialLongitude, double lan, double inc,
            out double time, out double inclination)
        {
            var north = TimeToPlane(rotationPeriod, latitude, celestialLongitude, lan, Math.Abs(inc));
            var south = TimeToPlane(rotationPeriod, latitude, celestialLongitude, lan, -Math.Abs(inc));
            if (north < south)
            {
                time = north;
                inclination = Math.Abs(inc);
            }
            else
            {
                time = south;
                inclination = -Math.Abs(inc);
            }
        }

        /// <summary>
        /// Seconds until the site at <paramref name="latitude"/> and <paramref name="celestialLongitude"/> (its longitude plus the body's
        /// rotation angle) next passes under the plane with ascending node <paramref name="lan"/> and inclination
        /// <paramref name="inc"/> (negative for the southbound pass), on a body turning once in <paramref name="rotationPeriod"/>
        /// (negative if it turns backwards).
        /// </summary>
        public static double TimeToPlane(double rotationPeriod, double latitude, double celestialLongitude, double lan, double inc)
        {
            Check(rotationPeriod != 0 && IsFinite(rotationPeriod), "rotationPeriod", rotationPeriod, "out of range");
            Check(IsFinite(latitude) && Math.Abs(latitude) <= 90, "latitude", latitude, "out of range");
            Check(IsFinite(celestialLongitude), "celestialLongitude", celestialLongitude, "out of range");
            Check(IsFinite(lan), "lan", lan, "out of range");
            Check(IsFinite(inc) && Math.Abs(inc) <= 180, "inc", inc, "out of range");

            var lat = ToRadians(latitude);
            var longitude = ToRadians(celestialLongitude);
            var node = ToRadians(lan);
            var incRad = ToRadians(inc);

            // The poles, where tan(lat) is infinite.
            if (Math.Abs(Math.Abs(lat) - Math.PI / 2) < Eps) return 0;

            // An equatorial orbit: the longitude doesn't matter.
            if (Math.Abs(incRad) < Eps || Math.Abs(Math.Abs(incRad) - Math.PI) < Eps) return 0;

            // MechJeb's clamped arcsine would give a plane the latitude can't reach the nearest it can; that is not a window.
            var ratio = Math.Tan(lat) / Math.Tan(Math.Abs(incRad));
            Check(Math.Abs(ratio) <= 1 + 1e-12, "inc", inc, "the plane's inclination is below the site's latitude");

            // Napier's rules. Launching north the ascending node is 90 degrees either side of the site's longitude, facing it;
            // launching south the node is behind the body.
            var angleEastOfAN = Math.Asin(Math.Max(-1, Math.Min(1, ratio)));
            if (incRad < 0) angleEastOfAN = Math.PI - angleEastOfAN;

            var lanNow = longitude - angleEastOfAN;
            var lanDiff = node - lanNow;

            // A body that turns backwards.
            if (rotationPeriod < 0) lanDiff = -lanDiff;

            return Clamp2Pi(lanDiff) / Tau * Math.Abs(rotationPeriod);
        }

        private static double Clamp2Pi(double x)
        {
            x %= Tau;
            x = x < 0 ? x + Tau : x;
            return x >= Tau ? 0 : x;
        }

        private static double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180;
        }

        private static bool IsFinite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static void Check(bool ok, string name, double value, string why)
        {
            if (!ok) throw new ArgumentOutOfRangeException(name, value, why);
        }
    }
}
