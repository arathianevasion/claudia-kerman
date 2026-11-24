// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// <c>survey_schedule</c> (#1308): the Survey Schedule of the Chosen Contracts' orbital survey waypoints, for the craft's orbit as it is now, so
    /// the Sequence Plan quotes its pass times and total wait and sets its expected duration from them. The search is #1307's
    /// <see cref="SurveySchedule"/>, the one <c>ADDONS:CLAUDIA:SURVEYSCHEDULE</c> runs in flight; this side gives it the Game Snapshot's orbit,
    /// propagated by Core, where the script call gives it the game's own propagation. Offered only when a Chosen Contract is an orbital
    /// survey (<see cref="PlanningTools.Definitions(ToolOffer, bool)"/>), so other flights pay nothing for it.
    /// </summary>
    internal sealed class SurveyScheduleTool : IPlanningTool
    {
        public const string ToolName = "survey_schedule";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "The Survey Schedule for the chosen survey Contracts' waypoints, for the craft's orbit as it is. Taken from orbit: per waypoint, "
                    + "its pass (pass_ut, pass_in, altitude), out_of_band (the Band the pass misses, \"\" if inside), unreachable (with reaching_inclination, "
                    + "the inclination that would reach it) or no pass; and total_wait_s, from now to the last pass. Passes are in flying order. "
                    + "Taken landed: landings, the sites in landing order (landing 1 is landed on from orbit with land, each next is a site_hop from the one before), "
                    + "each with needs_precision true where the Contract's radius is tighter than land's aim of about 5 km. "
                    + "Quote the pass times, the total wait and the unreachable waypoints in the survey's Sequence Plan and set its expected duration from them; "
                    + "name each needs_precision site in the plan. "
                    + "The craft must orbit the body for the passes; call it again once the survey orbit is flown. The survey leg's script gets the same list from ADDONS:CLAUDIA:SURVEYSCHEDULE().";
            }
        }

        public string InputSchemaJson
        {
            get { return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"; }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "scheduling the survey passes"; }
        }

        /// <summary>Whether a Chosen Contract's waypoint is one the Survey Schedule covers: taken from orbit, not landed and not on EVA.</summary>
        internal static bool IsOrbital(SurveyWaypoint waypoint)
        {
            return waypoint != null && !waypoint.Ground && !waypoint.Eva;
        }

        /// <summary>#1311: whether a Chosen Contract's waypoint is taken landed: the Survey Schedule gives it a place in the landing order.</summary>
        internal static bool IsGround(SurveyWaypoint waypoint)
        {
            return waypoint != null && waypoint.Ground;
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody api)
        {
            var all = snapshot.SurveyWaypoints ?? new List<SurveyWaypoint>();
            var orbit = snapshot.Orbit;
            var orbitBody = orbit == null || string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body;
            var ground = all.Where(IsGround).ToList();
            // A ground survey's order needs no orbit: the body is the one its sites are on.
            var landings = ground.Where(w => w.Body == orbitBody).ToList();
            if (landings.Count == 0 && ground.Count > 0) landings = ground.Where(w => w.Body == ground[0].Body).ToList();
            var landBody = landings.Count > 0 ? new BodySystem(snapshot.Bodies).Find(landings[0].Body) : null;
            var hasOrbital = all.Any(IsOrbital);
            var sites = landBody == null ? new List<SurveyPass>() : SurveySchedule.Ground(new SurveyBody { Radius = landBody.Radius }, landings);
            var none = new List<SurveyPass>();
            // The landing order needs no orbit: with no passes to give, or none to be had from this orbit, the sites still go back.
            if (!hasOrbital && landBody != null) return Answer(landBody.Name, none, snapshot.Ut, sites);
            var unusable = orbit == null ? "the craft has no orbit to schedule passes from" : orbit.Eccentricity >= 1 ? "the Survey Schedule needs a closed orbit" : null;
            if (unusable != null) return landBody != null ? Answer(landBody.Name, none, snapshot.Ut, sites) : PlanningJson.Error(unusable);
            var body = new BodySystem(snapshot.Bodies).Find(orbitBody);
            if (body == null) return PlanningJson.Error("the Game Snapshot has no body table entry for " + PlanningJson.Quote(orbitBody ?? ""));
            var waypoints = all.Where(w => IsOrbital(w) && w.Body == body.Name).ToList();
            if (waypoints.Count == 0 && landBody == null) return PlanningJson.Error("no chosen survey Contract has a waypoint to take from orbit round " + body.Name);
            if (waypoints.Count == 0) return Answer(landBody.Name, none, snapshot.Ut, sites);
            double lonOffset, handed;
            var frame = LandingSiteSearch.Frame(body.Name, api, out lonOffset, out handed);
            if (frame != null) return PlanningJson.Error(frame);

            var conic = Conic.FromElements(body.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan, orbit.ArgumentOfPeriapsis,
                orbit.MeanAnomalyAtEpoch, orbit.Epoch);
            var now = snapshot.Ut;
            var turn = body.RotationPeriod > 0 ? 360 / body.RotationPeriod : 0;
            var schedule = SurveySchedule.For(new SurveyBody { Radius = body.Radius, RotationPeriod = body.RotationPeriod },
                new SurveyOrbit { Track = Track(conic, body.Radius, now, turn, lonOffset, handed), Period = conic.Period, Inclination = orbit.Inclination },
                now, waypoints, snapshot.SurveyTriggerRange);
            // #1341: the Survey Plane and the node that reaches it, from the same Core function the script call runs.
            var plane = SurveySchedule.Plane(new SurveyBody { Radius = body.Radius, RotationPeriod = body.RotationPeriod, Gm = body.Gm }, orbit, now,
                (double x, double y, double z, out double lat, out double lon) => api.SurfaceUnder(body.Name, x, y, z, out lat, out lon),
                waypoints, snapshot.SurveyTriggerRange);
            return Answer(body.Name, schedule, now, sites, plane);
        }

        /// <summary>The ground track as Core propagates the orbit, fixed to the turning body from <paramref name="now"/>.</summary>
        internal static GroundTrack Track(Conic conic, double radius, double now, double turnDegPerS, double lonOffset, double handed)
        {
            return ut =>
            {
                var r = conic.Position(ut);
                var length = r.Length;
                var lat = Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI;
                var lon = lonOffset + handed * Math.Atan2(r.Y, r.X) * 180 / Math.PI - turnDegPerS * (ut - now);
                return new TrackPoint { Latitude = lat, Longitude = LandingSiteSearch.Wrap180(lon), Altitude = length - radius };
            };
        }

        private static ToolOutcome Answer(string body, IList<SurveyPass> schedule, double now, IList<SurveyPass> sites, SurveyPlane plane = null)
        {
            var json = new JsonWriter().BeginObject().Field("body", body);
            if (plane != null)
            {
                json.Name("survey_plane").BeginObject().Field("inclination", PlanningJson.Round(plane.Inclination, 1)).Field("lan", PlanningJson.Round(plane.Lan, 1));
                json.Name("passes_over").BeginArray();
                foreach (var waypoint in plane.Passed) json.String(waypoint.Where());
                json.EndArray().EndObject();
                if (plane.Node != null)
                {
                    json.Name("node").BeginObject();
                    PlanningJson.Time(json, "burn", plane.Node.Ut, now);
                    json.Field("prograde", PlanningJson.Round(plane.Node.Prograde, 2)).Field("normal", PlanningJson.Round(plane.Node.Normal, 2))
                        .Field("radial", PlanningJson.Round(plane.Node.Radial, 2)).Field("delta_v", PlanningJson.Round(plane.Node.DeltaV, 1)).EndObject();
                }
            }
            json.Name("passes").BeginArray();
            foreach (var pass in schedule)
            {
                json.BeginObject().Field("waypoint", pass.Waypoint.Where());
                if (pass.Ut.HasValue) PlanningJson.Time(json, "pass", pass.Ut.Value, now);
                if (pass.Altitude.HasValue) json.Field("altitude", PlanningJson.Round(pass.Altitude.Value, 0));
                json.Field("out_of_band", pass.OutOfBand).Field("unreachable", pass.Unreachable);
                if (pass.ReachingInclination.HasValue) json.Field("reaching_inclination", PlanningJson.Round(pass.ReachingInclination.Value, 1));
                json.EndObject();
            }
            json.EndArray();
            // #1311: a ground survey's sites in landing order; the first is landed on with land, each next flown to with site_hop.
            if (sites.Count > 0)
            {
                json.Name("landings").BeginArray();
                foreach (var site in sites)
                {
                    json.BeginObject().Field("landing", site.Landing).Field("waypoint", site.Waypoint.Where());
                    if (site.Waypoint.Radius.HasValue) json.Field("radius", PlanningJson.Round(site.Waypoint.Radius.Value, 0));
                    json.Field("needs_precision", site.NeedsPrecision).Field("radius_unknown", site.RadiusUnknown).EndObject();
                }
                json.EndArray();
            }
            // With no pass at all there is no wait to quote: a zero would tell Claudia the survey costs nothing.
            var passed = schedule.Where(p => p.Ut.HasValue).Select(p => p.Ut.Value).ToList();
            if (passed.Count > 0) json.Field("total_wait_s", PlanningJson.Round(Math.Max(0, passed.Max() - now), 0));
            var unreachable = schedule.Count(p => p.Unreachable);
            json.Field("unreachable_count", unreachable);
            json.EndObject();
            var passes = schedule.Count(p => p.Ut.HasValue);
            var summary = passes + (passes == 1 ? " pass" : " passes") + (unreachable > 0 ? ", " + unreachable + " unreachable" : "");
            if (plane != null) summary += plane.Node != null ? ", Survey Plane " + PlanningJson.Round(plane.Inclination, 1) + "° over " + plane.Passed.Count + " by a " + PlanningJson.Round(plane.Node.DeltaV, 0) + " m/s node" : ", own plane serves";
            if (sites.Count > 0) summary = (schedule.Count > 0 ? summary + ", " : "") + sites.Count + (sites.Count == 1 ? " landing" : " landings") + (sites.Any(x => x.NeedsPrecision) ? ", " + sites.Count(x => x.NeedsPrecision) + " needing precision" : "") + (sites.Any(x => x.RadiusUnknown) ? ", " + sites.Count(x => x.RadiusUnknown) + " radius unknown" : "");
            return new ToolOutcome { Json = json.ToString(), Summary = summary };
        }
    }
}
