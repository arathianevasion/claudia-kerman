// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1341: where a direction in the orbit's frame (z north) comes down on the body, as the game reads it; false when it can't say.</summary>
    public delegate bool SurfaceUnder(double x, double y, double z, out double latitude, out double longitude);

    /// <summary>#1341: the plane change to the Survey Plane, one combined burn, as a maneuver node: its UT and its delta-v along prograde, normal and radial out, m/s.</summary>
    public sealed class SurveyNode
    {
        public double Ut;
        public double Prograde;
        public double Normal;
        public double Radial;
        /// <summary>The burn's whole delta-v, m/s.</summary>
        public double DeltaV;
    }

    /// <summary>#1341: the Survey Plane the Survey Schedule picks, and the node that reaches it.</summary>
    public sealed class SurveyPlane
    {
        /// <summary>Degrees, 0 to 180, and the longitude of its ascending node in the orbit's frame, degrees 0 to 360, as the craft's own orbit gives them.</summary>
        public double Inclination;
        public double Lan;
        /// <summary>The waypoints it passes over in one revolution, allowing for the body's turn during it.</summary>
        public List<SurveyWaypoint> Passed = new List<SurveyWaypoint>();
        /// <summary>The burn onto it; null when the craft's own plane already passes over the most.</summary>
        public SurveyNode Node;
    }

    public static partial class SurveySchedule
    {
        /// <summary>The candidate planes' spacing in inclination and in node, degrees.</summary>
        private const double PlaneStep = 0.5;

        /// <summary>
        /// #1341: the Survey Plane for an orbital survey's remaining waypoints: of the planes the craft can reach with one burn where its orbit
        /// crosses them, the one that passes over the most of them in the revolution after that burn, allowing for the body's turn; ties go to
        /// the least delta-v. The craft's own plane counts its revolution from <paramref name="now"/>; where it passes over as many, it stays,
        /// with no node. The burn keeps the orbit's shape and turns only its plane, and the craft keeps its sense of travel round the body
        /// (prograde stays prograde). A pass is reckoned on a near-circular orbit, as a survey orbit is: the craft moves round the plane at
        /// the orbit's mean motion.
        /// </summary>
        public static SurveyPlane Plane(SurveyBody body, SnapshotOrbit orbit, double now, SurfaceUnder under, IList<SurveyWaypoint> waypoints, double triggerRange)
        {
            if (orbit.Eccentricity >= 1) throw new ArgumentException("the orbit has no period: it isn't closed");
            double lonOffset, handed;
            var frame = LandingSiteSearch.Frame("the body", under, out lonOffset, out handed);
            if (frame != null) throw new InvalidOperationException(frame);
            var conic = Conic.FromElements(body.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan, orbit.ArgumentOfPeriapsis,
                orbit.MeanAnomalyAtEpoch, orbit.Epoch);
            var sky = new Sky(body, conic.N, now, lonOffset, handed, waypoints, triggerRange);

            var own = conic.W;
            var best = new SurveyPlane { Inclination = conic.Inclination, Lan = Lan(own), Passed = sky.Over(own, conic.Position(now).Normalized, now) };
            double bestDv = 0, bestUt = now;
            Vec3 bestNormal = own;
            var prograde = orbit.Inclination <= 90;
            for (var i = prograde ? 0.0 : 90.0; i <= (prograde ? 90.0 : 180.0) + 1e-9; i += PlaneStep)
            {
                // A plane on the equator has one node line for every longitude of node: one candidate stands for them all.
                for (var lan = 0.0; lan < 360 - 1e-9; lan += i == 0 || i == 180 ? 360 : PlaneStep)
                {
                    var normal = Normal(i, lan);
                    var line = Vec3.Cross(own, normal);
                    if (line.Length < 1e-9) continue;
                    // The craft crosses the new plane twice an orbit, on the line both planes share: the burn is at the sooner crossing.
                    var nu = Math.Atan2(Vec3.Dot(line, conic.Q), Vec3.Dot(line, conic.P));
                    var ut = Math.Min(conic.TimeAtTrue(nu, now).Value, conic.TimeAtTrue(nu + Math.PI, now).Value);
                    Vec3 r, v;
                    conic.State(ut, out r, out v);
                    var dv = Burn(r, v, normal).Length;
                    var passed = sky.Over(normal, r.Normalized, ut);
                    if (passed.Count < best.Passed.Count) continue;
                    if (passed.Count == best.Passed.Count && (best.Node == null || dv > bestDv || (dv == bestDv && ut >= bestUt))) continue;
                    best = new SurveyPlane { Inclination = i, Lan = lan, Passed = passed, Node = new SurveyNode { Ut = ut } };
                    bestDv = dv;
                    bestUt = ut;
                    bestNormal = normal;
                }
            }
            if (best.Node == null) return best;
            Vec3 at, before;
            conic.State(bestUt, out at, out before);
            var change = Burn(at, before, bestNormal);
            var forward = before.Normalized;
            var up = own;
            best.Node.Prograde = Vec3.Dot(change, forward);
            best.Node.Normal = Vec3.Dot(change, up);
            best.Node.Radial = Vec3.Dot(change, Vec3.Cross(forward, up));
            best.Node.DeltaV = change.Length;
            return best;
        }

        /// <summary>The delta-v that turns the velocity into the plane with this normal at this point on the line the planes share, keeping its speed and climb.</summary>
        private static Vec3 Burn(Vec3 r, Vec3 v, Vec3 normal)
        {
            var outward = r.Normalized;
            var climb = Vec3.Dot(v, outward);
            var across = (v - climb * outward).Length;
            return climb * outward + across * Vec3.Cross(normal, outward) - v;
        }

        private static Vec3 Normal(double inclination, double lan)
        {
            double i = inclination * Math.PI / 180, o = lan * Math.PI / 180;
            return new Vec3(Math.Sin(o) * Math.Sin(i), -Math.Cos(o) * Math.Sin(i), Math.Cos(i));
        }

        private static double Lan(Vec3 normal)
        {
            if (Math.Abs(normal.X) < 1e-12 && Math.Abs(normal.Y) < 1e-12) return 0;
            var lan = Math.Atan2(normal.X, -normal.Y) * 180 / Math.PI;
            return lan < 0 ? lan + 360 : lan;
        }

        /// <summary>The waypoints over the turning body, seen from the orbit's frame.</summary>
        private sealed class Sky
        {
            private readonly double _radius;
            private readonly double _mean;
            private readonly double _turn;
            private readonly double _now;
            private readonly double _lonOffset;
            private readonly double _handed;
            private readonly IList<SurveyWaypoint> _waypoints;
            private readonly double _range;

            public Sky(SurveyBody body, double mean, double now, double lonOffset, double handed, IList<SurveyWaypoint> waypoints, double range)
            {
                _radius = body.Radius;
                _mean = mean;
                _turn = body.RotationPeriod > 0 ? 360 / body.RotationPeriod : 0;
                _now = now;
                _lonOffset = lonOffset;
                _handed = handed;
                _waypoints = waypoints;
                _range = range;
            }

            /// <summary>The waypoints a craft passes over in one revolution of the plane with this normal, starting at <paramref name="start"/> at <paramref name="t0"/>.</summary>
            public List<SurveyWaypoint> Over(Vec3 normal, Vec3 start, double t0)
            {
                var ahead = Vec3.Cross(normal, start);
                var passed = new List<SurveyWaypoint>();
                foreach (var waypoint in _waypoints)
                {
                    // Where the craft is abreast of the waypoint; the body turns meanwhile, so look again from there.
                    var angle = 0.0;
                    for (var k = 0; k < 3; k++)
                    {
                        var w = At(waypoint, t0 + angle / _mean);
                        angle = Math.Atan2(Vec3.Dot(w, ahead), Vec3.Dot(w, start));
                        if (angle < 0) angle += 2 * Math.PI;
                    }
                    var t = t0 + angle / _mean;
                    var craft = Math.Cos(angle) * start + Math.Sin(angle) * ahead;
                    var off = Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(craft, At(waypoint, t)))));
                    if (_radius * off <= _range) passed.Add(waypoint);
                }
                return passed;
            }

            /// <summary>The waypoint's direction in the orbit's frame at <paramref name="ut"/>.</summary>
            private Vec3 At(SurveyWaypoint waypoint, double ut)
            {
                var lat = waypoint.Latitude * Math.PI / 180;
                var angle = _handed * (waypoint.Longitude - _lonOffset + _turn * (ut - _now)) * Math.PI / 180;
                return new Vec3(Math.Cos(lat) * Math.Cos(angle), Math.Cos(lat) * Math.Sin(angle), Math.Sin(lat));
            }
        }
    }
}
