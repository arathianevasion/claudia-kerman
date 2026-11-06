// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The planned orbit and frame of a landing block's call, as one line for the block-call log (#791): grep KSP.log for
    /// <see cref="Tag"/> to set the orbit a deorbit, return or correction burn planned against the one flown, and the frame the
    /// ground was read in, when a landing misses (#482).
    /// </summary>
    internal static class BurnPlanLog
    {
        public const string Tag = "[plan-vs-flown]";

        /// <summary>
        /// DEORBITBURN's and REENTRYIMPACT's line: the orbit after the burn (<paramref name="after"/>) as planned, its periapsis
        /// and apoapsis above sea level, period, the UT of its next periapsis from <paramref name="from"/>, and the frame
        /// (<paramref name="frame"/>'s LonOffset and handedness) and the altitude (<paramref name="groundAlt"/>) the landing was read at.
        /// </summary>
        public static string Orbit(string call, Conic after, double bodyRadius, BodyFrame frame, double groundAlt, double from)
        {
            var peUt = after.NextPeriapsis(from);
            return string.Format(CultureInfo.InvariantCulture,
                "{0} {1} planned orbit: Pe {2:0} m, Ap {3:0} m, period {4:0.0} s, Pe at UT {5}; frame LonOffset {6:0.0000} deg, handed {7:0}, ground_alt {8:0} m",
                Tag, call, after.PeR - bodyRadius, after.ApR - bodyRadius, after.Period,
                peUt.HasValue ? peUt.Value.ToString("0.0", CultureInfo.InvariantCulture) : "never", frame.LonOffset, frame.Handed, groundAlt);
        }

        /// <summary>
        /// CORRECTIONBURN's and RETURNBURN's line: the burn's UT and the periapsis (m) and inclination (degrees) it plans round the
        /// planet, with the landing target (lat, lng) when the ask names one. The frame is REENTRYIMPACT's line.
        /// </summary>
        public static string Arrival(string call, double ut, double pe, double inc, bool hasTarget, double lat, double lng)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} planned arrival: burn at UT {2:0.0}, Pe {3:0} m, inclination {4:0.00} deg; landing target {5}",
                Tag, call, ut, pe, inc, hasTarget ? lat.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + lng.ToString("0.0000", CultureInfo.InvariantCulture) : "none");
        }
    }
}
