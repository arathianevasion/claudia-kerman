// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>find_poi</c> (#713): a body's Points of Interest (<see cref="PointsOfInterest"/>), the table's names on the game's
    /// positions, each with the block that lands there. Cheap: one read of the body's surface objects (cached by the adapter for
    /// the scene) and terrain heights the game didn't give; no search.
    /// </summary>
    internal sealed class FindPoi : IPlanningTool
    {
        public const string Reentry = "reentry (no precision landing yet)";

        public string Name
        {
            get { return "find_poi"; }
        }

        public string Description
        {
            get
            {
                return "A body's Points of Interest (anomalies): name, note, latitude, longitude, terrain_altitude, landable, block, beyond_track_km. "
                    + "Its block is land, beside one, never on it: its name as find_landing_site's poi and land's target lexicon(\"poi\", name). Don't call it for a site.";
            }
        }

        public string InputSchemaJson
        {
            get { return "{\"type\":\"object\",\"properties\":{\"body\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"}},\"required\":[\"body\"],\"additionalProperties\":false}"; }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "finding Points of Interest"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody api)
        {
            var name = JsonNode.Str(input["body"]);
            if (snapshot.Bodies.Count == 0) return PlanningJson.Error("the Game Snapshot has no body table");
            var body = new BodySystem(snapshot.Bodies).Find(name);
            if (body == null)
                return PlanningJson.Error("there is no body named " + PlanningJson.Quote(name) + "; the bodies are "
                    + string.Join(", ", snapshot.Bodies.ConvertAll(b => b.Name).ToArray()));
            var pois = PointsOfInterest.Merge(body.Name, api.SurfaceObjects(body.Name));
            var wanted = JsonNode.Str(input["name"]);
            if (!string.IsNullOrEmpty(wanted))
            {
                var one = PointsOfInterest.Find(pois, wanted);
                if (one == null)
                    return PlanningJson.Error(PlanningJson.Quote(wanted) + " names no one Point of Interest on " + body.Name
                        + (pois.Count == 0 ? "; it has none" : "; its Points of Interest are: " + string.Join(", ", pois.Select(p => p.Name).ToArray())));
                pois = new List<PointOfInterest> { one };
            }
            var unread = pois.Where(p => double.IsNaN(p.Altitude)).Select(p => new SiteSample { Latitude = p.Latitude, Longitude = p.Longitude, Ut = snapshot.Ut }).ToList();
            if (unread.Count > 0) api.Survey(body.Name, unread, SiteFacts.Altitude);

            var orbit = snapshot.Orbit;
            var orbitBody = orbit == null ? "" : string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body;
            var orbiting = orbit != null && string.Equals(orbitBody, body.Name, StringComparison.OrdinalIgnoreCase) && orbit.Eccentricity < 1;
            var air = body.AtmosphereHeight > 0 || body.SeaLevelAtm > 0;
            var json = new JsonWriter().BeginObject().Field("body", body.Name);
            json.Name("pois").BeginArray();
            var at = 0;
            foreach (var poi in pois)
            {
                var altitude = poi.Altitude;
                if (double.IsNaN(altitude))
                {
                    altitude = unread[at].Altitude;
                    at++;
                }
                json.BeginObject().Field("name", poi.Name).Field("note", poi.Note)
                    .Field("latitude", PlanningJson.Round(poi.Latitude, 3)).Field("longitude", PlanningJson.Round(poi.Longitude, 3));
                if (!double.IsNaN(altitude)) json.Field("terrain_altitude", PlanningJson.Round(altitude, 0));
                json.Field("landable", poi.Landable).Field("block", air ? Reentry : "land");
                if (orbiting) json.Field("beyond_track_km", PlanningJson.Round(BeyondTrackKm(poi.Latitude, orbit.Inclination, body.Radius), 1));
                json.EndObject();
            }
            json.EndArray();
            if (pois.Count == 0) json.Field("note", body.Name + " has no Points of Interest the game lists");
            json.EndObject();
            return new ToolOutcome { Json = json.ToString(), Summary = pois.Count + (pois.Count == 1 ? " Point of Interest" : " Points of Interest") + " on " + body.Name };
        }

        /// <summary>
        /// How far beyond the ground track's reach a point at <paramref name="latitude"/> lies, km: the track of an orbit inclined
        /// <paramref name="inclination"/> degrees sweeps every longitude as the body turns, but no latitude past the inclination.
        /// </summary>
        internal static double BeyondTrackKm(double latitude, double inclination, double radius)
        {
            var reach = inclination > 90 ? 180 - inclination : inclination;
            return Math.Max(0, Math.Abs(latitude) - reach) * radius * Math.PI / 180 / 1000;
        }
    }
}
