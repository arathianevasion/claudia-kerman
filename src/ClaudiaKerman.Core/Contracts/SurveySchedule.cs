// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1307: where the craft is over the body's turning surface at one UT.</summary>
    public sealed class TrackPoint
    {
        /// <summary>Degrees, north and east positive.</summary>
        public double Latitude;
        public double Longitude;
        /// <summary>Above sea level, m.</summary>
        public double Altitude;
    }

    /// <summary>#1307: the craft's ground track at any UT, as the game propagates its orbit.</summary>
    public delegate TrackPoint GroundTrack(double ut);

    /// <summary>#1307: the body a survey is flown over.</summary>
    public sealed class SurveyBody
    {
        public double Radius;
        /// <summary>One turn of the body, s; 0 for a body that doesn't turn.</summary>
        public double RotationPeriod;
        /// <summary>#1341: the body's gravitational parameter, m³/s²; the Survey Plane's burn needs it.</summary>
        public double Gm;
    }

    /// <summary>#1307: the craft's orbit, as the Survey Schedule needs it.</summary>
    public sealed class SurveyOrbit
    {
        public GroundTrack Track;
        /// <summary>One orbit, s.</summary>
        public double Period;
        /// <summary>Degrees, 0 to 180.</summary>
        public double Inclination;
    }

    /// <summary>#1307: one waypoint's line in the Survey Schedule.</summary>
    public sealed class SurveyPass
    {
        public SurveyWaypoint Waypoint;
        /// <summary>The UT the craft is nearest the waypoint on its pass over it; null when it has none.</summary>
        public double? Ut;
        /// <summary>The craft's altitude at that pass, m; null when it has none.</summary>
        public double? Altitude;
        /// <summary>The Band the pass misses, "below 30 km" or "above 40 km"; "" when the pass is inside it.</summary>
        public string OutOfBand = "";
        /// <summary>The orbit never comes over the waypoint: its latitude is beyond the orbit's tilt.</summary>
        public bool Unreachable;
        /// <summary>The inclination that would reach it, degrees; null unless <see cref="Unreachable"/>.</summary>
        public double? ReachingInclination;
        /// <summary>#1311: a ground survey's site: 1 for the first landed (with <c>land</c>), 2 and on flown to with <c>site_hop</c>; 0 for an orbital pass.</summary>
        public int Landing;
        /// <summary>#1311: the Contract's radius is tighter than <c>land</c>'s aim, <see cref="SurveySchedule.LandAim"/>.</summary>
        public bool NeedsPrecision;
        /// <summary>#1326: the Contract's radius couldn't be read, so whether the site needs precision isn't known; treat it as needing it.</summary>
        public bool RadiusUnknown;
    }

    /// <summary>
    /// #1307: the Survey Schedule of an orbital survey. Looking up to one turn of the body ahead, each waypoint's first pass: the
    /// ground track's nearest approach to it, where that comes within the survey's trigger range. The range is measured along the ground, from
    /// the point under the craft to the waypoint, so a pass counts from any orbital altitude. Passes
    /// come in flying order; a waypoint with none follows them, "unreachable" with the inclination that reaches it where the orbit's
    /// tilt can't take the track to its latitude.
    /// </summary>
    public static partial class SurveySchedule
    {
        /// <summary>The stock Contracts.cfg's Survey <c>MaximumTriggerRange</c>, m, where the game's settings can't be read.</summary>
        public const double StockTriggerRange = 15000;

        /// <summary>#1311: how near a site <c>land</c> aims to come, m ("about 5 km"); a Contract radius under it is "needs precision".</summary>
        public const double LandAim = 5000;

        /// <summary>
        /// #1311: a ground survey's sites in landing order: the least travel between them, the order the route prices. The first is landed on
        /// from orbit with <c>land</c>; each next is a <c>site_hop</c> from the one before. Each is marked "needs precision" where its Contract
        /// radius is tighter than <see cref="LandAim"/>; a site whose radius can't be read is marked <see cref="SurveyPass.RadiusUnknown"/> instead.
        /// </summary>
        public static List<SurveyPass> Ground(SurveyBody body, IList<SurveyWaypoint> waypoints)
        {
            return SurveyPasses.LandingOrder(body.Radius, waypoints)
                .Select((w, i) => new SurveyPass { Waypoint = w, Landing = i + 1, NeedsPrecision = w.Radius.HasValue && w.Radius.Value < LandAim, RadiusUnknown = !w.Radius.HasValue })
                .ToList();
        }

        /// <summary>
        /// #1327: the schedule the script call returns: the orbital passes in flying order, then the ground survey's sites in landing order.
        /// A ground survey's order needs no orbit, as in the Planning Tool: with <paramref name="orbit"/> null (no closed orbit) or no orbital
        /// waypoint to pass, the sites alone come back.
        /// </summary>
        public static List<SurveyPass> Entries(SurveyBody body, SurveyOrbit orbit, double now, IList<SurveyWaypoint> orbital, IList<SurveyWaypoint> ground, double triggerRange)
        {
            var entries = orbit == null || orbital.Count == 0 ? new List<SurveyPass>() : For(body, orbit, now, orbital, triggerRange);
            entries.AddRange(Ground(body, ground));
            return entries;
        }

        /// <summary>Samples of the ground track per orbit: a Mun orbit's track moves about 0.5° between them.</summary>
        private const int SamplesPerOrbit = 720;
        /// <summary>A pass's UT is refined to this, s.</summary>
        private const double UtTolerance = 0.05;

        public static List<SurveyPass> For(SurveyBody body, SurveyOrbit orbit, double now, IList<SurveyWaypoint> waypoints, double triggerRange)
        {
            if (orbit.Period <= 0) throw new ArgumentException("the orbit has no period: it isn't closed");
            var ahead = body.RotationPeriod > 0 ? body.RotationPeriod : orbit.Period;
            var step = orbit.Period / SamplesPerOrbit;
            var samples = (int)Math.Ceiling(ahead / step) + 1;
            var found = new double?[waypoints.Count];
            var left = waypoints.Count;
            // The last two distances to each waypoint: a nearest approach is a sample no farther than its neighbours.
            var before = new double[waypoints.Count];
            var at = new double[waypoints.Count];
            for (var k = 0; k < samples && left > 0; k++)
            {
                var ut = Math.Min(now + k * step, now + ahead);
                var point = orbit.Track(ut);
                for (var w = 0; w < waypoints.Count; w++)
                {
                    if (found[w].HasValue) continue;
                    var next = Ground(body.Radius, point, waypoints[w]);
                    if (k >= 1 && at[w] <= next && (k == 1 || at[w] <= before[w]))
                    {
                        var nearest = Nearest(body.Radius, orbit.Track, waypoints[w], Math.Max(now, ut - 2 * step), ut);
                        if (Ground(body.Radius, orbit.Track(nearest), waypoints[w]) <= triggerRange)
                        {
                            found[w] = nearest;
                            left--;
                        }
                    }
                    before[w] = at[w];
                    at[w] = next;
                }
            }
            var passes = new List<SurveyPass>();
            var missed = new List<SurveyPass>();
            var reach = orbit.Inclination <= 90 ? orbit.Inclination : 180 - orbit.Inclination;
            for (var w = 0; w < waypoints.Count; w++)
            {
                var waypoint = waypoints[w];
                if (found[w].HasValue)
                {
                    var altitude = orbit.Track(found[w].Value).Altitude;
                    passes.Add(new SurveyPass { Waypoint = waypoint, Ut = found[w], Altitude = altitude, OutOfBand = OutOfBand(waypoint, altitude) });
                    continue;
                }
                var latitude = Math.Abs(waypoint.Latitude);
                var unreachable = latitude > reach;
                missed.Add(new SurveyPass { Waypoint = waypoint, Unreachable = unreachable, ReachingInclination = unreachable ? latitude : (double?)null });
            }
            return passes.OrderBy(p => p.Ut.Value).Concat(missed).ToList();
        }

        /// <summary>The Band a pass at this altitude misses, in words; "" inside it.</summary>
        private static string OutOfBand(SurveyWaypoint waypoint, double altitude)
        {
            if (waypoint.MaxAltitude.HasValue && altitude > waypoint.MaxAltitude.Value) return "below " + Km(waypoint.MaxAltitude.Value);
            if (waypoint.MinAltitude.HasValue && altitude < waypoint.MinAltitude.Value) return "above " + Km(waypoint.MinAltitude.Value);
            return "";
        }

        private static string Km(double metres)
        {
            return (metres / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km";
        }

        /// <summary>The UT between the two where the craft is nearest the waypoint (golden-section search).</summary>
        private static double Nearest(double radius, GroundTrack track, SurveyWaypoint waypoint, double from, double to)
        {
            var golden = (Math.Sqrt(5) - 1) / 2;
            var a = from;
            var b = to;
            while (b - a > UtTolerance)
            {
                var c = b - golden * (b - a);
                var d = a + golden * (b - a);
                if (Ground(radius, track(c), waypoint) <= Ground(radius, track(d), waypoint)) b = d;
                else a = c;
            }
            return (a + b) / 2;
        }

        /// <summary>The great-circle distance along the ground from the point under the craft to the waypoint, m; the craft's altitude doesn't enter it.</summary>
        private static double Ground(double radius, TrackPoint point, SurveyWaypoint waypoint)
        {
            var lat1 = point.Latitude * Math.PI / 180;
            var lat2 = waypoint.Latitude * Math.PI / 180;
            var cos = Math.Sin(lat1) * Math.Sin(lat2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Cos((point.Longitude - waypoint.Longitude) * Math.PI / 180);
            return radius * Math.Acos(Math.Max(-1, Math.Min(1, cos)));
        }
    }
}
