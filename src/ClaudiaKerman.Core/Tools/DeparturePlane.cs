// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The departure plane from a launch site or a landed craft on any body (#727): the orbit to launch into, and when, for a
    /// transfer. A moon's plane is its orbit's (<see cref="LaunchWindow.Find"/>: into it when the site passes under it, else due
    /// east with the residual fixed on the way). A planet or an escape needs any orbit containing the departure asymptote: its
    /// inclination at least the asymptote's declination, the launch time setting the node, so there is no residual (<see cref="ForAsymptote"/>).
    /// The Transfer Windows' rows cost and name it (<see cref="TransferWindows"/>); <c>launch</c> flies the inclination and time
    /// they give (<c>launch_at</c> a UT, <c>target_inc</c>), so the block keeps only steering, staging and warp.
    /// </summary>
    internal static class DeparturePlane
    {
        /// <summary>The rows' case notes (#727), one per row, so no row falls back silently.</summary>
        public const string LaunchedInto = "plane: launched into";
        public const string AtNode = "plane: residual fixed at node arrival";
        /// <summary>A moon's plane change under this, m/s, is the node arrival's: the moon is all but in the parking orbit's plane.</summary>
        public const double NodeFreeDv = 1;

        /// <summary>The note for a residual of <paramref name="degrees"/> fixed mid-course.</summary>
        public static string MidCourse(double degrees)
        {
            return "plane: residual " + Math.Round(degrees, 1).ToString("0.#", CultureInfo.InvariantCulture) + "° fixed mid-course";
        }

        /// <summary>The note when no launch plane could be found, and the rows cost the equator's instead.</summary>
        public static string Assumed(string why)
        {
            return "plane: equatorial parking orbit assumed, " + why;
        }

        /// <summary>
        /// The launch from the site at <paramref name="latitude"/> and <paramref name="celestialLongitude"/> (degrees, at
        /// <paramref name="ut"/>) on <paramref name="body"/> into an orbit <paramref name="targetAp"/> m up containing the departure
        /// asymptote <paramref name="vInf"/> (any length; in the body-table frame, whose z is every body's pole): into the
        /// asymptote's declination when the site passes under that plane, else due east at the time whose node puts the asymptote
        /// in the orbit. The latest such launch at or before <paramref name="byUt"/> (the departure less the launch lead), and not
        /// before <paramref name="ut"/>; the soonest after <paramref name="ut"/> when none is that early.
        /// </summary>
        public static LaunchWindow ForAsymptote(SnapshotBody body, double ut, double latitude, double celestialLongitude, Vec3 vInf, double byUt, double targetAp)
        {
            if (body == null || body.RotationPeriod == 0 || double.IsNaN(body.RotationPeriod) || double.IsInfinity(body.RotationPeriod))
                return LaunchWindow.Fail("the body has no rotation period to time a launch by");
            if (!(vInf.Length > 0)) return LaunchWindow.Fail("the departure has no asymptote");
            var d = vInf.Normalized;
            var declination = Math.Asin(Math.Max(-1, Math.Min(1, d.Z))) * 180 / Math.PI;
            var rightAscension = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
            var site = Math.Abs(latitude);
            var size = Math.Max(site, Math.Abs(declination));
            var period = body.RotationPeriod;
            var start = Math.Max(ut, byUt - Math.Abs(period));
            if (size < 1e-9) start = Math.Max(ut, byUt);

            double[] nodes;
            if (Math.Abs(declination) >= site)
                // Into the declination: the asymptote is the orbit's northernmost (or southernmost) point.
                nodes = new[] { declination >= 0 ? rightAscension - 90 : rightAscension + 90 };
            else
            {
                // Due east: the asymptote is in the plane where sin(RA - LAN) = tan(dec) / tan(i), at two nodes.
                var s = Math.Asin(Math.Max(-1, Math.Min(1, Math.Tan(declination * Math.PI / 180) / Math.Tan(size * Math.PI / 180)))) * 180 / Math.PI;
                nodes = new[] { rightAscension - s, rightAscension - 180 + s };
            }

            LaunchWindow latest = null, soonest = null;
            foreach (var node in nodes)
            {
                var lan = ((node % 360) + 360) % 360;
                var lon = celestialLongitude + 360 * (start - ut) / period;
                var w = LaunchWindow.Find(body, start, latitude, lon, size, lan, targetAp);
                if (w.Error != null) return w;
                // The first pass after start is within a turn of it; one turn earlier may still be on or after ut.
                foreach (var shift in new[] { 0.0, -Math.Abs(period) })
                {
                    var at = w.Ut + shift;
                    if (at < ut - 1e-6) continue;
                    var candidate = new LaunchWindow
                    {
                        WaitSeconds = at - ut, Ut = at, North = w.North, Inclination = w.Inclination, Heading = w.Heading, Lan = lan,
                    };
                    if (at <= byUt + 1e-6 && (latest == null || at > latest.Ut)) latest = candidate;
                    if (soonest == null || at < soonest.Ut) soonest = candidate;
                }
            }
            return latest ?? soonest;
        }
    }
}
