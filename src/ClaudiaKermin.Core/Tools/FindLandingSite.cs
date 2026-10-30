// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>find_landing_site</c>: up to 3 landing sites on an airless body, each under the craft's current ground track within a
    /// window, with the biome, terrain altitude, slope, the UT the track passes over it and the sun's elevation then. The site
    /// search is <see cref="LandingSiteSearch"/>; terrain, biome and sun come live from the body interface.
    /// </summary>
    internal sealed class FindLandingSite : IPlanningTool
    {
        public string Name
        {
            get { return "find_landing_site"; }
        }

        public string Description
        {
            get
            {
                return "Up to 3 landing sites on an airless body that the craft's current orbit passes over within a window, found from "
                    + "the game's live terrain, biome and sun: each has latitude and longitude (degrees), biome, terrain altitude (m above "
                    + "sea level), slope (degrees), the UT the ground track passes over it, the sun's elevation then, and landing_ut, when "
                    + "land would touch down there from this orbit (correction_dv: its burn onto the site, m/s). Only sites land can fly "
                    + "come back. Filters: an optional biome, daylight (defaults to the Mission Settings), a slope limit (default 5 "
                    + "degrees), a window in hours (defaults to the larger of the Mission Settings time cap and one turn of the body, at "
                    + "most 48) and an optional point to be near. The craft must be in orbit round the body. Its block is land: call it "
                    + "to see that a biome target or a landing the Mission Goal names can be flown, instead of searching by hand, then pass "
                    + "land the biome with the site as near (or the site alone as its target), and any within_hours or max_correction_dv "
                    + "you gave here. plane_changes, for near (else the first site): change_orbit calls (set_inc) that put it under the "
                    + "track, with plane_change_dv, landing_dv and total_dv: cheapest and soonest when land cannot reach it from this orbit, "
                    + "else one only if it saves 20 m/s net. Fly the one you pick as its own step before land, budgeting its total_dv. "
                    + "Don't call it for land's target \"any\", or for a body with an atmosphere (an error; deorbit aims those landings).";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"body\":{\"type\":\"string\",\"description\":\"The airless body to land on, which the craft is orbiting.\"},"
                    + "\"biome\":{\"type\":\"string\",\"description\":\"Only sites in this biome (e.g. Highlands, Midlands, Poles); omit for any.\"},"
                    + "\"daylight\":{\"type\":\"boolean\",\"description\":\"Only sites in Daylight when the track passes: the sun more than 20 degrees above the horizon; defaults to the Mission Settings.\"},"
                    + "\"max_slope_deg\":{\"type\":\"number\",\"description\":\"The steepest ground allowed, degrees; default 5.\"},"
                    + "\"within_hours\":{\"type\":\"number\",\"description\":\"How far ahead to look along the ground track, hours (at most 48); defaults to the larger of the Mission Settings time cap and one turn of the body.\"},"
                    + "\"max_correction_dv\":{\"type\":\"number\",\"description\":\"The most land's correction burn onto a site may cost, m/s; defaults to land's: the smaller of 30 and 5% of the craft's delta-v.\"},"
                    + "\"edge_margin_m\":{\"type\":\"number\",\"description\":\"With a biome: how far inside its edge a site should be, m; default 5000 (about the landing accuracy), 0 for none. Sites at the edge come back only when nothing deeper is reachable, marked near_biome_edge.\"},"
                    + "\"near\":{\"type\":\"object\",\"properties\":{\"latitude\":{\"type\":\"number\"},\"longitude\":{\"type\":\"number\"}},"
                    + "\"required\":[\"latitude\",\"longitude\"],\"additionalProperties\":false,\"description\":\"Prefer sites close to this point, degrees (within 15 degrees of arc of it); plane_changes are for it.\"}"
                    + "},\"required\":[\"body\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "finding a landing site"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody api)
        {
            var name = JsonNode.Str(input["body"]);
            if (snapshot.Bodies.Count == 0) return PlanningJson.Error("the Game Snapshot has no body table");
            var system = new BodySystem(snapshot.Bodies);
            var body = system.Find(name);
            if (body == null)
                return PlanningJson.Error("there is no body named " + PlanningJson.Quote(name) + "; the bodies are "
                    + string.Join(", ", snapshot.Bodies.ConvertAll(b => b.Name).ToArray()));
            if (body.AtmosphereHeight > 0 || body.SeaLevelAtm > 0)
                return PlanningJson.Error(body.Name + " has an atmosphere, and find_landing_site is airless-only for now");
            var orbit = snapshot.Orbit;
            var orbitBody = orbit == null ? "" : string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body;
            if (orbit == null || !string.Equals(orbitBody, body.Name, StringComparison.OrdinalIgnoreCase))
                return PlanningJson.Error("the craft is not in orbit round " + body.Name + "; its ground track is only known for the body it orbits, so ask again once it is");

            var within = JsonNode.Double(input["within_hours"]);
            if (within.HasValue && within.Value <= 0) return PlanningJson.Error("within_hours must be more than 0");
            var query = new LandingSiteQuery
            {
                Biome = JsonNode.Str(input["biome"]),
                Daylight = JsonNode.Bool(input["daylight"]) ?? snapshot.DaylightLandingDefault,
                MaxSlopeDeg = JsonNode.Double(input["max_slope_deg"]) ?? 5,
                WithinHours = LandingPass.WindowHours(within, snapshot.TimeCapHours, body.RotationPeriod),
                MaxCorrectionDv = JsonNode.Double(input["max_correction_dv"]) ?? LandingPass.DefaultMaxCorrectionDv(LanderDv(snapshot)),
                Craft = DescentApproach.FromSnapshot(snapshot),
            };
            query.EdgeMarginM = JsonNode.Double(input["edge_margin_m"]) ?? LandingSiteSearch.DefaultEdgeMarginM;
            if (query.MaxSlopeDeg <= 0) return PlanningJson.Error("max_slope_deg must be more than 0");
            if (!(query.EdgeMarginM >= 0 && query.EdgeMarginM <= 50000)) return PlanningJson.Error("edge_margin_m must be from 0 to 50000");
            if (!(query.MaxCorrectionDv >= 0 && query.MaxCorrectionDv <= 1000)) return PlanningJson.Error("max_correction_dv must be from 0 to 1000 m/s");
            var near = input["near"];
            if (near != null)
            {
                query.HasNear = true;
                query.NearLatitude = JsonNode.Double(near["latitude"]) ?? 0;
                query.NearLongitude = JsonNode.Double(near["longitude"]) ?? 0;
                if (Math.Abs(query.NearLatitude) > 90) return PlanningJson.Error("near.latitude must be between -90 and 90");
            }

            var conic = Conic.FromElements(body.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan, orbit.ArgumentOfPeriapsis,
                orbit.MeanAnomalyAtEpoch, orbit.Epoch);
            var found = LandingSiteSearch.Find(body, conic, snapshot.Ut, query, api);
            if (found.Error != null) return PlanningJson.Error(found.Error);

            var json = new JsonWriter().BeginObject().Field("body", body.Name);
            json.Name("sites").BeginArray();
            foreach (var site in found.Sites)
            {
                json.BeginObject()
                    .Field("latitude", PlanningJson.Round(site.Latitude, 3))
                    .Field("longitude", PlanningJson.Round(site.Longitude, 3))
                    .Field("biome", site.Biome)
                    .Field("terrain_altitude", PlanningJson.Round(site.Altitude, 1))
                    .Field("slope_deg", PlanningJson.Round(site.SlopeDeg, 1));
                PlanningJson.Time(json, "pass", site.PassUt, snapshot.Ut)
                    .Field("sun_elevation_deg", PlanningJson.Round(site.SunElevationDeg, 1));
                PlanningJson.Time(json, "landing", site.LandingUt, snapshot.Ut)
                    .Field("correction_dv", PlanningJson.Round(site.CorrectionDv, 1));
                if (!double.IsNaN(site.PredictedDv))
                    json.Field("predicted_dv", PlanningJson.Round(site.PredictedDv, 0)).Field("descent_shape", PlanningJson.Round(site.DescentShape, 3));
                if (!double.IsNaN(site.FromNearDeg)) json.Field("from_near_deg", PlanningJson.Round(site.FromNearDeg, 1));
                if (site.AtEdge) json.Field("near_biome_edge", true);
                json.EndObject();
            }
            json.EndArray();
            json.Field("window_hours", PlanningJson.Round(found.WindowHours, 2));
            if (found.Sites.Count > 0 && found.Sites[0].AtEdge)
                json.Field("note", "no site more than " + query.EdgeMarginM.ToString("0.##", CultureInfo.InvariantCulture) + " m inside the " + found.Sites[0].Biome
                    + " biome was reachable in " + found.WindowHours.ToString("0.##", CultureInfo.InvariantCulture) + " h; the sites returned are closer than that to the edge, so a landing may come down outside the biome");
            if (found.Sites.Count == 0)
                json.Field("note", "no site met the limits; of " + LandingSiteSearch.Count(found.Candidates) + " ground-track points, "
                    + LandingSiteSearch.Count(found.AfterBiome) + " were in the biome, " + LandingSiteSearch.Count(found.AfterDaylight)
                    + " also in daylight, " + LandingSiteSearch.Count(found.AfterSlope) + " also gentle enough"
                    + (found.Unflyable > 0 ? ", " + LandingSiteSearch.Count(found.Unflyable) + " of them with no pass land can fly in the window (max_correction_dv " + query.MaxCorrectionDv.ToString("0.#", CultureInfo.InvariantCulture) + " m/s)" : "")
                    + (query.Daylight && found.AfterDaylight == 0 && found.AfterBiome > 0
                        ? "; the sun never gets over " + LandingSiteSearch.MinSunElevationDeg.ToString("0.##", CultureInfo.InvariantCulture) + " degrees there in the window, so try a lower-latitude site, a longer window, or daylight false"
                        : "")
                    + "; widen within_hours, the slope or the biome"
                    + (query.HasNear ? "" : ", or give near (the site wanted) for plane changes that reach it"));
            PlaneChangesFor(json, body, conic, snapshot.Ut, query, found, api);
            json.EndObject();

            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = found.Sites.Count == 0 ? "no landing site on " + body.Name
                    : found.Sites.Count + (found.Sites.Count == 1 ? " site" : " sites") + " on " + body.Name + ", the first in the " + found.Sites[0].Biome,
            };
        }

        /// <summary>
        /// The plane changes (#521) for the <c>near</c> point, or without one the first site: <see cref="PlaneChanges"/> over the
        /// window <c>land</c> searches, against the landing <c>land</c> plans there from the orbit as it is (a <c>near</c> point is
        /// coordinates, so it may move up to 5 km for terrain, as <c>land</c> would).
        /// </summary>
        private static void PlaneChangesFor(JsonWriter json, SnapshotBody body, Conic conic, double now, LandingSiteQuery query, LandingSiteResult found, IPlanningBody api)
        {
            if (query.Craft == null || (!query.HasNear && found.Sites.Count == 0)) return;
            double lat, lng, altitude;
            if (query.HasNear)
            {
                lat = query.NearLatitude;
                lng = query.NearLongitude;
                var at = new SiteSample { Latitude = lat, Longitude = lng, Ut = now };
                api.Survey(body.Name, new[] { at }, SiteFacts.Altitude);
                altitude = double.IsNaN(at.Altitude) ? 0 : at.Altitude;
            }
            else
            {
                lat = found.Sites[0].Latitude;
                lng = found.Sites[0].Longitude;
                altitude = found.Sites[0].Altitude;
            }
            var ask = new LandingPassAsk
            {
                Latitude = lat, Longitude = lng, PeRadius = body.Radius + Math.Max(0, altitude) + LandingPass.ApproachM, EarliestUt = now + LandingPass.LeadS,
                LatestUt = now + LandingPass.ReachSeconds(found.WindowHours, query.Daylight, body.RotationPeriod), Daylight = query.Daylight,
                MaxCorrectionDv = query.MaxCorrectionDv, Craft = query.Craft, MoveSite = query.HasNear,
            };
            var currentDv = found.Sites.Count > 0 ? found.Sites[0].PredictedDv : double.NaN;
            var moved = false;
            if (query.HasNear)
            {
                var current = LandingPass.Find(body, conic, now, ask, api);
                currentDv = current.Error == null ? current.LandingDv : double.NaN;
                moved = current.Error == null && current.Moved;
            }
            var changes = PlaneChanges.Find(body, conic, now, ask, api, currentDv, moved);
            json.Name("plane_change_site").BeginObject().Field("latitude", PlanningJson.Round(lat, 3)).Field("longitude", PlanningJson.Round(lng, 3)).EndObject();
            json.Name("plane_changes").BeginArray();
            foreach (var change in changes.Options)
            {
                json.BeginObject().Field("option", change.Option).Field("call", change.Call).Field("goal", "set_inc")
                    .Field("value", PlanningJson.Round(change.Inclination, 3));
                PlanningJson.Time(json, "burn", change.BurnUt, now)
                    .Field("plane_change_dv", PlanningJson.Round(change.PlaneDv, 0))
                    .Field("landing_dv", PlanningJson.Round(change.LandingDv, 0))
                    .Field("total_dv", PlanningJson.Round(change.TotalDv, 0));
                PlanningJson.Time(json, "landing", change.ArriveUt, now)
                    .Field("approach", change.Approach)
                    .Field("heading_deg", PlanningJson.Round(change.Heading, 0))
                    .Field("descent_shape", PlanningJson.Round(change.Shape, 3));
                if (change.Moved) json.Field("moved_for_terrain", true);
                json.EndObject();
            }
            json.EndArray();
            if (changes.Options.Count == 0)
                json.Field("plane_change_note", changes.Note ?? (double.IsNaN(currentDv) ? "no plane change tried reaches the site"
                    : "land reaches the site from this orbit, and no plane change saves " + PlaneChanges.SavingDv.ToString("0", CultureInfo.InvariantCulture) + " m/s net"));
        }

        /// <summary>The craft's delta-v from the stock simulator (m/s, vacuum), as land reads it; 0 (unknown) when it isn't reliable.</summary>
        private static double LanderDv(GameSnapshot snapshot)
        {
            if (!snapshot.DeltaVSimulator || snapshot.DeltaVUnreliable) return 0;
            var total = 0.0;
            foreach (var stage in snapshot.Stages) total += stage.DeltaVVac;
            return total;
        }
    }
}
