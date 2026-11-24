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
    }

    /// <summary>
    /// #1307: the Survey Schedule of an orbital survey. Looking up to one turn of the body ahead, each waypoint's first pass: the
    /// ground track's nearest approach to it, where that comes within the survey's trigger range. The range is a slant distance from
    /// the craft to the waypoint at sea level, so it holds whether the game measures it along the ground or through space. Passes
    /// come in flying order; a waypoint with none follows them, "unreachable" with the inclination that reaches it where the orbit's
    /// tilt can't take the track to its latitude.
    /// </summary>
    public static class SurveySchedule
    {
        /// <summary>The stock Contracts.cfg's Survey <c>MaximumTriggerRange</c>, m, where the game's settings can't be read.</summary>
        public const double StockTriggerRange = 15000;

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
                    var next = Slant(body.Radius, point, waypoints[w]);
                    if (k >= 1 && at[w] <= next && (k == 1 || at[w] <= before[w]))
                    {
                        var nearest = Nearest(body.Radius, orbit.Track, waypoints[w], Math.Max(now, ut - 2 * step), ut);
                        if (Slant(body.Radius, orbit.Track(nearest), waypoints[w]) <= triggerRange)
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
                if (Slant(radius, track(c), waypoint) <= Slant(radius, track(d), waypoint)) b = d;
                else a = c;
            }
            return (a + b) / 2;
        }

        /// <summary>The straight-line distance from the craft to the waypoint at sea level, m.</summary>
        private static double Slant(double radius, TrackPoint point, SurveyWaypoint waypoint)
        {
            var lat1 = point.Latitude * Math.PI / 180;
            var lat2 = waypoint.Latitude * Math.PI / 180;
            var cos = Math.Sin(lat1) * Math.Sin(lat2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Cos((point.Longitude - waypoint.Longitude) * Math.PI / 180);
            var r = radius + point.Altitude;
            return Math.Sqrt(Math.Max(0, radius * radius + r * r - 2 * radius * r * Math.Max(-1, Math.Min(1, cos))));
        }
    }
}
