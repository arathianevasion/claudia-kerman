// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The approach a descent flies over a site (#511, ADR 0011): the track across the ground into the site, the terrain under it
    /// read through the body interface, and the plan-time search for the Descent Shape and periapsis (<see cref="BrakePath.Search"/>)
    /// over it. The track is the great circle that reaches the site on the heading the craft moves across the ground there, which
    /// allows for the body's turn: that heading and the speed it takes off are worked out from the orbit's plane and the turn.
    /// </summary>
    internal static class DescentApproach
    {
        /// <summary>m between the terrain samples along the track.</summary>
        public const double ProfileStep = 200;
        /// <summary>m between the terrain samples under the whole fall from the deorbit burn (#530): about the block's look-ahead spacing, 3 s at 150 m/s or more.</summary>
        public const double FallProfileStep = 500;
        /// <summary>m past the site the terrain is read, for a descent that stops long.</summary>
        public const double PastSiteM = 3000;
        /// <summary>m a site may move off its coordinates for terrain (#511).</summary>
        public const double MoveSiteM = 5000;
        /// <summary>The sites tried round a fixed site when no shape clears it, nearest first: m off it and the bearing, degrees.</summary>
        public static readonly double[][] Neighbours =
        {
            new[] { 1000.0, 0 }, new[] { 1000.0, 120 }, new[] { 1000.0, 240 },
            new[] { 2500.0, 60 }, new[] { 2500.0, 180 }, new[] { 2500.0, 300 },
            new[] { 5000.0, 45 }, new[] { 5000.0, 135 }, new[] { 5000.0, 225 }, new[] { 5000.0, 315 },
        };

        /// <summary>The point <paramref name="dist"/> m from (<paramref name="lat"/>, <paramref name="lng"/>) on <paramref name="bearing"/> (degrees from north) along the surface.</summary>
        public static void Destination(double lat, double lng, double bearing, double dist, double radius, out double toLat, out double toLng)
        {
            var d = dist / radius;
            var phi = lat * Math.PI / 180;
            var b = bearing * Math.PI / 180;
            var phi2 = Math.Asin(Math.Max(-1, Math.Min(1, Math.Sin(phi) * Math.Cos(d) + Math.Cos(phi) * Math.Sin(d) * Math.Cos(b))));
            var dl = Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(phi), Math.Cos(d) - Math.Sin(phi) * Math.Sin(phi2));
            toLat = phi2 * 180 / Math.PI;
            toLng = LandingSiteSearch.Wrap180(lng + dl * 180 / Math.PI);
        }

        /// <summary>
        /// The terrain on the track that reaches the site on <paramref name="heading"/> (degrees, across the ground), from
        /// <paramref name="far"/> m before the site to <see cref="PastSiteM"/> past it every <paramref name="step"/> m, in one survey;
        /// null with <paramref name="error"/> if unreadable.
        /// </summary>
        public static TerrainProfile Read(IPlanningBody api, string body, double radius, double lat, double lng, double heading, double far, out string error,
            double step = ProfileStep)
        {
            error = null;
            var count = (int)Math.Ceiling((far + PastSiteM) / step) + 1;
            var samples = new List<SiteSample>(count);
            for (var i = 0; i < count; i++)
            {
                var s = -PastSiteM + i * step;
                double pLat, pLng;
                Destination(lat, lng, s >= 0 ? heading + 180 : heading, Math.Abs(s), radius, out pLat, out pLng);
                samples.Add(new SiteSample { Latitude = pLat, Longitude = pLng });
            }
            api.Survey(body, samples, SiteFacts.Altitude);
            var heights = new double[count];
            for (var i = 0; i < count; i++)
            {
                if (double.IsNaN(samples[i].Altitude))
                {
                    error = "the terrain on the approach to the site could not be read";
                    return null;
                }
                heights[i] = samples[i].Altitude;
            }
            return new TerrainProfile(-PastSiteM, step, heights);
        }

        /// <summary>
        /// The heading across the ground (degrees) and the speed the body's turn takes off it (m/s) for a craft moving
        /// <paramref name="speed"/> m/s through space in the plane of normal <paramref name="normal"/> over the point
        /// <paramref name="site"/> (a unit vector in the orbit's frame), on a body of <paramref name="radius"/> turning once in
        /// <paramref name="rotationPeriod"/> s, longitude growing <paramref name="handed"/> with the frame's angle.
        /// </summary>
        public static void Heading(Vec3 normal, Vec3 site, double speed, double radius, double rotationPeriod, double handed, out double heading, out double vRotAlong)
        {
            var z = new Vec3(0, 0, 1);
            var moving = Vec3.Cross(normal, site).Normalized;
            var east = (Vec3.Cross(z, site) * handed).Normalized;
            var north = (z - site * Vec3.Dot(z, site)).Normalized;
            var turn = rotationPeriod != 0 ? 2 * Math.PI / Math.Abs(rotationPeriod) * radius * Vec3.Cross(z, site).Length : 0;
            var ground = moving * speed - east * turn;
            heading = (Math.Atan2(Vec3.Dot(ground, east), Vec3.Dot(ground, north)) * 180 / Math.PI + 360) % 360;
            vRotAlong = speed - ground.Length;
        }

        /// <summary>
        /// The plan-time search over the approach to (<paramref name="lat"/>, <paramref name="lng"/>) on <paramref name="heading"/>
        /// from an orbit of radius <paramref name="r0"/> (#511): periapsis heights from the highest ground on the final stretch (where
        /// braking flies) plus the margin, in 500 m steps up to 10 km over the site's terrain and 1 km under the orbit, against the
        /// Descent Shapes (only <paramref name="shape"/> if given, else <paramref name="shapes"/>, a coarse grid, if given), passing
        /// over a periapsis whose fall from the deorbit burn comes inside the margin over the ground before braking is due (#530),
        /// that ground read every <paramref name="fallStep"/> m (the plane-change search's coarse pass reads it sparser).
        /// Without a clearing pair, <see cref="DescentChoice.Error"/> says so.
        /// </summary>
        public static DescentChoice Plan(BrakeCraft craft, SnapshotBody body, IPlanningBody api, double r0, double lat, double lng, double heading,
            double vRotAlong, double? shape, double margin = 1.1, IList<double> shapes = null, double fallStep = FallProfileStep)
        {
            var vPeMost = Math.Sqrt(body.Gm * (2 / body.Radius - 2 / (r0 + body.Radius)));
            var far = BrakePath.FallFrom(craft, body.Gm, body.Radius, vPeMost, margin) + 2000;
            string error;
            var ground = Read(api, body.Name, body.Radius, lat, lng, heading, far, out error);
            if (ground == null) return new DescentChoice { Error = error };
            var world = new BrakeWorld { Mu = body.Gm, Radius = body.Radius, Ground = ground, Margin = margin };
            var site = ground.At(0);
            var g = body.Gm / (body.Radius * body.Radius);
            var stretch = BrakeGuidance.BrakeDistance(vPeMost, BrakeGuidance.BrakeAccel(craft.ThrustN / craft.MassKg, g), margin, 0);
            var lowest = Math.Ceiling((ground.Max(0, stretch) + BrakeGuidance.ClearMargin(0)) / BrakePath.PeStep) * BrakePath.PeStep;
            var highest = Math.Min(site + BrakePath.PeMostOverSite, r0 - body.Radius - 1000);
            if (lowest > highest)
                return new DescentChoice
                {
                    Error = "terrain on the approach: the ground under the braking, " + Whole(ground.Max(0, stretch)) + " m up, leaves no periapsis "
                        + Whole(BrakeGuidance.ClearMargin(0)) + " m over it under the orbit",
                };
            // The whole fall from the deorbit burn, wherever the lowest periapsis's descent orbit is under the search's ceiling (#530):
            // a ridge it passes low over far out would start braking there.
            var fallFar = BrakePath.FallReach(body.Radius, r0, body.Radius + lowest, highest);
            if (fallFar > far)
            {
                world.FallGround = Read(api, body.Name, body.Radius, lat, lng, heading, fallFar, out error, fallStep);
                if (world.FallGround == null) return new DescentChoice { Error = error };
            }
            var choice = BrakePath.Search(craft, world, r0, vRotAlong, lowest, highest, shape.HasValue ? new[] { shape.Value } : shapes);
            if (shape.HasValue && choice.Best == null)
            {
                // The range that clears, for the refusal.
                var all = BrakePath.Search(craft, world, r0, vRotAlong, lowest, highest);
                choice.ShapeLo = all.ShapeLo;
                choice.ShapeHi = all.ShapeHi;
                choice.Error = "descent_shape " + shape.Value.ToString("0.###", CultureInfo.InvariantCulture) + " doesn't clear the terrain on the approach"
                    + (double.IsNaN(all.ShapeLo) ? "; no shape does" : "; shapes from " + Range(all.ShapeLo, all.ShapeHi) + " clear");
            }
            else if (choice.Best == null)
                choice.Error = "terrain on the approach: " + choice.Error;
            return choice;
        }

        /// <summary>The range of shapes "lo to hi" for a report.</summary>
        public static string Range(double lo, double hi)
        {
            return lo.ToString("0.###", CultureInfo.InvariantCulture) + " to " + hi.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The craft as the Game Snapshot has it, for <c>find_landing_site</c>: the highest stage with thrust (the one that fires
        /// next), its Isp from its delta-v and masses, the craft's mass, and dry once it burns all the stages' delta-v (as
        /// <c>land_craft</c> works it out); null when the stages give no thrust. Where a stage with thrust lies below it and the
        /// stages below have the landing's delta-v (<see cref="LanderNeed"/>), land stages the firing one off before the deorbit
        /// burn as a droppable Underhang (#596; the snapshot can't tell drop_underhang or whether staging drops all of it), so the
        /// craft is the lander alone: that stage's thrust, Isp and starting mass, dry once it burns the delta-v below (#528).
        /// </summary>
        public static BrakeCraft FromSnapshot(GameSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Stages == null) return null;
            SnapshotStage firing = null;
            foreach (var stage in snapshot.Stages)
                if (stage.ThrustVacKn > 0 && (firing == null || stage.Number > firing.Number)) firing = stage;
            if (firing == null) return null;
            SnapshotStage lander = null;
            double belowDv = 0, allDv = 0;
            foreach (var stage in snapshot.Stages)
            {
                if (stage.Number > firing.Number || stage.DeltaVUnreliable) continue;
                allDv += stage.DeltaVVac;
                if (stage.Number == firing.Number) continue;
                belowDv += stage.DeltaVVac;
                if (stage.ThrustVacKn > 0 && (lander == null || stage.Number > lander.Number)) lander = stage;
            }
            var need = LanderNeed(snapshot);
            if (lander != null && lander.StartMassT > 0 && need > 0 && belowDv >= need) return StageCraft(lander, lander.StartMassT, belowDv);
            var mass = snapshot.Craft != null && snapshot.Craft.MassTonnes > 0 ? snapshot.Craft.MassTonnes : firing.StartMassT;
            if (mass <= 0) return null;
            return StageCraft(firing, mass, allDv);
        }

        /// <summary>A stage's craft at <paramref name="massT"/> t: its thrust, its Isp from its figures (else 300 s), and dry once it burns <paramref name="dv"/> m/s (never with none).</summary>
        private static BrakeCraft StageCraft(SnapshotStage stage, double massT, double dv)
        {
            var isp = stage.EndMassT > 0 && stage.StartMassT > stage.EndMassT && stage.DeltaVVac > 0
                ? stage.DeltaVVac / (9.80665 * Math.Log(stage.StartMassT / stage.EndMassT))
                : 300;
            var dry = dv > 0 ? massT / Math.Exp(dv / (isp * 9.80665)) : 0;
            return new BrakeCraft { ThrustN = stage.ThrustVacKn * 1000, MassKg = massT * 1000, Isp = isp, DryMassKg = dry * 1000, LagS = 2 };
        }

        /// <summary>
        /// The landing's delta-v before a plan, m/s, close to land's own figures: the deorbit burn from the orbit's semi-major axis
        /// to a periapsis <see cref="LandingPass.ApproachM"/> up, plus 1.2 times the speed there and 100 (land's braking
        /// estimate); 0 where the snapshot has no orbit round a body it knows.
        /// </summary>
        private static double LanderNeed(GameSnapshot snapshot)
        {
            var orbit = snapshot.Orbit;
            if (orbit == null || orbit.SemiMajorAxis <= 0) return 0;
            var at = new BodySystem(snapshot.Bodies).Find(string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body);
            if (at == null || at.Gm <= 0) return 0;
            double r0 = orbit.SemiMajorAxis, rPe = at.Radius + LandingPass.ApproachM;
            if (rPe >= r0) return 0;
            var deorbit = Math.Sqrt(at.Gm / r0) - Math.Sqrt(at.Gm * (2 / r0 - 2 / (r0 + rPe)));
            return deorbit + 1.2 * Math.Sqrt(at.Gm * (2 / rPe - 2 / (r0 + rPe))) + 100;
        }

        private static string Whole(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
