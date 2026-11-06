// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// When the launch site next passes under a target plane (#464), by <see cref="PlaneTiming"/>: the soonest of the northbound and
    /// southbound passes, and the compass heading to fly into the plane from there. <c>plan_ascent</c> reports it and the <c>launch</c>
    /// block waits for it through <c>ADDONS:CLAUDIA:LAUNCHWINDOW</c>; both run this.
    /// </summary>
    internal sealed class LaunchWindow
    {
        /// <summary>Why there is no window; null when there is one.</summary>
        public string Error;
        /// <summary>Seconds from now to the window.</summary>
        public double WaitSeconds;
        /// <summary>The UT of the window.</summary>
        public double Ut;
        /// <summary>The window launches northward (false: southward).</summary>
        public bool North;
        /// <summary>The plane's inclination, degrees: negative for the southbound window.</summary>
        public double Inclination;
        /// <summary>Compass degrees to fly from the pad at the window, the body's turning allowed for.</summary>
        public double Heading;

        public static LaunchWindow Fail(string why)
        {
            return new LaunchWindow { Error = why };
        }

        /// <summary>
        /// The window for a site at <paramref name="latitude"/> and <paramref name="celestialLongitude"/> (degrees; the longitude plus the
        /// body's rotation angle) on <paramref name="body"/> at <paramref name="ut"/>, into the plane with inclination
        /// <paramref name="inc"/> (0 to 180; the size of a negative one is used) and ascending node <paramref name="lan"/>, to a circular
        /// orbit <paramref name="targetAp"/> m up (for the heading's orbital speed).
        /// </summary>
        public static LaunchWindow Find(SnapshotBody body, double ut, double latitude, double celestialLongitude, double inc, double lan, double targetAp)
        {
            if (body == null || body.RotationPeriod == 0 || !Finite(body.RotationPeriod))
                return Fail("the body has no rotation period to time a launch by");
            if (!Finite(latitude) || Math.Abs(latitude) > 90 || !Finite(celestialLongitude)) return Fail("the launch site's latitude and longitude are not known");
            if (!Finite(inc) || Math.Abs(inc) > 180 || !Finite(lan)) return Fail("the plane's inclination and LAN must be numbers (inclination within 180 degrees)");
            var size = Math.Abs(inc);
            // An equatorial plane (the Mun's) is under a pad within a degree of the equator at once: the longitude doesn't matter (as MechJeb).
            var equatorial = (size < 1e-9 || size > 180 - 1e-9) && Math.Abs(latitude) <= 1;
            if (!equatorial && (size < Math.Abs(latitude) - 1e-9 || size > 180 - Math.Abs(latitude) + 1e-9))
                return Fail("the pad never passes under a plane of inclination " + Round(size) + ": the pad's latitude is " + Round(Math.Abs(latitude))
                    + " degrees, past it");

            double wait, signed;
            PlaneTiming.MinimumTimeToPlane(body.RotationPeriod, latitude, celestialLongitude, lan, size, out wait, out signed);
            var north = signed >= 0;
            return new LaunchWindow
            {
                WaitSeconds = wait,
                Ut = ut + wait,
                North = north,
                Inclination = signed,
                Heading = HeadingInto(body, latitude, size, north, targetAp),
            };
        }

        /// <summary>
        /// The compass heading from a pad at <paramref name="latitude"/> into a plane of inclination <paramref name="inc"/> (the size): the
        /// inertial azimuth asin(cos i / cos lat), the body's eastward turning at the pad taken off the orbital velocity (what KSLib's LAZcalc did before #475).
        /// </summary>
        public static double HeadingInto(SnapshotBody body, double latitude, double inc, bool north, double targetAp)
        {
            var cosLat = Math.Cos(latitude * Math.PI / 180);
            if (cosLat < 1e-9) return north ? 0 : 180;
            var inertial = Math.Asin(Math.Max(-1, Math.Min(1, Math.Cos(inc * Math.PI / 180) / cosLat)));
            if (!north) inertial = Math.PI - inertial;
            var orbitSpeed = Math.Sqrt(body.Gm / (body.Radius + Math.Max(0, targetAp)));
            var padSpeed = 2 * Math.PI * body.Radius / body.RotationPeriod * cosLat;
            var east = orbitSpeed * Math.Sin(inertial) - padSpeed;
            var northward = orbitSpeed * Math.Cos(inertial);
            var degrees = Math.Atan2(east, northward) * 180 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }

        private static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static string Round(double x)
        {
            return x.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
