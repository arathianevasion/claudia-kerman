// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1307, at the Survey Schedule seam: given the body, the craft's ground track and a survey's waypoints, the waypoints in flying
    /// order with each pass's UT and predicted altitude. The track here is a circular Kepler orbit over a turning Mun, worked out
    /// independently of the schedule, so "over its waypoint" is checked against the orbit itself.
    /// </summary>
    public sealed class SurveyScheduleTests
    {
        private const double MunRadius = 200000;
        private const double MunGm = 6.5138398e10;
        private const double MunTurn = 138984.38;
        private const double Now = 448944;
        /// <summary>The stock survey's high-altitude trigger range, m (Contracts.cfg, Survey MaximumTriggerRange).</summary>
        private const double Range = 15000;

        [Fact]
        public void The1287SurveyGivesFiveDistinctPassesEachOverItsWaypointInFlyingOrder()
        {
            var orbit = new CircularOrbit(12500, 20, 30, 0);
            var waypoints = new[] { "0,10", "0,100", "0,-170", "0,-80", "0,40" }.Select((w, i) => Waypoint(i, w, maxAltitude: 30000)).ToList();

            var schedule = SurveySchedule.For(Mun(), orbit.Survey(), Now, waypoints, Range);

            Assert.Equal(5, schedule.Count);
            Assert.Equal(5, schedule.Select(p => p.Ut.Value).Distinct().Count());
            Assert.Equal(schedule.Select(p => p.Ut.Value).OrderBy(u => u), schedule.Select(p => p.Ut.Value));
            Assert.Equal(5, schedule.Select(p => p.Waypoint.Key).Distinct().Count());
            foreach (var pass in schedule)
            {
                Assert.InRange(pass.Ut.Value, Now, Now + MunTurn);
                Assert.True(orbit.SlantTo(pass.Waypoint, pass.Ut.Value) <= Range, pass.Waypoint.Where() + " is passed too far off");
                Assert.InRange(pass.Altitude.Value, 12499, 12501);
                Assert.Equal("", pass.OutOfBand);
                Assert.False(pass.Unreachable);
            }
        }

        [Fact]
        public void APassAboveTheWaypointsBandIsMarkedWithTheBand()
        {
            var orbit = new CircularOrbit(12500, 20, 30, 0);
            var low = Waypoint(0, "0,10", maxAltitude: 10000);
            var high = Waypoint(1, "0,100", minAltitude: 40000);
            var inside = Waypoint(2, "0,-80", maxAltitude: 30000);

            var schedule = SurveySchedule.For(Mun(), orbit.Survey(), Now, new List<SurveyWaypoint> { low, high, inside }, Range);

            Assert.Equal("below 10 km", schedule.Single(p => p.Waypoint == low).OutOfBand);
            Assert.Equal("above 40 km", schedule.Single(p => p.Waypoint == high).OutOfBand);
            Assert.Equal("", schedule.Single(p => p.Waypoint == inside).OutOfBand);
            Assert.All(schedule, p => Assert.True(p.Ut.HasValue));
        }

        [Fact]
        public void AWaypointTheOrbitCantReachIsUnreachableWithTheInclinationThatReachesIt()
        {
            var orbit = new CircularOrbit(12500, 20, 30, 0);
            var north = Waypoint(0, "35.5,10", maxAltitude: 30000);
            var equator = Waypoint(1, "0,100", maxAltitude: 30000);

            var schedule = SurveySchedule.For(Mun(), orbit.Survey(), Now, new List<SurveyWaypoint> { north, equator }, Range);

            Assert.Equal(new[] { equator, north }, schedule.Select(p => p.Waypoint));
            var skipped = schedule.Last();
            Assert.True(skipped.Unreachable);
            Assert.Null(skipped.Ut);
            Assert.Null(skipped.Altitude);
            Assert.Equal(35.5, skipped.ReachingInclination.Value, 1);
            Assert.False(schedule.First().Unreachable);
        }

        [Fact]
        public void ARetrogradeOrbitReachesTheLatitudesItsTiltReaches()
        {
            var orbit = new CircularOrbit(12500, 160, 30, 0);
            var reached = Waypoint(0, "15,10", maxAltitude: 30000);

            var pass = Assert.Single(SurveySchedule.For(Mun(), orbit.Survey(), Now, new List<SurveyWaypoint> { reached }, Range));

            Assert.False(pass.Unreachable);
            Assert.True(orbit.SlantTo(reached, pass.Ut.Value) <= Range);
        }

        // ---- #1311: a ground survey ----

        private static SurveyWaypoint Ground(int i, string where, double? radius = null)
        {
            var site = Waypoint(i, where);
            site.Ground = true;
            site.Radius = radius;
            return site;
        }

        [Fact]
        public void AGroundSurveyGetsALandingOrderByLeastTravelBetweenItsSites()
        {
            // Along the equator: asked in the order 0, 40, 10, 20; the shortest tour from the first-keyed site is 0, 10, 20, 40.
            var sites = new[] { Ground(0, "0,0"), Ground(1, "0,40"), Ground(2, "0,10"), Ground(3, "0,20") };

            var schedule = SurveySchedule.Ground(Mun(), sites);

            Assert.Equal(new[] { 1, 2, 3, 4 }, schedule.Select(p => p.Landing).ToArray());
            Assert.Equal(new[] { "c-survey/0", "c-survey/2", "c-survey/3", "c-survey/1" }, schedule.Select(p => p.Waypoint.Key).ToArray());
            Assert.All(schedule, p => Assert.False(p.Ut.HasValue));
        }

        [Fact]
        public void ASiteWhoseRadiusIsTighterThanLandsAimNeedsPrecision()
        {
            var tight = Ground(0, "0,0", radius: 2000);
            var roomy = Ground(1, "0,10", radius: 8000);
            var edge = Ground(2, "0,20", radius: SurveySchedule.LandAim);
            var unknown = Ground(3, "0,30");

            var schedule = SurveySchedule.Ground(Mun(), new[] { tight, roomy, edge, unknown });

            Assert.True(schedule.Single(p => p.Waypoint == tight).NeedsPrecision);
            Assert.False(schedule.Single(p => p.Waypoint == roomy).NeedsPrecision);
            Assert.False(schedule.Single(p => p.Waypoint == edge).NeedsPrecision);
            Assert.False(schedule.Single(p => p.Waypoint == unknown).NeedsPrecision);
        }

        private static SurveyBody Mun()
        {
            return new SurveyBody { Radius = MunRadius, RotationPeriod = MunTurn };
        }

        private static SurveyWaypoint Waypoint(int i, string where, double? maxAltitude = null, double? minAltitude = null)
        {
            var parts = where.Split(',');
            return new SurveyWaypoint
            {
                Key = "c-survey/" + i, Body = "Mun", Latitude = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                Longitude = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), MaxAltitude = maxAltitude, MinAltitude = minAltitude,
            };
        }

        /// <summary>A circular orbit of the Mun, seen from its turning surface: the test's own model of the ground track.</summary>
        private sealed class CircularOrbit
        {
            private readonly double _altitude;
            private readonly double _inclination;
            private readonly double _node;
            private readonly double _phase;
            private readonly double _period;

            public CircularOrbit(double altitude, double inclination, double node, double phase)
            {
                _altitude = altitude;
                _inclination = inclination * Math.PI / 180;
                _node = node * Math.PI / 180;
                _phase = phase * Math.PI / 180;
                var r = MunRadius + altitude;
                _period = 2 * Math.PI * Math.Sqrt(r * r * r / MunGm);
            }

            public SurveyOrbit Survey()
            {
                return new SurveyOrbit { Track = At, Period = _period, Inclination = _inclination * 180 / Math.PI };
            }

            public TrackPoint At(double ut)
            {
                var u = _phase + 2 * Math.PI * (ut - Now) / _period;
                var latitude = Math.Asin(Math.Sin(_inclination) * Math.Sin(u));
                var inertial = _node + Math.Atan2(Math.Cos(_inclination) * Math.Sin(u), Math.Cos(u));
                var longitude = inertial - 2 * Math.PI * (ut - Now) / MunTurn;
                longitude = Math.IEEERemainder(longitude, 2 * Math.PI);
                return new TrackPoint { Latitude = latitude * 180 / Math.PI, Longitude = longitude * 180 / Math.PI, Altitude = _altitude };
            }

            public double SlantTo(SurveyWaypoint waypoint, double ut)
            {
                var craft = Cartesian(At(ut).Latitude, At(ut).Longitude, MunRadius + _altitude);
                var ground = Cartesian(waypoint.Latitude, waypoint.Longitude, MunRadius);
                return Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (craft[i] - ground[i]) * (craft[i] - ground[i])));
            }

            private static double[] Cartesian(double latitude, double longitude, double r)
            {
                var lat = latitude * Math.PI / 180;
                var lng = longitude * Math.PI / 180;
                return new[] { r * Math.Cos(lat) * Math.Cos(lng), r * Math.Cos(lat) * Math.Sin(lng), r * Math.Sin(lat) };
            }
        }
    }
}
