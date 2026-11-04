// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        /// <summary>
        /// The time the tool's search has, ms (#536): it answers with what it found by then, inside <see cref="ToolStep.CallTimeout"/>
        /// with room for the last read the game is serving and the answer.
        /// </summary>
        public const double ToolBudgetMs = 3500;

        /// <summary>The search's time, ms; null to run to the end (land's block call, on the main thread, flies what the whole window gives).</summary>
        internal double? BudgetMs = ToolBudgetMs;
        /// <summary>Work out the plane changes; land's block call uses only the site.</summary>
        internal bool WithPlaneChanges = true;
        /// <summary>The craft land will fly (#528: land_craft's, through its block call); null for the Game Snapshot's (<see cref="DescentApproach.FromSnapshot"/>).</summary>
        internal BrakeCraft Craft;
        /// <summary>Sites land has tried already (#528), skipped as <see cref="LandingSiteQuery.Exclude"/>; none for the tool.</summary>
        internal List<double[]> Exclude;

        public string Name
        {
            get { return "find_landing_site"; }
        }

        public string Description
        {
            get
            {
                return "Up to 3 landing sites on an airless body the craft's orbit passes over in a window, from the live terrain, biome "
                    + "and sun: latitude and longitude (degrees), biome, terrain altitude (m), slope (degrees), the UT the track passes over it, the "
                    + "sun's elevation then, and landing_ut, when land would touch down (correction_dv: its burn onto the site, m/s), predicted_dv (m/s land uses from the pass; prefer one the stage fits); descent_shape (small steep and safe, large cheap). Only "
                    + "sites land can fly come back. Filters: biome, daylight (default the Mission Settings), slope (default 5 degrees), a "
                    + "window in hours (default the larger of the time cap and one turn of the body, at most 48), a near point, or a poi. The "
                    + "craft must orbit the body. Its block is land: call it to see a biome or goal can be flown, then pass land "
                    + "the biome with the site as near (or as its target), and any within_hours or max_correction_dv you gave. "
                    + "plane_changes, for near (else the first site or the biome's): change_orbit (set_inc) calls with plane_change_dv, "
                    + "landing_dv and total_dv: cheapest and soonest when land can't reach it, else one only if it saves 20 m/s net; fly it as "
                    + "a step before land, budgeting its total_dv. Don't call it for land's target \"any\" or a body with air (deorbit aims those).";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"body\":{\"type\":\"string\",\"description\":\"The airless body orbited.\"},"
                    + "\"biome\":{\"type\":\"string\",\"description\":\"Only sites in this biome; omit for any.\"},"
                    + "\"daylight\":{\"type\":\"boolean\",\"description\":\"Only sites with the sun over 20 degrees up at the pass.\"},"
                    + "\"max_slope_deg\":{\"type\":\"number\",\"description\":\"The steepest ground, degrees.\"},"
                    + "\"within_hours\":{\"type\":\"number\",\"description\":\"Hours ahead to search.\"},"
                    + "\"max_correction_dv\":{\"type\":\"number\",\"description\":\"The most land's correction burn may cost, m/s; default the smaller of 30 and 5% of the craft's delta-v.\"},"
                    + "\"edge_margin_m\":{\"type\":\"number\",\"description\":\"With a biome: m inside its edge a site should be; default 5000, 0 for none. Sites at the edge come back only when nothing deeper is reachable, marked near_biome_edge.\"},"
                    + "\"near\":{\"type\":\"object\",\"properties\":{\"latitude\":{\"type\":\"number\"},\"longitude\":{\"type\":\"number\"}},"
                    + "\"required\":[\"latitude\",\"longitude\"],\"additionalProperties\":false,\"description\":\"Prefer sites within 15 degrees of arc of this point; plane_changes are for it.\"},"
                    + "\"poi\":{\"description\":\"A find_poi name, or {latitude, longitude}: sites only poi_min_m (150) to poi_max_m (1500) m from it, flattest first; never on it.\"},"
                    + "\"poi_min_m\":{\"type\":\"number\"},\"poi_max_m\":{\"type\":\"number\"}"
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
            // The deadline before the wrapper: it times by the body interface's own clock where it keeps one.
            var deadline = BudgetMs.HasValue ? SearchDeadline.After(BudgetMs.Value, api) : SearchDeadline.None;
            api = new FrameReadOnce(api);
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
                Craft = Craft ?? DescentApproach.FromSnapshot(snapshot),
                Deadline = deadline,
            };
            if (Exclude != null) query.Exclude.AddRange(Exclude);
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

            // #713: a Point of Interest, by name or as coordinates: sites only in the ring round it.
            var poiName = "";
            var poi = input["poi"];
            if (poi != null)
            {
                if (!string.IsNullOrEmpty(query.Biome) || near != null) return PlanningJson.Error("poi takes no biome or near: its sites are in the ring round it");
                var problem = PoiQuery(poi, body, input, query, api, out poiName);
                if (problem != null) return PlanningJson.Error(problem);
            }

            // #591: a biome the body doesn't have is an error naming the body's own, not an empty search.
            if (!string.IsNullOrEmpty(query.Biome))
            {
                var unknown = LandingSiteSearch.UnknownBiome(body.Name, query.Biome, api.BiomeNames(body.Name));
                if (unknown != null) return PlanningJson.Error(unknown);
            }

            var conic = Conic.FromElements(body.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan, orbit.ArgumentOfPeriapsis,
                orbit.MeanAnomalyAtEpoch, orbit.Epoch);
            var found = query.HasPoi ? LandingSiteSearch.Ring(body, conic, snapshot.Ut, query, api) : LandingSiteSearch.Find(body, conic, snapshot.Ut, query, api);
            if (found.Error != null) return PlanningJson.Error(found.Error);
            if (query.HasPoi) return RingAnswer(body, poiName, query, found, snapshot.Ut);
            // #545: a biome no pass reaches gets the plane changes for its best site off the track.
            var lookOffTrack = WithPlaneChanges && !query.HasNear && found.Sites.Count == 0 && !found.OutOfTime && !string.IsNullOrEmpty(query.Biome)
                && query.Craft != null && !deadline.Passed;
            var offTrack = lookOffTrack ? LandingSiteSearch.BiomeSite(body, conic.Inclination, snapshot.Ut, query, api) : null;

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
            if (found.OutOfTime && !query.HasNear) json.Field("searched_hours", PlanningJson.Round(found.SearchedHours, 2));
            var edge = found.Sites.Count > 0 && found.Sites[0].AtEdge
                ? "no site more than " + query.EdgeMarginM.ToString("0.##", CultureInfo.InvariantCulture) + " m inside the " + found.Sites[0].Biome
                    + " biome was reachable in " + (found.OutOfTime ? found.SearchedHours : found.WindowHours).ToString("0.##", CultureInfo.InvariantCulture)
                    + " h; the sites returned are closer than that to the edge, so a landing may come down outside the biome"
                : null;
            if (found.OutOfTime)
                json.Field("note", OutOfTimeNote(query, found) + (edge != null ? "; " + edge : ""));
            else if (edge != null)
                json.Field("note", edge);
            else if (found.Sites.Count == 0)
                json.Field("note", "no site met the limits; of " + LandingSiteSearch.Count(found.Candidates) + " ground-track points, "
                    + LandingSiteSearch.Count(found.AfterBiome) + " were in the biome, " + LandingSiteSearch.Count(found.AfterDaylight)
                    + " also in daylight, " + LandingSiteSearch.Count(found.AfterSlope) + " also gentle enough"
                    + (found.Unflyable > 0 ? ", " + LandingSiteSearch.Count(found.Unflyable) + " of them with no pass land can fly in the window (max_correction_dv " + query.MaxCorrectionDv.ToString("0.#", CultureInfo.InvariantCulture) + " m/s)" : "")
                    + (query.Daylight && found.AfterDaylight == 0 && found.AfterBiome > 0
                        ? "; the sun never gets over " + LandingSiteSearch.MinSunElevationDeg.ToString("0.##", CultureInfo.InvariantCulture) + " degrees there in the window, so try a lower-latitude site, a longer window, or daylight false"
                        : "")
                    + "; widen within_hours, the slope or the biome"
                    + (offTrack != null ? "; plane_changes are for a site in the biome off the track (plane_change_site)"
                        : lookOffTrack ? "; a survey of " + body.Name + " every " + LandingSiteSearch.BiomeGridDeg.ToString("0", CultureInfo.InvariantCulture)
                            + " degrees found no point in the biome gentle enough to plan plane changes for (give near for a smaller biome)"
                        : query.HasNear ? "" : ", or give near (the site wanted) for plane changes that reach it")
                    // Sites at the biome edge are the fallback whatever the margin, so this holds for land's own (the default).
                    + "; land given this biome and these limits and window searches the same ground-track points, so it finds none either, but a site given to land as coordinates may lie off the track, reached by a correction burn");
            var changes = WithPlaneChanges ? PlaneChangesFor(json, body, conic, snapshot.Ut, query, found, offTrack, api) : 0;
            json.EndObject();

            var summary = found.Sites.Count == 0 ? "no landing site on " + body.Name
                : found.Sites.Count + (found.Sites.Count == 1 ? " site" : " sites") + " on " + body.Name + ", the first in the " + found.Sites[0].Biome;
            if (offTrack != null && changes > 0) summary += " under the track; plane changes reach the " + offTrack.Biome;
            if (found.OutOfTime)
                summary += query.HasNear ? " (out of time)" : " (out of time at " + found.SearchedHours.ToString("0.#", CultureInfo.InvariantCulture) + " h)";
            return new ToolOutcome { Json = json.ToString(), Summary = summary };
        }

        /// <summary>
        /// The query's Point of Interest (#713) from the <c>poi</c> input: a find_poi name on the body (one landable), or
        /// {latitude, longitude}; with the ring's <c>poi_min_m</c> and <c>poi_max_m</c>. Null, or why it can't be used.
        /// </summary>
        internal static string PoiQuery(JsonNode poi, SnapshotBody body, JsonNode input, LandingSiteQuery query, IPlanningBody api, out string name)
        {
            name = "";
            if (poi.Kind == JsonKind.String)
            {
                var pois = PointsOfInterest.Merge(body.Name, api.SurfaceObjects(body.Name));
                var one = PointsOfInterest.Find(pois, poi.StringValue);
                if (one == null)
                    return PlanningJson.Quote(poi.StringValue) + " names no one Point of Interest on " + body.Name
                        + (pois.Count == 0 ? "; it has none" : "; its Points of Interest are: " + string.Join(", ", pois.Select(p => p.Name).ToArray()));
                if (!one.Landable) return one.Name + " is not a landable Point of Interest: " + one.Note;
                name = one.Name;
                query.PoiLatitude = one.Latitude;
                query.PoiLongitude = one.Longitude;
            }
            else if (poi.Kind == JsonKind.Object && JsonNode.Double(poi["latitude"]).HasValue && JsonNode.Double(poi["longitude"]).HasValue)
            {
                query.PoiLatitude = JsonNode.Double(poi["latitude"]).Value;
                query.PoiLongitude = JsonNode.Double(poi["longitude"]).Value;
                if (Math.Abs(query.PoiLatitude) > 90) return "poi.latitude must be between -90 and 90";
                name = query.PoiLatitude.ToString("0.###", CultureInfo.InvariantCulture) + ", " + query.PoiLongitude.ToString("0.###", CultureInfo.InvariantCulture);
            }
            else return "poi must be a find_poi name or {\"latitude\": x, \"longitude\": y}";
            query.HasPoi = true;
            query.PoiMinM = JsonNode.Double(input["poi_min_m"]) ?? LandingSiteSearch.PoiMinM;
            query.PoiMaxM = JsonNode.Double(input["poi_max_m"]) ?? LandingSiteSearch.PoiMaxM;
            if (!(query.PoiMinM >= 50 && query.PoiMinM <= 5000)) return "poi_min_m must be from 50 to 5000: never on the Point of Interest";
            if (!(query.PoiMaxM > query.PoiMinM && query.PoiMaxM <= 10000)) return "poi_max_m must be more than poi_min_m, at most 10000";
            return null;
        }

        /// <summary>The ring search's answer (#713): each site with its distance and bearing from the Point of Interest, and never the point itself.</summary>
        private static ToolOutcome RingAnswer(SnapshotBody body, string poiName, LandingSiteQuery query, LandingSiteResult found, double now)
        {
            var json = new JsonWriter().BeginObject().Field("body", body.Name).Field("poi", poiName)
                .Field("poi_latitude", PlanningJson.Round(query.PoiLatitude, 4)).Field("poi_longitude", PlanningJson.Round(query.PoiLongitude, 4)).Field("poi_min_m", query.PoiMinM);
            json.Name("sites").BeginArray();
            foreach (var site in found.Sites)
            {
                json.BeginObject()
                    .Field("latitude", PlanningJson.Round(site.Latitude, 4))
                    .Field("longitude", PlanningJson.Round(site.Longitude, 4))
                    .Field("from_poi_m", PlanningJson.Round(site.FromPoiM, 0))
                    .Field("bearing_from_poi_deg", PlanningJson.Round(site.BearingFromPoi, 0))
                    .Field("biome", site.Biome)
                    .Field("terrain_altitude", PlanningJson.Round(site.Altitude, 1))
                    .Field("slope_deg", PlanningJson.Round(site.SlopeDeg, 1))
                    .Field("sun_elevation_deg", PlanningJson.Round(site.SunElevationDeg, 1));
                PlanningJson.Time(json, "landing", site.LandingUt, now)
                    .Field("correction_dv", PlanningJson.Round(site.CorrectionDv, 1));
                if (!double.IsNaN(site.PredictedDv))
                    json.Field("predicted_dv", PlanningJson.Round(site.PredictedDv, 0)).Field("descent_shape", PlanningJson.Round(site.DescentShape, 3));
                json.EndObject();
            }
            json.EndArray();
            json.Field("window_hours", PlanningJson.Round(found.WindowHours, 2));
            var ring = query.PoiMinM.ToString("0", CultureInfo.InvariantCulture) + " to " + query.PoiMaxM.ToString("0", CultureInfo.InvariantCulture) + " m from " + poiName;
            if (found.Sites.Count == 0)
                json.Field("note", (found.OutOfTime ? "partial: out of time; " : "") + "no site in the ring " + ring + ": of " + LandingSiteSearch.Count(found.Candidates)
                    + " points measured, " + LandingSiteSearch.Count(found.AfterSlope) + " were within max_slope_deg "
                    + query.MaxSlopeDeg.ToString("0.#", CultureInfo.InvariantCulture)
                    + (found.Unflyable > 0 ? ", " + LandingSiteSearch.Count(found.Unflyable) + " of them with no pass land can fly in the window" : "")
                    + "; widen poi_max_m, max_slope_deg or within_hours. The Point of Interest itself is never a site: land beside it, not on it");
            else if (found.OutOfTime)
                json.Field("note", "partial: out of time; the sites are those found in the ring " + ring);
            json.EndObject();
            var summary = found.Sites.Count == 0 ? "no landing site beside " + poiName + " on " + body.Name
                : found.Sites.Count + (found.Sites.Count == 1 ? " site" : " sites") + " beside " + poiName + " on " + body.Name;
            return new ToolOutcome { Json = json.ToString(), Summary = summary };
        }

        /// <summary>A search that ran out of time (#536): how far it got, and that land, searching the whole window, may still find a site.</summary>
        private string OutOfTimeNote(LandingSiteQuery query, LandingSiteResult found)
        {
            var how = query.HasNear
                ? LandingSiteSearch.Count(found.Tried) + " of the " + LandingSiteSearch.Count(found.NearPoints) + " track points nearest near were tried"
                : "the ground track was searched for the first " + found.SearchedHours.ToString("0.#", CultureInfo.InvariantCulture) + " h of the "
                    + found.WindowHours.ToString("0.##", CultureInfo.InvariantCulture) + " h window";
            var sites = found.Sites.Count == 0 ? "no site met the limits there" : "the sites are those found there";
            return "partial: out of time (" + (BudgetMs.GetValueOrDefault() / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s): " + how + ", and " + sites
                + "; land given this biome and these limits and window searches the whole window"
                + (found.Sites.Count == 0 ? ", so it may still find a site later in it" : " and takes the first site it finds, the first here");
        }

        /// <summary>
        /// The plane changes (#521) for the <c>near</c> point, or without one the first site, or without a site the biome's best
        /// site off the track (<paramref name="offTrack"/>, #545): <see cref="PlaneChanges"/> over the window <c>land</c> searches,
        /// against the landing <c>land</c> plans there from the orbit as it is (a <c>near</c> point or the site off the track is
        /// coordinates, so it may move up to 5 km for terrain, as <c>land</c> would). The number of plane changes offered.
        /// </summary>
        private static int PlaneChangesFor(JsonWriter json, SnapshotBody body, Conic conic, double now, LandingSiteQuery query, LandingSiteResult found,
            LandingSite offTrack, IPlanningBody api)
        {
            if (query.Craft == null || (!query.HasNear && offTrack == null && found.Sites.Count == 0)) return 0;
            var moveSite = query.HasNear || offTrack != null;
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
                var site = offTrack ?? found.Sites[0];
                lat = site.Latitude;
                lng = site.Longitude;
                altitude = site.Altitude;
            }
            var ask = new LandingPassAsk
            {
                Latitude = lat, Longitude = lng, PeRadius = body.Radius + Math.Max(0, altitude) + LandingPass.ApproachM, EarliestUt = now + LandingPass.LeadS,
                LatestUt = now + LandingPass.ReachSeconds(found.WindowHours, query.Daylight, body.RotationPeriod), Daylight = query.Daylight,
                MaxCorrectionDv = query.MaxCorrectionDv, Craft = query.Craft, MoveSite = moveSite, Deadline = query.Deadline,
            };
            json.Name("plane_change_site").BeginObject().Field("latitude", PlanningJson.Round(lat, 3)).Field("longitude", PlanningJson.Round(lng, 3)).EndObject();
            var currentDv = found.Sites.Count > 0 ? found.Sites[0].PredictedDv : double.NaN;
            var moved = false;
            var late = query.Deadline != null && query.Deadline.Passed;
            if (moveSite && !late)
            {
                var current = LandingPass.Find(body, conic, now, ask, api);
                late = current.OutOfTime;
                currentDv = current.Error == null ? current.LandingDv : double.NaN;
                moved = current.Error == null && current.Moved;
            }
            if (late)
            {
                // Whether land reaches the site from this orbit isn't known, so neither is which plane changes to offer.
                json.Name("plane_changes").BeginArray().EndArray();
                json.Field("plane_change_note", "out of time before the plane changes were worked out; ask again with near set to the site and a shorter within_hours");
                return 0;
            }
            var changes = PlaneChanges.Find(body, conic, now, ask, api, currentDv, moved);
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
            if (changes.Options.Count > 0 && changes.OutOfTime)
                json.Field("plane_change_note", "partial: out of time, so these are the best of the " + changes.Planes.ToString(CultureInfo.InvariantCulture) + " planes tried"
                    + (changes.Coarse ? ", landing_dv from a coarse plan of the descent; ask again with near set to the site to plan it fully" : ""));
            else if (changes.Options.Count == 0)
                json.Field("plane_change_note", changes.Note ?? (double.IsNaN(currentDv) ? "no plane change tried reaches the site"
                    : "land reaches the site from this orbit, and no plane change saves " + PlaneChanges.SavingDv.ToString("0", CultureInfo.InvariantCulture) + " m/s net"));
            return changes.Options.Count;
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
