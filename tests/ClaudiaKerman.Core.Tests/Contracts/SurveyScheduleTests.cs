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
                Assert.True(orbit.GroundDistanceTo(pass.Waypoint, pass.Ut.Value) <= Range, pass.Waypoint.Where() + " is passed too far off");
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
        public void TheReachingInclinationForThePlaytestsWaypointAllowsForTheTriggerRange()
        {
            // #1346: the 24 Nov playtest's waypoint at 19.2° N asked for 19.2° though 15 km of ground is about 4.3° of arc.
            var orbit = new CircularOrbit(12500, 0.9, 30, 0);
            var waypoint = Waypoint(0, "19.2,10", maxAltitude: 30000);

            var schedule = SurveySchedule.For(Mun(), orbit.Survey(), Now, new List<SurveyWaypoint> { waypoint }, Range);

            Assert.InRange(schedule.Single().ReachingInclination.Value, 14.8, 15.0);
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
            // #1346: a pass counts within the trigger range along the ground, so the inclination that reaches it is the latitude less that range's arc (15 km of the Mun's 200 km radius).
            Assert.Equal(35.5 - Range / 200000 * 180 / Math.PI, skipped.ReachingInclination.Value, 3);
            Assert.False(schedule.First().Unreachable);
        }

        [Fact]
        public void ARetrogradeOrbitReachesTheLatitudesItsTiltReaches()
        {
            var orbit = new CircularOrbit(12500, 160, 30, 0);
            var under = orbit.At(Now + 5000);
            var reached = Waypoint(0, under.Latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + under.Longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture), maxAltitude: 30000);

            var pass = Assert.Single(SurveySchedule.For(Mun(), orbit.Survey(), Now, new List<SurveyWaypoint> { reached }, Range));

            Assert.False(pass.Unreachable);
            Assert.True(orbit.GroundDistanceTo(reached, pass.Ut.Value) <= Range);
        }

        [Fact]
        public void APassIsMeasuredAlongTheGroundSoAFiveWaypointSurveyFrom30KmCountsEveryPass()
        {
            // #1340: the 24 Nov playtest: a 30 km orbit at 20° got "0 passes"; a straight-line distance is never under the 30 km altitude, so it never came within the 15 km range.
            var orbit = new CircularOrbit(30000, 20, 30, 0);
            var waypoints = new[] { "10.0,58.2", "12.5,12.5", "7.1,6.0", "13.2,4.2", "19.2,0.7" }.Select((w, i) => Waypoint(i, w)).ToList();

            var schedule = SurveySchedule.For(Mun(), orbit.Survey(), Now, waypoints, Range);

            Assert.Equal(5, schedule.Count);
            Assert.All(schedule, p =>
            {
                Assert.True(p.Ut.HasValue, p.Waypoint.Where() + " has no pass");
                var track = orbit.At(p.Ut.Value);
                Assert.True(orbit.GroundDistanceTo(p.Waypoint, p.Ut.Value) <= Range, p.Waypoint.Where() + " is passed too far off along the ground");
                Assert.InRange(track.Altitude, 29999, 30001);
            });
        }

        [Fact]
        public void TheWindowIsOneTwelveHoursSharedByEveryWaypointAndTheRestAreLeftForTheNextSurveyPlane()
        {
            // #1342: four waypoints under the track at 2 h, 9 h, 11 h and 30 h: the first three are in the one window, the fourth waits for the next Survey Plane.
            var orbit = new CircularOrbit(12500, 20, 30, 0);
            var waypoints = new[] { 2.0, 9.0, 11.0, 30.0 }.Select((h, i) =>
            {
                var at = orbit.At(Now + Math.Round(h * 3600 / orbit.Survey().Period) * orbit.Survey().Period);
                return Waypoint(i, at.Latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + at.Longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }).ToList();

            var schedule = SurveySchedule.For(Mun(), orbit.Survey(), Now, waypoints, 1000);

            var listed = schedule.Where(p => p.Ut.HasValue).ToList();
            Assert.Equal(new[] { waypoints[0], waypoints[1], waypoints[2] }, listed.Select(p => p.Waypoint));
            Assert.All(listed, p => Assert.InRange(p.Ut.Value - Now, 0, SurveySchedule.PlaneWindow));
            var left = Assert.Single(schedule, p => p.LeftForNextPlane);
            Assert.Same(waypoints[3], left.Waypoint);
            Assert.Null(left.Ut);
            Assert.False(left.Unreachable);
            Assert.Equal(43200, SurveySchedule.PlaneWindow);
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

        [Fact]
        public void ASiteWhoseRadiusCantBeReadIsMarkedRadiusUnknownAndOneWithARadiusIsNot()
        {
            var tight = Ground(0, "0,0", radius: 2000);
            var roomy = Ground(1, "0,10", radius: 8000);
            var unknown = Ground(2, "0,20");

            var schedule = SurveySchedule.Ground(Mun(), new[] { tight, roomy, unknown });

            Assert.False(schedule.Single(p => p.Waypoint == tight).RadiusUnknown);
            Assert.False(schedule.Single(p => p.Waypoint == roomy).RadiusUnknown);
            Assert.True(schedule.Single(p => p.Waypoint == unknown).RadiusUnknown);
        }

        [Fact]
        public void AGroundSurveyGetsItsLandingOrderWithNoClosedOrbitToSchedulePassesFrom()
        {
            var sites = new[] { Ground(0, "0,0"), Ground(1, "0,40"), Ground(2, "0,10") };
            var pass = Waypoint(3, "0,100", maxAltitude: 30000);

            // #1327: the script call has no closed orbit on the ground or a suborbital path; the Planning Tool's order needs none.
            var entries = SurveySchedule.Entries(Mun(), null, Now, new[] { pass }, sites, Range);

            Assert.Equal(SurveySchedule.Ground(Mun(), sites).Select(p => p.Waypoint.Key), entries.Select(p => p.Waypoint.Key));
            Assert.Equal(new[] { 1, 2, 3 }, entries.Select(p => p.Landing));
        }

        [Fact]
        public void AGroundSurveyAloneNeedsNoOrbitEvenWithOneGiven()
        {
            var sites = new[] { Ground(0, "0,0"), Ground(1, "0,40") };

            var entries = SurveySchedule.Entries(Mun(), new CircularOrbit(12500, 20, 30, 0).Survey(), Now, new List<SurveyWaypoint>(), sites, Range);

            Assert.Equal(new[] { 1, 2 }, entries.Select(p => p.Landing));
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

            /// <summary>The great-circle distance along the surface from the point under the craft to the waypoint, m.</summary>
            public double GroundDistanceTo(SurveyWaypoint waypoint, double ut)
            {
                var under = Cartesian(At(ut).Latitude, At(ut).Longitude, 1);
                var ground = Cartesian(waypoint.Latitude, waypoint.Longitude, 1);
                var dot = Enumerable.Range(0, 3).Sum(i => under[i] * ground[i]);
                return MunRadius * Math.Acos(Math.Max(-1, Math.Min(1, dot)));
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
