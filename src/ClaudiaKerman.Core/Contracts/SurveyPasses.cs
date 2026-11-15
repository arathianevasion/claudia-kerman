// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1030: what a body's survey waypoints add to the route: delta-v over its orbit or landing, their order, and when each is taken.</summary>
    internal sealed class SurveyLeg
    {
        /// <summary>m/s on top of the route's own legs.</summary>
        public double DeltaV;
        /// <summary>The waypoints in the order they are taken.</summary>
        public readonly List<SurveyWaypoint> Order = new List<SurveyWaypoint>();
        /// <summary>Seconds after the craft is in orbit (space band) or landed (ground) that each waypoint is taken, by key.</summary>
        public readonly Dictionary<string, double> After = new Dictionary<string, double>();

        /// <summary>Seconds until the last is taken.</summary>
        public double Seconds
        {
            get { return After.Values.DefaultIfEmpty(0).Max(); }
        }
    }

    /// <summary>
    /// #1030 (spec #1021 decisions 4 and 21): survey waypoints priced and ordered. In a space band: the plane change that puts the ground
    /// track over the furthest waypoint from the equator (a turn in low orbit, or the plane aimed on the approach, the Map's polar row, when
    /// that is cheaper), a raise to the band's floor when it is above low orbit, and the wait for the passes: the body turns east under the
    /// orbit, so the track sweeps west over the surface; the first pass is up to half a turn away (the plane crosses every point under it
    /// twice a turn) and the rest follow westward from the one after the widest gap. On the ground: a landing at each (the Map's polar row
    /// when one is at a high latitude), ordered for the least travel between them, each next site a hop.
    /// </summary>
    internal static class SurveyPasses
    {
        /// <summary>Degrees of latitude from which a landing site is polar, as for a biome (ADR 0016).</summary>
        public const double PolarFrom = 60;
        /// <summary>A raise to a band's floor goes this far above it, so the pass is inside the band.</summary>
        public const double AboveFloor = 1.1;
        /// <summary>A hop between landing sites, s.</summary>
        public const double HopSeconds = 600;
        /// <summary>Above this many sites the order is nearest-next from the first, not every order.</summary>
        private const int MostSitesToPermute = 6;

        public static SurveyLeg Space(SnapshotBody body, DeltaVMap map, IList<SurveyWaypoint> waypoints)
        {
            var leg = new SurveyLeg();
            if (waypoints.Count == 0) return leg;
            var low = DeltaVMap.LowOrbit(body);
            var speed = Math.Sqrt(body.Gm / low);
            var turn = OrbitChange.PlaneChangeDeltaV(speed, waypoints.Max(w => Math.Abs(w.Latitude)));
            var polar = map.Leg("polar:" + body.Name);
            leg.DeltaV = polar == null ? turn : Math.Min(turn, polar.DeltaV);
            var floor = waypoints.Where(w => w.MinAltitude.HasValue).Select(w => w.MinAltitude.Value).DefaultIfEmpty(0).Max();
            if (body.Radius + floor >= low)
            {
                var raise = Hohmann(body.Gm, low, body.Radius + floor * AboveFloor);
                // A low band as well: down again for it.
                leg.DeltaV += waypoints.Any(w => w.MaxAltitude.HasValue) ? 2 * raise : raise;
            }
            var turnSeconds = body.RotationPeriod > 0 ? body.RotationPeriod : 0;
            var east = waypoints.OrderByDescending(w => East(w.Longitude)).ThenBy(w => w.Key, StringComparer.Ordinal).ToList();
            // The widest gap westward is skipped: the sweep starts at the waypoint after it.
            var start = Enumerable.Range(0, east.Count).OrderByDescending(i => West(east[(i + east.Count - 1) % east.Count].Longitude, east[i].Longitude)).ThenBy(i => i).First();
            for (var k = 0; k < east.Count; k++)
            {
                var waypoint = east[(start + k) % east.Count];
                leg.Order.Add(waypoint);
                leg.After[waypoint.Key] = turnSeconds / 2 + turnSeconds * West(east[start].Longitude, waypoint.Longitude) / 360;
            }
            return leg;
        }

        public static SurveyLeg Ground(SnapshotBody body, DeltaVMap map, IList<SurveyWaypoint> waypoints)
        {
            var leg = new SurveyLeg();
            if (waypoints.Count == 0) return leg;
            var polar = map.Leg("polar:" + body.Name);
            if (polar != null && waypoints.Any(w => Math.Abs(w.Latitude) >= PolarFrom)) leg.DeltaV += polar.DeltaV;
            var sites = waypoints.OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
            var order = sites.Count <= MostSitesToPermute ? Permutations(sites).OrderBy(o => Travel(body, o)).First() : NearestNext(body, sites);
            var ascend = map.Leg("ascend:" + body.Name);
            var land = map.Leg("land:" + body.Name);
            var orbitAndBack = ascend != null && land != null ? ascend.DeltaV + land.DeltaV : double.MaxValue;
            var gravity = body.Gm / (body.Radius * body.Radius);
            for (var i = 0; i < order.Count; i++)
            {
                leg.Order.Add(order[i]);
                leg.After[order[i].Key] = i * HopSeconds;
                if (i == 0) continue;
                // Up and down again on a ballistic hop over flat ground; never more than going to orbit and landing again.
                var hop = 2 * Math.Sqrt(gravity * Distance(body, order[i - 1], order[i]));
                leg.DeltaV += Math.Min(hop, orbitAndBack);
            }
            return leg;
        }

        /// <summary>The great-circle distance between two waypoints on the surface, m.</summary>
        internal static double Distance(SnapshotBody body, SurveyWaypoint a, SurveyWaypoint b)
        {
            var la = a.Latitude * Math.PI / 180;
            var lb = b.Latitude * Math.PI / 180;
            var dl = (b.Longitude - a.Longitude) * Math.PI / 180;
            var cos = Math.Sin(la) * Math.Sin(lb) + Math.Cos(la) * Math.Cos(lb) * Math.Cos(dl);
            return body.Radius * Math.Acos(Math.Max(-1, Math.Min(1, cos)));
        }

        private static double Travel(SnapshotBody body, IList<SurveyWaypoint> order)
        {
            var total = 0.0;
            for (var i = 1; i < order.Count; i++) total += Distance(body, order[i - 1], order[i]);
            return total;
        }

        private static List<SurveyWaypoint> NearestNext(SnapshotBody body, List<SurveyWaypoint> sites)
        {
            var order = new List<SurveyWaypoint> { sites[0] };
            var left = sites.Skip(1).ToList();
            while (left.Count > 0)
            {
                var next = left.OrderBy(w => Distance(body, order[order.Count - 1], w)).First();
                order.Add(next);
                left.Remove(next);
            }
            return order;
        }

        private static IEnumerable<List<SurveyWaypoint>> Permutations(List<SurveyWaypoint> items)
        {
            if (items.Count <= 1)
            {
                yield return items.ToList();
                yield break;
            }
            for (var i = 0; i < items.Count; i++)
                foreach (var tail in Permutations(items.Where((_, j) => j != i).ToList()))
                {
                    tail.Insert(0, items[i]);
                    yield return tail;
                }
        }

        /// <summary>Longitude in [0, 360).</summary>
        private static double East(double longitude)
        {
            var east = longitude % 360;
            return east < 0 ? east + 360 : east;
        }

        /// <summary>Degrees westward from <paramref name="from"/> to <paramref name="to"/>, in [0, 360).</summary>
        private static double West(double from, double to)
        {
            return East(from - to);
        }

        /// <summary>Two burns from a circular orbit of radius <paramref name="r1"/> to one of <paramref name="r2"/>, m/s.</summary>
        private static double Hohmann(double gm, double r1, double r2)
        {
            var a = (r1 + r2) / 2;
            return Math.Abs(Math.Sqrt(gm * (2 / r1 - 1 / a)) - Math.Sqrt(gm / r1)) + Math.Abs(Math.Sqrt(gm / r2) - Math.Sqrt(gm * (2 / r2 - 1 / a)));
        }
    }
}
