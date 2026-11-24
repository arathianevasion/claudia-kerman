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
                return "The Survey Schedule for the chosen survey Contracts' waypoints taken from orbit, for the craft's orbit as it is: per waypoint, "
                    + "its pass (pass_ut, pass_in, altitude), out_of_band (the Band the pass misses, \"\" if inside), unreachable (with reaching_inclination, "
                    + "the inclination that would reach it) or no pass; and total_wait_s, from now to the last pass. Passes are in flying order. "
                    + "Quote the pass times, the total wait and the unreachable waypoints in the survey's Sequence Plan and set its expected duration from them. "
                    + "The craft must orbit the body; call it again once the survey orbit is flown. The survey leg's script gets the same list from ADDONS:CLAUDIA:SURVEYSCHEDULE().";
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

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody api)
        {
            var orbit = snapshot.Orbit;
            if (orbit == null) return PlanningJson.Error("the craft has no orbit to schedule passes from");
            if (orbit.Eccentricity >= 1) return PlanningJson.Error("the Survey Schedule needs a closed orbit");
            var orbitBody = string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body;
            var body = new BodySystem(snapshot.Bodies).Find(orbitBody);
            if (body == null) return PlanningJson.Error("the Game Snapshot has no body table entry for " + PlanningJson.Quote(orbitBody ?? ""));
            var waypoints = (snapshot.SurveyWaypoints ?? new List<SurveyWaypoint>()).Where(w => IsOrbital(w) && w.Body == body.Name).ToList();
            if (waypoints.Count == 0) return PlanningJson.Error("no chosen survey Contract has a waypoint to take from orbit round " + body.Name);
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
            return Answer(body.Name, schedule, now);
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

        private static ToolOutcome Answer(string body, IList<SurveyPass> schedule, double now)
        {
            var json = new JsonWriter().BeginObject().Field("body", body);
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
            // With no pass at all there is no wait to quote: a zero would tell Claudia the survey costs nothing.
            var passed = schedule.Where(p => p.Ut.HasValue).Select(p => p.Ut.Value).ToList();
            if (passed.Count > 0) json.Field("total_wait_s", PlanningJson.Round(Math.Max(0, passed.Max() - now), 0));
            var unreachable = schedule.Count(p => p.Unreachable);
            json.Field("unreachable_count", unreachable);
            json.EndObject();
            var passes = schedule.Count(p => p.Ut.HasValue);
            return new ToolOutcome { Json = json.ToString(), Summary = passes + (passes == 1 ? " pass" : " passes") + (unreachable > 0 ? ", " + unreachable + " unreachable" : "") };
        }
    }
}
