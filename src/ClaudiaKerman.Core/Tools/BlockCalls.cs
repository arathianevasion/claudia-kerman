// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The block-internal calls (ADR 0006, ADR 0007): the mod's calculations a Building Block makes while it flies, through
    /// <c>ADDONS:CLAUDIA</c>: the site search, the reentry predictor with the deorbit burn search on it, the turn time, the
    /// ejection burn to a moon and the one home from it, the launch window, the Course Correction and solve_burn's orbit
    /// changes. They run the same C# as the Planning Tools, on a Game Snapshot read at the call (the reentry calls also read the
    /// air and the craft's drag cubes live, through <see cref="IReentryAir"/> and <see cref="IReentryCraft"/>), and only read. The
    /// turn time is the exception: it needs no Snapshot, only the craft's torque and moment of inertia, read live into a
    /// <see cref="TurnCraft"/>. The Script Check lets the Core Library call them and refuses them in a Mission Script. Inputs and
    /// results are plain values, as <c>KosValues</c> converts a kOS lexicon.
    /// </summary>
    public static class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDINGSITE</c>, for <c>land</c> with a biome target: <c>find_landing_site</c>'s search on the body the
        /// craft orbits (any <c>body</c> given is ignored), taking its other inputs (<c>biome</c>, <c>daylight</c>,
        /// <c>max_slope_deg</c>, <c>within_hours</c>, <c>near</c>). Gives the first site, the soonest pass or the nearest the
        /// <c>near</c> point: <c>error</c> "", <c>lat</c>, <c>lng</c>, <c>biome</c>, <c>terrain_altitude</c>, <c>slope_deg</c>,
        /// <c>pass_ut</c>, <c>sun_elevation_deg</c>, <c>near_edge</c> (true if nothing deeper in the biome was reachable). With no site, or a search that could not run, only <c>error</c>, saying why.
        /// Two inputs are the block's alone, not the tool's (#528): <c>craft</c>, the craft the pass search will fly (<see cref="CraftOf"/>'s keys;
        /// without thrust and mass, the Game Snapshot's), whose descents keep the sites; and <c>exclude</c>, the sites tried already (lexicons of
        /// <c>lat</c>, <c>lng</c>), none of whose neighbours comes back; and <c>check_only</c> (#622), true to check <c>biome</c> alone, before the
        /// craft stages: <c>error</c> "" if the body orbited has it (or the check can't tell), else the tool's unknown-biome error.
        /// </summary>
        public static IDictionary<string, object> LandingSite(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var orbited = snapshot == null ? "" : snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            ask["body"] = orbited ?? "";
            // land stays airless-only (#828): the tool's search on a body with air is for deorbit's target.
            var over = snapshot == null ? null : snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, orbited, StringComparison.OrdinalIgnoreCase));
            if (over != null && LandingSiteSearch.HasAir(over)) return Error("land flies airless bodies only; " + over.Name + " has an atmosphere; use reentry");
            object checkOnly;
            if (ask.TryGetValue("check_only", out checkOnly) && checkOnly is bool && (bool)checkOnly)
            {
                object named;
                if (ask.TryGetValue("poi", out named)) return PoiCheck(Convert.ToString(named, CultureInfo.InvariantCulture), snapshot, orbited, body);
                ask.TryGetValue("biome", out named);
                var unknown = LandingSiteSearch.UnknownBiome(orbited, Convert.ToString(named, CultureInfo.InvariantCulture), body.BiomeNames(orbited ?? ""));
                return unknown == null ? new Dictionary<string, object> { { "error", "" } } : Error(unknown);
            }
            ask.Remove("check_only");
            object craft, exclude;
            ask.TryGetValue("craft", out craft);
            ask.TryGetValue("exclude", out exclude);
            ask.Remove("craft");
            ask.Remove("exclude");
            var tried = new List<double[]>();
            var excluded = exclude as IEnumerable<object>;
            if (excluded != null)
                foreach (var point in excluded)
                {
                    var at = point as IDictionary<string, object>;
                    if (at != null && NumberOrNull(at, "lat").HasValue && NumberOrNull(at, "lng").HasValue) tried.Add(new[] { Number(at, "lat", 0), Number(at, "lng", 0) });
                }
            // The whole window, with no time limit (#536: land flies what all of it gives), and no plane changes: land uses only the site.
            var search = new PlanningTools(new IPlanningTool[]
            {
                new FindLandingSite
                {
                    BudgetMs = null, WithPlaneChanges = false, Exclude = tried,
                    Craft = craft is IDictionary<string, object> ? CraftOf(new Dictionary<string, object>((IDictionary<string, object>)craft, StringComparer.OrdinalIgnoreCase)) : null,
                },
            });
            var outcome = search.Run("find_landing_site", ToolTestGate.InputJson(ask), snapshot, body);
            var result = JsonReader.Parse(outcome.Json);
            if (outcome.IsError) return Error(JsonNode.Str(result["error"]));

            var sites = result["sites"];
            if (result["poi"] != null) return PoiSite(result, sites, snapshot);
            if (sites == null || sites.Items.Count == 0)
            {
                object biome;
                ask.TryGetValue("biome", out biome);
                var hours = (JsonNode.Double(result["window_hours"]) ?? 0).ToString("0.#", CultureInfo.InvariantCulture);
                // The tool's advice is for Claudia's inputs; the block gives its own.
                var note = JsonNode.Str(result["note"]);
                var advice = note.IndexOf("; widen", StringComparison.Ordinal);
                if (advice >= 0) note = note.Substring(0, advice);
                return Error("no site in the " + Convert.ToString(biome, CultureInfo.InvariantCulture) + " biome on " + orbited + " under the next "
                    + hours + " h of ground track: " + note);
            }
            var site = sites.Items[0];
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "lat", JsonNode.Double(site["latitude"]) ?? 0 },
                { "lng", JsonNode.Double(site["longitude"]) ?? 0 },
                { "biome", JsonNode.Str(site["biome"]) },
                { "terrain_altitude", JsonNode.Double(site["terrain_altitude"]) ?? 0 },
                { "slope_deg", JsonNode.Double(site["slope_deg"]) ?? 0 },
                { "pass_ut", JsonNode.Double(site["pass_ut"]) ?? 0 },
                { "sun_elevation_deg", JsonNode.Double(site["sun_elevation_deg"]) ?? 0 },
                { "near_edge", site["near_biome_edge"] != null },
                { "predicted_dv", JsonNode.Double(site["predicted_dv"]) ?? -1 },
                { "descent_shape", JsonNode.Double(site["descent_shape"]) ?? -1 },
            };
        }

        /// <summary>
        /// LANDINGSITE's check-only call for a Point of Interest target (#713): <c>error</c> "" if the body orbited has one landable
        /// Point of Interest of that name, else why not, before land stages.
        /// </summary>
        private static IDictionary<string, object> PoiCheck(string name, GameSnapshot snapshot, string orbited, IPlanningBody api)
        {
            var at = snapshot == null || snapshot.Bodies.Count == 0 ? null : new BodySystem(snapshot.Bodies).Find(orbited ?? "");
            if (at == null) return new Dictionary<string, object> { { "error", "" } };
            string found;
            var problem = FindLandingSite.PoiQuery(JsonReader.Parse(PlanningJson.Quote(name ?? "")), at, JsonReader.Parse("{}"), new LandingSiteQuery(), api, out found);
            return problem == null ? new Dictionary<string, object> { { "error", "" } } : Error(problem);
        }

        /// <summary>
        /// LANDINGSITE's answer for a Point of Interest target (#713): the first ring site, with <c>poi</c> (its name),
        /// <c>poi_lat</c>, <c>poi_lng</c>, <c>clear_m</c> (poi_min_m: land's keep_clear on the point), <c>max_divert</c> (m a divert
        /// may move the site and keep 50 m across either pass's track from the point) and <c>note</c> (the trace: the site's distance
        /// and bearing from the point). With no site, only <c>error</c>.
        /// </summary>
        private static IDictionary<string, object> PoiSite(JsonNode result, JsonNode sites, GameSnapshot snapshot)
        {
            var poi = JsonNode.Str(result["poi"]);
            var orbited = snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            if (sites == null || sites.Items.Count == 0)
            {
                var note = JsonNode.Str(result["note"]);
                var advice = note.IndexOf("; widen", StringComparison.Ordinal);
                if (advice >= 0) note = note.Substring(0, advice);
                return Error("no site beside " + poi + " on " + orbited + ": " + note);
            }
            var site = sites.Items[0];
            var fromM = JsonNode.Double(site["from_poi_m"]) ?? 0;
            var bearing = JsonNode.Double(site["bearing_from_poi_deg"]) ?? 0;
            var poiLat = JsonNode.Double(result["poi_latitude"]) ?? 0;
            var clearM = JsonNode.Double(result["poi_min_m"]) ?? LandingSiteSearch.PoiMinM;
            var across = LandingSiteSearch.AcrossM(fromM, bearing, poiLat, snapshot.Orbit != null ? snapshot.Orbit.Inclination : 0);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "lat", JsonNode.Double(site["latitude"]) ?? 0 },
                { "lng", JsonNode.Double(site["longitude"]) ?? 0 },
                { "biome", JsonNode.Str(site["biome"]) },
                { "terrain_altitude", JsonNode.Double(site["terrain_altitude"]) ?? 0 },
                { "slope_deg", JsonNode.Double(site["slope_deg"]) ?? 0 },
                { "near_edge", false },
                { "predicted_dv", JsonNode.Double(site["predicted_dv"]) ?? -1 },
                { "descent_shape", JsonNode.Double(site["descent_shape"]) ?? -1 },
                { "poi", poi },
                { "poi_lat", poiLat },
                { "poi_lng", JsonNode.Double(result["poi_longitude"]) ?? 0 },
                { "clear_m", clearM },
                { "max_divert", Math.Max(0, across - KeepClear.DefaultClearM) },
                { "note", "beside " + poi + ": " + fromM.ToString("0", CultureInfo.InvariantCulture) + " m from it, bearing "
                    + bearing.ToString("0", CultureInfo.InvariantCulture) + " degrees" },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDINGPASS</c>, for <c>land</c> with a site (#510): its pass search (<see cref="Tools.LandingPass"/>)
        /// over the site <c>lat</c>, <c>lng</c> (degrees) on the body the craft orbits, the one <c>find_landing_site</c> keeps its
        /// sites by. Takes <c>r_pe</c> (m from the body's centre: the descent's periapsis), <c>earliest_ut</c> (UT; now plus 120 s if
        /// missing), <c>daylight</c> (default true), <c>max_correction_dv</c> (m/s; 30 if missing) and <c>within_hours</c> (the
        /// window as <c>find_landing_site</c> takes it; its default if missing). Gives <c>error</c> "", <c>burn_ut</c>,
        /// <c>arrive_ut</c>, <c>correction_dv</c>, <c>cross_m</c>, <c>sun</c> (degrees; -90 where not asked), <c>next_pass_ut</c> (the
        /// next pass's touchdown; -1 for none), <c>fall_s</c>, <c>window_hours</c> (how far it looked, with daylight's reach) and
        /// <c>passes</c> (each pass looked at: <c>burn_ut</c>, <c>arrive_ut</c>, <c>cross_km</c>, <c>correction_dv</c>, <c>sun</c>,
        /// <c>verdict</c>). With no pass, <c>error</c> says why, with <c>passes</c> and <c>next_pass_ut</c> -1; asked with
        /// <c>plane_changes</c> true (and no shape), it also gives the plane changes that reach the site (<see cref="PlaneChanges"/>,
        /// #521) in <c>error</c> and as <c>change_orbit</c> calls in <c>plane_changes</c> (empty otherwise). A search that could not
        /// run gives only <c>error</c>.
        /// </summary>
        public static IDictionary<string, object> LandingPass(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || snapshot.Orbit == null) return Error("the craft is not in orbit, so it has no passes over the site");
            if (snapshot.Bodies.Count == 0) return Error("the Game Snapshot has no body table");
            var orbited = !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            var at = new BodySystem(snapshot.Bodies).Find(orbited);
            if (at == null) return Error("there is no body named " + PlanningJson.Quote(orbited));
            if (at.AtmosphereHeight > 0 || at.SeaLevelAtm > 0) return Error("land flies airless bodies only; " + at.Name + " has an atmosphere; use reentry");
            foreach (var key in new[] { "lat", "lng", "r_pe" })
                if (!(NumberOrNull(ask, key).HasValue)) return Error(key + " is required");
            object daylight;
            if (ask.TryGetValue("daylight", out daylight) && !(daylight is bool)) return Error("daylight must be true or false");

            var orbit = snapshot.Orbit;
            var conic = Conic.FromElements(at.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan, orbit.ArgumentOfPeriapsis,
                orbit.MeanAnomalyAtEpoch, orbit.Epoch);
            var window = Tools.LandingPass.WindowHours(NumberOrNull(ask, "within_hours"), snapshot.TimeCapHours, at.RotationPeriod);
            var wantDaylight = daylight == null || (bool)daylight;
            var reach = Tools.LandingPass.ReachSeconds(window, wantDaylight, at.RotationPeriod);
            double? shape;
            var shapeProblem = ShapeOf(ask, out shape);
            if (shapeProblem != null) return Error(shapeProblem);
            object move;
            ask.TryGetValue("move_site", out move);
            var passAsk = new LandingPassAsk
            {
                Latitude = Number(ask, "lat", 0),
                Longitude = Number(ask, "lng", 0),
                PeRadius = Number(ask, "r_pe", 0),
                EarliestUt = Number(ask, "earliest_ut", snapshot.Ut + Tools.LandingPass.LeadS),
                LatestUt = Math.Min(snapshot.Ut + reach, Number(ask, "latest_ut", double.PositiveInfinity)),
                Daylight = wantDaylight,
                MaxCorrectionDv = Number(ask, "max_correction_dv", Tools.LandingPass.MostCorrectionDv),
                Craft = CraftOf(ask),
                Shape = shape,
                Margin = Number(ask, "burn_margin", 1.1),
                MoveSite = move is bool && (bool)move,
            };
            var found = Tools.LandingPass.Find(at, conic, snapshot.Ut, passAsk, body);
            if (!found.Ran) return Error(found.Error);
            // #521: with no pass, the plane changes that reach the site (find_landing_site's), for the report; land never flies them.
            object wantChanges;
            ask.TryGetValue("plane_changes", out wantChanges);
            var changes = new List<object>();
            var error = found.Error;
            if (error != null && wantChanges is bool && (bool)wantChanges && !shape.HasValue && passAsk.Craft != null)
            {
                var offered = PlaneChanges.Find(at, conic, snapshot.Ut, passAsk, body, double.NaN, false);
                var lines = new List<string>();
                foreach (var change in offered.Options)
                {
                    changes.Add(change.Call);
                    lines.Add(change.Line(snapshot.Ut));
                }
                if (lines.Count > 0) error += "; plane changes that reach it (fly one as its own step, then land again): " + string.Join("; ", lines.ToArray());
            }
            var passes = new List<object>();
            foreach (var pass in found.Passes)
                passes.Add(new Dictionary<string, object>
                {
                    { "burn_ut", Math.Round(pass.BurnUt, 1) },
                    { "arrive_ut", Math.Round(pass.ArriveUt, 1) },
                    { "cross_km", Math.Round(pass.CrossM / 1000, 1) },
                    { "correction_dv", Math.Round(pass.CorrectionDv, 1) },
                    { "sun", double.IsNaN(pass.SunDeg) ? -90.0 : Math.Round(pass.SunDeg, 1) },
                    { "verdict", pass.Verdict },
                    { "landing_dv", double.IsNaN(pass.LandingDv) ? -1.0 : Math.Round(pass.LandingDv, 1) },
                });
            var result = new Dictionary<string, object>
            {
                { "error", error ?? "" },
                { "plane_changes", changes },
                { "burn_ut", found.BurnUt },
                { "arrive_ut", found.ArriveUt },
                { "correction_dv", found.CorrectionDv },
                { "cross_m", found.CrossM },
                { "sun", double.IsNaN(found.SunDeg) ? -90.0 : found.SunDeg },
                { "next_pass_ut", found.NextPassUt },
                { "fall_s", found.FallS },
                { "window_hours", reach / 3600 },
                { "passes", passes },
                { "r_pe", found.PeRadius },
                { "lat", found.Error == null ? found.SiteLat : Number(ask, "lat", 0) },
                { "lng", found.Error == null ? found.SiteLng : Number(ask, "lng", 0) },
                { "moved_for_terrain", found.Moved },
                { "terrain_passes", found.TerrainPasses },
                { "shape_lo", double.IsNaN(found.ShapeLo) ? -1.0 : found.ShapeLo },
                { "shape_hi", double.IsNaN(found.ShapeHi) ? -1.0 : found.ShapeHi },
            };
            Descent(result, found.Descent, found.LandingDv);
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BRAKEPATH</c>, for <c>land</c> as braking begins (#511): its descent simulated (<see cref="BrakePath"/>) from
        /// the craft's state, braking from now, onto the site <c>lat</c>, <c>lng</c> (degrees) <c>s</c> m ahead along the track on
        /// <c>heading</c> (degrees across the ground), at <c>alt</c> m above sea level moving <c>v_h</c> m/s across the ground and
        /// <c>v_up</c> m/s up, for the craft (<see cref="CraftOf"/>), on <c>shape</c> (a Descent Shape, or "auto" for the cheapest
        /// that clears). Gives <c>error</c> "", <c>clears</c>, <c>descent_shape</c>, <c>shape_lo</c>, <c>shape_hi</c> (-1: none clears),
        /// <c>brake_dv</c>, <c>predicted_clearance</c> and <c>path</c> (lexicons of <c>s</c>, <c>alt</c>, <c>ground</c>); not clearing,
        /// <c>clears</c> false with the shape's own figures and <c>error</c> "". A call that could not run gives only <c>error</c>.
        /// </summary>
        public static IDictionary<string, object> BrakePathCall(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return Error("no Game Snapshot");
            var name = snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            var at = new BodySystem(snapshot.Bodies).Find(name);
            if (at == null) return Error("there is no body named " + PlanningJson.Quote(name));
            foreach (var key in new[] { "lat", "lng", "heading", "s", "alt", "v_h", "v_up" })
                if (!NumberOrNull(ask, key).HasValue) return Error(key + " is required");
            double? shape;
            var shapeProblem = ShapeOf(ask, out shape);
            if (shapeProblem != null) return Error(shapeProblem);
            var craft = CraftOf(ask);
            if (craft == null) return Error("thrust and mass are required");
            var s = Number(ask, "s", 0);
            string error;
            var ground = DescentApproach.Read(body, at.Name, at.Radius, Number(ask, "lat", 0), Number(ask, "lng", 0), Number(ask, "heading", 0),
                Math.Max(0, s) + 2000, out error);
            if (ground == null) return Error(error);
            var world = new BrakeWorld { Mu = at.Gm, Radius = at.Radius, Ground = ground, Margin = Number(ask, "burn_margin", 1.1) };
            var start = new BrakeStart { S = s, Alt = Number(ask, "alt", 0), VH = Number(ask, "v_h", 0), VUp = Number(ask, "v_up", 0), Braking = true };
            var all = BrakePath.FromState(craft, world, start, BrakePath.Shapes);
            var flown = shape.HasValue ? BrakePath.Simulate(craft, world, start, shape.Value) : all.Best != null ? all.Best.Result : null;
            var result = new Dictionary<string, object>
            {
                { "error", "" },
                { "clears", flown != null && flown.Clears },
                { "shape_lo", double.IsNaN(all.ShapeLo) ? -1.0 : all.ShapeLo },
                { "shape_hi", double.IsNaN(all.ShapeHi) ? -1.0 : all.ShapeHi },
            };
            if (flown == null)
            {
                result["descent_shape"] = -1.0;
                return result;
            }
            result["descent_shape"] = flown.Shape;
            result["brake_dv"] = flown.DvUsed;
            result["predicted_clearance"] = Finite(flown.LowestClearance);
            result["path"] = PathOf(flown);
            return result;
        }

        /// <summary>The descent's figures for <c>land</c>: shape, delta-v, clearance, braking start and the path.</summary>
        private static void Descent(Dictionary<string, object> result, DescentTry descent, double landingDv)
        {
            if (descent == null)
            {
                result["descent_shape"] = -1.0;
                result["predicted_dv"] = -1.0;
                result["path"] = new List<object>();
                return;
            }
            result["descent_shape"] = descent.Shape;
            result["predicted_dv"] = landingDv;
            result["deorbit_dv"] = descent.DeorbitDv;
            result["brake_dv"] = descent.Result.DvUsed;
            result["predicted_clearance"] = Finite(descent.Result.LowestClearance);
            result["brake_start_s"] = Finite(descent.Result.BrakeStartS);
            result["brake_end_agl"] = Finite(descent.Result.BrakeEndAgl);
            result["heading"] = descent.Heading;
            result["path"] = PathOf(descent.Result);
        }

        private static List<object> PathOf(BrakeResult flown)
        {
            var path = new List<object>();
            foreach (var p in flown.Path)
                path.Add(new Dictionary<string, object> { { "s", Math.Round(p.S) }, { "alt", Math.Round(p.Alt) }, { "ground", Math.Round(p.Ground) } });
            return path;
        }

        private static double Finite(double x)
        {
            return double.IsNaN(x) || double.IsInfinity(x) ? -1 : x;
        }

        /// <summary>
        /// The craft that will brake, as <c>land</c> measures it: <c>thrust</c> (kN), <c>mass</c> (t), <c>isp</c> (s), <c>turn_lag</c>
        /// (s), <c>dry_mass</c> (t, where the stage runs dry; 0 or missing for never), and with an Underhang <c>drop_agl</c> (m), <c>lander_thrust</c> (kN), <c>lander_mass</c> (t), <c>lander_isp</c> (s);
        /// null without thrust and mass.
        /// </summary>
        internal static BrakeCraft CraftOf(IDictionary<string, object> ask)
        {
            var thrust = Number(ask, "thrust", 0);
            var mass = Number(ask, "mass", 0);
            if (thrust <= 0 || mass <= 0) return null;
            return new BrakeCraft
            {
                ThrustN = thrust * 1000,
                MassKg = mass * 1000,
                Isp = Number(ask, "isp", 300),
                DryMassKg = Number(ask, "dry_mass", 0) * 1000,
                LagS = Number(ask, "turn_lag", 1),
                DropAgl = Number(ask, "drop_agl", -1),
                LanderThrustN = Number(ask, "lander_thrust", 0) * 1000,
                LanderMassKg = Number(ask, "lander_mass", 0) * 1000,
                LanderIsp = Number(ask, "lander_isp", 300),
            };
        }

        /// <summary>The Descent Shape asked for: <c>shape</c> a number, or "auto" or missing for none; a problem text otherwise.</summary>
        private static string ShapeOf(IDictionary<string, object> ask, out double? shape)
        {
            shape = null;
            object value;
            if (!ask.TryGetValue("shape", out value) || value == null) return null;
            if (value is string && string.Equals((string)value, "auto", StringComparison.OrdinalIgnoreCase)) return null;
            if (!(value is double) || (double)value <= 0) return "shape must be \"auto\" or a Descent Shape over 0";
            shape = (double)value;
            return null;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DEORBITBURN</c>, for <c>deorbit</c>: the deorbit burn search (<see cref="DeorbitBurn"/>) from the craft's
        /// orbit, by the reentry predictor with <paramref name="air"/> and <paramref name="craft"/>. Takes <c>lat</c>, <c>lng</c>
        /// (the target), <c>earliest_ut</c>, <c>max_wait</c> (s), <c>ground_alt</c> (m), <c>accuracy_km</c>, <c>isp</c> (s; 0 or
        /// missing for none), <c>max_dv</c> (m/s; missing or not a number is auto) and <c>dv_left</c> (m/s; 0 when stock can't say, which auto refuses); <c>chutes</c> (#477, true or missing: the predictor opens the stowed chutes; false keeps them stowed;
        /// anything else is an error); <c>leave_out</c> (<see cref="LeftOut"/>) is for the adapter's <paramref name="craft"/>. Gives <c>error</c> "", <c>burn_ut</c>, <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver
        /// node components), <c>dv</c>, and the landing: <c>lat</c>, <c>lng</c>, <c>error_km</c>, <c>landing_ut</c>. A best burn
        /// outside the accuracy gives its <c>error</c> with the landing; a search that could not run, only <c>error</c>.
        /// </summary>
        public static IDictionary<string, object> DeorbitBurn(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body,
            IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            bool chutes;
            var chutesProblem = ChutesOf(ask, out chutes);
            if (chutesProblem != null) return Error(chutesProblem);
            DropBurn payer;
            var craftProblem = EntryCraftOf(ask, out payer);
            if (craftProblem != null) return Error(craftProblem);
            SnapshotBody at;
            Conic orbit;
            BodyFrame frame;
            var problem = Reentry(snapshot, body, null, out at, out orbit, out frame);
            if (problem != null) return Error(problem);
            var plan = Tools.DeorbitBurn.Find(at, orbit, new DeorbitAsk
            {
                TargetLatitude = Number(ask, "lat", 0),
                TargetLongitude = Number(ask, "lng", 0),
                EarliestUt = Number(ask, "earliest_ut", snapshot.Ut),
                MaxWait = Number(ask, "max_wait", at.RotationPeriod),
                GroundAltitude = Number(ask, "ground_alt", 0),
                AccuracyKm = Number(ask, "accuracy_km", 5),
                Isp = Number(ask, "isp", 0),
                Payer = payer,
                Chutes = chutes,
                MaxDv = Number(ask, "max_dv", 0),
                DeltaVLeft = Number(ask, "dv_left", 0),
            }, air, craft, frame);
            if (plan.Landing == null || plan.Landing.Error != null) return Error(plan.Error ?? plan.Landing.Error);
            var result = Landing(plan.Landing, plan.ErrorKm);
            result["error"] = plan.Error ?? "";
            result["burn_ut"] = plan.BurnUt;
            result["prograde"] = plan.Prograde;
            result["normal"] = plan.Normal;
            result["radial"] = 0.0;
            result["dv"] = plan.DeltaV;
            result["plan_log"] = BurnPlanLog.Orbit("DEORBITBURN", plan.After, at.Radius, frame, Number(ask, "ground_alt", 0), plan.BurnUt);
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDINGTARGET</c>, for a <c>return</c> or <c>deorbit</c> whose target is "auto" (#790): <c>target</c> "ksc" or "none" (<see cref="HomeLanding.DefaultTarget"/>), from <c>planet</c> and, for a return,
        /// <c>target_pe</c> (m), <c>target_inc</c> (degrees; "any" or missing for none) and <c>window</c> (UT; "next" or missing for none).
        /// </summary>
        public static IDictionary<string, object> LandingTarget(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            object planet;
            ask.TryGetValue("planet", out planet);
            return new Dictionary<string, object>
            {
                { "target", HomeLanding.DefaultTarget(snapshot, new LandingTargetAsk { Planet = planet as string ?? "", TargetPe = NumberOrNull(ask, "target_pe"), TargetInc = NumberOrNull(ask, "target_inc"), Window = NumberOrNull(ask, "window") }) },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:REENTRYIMPACT</c>, for <c>deorbit</c> once it has burned: where the craft's orbit as it is now comes
        /// down, by the reentry predictor. Takes <c>ground_alt</c> (m) and, for the distance, <c>lat</c> and <c>lng</c>. Given
        /// <c>orbit_sma</c>, <c>orbit_ecc</c>, <c>orbit_inc</c>, <c>orbit_lan</c>, <c>orbit_argpe</c>, <c>orbit_mna</c> (degrees, as kOS
        /// gives them), <c>orbit_epoch</c> (UT) and <c>orbit_body</c> (name), it predicts that orbit instead of the craft's: <c>return</c>
        /// asks it of its planned path round Kerbin before the craft is there, with <c>from_ut</c> (UT, default now) the UT that
        /// path begins, so the fall is not taken from an earlier pass of the same conic. Gives
        /// <c>error</c> "", <c>lat</c>, <c>lng</c>, <c>landing_ut</c> and <c>error_km</c> (-1 without a target); or only
        /// <c>error</c>, saying why there is no landing point. <c>chutes</c> (#477) is as for <see cref="DeorbitBurn"/>.
        /// </summary>
        public static IDictionary<string, object> ReentryImpact(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body,
            IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            bool chutes;
            var chutesProblem = ChutesOf(ask, out chutes);
            if (chutesProblem != null) return Error(chutesProblem);
            DropBurn payer;
            var craftProblem = EntryCraftOf(ask, out payer);
            if (craftProblem != null) return Error(craftProblem);
            SnapshotBody at;
            Conic orbit;
            BodyFrame frame;
            var problem = Reentry(snapshot, body, ask, out at, out orbit, out frame);
            if (problem != null) return Error(problem);
            var landing = ReentryPredictor.Predict(at, orbit, Number(ask, "from_ut", snapshot.Ut), Number(ask, "ground_alt", 0), craft.MassKg, air, craft, frame, chutes);
            if (landing.Error != null) return Error(landing.Error);
            var km = ask.ContainsKey("lat") && ask.ContainsKey("lng")
                ? LandingSiteSearch.ArcDegrees(landing.Latitude, landing.Longitude, Number(ask, "lat", 0), Number(ask, "lng", 0)) * Math.PI / 180 * at.Radius / 1000
                : -1;
            var result = Landing(landing, km);
            result["error"] = "";
            result["plan_log"] = BurnPlanLog.Orbit("REENTRYIMPACT", orbit, at.Radius, frame, Number(ask, "ground_alt", 0), Number(ask, "from_ut", snapshot.Ut));
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:TURNTIME</c>, for a block that turns to a burn: how long to leave for the turn (<see cref="Tools.TurnTime"/>),
        /// from the torque and moment of inertia the adapter reads into <paramref name="craft"/>. Takes <c>max_stopping_time</c> (s,
        /// kOS's <c>steeringmanager:maxstoppingtime</c>; 2 if missing). Gives <c>error</c> "", <c>turn_time</c> (s, at least 30),
        /// <c>turn_s</c> (the 190° turn alone), <c>top_rate</c> (deg/s) and <c>alpha</c> (deg/s², the weaker axis's angular
        /// acceleration), on the wheels and RCS (if the craft has MonoPropellant, whatever the switch; where RCS adds torque the
        /// figures follow the burst turn, #692 and #730, and ignore <c>max_stopping_time</c>), and <c>wheels_turn_time</c> and <c>wheels_turn_s</c>, the same on the wheels alone (-1 when the
        /// wheels have no torque about pitch or yaw); or only <c>error</c>, "no usable torque".
        /// </summary>
        public static IDictionary<string, object> TurnTime(IDictionary<string, object> input, TurnCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var stopping = Number(ask, "max_stopping_time", Tools.TurnTime.KosMaxStoppingTime);
            var turn = Tools.TurnTime.Predict(craft, stopping);
            if (turn.Error != null) return Error(turn.Error);
            var wheels = Tools.TurnTime.PredictWheels(craft, stopping);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "turn_time", turn.Seconds },
                { "turn_s", turn.TurnSeconds },
                { "top_rate", turn.TopRateDegS },
                { "alpha", turn.AlphaDegS2 },
                { "wheels_turn_time", wheels.Error == null ? wheels.Seconds : -1.0 },
                { "wheels_turn_s", wheels.Error == null ? wheels.TurnSeconds : -1.0 },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:EJECTIONBURN</c>, for <c>transfer_to</c>: the departure burn of a transfer (<see cref="Transfer"/>), in
        /// place of RSVP's search, to <c>target</c> (the name of a moon of the body the craft orbits) arriving at <c>target_pe</c>
        /// (m), or to an orbit round the craft's body given as <c>target_sma</c> (m), <c>target_ecc</c>, <c>target_inc</c>,
        /// <c>target_lan</c>, <c>target_argpe</c>, <c>target_mna</c> (degrees, as kOS gives them) and <c>target_epoch</c> (UT).
        /// <c>mode</c> is "rendezvous" (the default: meet the target where it is) or "intercept" (an orbit only: reach it anywhere);
        /// <c>capture</c> (default true) minimises the departure plus the arrival burn, false the departure alone. It takes
        /// <c>earliest_ut</c> (UT; now if missing) and <c>window</c> ("next", the default, or a UT): it searches one synodic period
        /// (an intercept, one orbit of the craft) from the later of the two, so a window sooner than <c>earliest_ut</c> is skipped
        /// for the next. <c>latest_ut</c> (UT) ends the search there and <c>max_dv</c> (m/s) refuses a dearer departure; both are
        /// optional, for the correction after an ejection that missed (#363), which also gives <c>plane_change</c> false (default
        /// true): one burn, never a broken-plane plan. Gives <c>error</c> "", <c>ut</c>, <c>prograde</c>,
        /// <c>normal</c>, <c>radial</c> (m/s, maneuver node components), <c>dv</c>, <c>arrival_dv</c> (m/s: the circular capture at a
        /// body's periapsis, or matching an orbit's velocity), <c>encounter_ut</c>, <c>arrival_ut</c> (periapsis at a body; for an
        /// orbit both are the arrival), <c>target_pe_actual</c> (m; -1 for an orbit), <c>search_from</c> (UT) and <c>synodic</c>
        /// (s), and <c>plane_ut</c> (UT; -1 for none), <c>plane_prograde</c>, <c>plane_normal</c>, <c>plane_radial</c> and
        /// <c>plane_dv</c> (m/s; 0 for none): a broken-plane plan's plane change (#619), which <c>transfer_to</c> flies with
        /// CORRECTIONBURN at plane_ut, its aim_ut arrival_ut; or only <c>error</c>, "found no transfer: &lt;why&gt;".
        /// </summary>
        public static IDictionary<string, object> EjectionBurn(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            object target, window, mode, capture;
            ask.TryGetValue("target", out target);
            ask.TryGetValue("window", out window);
            ask.TryGetValue("mode", out mode);
            ask.TryGetValue("capture", out capture);
            var toOrbit = ask.ContainsKey("target_sma");
            if (!toOrbit && (!ask.ContainsKey("target_pe") || !(ask["target_pe"] is double))) return Error(Transfer.Fail("target_pe is required").Error);
            if (window != null && !(window is double) && !string.Equals(window as string, "next", StringComparison.OrdinalIgnoreCase))
                return Error(Transfer.Fail("window must be \"next\" or a UT, not " + Convert.ToString(window, CultureInfo.InvariantCulture)).Error);
            var intercept = string.Equals(mode as string, "intercept", StringComparison.OrdinalIgnoreCase);
            if (mode != null && !intercept && !string.Equals(mode as string, "rendezvous", StringComparison.OrdinalIgnoreCase))
                return Error(Transfer.Fail("mode must be \"rendezvous\" or \"intercept\", not " + Convert.ToString(mode, CultureInfo.InvariantCulture)).Error);
            if (capture != null && !(capture is bool)) return Error(Transfer.Fail("capture must be true or false").Error);
            object planeChange;
            ask.TryGetValue("plane_change", out planeChange);
            if (planeChange != null && !(planeChange is bool)) return Error(Transfer.Fail("plane_change must be true or false").Error);
            SnapshotOrbit orbit = null;
            if (toOrbit)
            {
                foreach (var key in new[] { "target_sma", "target_ecc", "target_inc", "target_lan", "target_argpe", "target_mna", "target_epoch" })
                {
                    if (!ask.ContainsKey(key)) return Error(Transfer.Fail(key + " is required with target_sma: the target orbit takes all seven elements").Error);
                    if (!(ask[key] is double)) return Error(Transfer.Fail(key + " must be a number").Error);
                }
                orbit = new SnapshotOrbit
                {
                    SemiMajorAxis = Number(ask, "target_sma", 0),
                    Eccentricity = Number(ask, "target_ecc", 0),
                    Inclination = Number(ask, "target_inc", 0),
                    Lan = Number(ask, "target_lan", 0),
                    ArgumentOfPeriapsis = Number(ask, "target_argpe", 0),
                    MeanAnomalyAtEpoch = Number(ask, "target_mna", 0) * Math.PI / 180,
                    Epoch = Number(ask, "target_epoch", 0),
                };
            }
            if (snapshot == null) return Error(Transfer.Fail("no Game Snapshot").Error);
            var plan = Transfer.Find(snapshot, new TransferAsk
            {
                Target = target as string,
                TargetOrbit = orbit,
                Mode = intercept ? TransferMode.Intercept : TransferMode.Rendezvous,
                Capture = ArrivalRule.Capture(snapshot, target as string, capture as bool?),
                TargetPe = Number(ask, "target_pe", 0),
                EarliestUt = Number(ask, "earliest_ut", snapshot.Ut),
                WindowUt = window as double?,
                LatestUt = NumberOrNull(ask, "latest_ut"),
                MaxDv = NumberOrNull(ask, "max_dv"),
                BrokenPlane = planeChange as bool? ?? true,
            });
            if (plan.Error != null) return Error(plan.Error);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "ut", plan.Ut },
                { "prograde", plan.Prograde },
                { "normal", plan.Normal },
                { "radial", plan.Radial },
                { "dv", plan.DeltaV },
                { "arrival_dv", plan.ArrivalDv },
                { "encounter_ut", plan.EncounterUt },
                { "arrival_ut", plan.ArrivalUt },
                { "target_pe_actual", double.IsNaN(plan.PeActual) ? -1.0 : plan.PeActual },
                { "search_from", plan.SearchFrom },
                { "synodic", plan.Synodic },
                { "plane_ut", plan.PlaneUt ?? -1.0 },
                { "plane_prograde", plan.PlanePrograde },
                { "plane_normal", plan.PlaneNormal },
                { "plane_radial", plan.PlaneRadial },
                { "plane_dv", plan.PlaneDv },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:TRANSFERSTEP</c>, for <c>transfer_to</c> (#459, ADR 0014): its next step (<see cref="TransferFlight"/>),
        /// asked again after each. Takes <c>target</c> (a body's name), <c>target_pe</c>, <c>window</c> ("next" or a UT),
        /// <c>correction</c>, <c>pe_tolerance</c>, <c>start_ut</c>, <c>max_duration</c> ("auto" or s), <c>auto</c> (whether it began
        /// "auto"), <c>turn_time</c>, <c>dv_left</c>, and what the last step carried: <c>phase</c> ("start" first), <c>arrival_ut</c>,
        /// <c>plane_ut</c>, <c>why</c>, <c>sibling</c>, <c>recovered</c>, <c>restored</c>, <c>drops</c>, <c>mid_ut</c>, <c>watch_dropped</c>, <c>checkpoint</c> ("off", "on" or a resume token, #775) and <c>last_dv</c> (m/s the last burn used). Gives <c>error</c> ("" or why the block fails),
        /// <c>do</c> ("burn", "wait" or "done"), <c>kind</c>, <c>ut</c>, <c>prograde</c>, <c>normal</c>, <c>radial</c>,
        /// <c>max_residual</c>, <c>fail_prefix</c>, <c>keep_going</c>, <c>report</c> (Script Result keys), <c>max_duration</c> (-1:
        /// unchanged), <c>watch</c> (a wait's Encounter Watch, for <see cref="EncounterWatch"/>; empty for none) and <c>carry</c> (the keys to ask with next).
        /// </summary>
        public static IDictionary<string, object> TransferStep(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            object target, window, correction, auto, phase, why, sibling, recovered, restored, watchDropped;
            ask.TryGetValue("target", out target);
            ask.TryGetValue("window", out window);
            ask.TryGetValue("correction", out correction);
            ask.TryGetValue("auto", out auto);
            ask.TryGetValue("phase", out phase);
            ask.TryGetValue("why", out why);
            ask.TryGetValue("sibling", out sibling);
            ask.TryGetValue("recovered", out recovered);
            ask.TryGetValue("restored", out restored);
            ask.TryGetValue("watch_dropped", out watchDropped);
            object timing;
            if (ask.TryGetValue("timing", out timing)) return Timing(ask, timing as string, snapshot);
            if (!(target is string)) return Step(new TransferFlightStep { Error = "found no transfer: target must be a body's name" });
            var maxDuration = NumberOrNull(ask, "max_duration");
            object checkpoint;
            ask.TryGetValue(CoastCheckpoints.TokenKey, out checkpoint);
            var mode = checkpoint as string ?? CoastCheckpoints.Off;
            var step = TransferFlight.Next(snapshot, new TransferFlightAsk
            {
                Target = (string)target,
                TargetPe = Number(ask, "target_pe", 0),
                Window = window as double?,
                Correction = correction as bool? ?? true,
                PeTolerance = Number(ask, "pe_tolerance", 1000),
                StartUt = Number(ask, "start_ut", snapshot != null ? snapshot.Ut : 0),
                MaxDuration = maxDuration,
                Auto = auto as bool? ?? !maxDuration.HasValue,
                TurnTime = Number(ask, "turn_time", 0),
                DvLeft = Number(ask, "dv_left", 0),
                Phase = phase as string ?? TransferFlight.Start,
                ArrivalUt = Number(ask, "arrival_ut", -1),
                PlaneUt = Number(ask, "plane_ut", -1),
                Why = why as string ?? "",
                LastDv = Number(ask, "last_dv", 0),
                Sibling = sibling as bool? ?? false,
                Recovered = recovered as bool? ?? false,
                Restored = restored as bool? ?? false,
                WatchDropped = watchDropped as bool? ?? false,
                Drops = (int)Number(ask, "drops", 0),
                MidUt = Number(ask, "mid_ut", -1),
                Checkpoints = mode != CoastCheckpoints.Off,
                Resume = CoastToken.Parse(mode),
                PlaneOption = PlaneOption.Read(ask.ContainsKey("plane") ? ask["plane"] : null),
            });
            var result = Step(step);
            result["aimed_plane"] = step.AimedPlane;
            // A resumed flight's token is spent; its later coasts may checkpoint again.
            ((IDictionary<string, object>)result["carry"])[CoastCheckpoints.TokenKey] = mode == CoastCheckpoints.Off ? CoastCheckpoints.Off : CoastCheckpoints.On;
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:CAPTURESTEP</c>, for <c>capture</c> (#788, ADR 0014): its next step (<see cref="CaptureFlight"/>), asked again after each.
        /// Takes <c>alt</c> and <c>max_plane_dv</c> ("auto" or a number), <c>plane</c> (as <see cref="PlaneOption.Read"/>), <c>turn_time</c>, and what the last step
        /// carried: <c>phase</c>, <c>why</c> (the way the tilt is flown) and <c>drops</c> (the burns flown). Gives what <see cref="TransferStep"/> gives.
        /// </summary>
        public static IDictionary<string, object> CaptureStep(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            object phase, why, alt, maxPlane;
            ask.TryGetValue("phase", out phase);
            ask.TryGetValue("why", out why);
            ask.TryGetValue("alt", out alt);
            ask.TryGetValue("max_plane_dv", out maxPlane);
            if (alt is string && (string)alt != "auto") return Step(new TransferFlightStep { Error = "alt must be \"auto\" or an altitude in m, not " + alt });
            if (maxPlane is string && (string)maxPlane != "auto") return Step(new TransferFlightStep { Error = "max_plane_dv must be \"auto\" or m/s, not " + maxPlane });
            return Step(CaptureFlight.Next(snapshot, new CaptureAsk
            {
                Alt = NumberOrNull(ask, "alt"),
                Plane = PlaneOption.Read(ask.ContainsKey("plane") ? ask["plane"] : null),
                MaxPlaneDv = NumberOrNull(ask, "max_plane_dv"),
                TurnTime = Number(ask, "turn_time", 0),
                Phase = phase as string ?? CaptureFlight.Start,
                Method = why as string ?? "",
                Burns = (int)Number(ask, "drops", 0),
            }));
        }

        /// <summary>
        /// TRANSFERSTEP's timing asks, for <c>return</c> (#731): <c>timing</c> "earliest" (<c>start_ut</c>, <c>turn_time</c>, <c>window</c>)
        /// gives <c>ut</c>, the earliest departure; "correction" (<c>mid</c>, <c>now</c>, <c>lead</c>, <c>start_ut</c>, <c>max_duration</c>
        /// "auto" or s) gives <c>ut</c> and <c>why</c> ("" or why there is no time for the Course Correction). Shaped as a step, with <c>do</c> "timing".
        /// "coast" (#775: <c>target</c>, <c>mid</c>, <c>turn_time</c>, <c>arrive_ut</c>, <c>checkpoint</c>) gives <c>do</c> "done" at a Coast Checkpoint, "wait" to <c>wait_ut</c> when resumed, or "go"; <c>ut</c> is the correction's.
        /// </summary>
        private static IDictionary<string, object> Timing(IDictionary<string, object> ask, string kind, GameSnapshot snapshot)
        {
            var step = new TransferFlightStep { Do = "timing", Kind = kind ?? "" };
            var why = "";
            if (kind == "coast")
            {
                object target, mode;
                ask.TryGetValue("target", out target);
                ask.TryGetValue(CoastCheckpoints.TokenKey, out mode);
                var coast = CoastCheckpoints.ReturnCoast(snapshot, target as string ?? "", Number(ask, "mid", 0), Number(ask, "turn_time", 0), Number(ask, "arrive_ut", 0), mode as string);
                var asked = Step(coast);
                asked["wait_ut"] = coast.WaitUt;
                return asked;
            }
            if (kind == "earliest")
                step.Ut = TransferFlight.Earliest(Number(ask, "start_ut", 0), Number(ask, "turn_time", 0), ask.ContainsKey("window") ? ask["window"] as double? : null);
            else if (kind == "correction")
                step.Ut = TransferFlight.CorrectionUt(Number(ask, "mid", 0), Number(ask, "now", 0), Number(ask, "lead", 0), Number(ask, "start_ut", 0), NumberOrNull(ask, "max_duration"), out why);
            else
                step.Error = "found no timing: timing must be 'earliest', 'correction' or 'coast'";
            var result = Step(step);
            result["why"] = why;
            return result;
        }

        private static IDictionary<string, object> Step(TransferFlightStep step)
        {
            return new Dictionary<string, object>
            {
                { "error", step.Error },
                { "do", step.Do },
                { "kind", step.Kind },
                { "ut", step.Ut },
                { "prograde", step.Prograde },
                { "normal", step.Normal },
                { "radial", step.Radial },
                { "max_residual", step.MaxResidual },
                { "fail_prefix", step.FailPrefix },
                { "keep_going", step.KeepGoing },
                { "report", new Dictionary<string, object>(step.Report) },
                { "max_duration", step.MaxDuration ?? -1.0 },
                { "watch", step.Watch != null ? step.Watch.ToLexicon() : new Dictionary<string, object>() },
                { "carry", new Dictionary<string, object>
                    {
                        { "phase", step.Phase }, { "arrival_ut", step.ArrivalUt }, { "plane_ut", step.PlaneUt }, { "why", step.Why ?? "" }, { "sibling", step.Sibling },
                        { "recovered", step.Recovered }, { "restored", step.Restored }, { "drops", (double)step.Drops }, { "mid_ut", step.MidUt }, { "watch_dropped", false },
                    }
                },
            };
        }

        /// <summary>
        /// The parts a reentry call leaves out of the craft (#326): <c>leave_out</c>, the kOS UIDs of the parts <c>deorbit</c> drops
        /// after its burn, so the predictor flies only what stays. One UID may be given bare. The adapter builds the craft from it.
        /// </summary>
        public static IList<string> LeftOut(IDictionary<string, object> input)
        {
            return Uids(input, "leave_out").Concat(Uids(CraftAsk(input), "leave_out")).ToList();
        }

        /// <summary>
        /// The parts the heat shield's drop takes (#462): <c>shield_drop</c>, kOS UIDs, as <see cref="LeftOut"/>. The adapter builds
        /// the craft after the drop from it; none (the default) keeps the heat shield to the ground in the prediction.
        /// Both also read <c>craft</c>, <see cref="EntryCraftCall"/>'s answer (#814), which a block passes on as it is.
        /// </summary>
        public static IList<string> ShieldDrop(IDictionary<string, object> input)
        {
            return Uids(input, "shield_drop").Concat(Uids(CraftAsk(input), "shield_drop")).ToList();
        }

        /// <summary>The <c>craft</c> lexicon in <paramref name="input"/>; null when there is none.</summary>
        private static IDictionary<string, object> CraftAsk(IDictionary<string, object> input)
        {
            object craft;
            if (input == null || !new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase).TryGetValue("craft", out craft)) return null;
            var given = craft as IDictionary<string, object>;
            return given == null ? null : new Dictionary<string, object>(given, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Why the <c>craft</c> a reentry call was given can't be flown (its own <c>error</c>, or not <see cref="EntryCraftCall"/>'s answer), or null; with
        /// <paramref name="payer"/> its masses for who pays a burn before the Entry (<see cref="DropGuard.MassAfterBurn"/>), null when no craft was given.
        /// </summary>
        private static string EntryCraftOf(IDictionary<string, object> ask, out DropBurn payer)
        {
            payer = null;
            if (!ask.ContainsKey("craft")) return null;
            var craft = CraftAsk(ask);
            if (craft == null) return "craft must be ENTRYCRAFT's answer";
            var error = craft.ContainsKey("error") ? craft["error"] as string : null;
            if (!string.IsNullOrEmpty(error)) return "the Entry's craft: " + error;
            if (!NumberOrNull(craft, "whole").HasValue) return "craft must be ENTRYCRAFT's answer";
            payer = new DropBurn { Whole = Number(craft, "whole", 0), GonePropellant = Number(craft, "gone_prop", 0), BurnersKept = Number(craft, "burners_kept", 0) };
            return null;
        }

        private static IList<string> Uids(IDictionary<string, object> input, string key)
        {
            var uids = new List<string>();
            object value;
            if (input == null || !new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase).TryGetValue(key, out value) || value == null)
                return uids;
            var many = value as System.Collections.IEnumerable;
            if (value is string || many == null) many = new[] { value };
            foreach (var uid in many)
                if (uid != null) uids.Add(Convert.ToString(uid, CultureInfo.InvariantCulture));
            return uids;
        }

        /// <summary>The body, the craft's orbit and how the frame sits on the body, for a reentry call; or why there are none.</summary>
        private static string Reentry(GameSnapshot snapshot, IPlanningBody body, IDictionary<string, object> ask, out SnapshotBody at, out Conic orbit, out BodyFrame frame)
        {
            at = null;
            orbit = null;
            frame = null;
            var elements = snapshot == null ? null : snapshot.Orbit;
            if (ask != null && ask.ContainsKey("orbit_sma"))
            {
                elements = new SnapshotOrbit
                {
                    SemiMajorAxis = Number(ask, "orbit_sma", 0),
                    Eccentricity = Number(ask, "orbit_ecc", 0),
                    Inclination = Number(ask, "orbit_inc", 0),
                    Lan = Number(ask, "orbit_lan", 0),
                    ArgumentOfPeriapsis = Number(ask, "orbit_argpe", 0),
                    MeanAnomalyAtEpoch = Number(ask, "orbit_mna", 0) * Math.PI / 180,
                    Epoch = Number(ask, "orbit_epoch", 0),
                    Body = ask.ContainsKey("orbit_body") ? ask["orbit_body"] as string : null,
                };
            }
            if (elements == null || elements.SemiMajorAxis == 0) return "the Game Snapshot has no orbit for the craft";
            var name = string.IsNullOrEmpty(elements.Body) ? snapshot.Body : elements.Body;
            at = new BodySystem(snapshot.Bodies).Find(name);
            if (at == null || at.Gm <= 0) return "the body table has no " + name;
            if (at.AtmosphereHeight <= 0) return "body has no atmosphere";
            orbit = Conic.FromElements(at.Gm, elements.SemiMajorAxis, elements.Eccentricity, elements.Inclination, elements.Lan,
                elements.ArgumentOfPeriapsis, elements.MeanAnomalyAtEpoch, elements.Epoch);
            string error;
            frame = BodyFrame.Read(at, body, snapshot.Ut, out error);
            return error;
        }

        private static Dictionary<string, object> Landing(ReentryPrediction landing, double km)
        {
            return new Dictionary<string, object>
            {
                { "lat", landing.Latitude },
                { "lng", landing.Longitude },
                { "landing_ut", landing.LandingUt },
                { "error_km", km },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LAUNCHWINDOW</c>, for <c>launch</c> given a plane to launch into (#464): when the craft's pad next passes
        /// under the plane of inclination <c>inc</c> and ascending node <c>lan</c> (degrees; the sign of <c>inc</c> is ignored), by
        /// <see cref="LaunchWindow"/> on the Game Snapshot's pad and body. <c>target_ap</c> (m, default 0) sets the heading's orbital
        /// speed. Gives <c>error</c> "", <c>wait</c> (s), <c>ut</c>, <c>north</c> (the window launches northward), <c>inc</c> (negative for
        /// a southbound window), <c>heading</c> (degrees) and <c>residual</c> (degrees: from a site past a prograde plane, the window
        /// is due east and this far from the plane, #727); or only <c>error</c>, saying why the pad never passes under a retrograde plane.
        /// </summary>
        public static IDictionary<string, object> LaunchWindow(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return Error("no Game Snapshot");
            var inc = NumberOrNull(ask, "inc");
            var lan = NumberOrNull(ask, "lan");
            if (!inc.HasValue || !lan.HasValue) return Error("inc and lan are required numbers");
            var body = new BodySystem(snapshot.Bodies).Find(snapshot.Body);
            if (body == null) return Error("the body table has no " + snapshot.Body);
            var window = Tools.LaunchWindow.Find(body, snapshot.Ut, snapshot.Latitude, snapshot.CelestialLongitude, inc.Value, lan.Value, Number(ask, "target_ap", 0));
            if (window.Error != null) return Error(window.Error);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "wait", window.WaitSeconds },
                { "ut", window.Ut },
                { "north", window.North },
                { "inc", window.Inclination },
                { "heading", window.Heading },
                { "residual", window.Residual },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LAUNCHHEADING</c>, for <c>launch</c>'s heading (#475): <see cref="Tools.LaunchWindow.HeadingInto"/>, the
        /// compass heading plan_ascent reports, from the pad at <c>lat</c> (degrees) into the plane of inclination <c>inc</c> (degrees;
        /// negative is southbound) to a circular orbit <c>target_ap</c> m up, on a body of <c>gm</c> (m^3/s^2), <c>radius</c> (m) and
        /// <c>rotation</c> (s). The block calls it again on the climb, as the pad's latitude moves. Gives <c>error</c> "" and
        /// <c>heading</c> (degrees), or only <c>error</c>.
        /// </summary>
        public static IDictionary<string, object> LaunchHeading(IDictionary<string, object> input)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "inc", "lat", "gm", "radius", "rotation" })
                if (!NumberOrNull(ask, key).HasValue) return Error(key + " is required");
            var body = new SnapshotBody { Gm = Number(ask, "gm", 0), Radius = Number(ask, "radius", 0), RotationPeriod = Number(ask, "rotation", 0) };
            if (body.Gm <= 0 || body.Radius <= 0 || body.RotationPeriod <= 0) return Error("the body needs gm, radius and rotation above zero");
            var inc = Number(ask, "inc", 0);
            var heading = Tools.LaunchWindow.HeadingInto(body, Number(ask, "lat", 0), Math.Abs(inc), inc >= 0, Number(ask, "target_ap", 0));
            return new Dictionary<string, object> { { "error", "" }, { "heading", heading } };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:CORRECTIONBURN</c>, for <c>transfer_to</c> and <c>return</c> (#449): the Course Correction, the smallest
        /// burn that brings the craft's trajectory to <c>target</c> (a body's name) at <c>target_pe</c> (m) and, if given, at
        /// <c>target_inc</c> (degrees), by <see cref="Tools.CorrectionBurn"/>. <c>earliest_ut</c> (UT; now if missing) and
        /// <c>latest_ut</c> (UT; earliest_ut if missing) are the burn's window: the same UT burns then, a wider window lets the
        /// solver choose the time. <c>pe_tolerance</c> (m, default 1000) and <c>inc_tolerance</c> (degrees, default 0.5) are how
        /// close the answer must be. <c>aim_ut</c> (UT, optional, #619): when the path without a burn misses the target, the solve
        /// starts from the burn whose Lambert arc reaches its centre then. Gives <c>error</c> "", <c>ut</c>, <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver
        /// node components), <c>dv</c>, <c>pe_predicted</c> (m) and <c>inc_predicted</c> (degrees); a best burn outside the
        /// tolerances gives its <c>error</c> with the rest; a solve that could not start, only <c>error</c>, "found no correction: &lt;why&gt;".
        /// Given <c>lat</c> and <c>lng</c> (#793), in place of target_inc, the burn also moves the landing toward them with the periapsis held, by the reentry
        /// predictor with <paramref name="body"/>, <paramref name="air"/> (the target's) and <paramref name="craft"/>; <c>ground_alt</c> (m) and <c>isp</c> (s) are as for
        /// <see cref="ReturnBurn"/>, and <c>dv_left</c> (m/s; negative or missing for unknown) limits what it adds for the landing. A miss across the track over 5 km is taken out too
        /// when the delta-v left less the reserve covers the burn (#790), else accepted: <c>cross_fix</c> "flown", "accepted" or "", <c>cross_dv</c> (the fix's m/s) and <c>aim_note</c> (the same in words). It adds <c>lat</c>, <c>lng</c>, <c>landing_ut</c> and
        /// <c>error_km</c>, the remainder it couldn't reach, when the predictor can say.
        /// <para>
        /// To the Target Vessel (#454), the two goals the <c>rendezvous</c> block re-solves at fly time: <c>target_distance</c> (m)
        /// in place of target_pe is the Course Correction to that closest approach (<c>distance_tolerance</c>, m, default 10), and
        /// <c>match_velocities</c> true is solve_burn's match_velocities at <c>at</c> (a word or a UT; default closest_approach).
        /// <c>target</c>, if given, is the vessel's name. Either gives <c>error</c>, <c>ut</c>, the components, <c>dv</c>,
        /// <c>distance_predicted</c> (m) and <c>relative_speed_predicted</c> (m/s), at the approach (with <c>approach_ut</c>) for
        /// target_distance and at the burn for match_velocities.
        /// </para>
        /// </summary>
        public static IDictionary<string, object> CorrectionBurn(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body = null,
            IReentryAir air = null, IReentryCraft craft = null)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return Error(Tools.CorrectionBurn.Fail("no Game Snapshot").Error);
            if (ask.ContainsKey("match_velocities")) return MatchVelocities(ask, snapshot);
            if (ask.ContainsKey("target_distance")) return ApproachCorrection(ask, snapshot);
            object target;
            ask.TryGetValue("target", out target);
            if (!(target is string)) return Error(Tools.CorrectionBurn.Fail("target must be a body's name").Error);
            var pe = NumberOrNull(ask, "target_pe");
            if (!pe.HasValue) return Error(Tools.CorrectionBurn.Fail("target_pe is required").Error);
            if (ask.ContainsKey("target_inc") && !(ask["target_inc"] is double))
                return Error(Tools.CorrectionBurn.Fail("target_inc must be a number of degrees").Error);
            var earliest = Number(ask, "earliest_ut", snapshot.Ut);
            var correction = new CorrectionAsk
            {
                Target = (string)target,
                TargetPe = pe.Value,
                TargetInc = NumberOrNull(ask, "target_inc"),
                EarliestUt = earliest,
                LatestUt = Number(ask, "latest_ut", earliest),
                PeTolerance = Number(ask, "pe_tolerance", 1000),
                IncTolerance = Number(ask, "inc_tolerance", 0.5),
                AimUt = NumberOrNull(ask, "aim_ut"),
                DvLeft = Number(ask, "dv_left", -1),
            };
            if (ask.ContainsKey("lat") || ask.ContainsKey("lng"))
            {
                var problem = CorrectionLandingOf(ask, snapshot, correction, body, air, craft);
                if (problem != null) return Error(Tools.CorrectionBurn.Fail(problem).Error);
            }
            var plan = Tools.CorrectionBurn.Find(snapshot, correction);
            if (!plan.HasBurn) return Error(plan.Error);
            var result = new Dictionary<string, object>
            {
                { "error", plan.Error ?? "" },
                { "ut", plan.Ut },
                { "prograde", plan.Prograde },
                { "normal", plan.Normal },
                { "radial", plan.Radial },
                { "dv", plan.DeltaV },
                { "pe_predicted", plan.PePredicted },
                { "inc_predicted", plan.IncPredicted },
                { "plan_log", BurnPlanLog.Arrival("CORRECTIONBURN", plan.Ut, plan.PePredicted, plan.IncPredicted, ask.ContainsKey("lat") && ask.ContainsKey("lng"), Number(ask, "lat", 0), Number(ask, "lng", 0)) },
            };
            if (plan.Landing != null)
                foreach (var key in Landing(plan.Landing, plan.ErrorKm)) result[key.Key] = key.Value;
            result["cross_fix"] = plan.CrossFix;
            result["cross_dv"] = plan.CrossDv;
            result["aim_note"] = plan.AimNote;
            return result;
        }

        /// <summary>Fills in the landing <c>CORRECTIONBURN</c> was asked for: why it can't be read, or null. An unknown target is the solve's to say.</summary>
        private static string CorrectionLandingOf(IDictionary<string, object> ask, GameSnapshot snapshot, CorrectionAsk into, IPlanningBody body, IReentryAir air, IReentryCraft craft)
        {
            var lat = NumberOrNull(ask, "lat");
            var lng = NumberOrNull(ask, "lng");
            if (!lat.HasValue || !lng.HasValue) return "lat and lng must both be numbers";
            var planet = new BodySystem(snapshot.Bodies).Find(into.Target);
            if (planet == null) return null;
            DropBurn payer;
            var craftProblem = EntryCraftOf(ask, out payer);
            if (craftProblem != null) return craftProblem;
            if (body == null || air == null || craft == null) return "the landing needs the planet's air and the craft";
            string problem;
            var frame = BodyFrame.Read(planet, body, snapshot.Ut, out problem);
            if (problem != null) return problem;
            into.Landing = new ReturnLanding
            {
                Latitude = lat.Value, Longitude = lng.Value, GroundAltitude = Number(ask, "ground_alt", 0), Isp = Number(ask, "isp", 0), Payer = payer, Air = air, Craft = craft, Frame = frame,
            };
            return null;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:TARGETVESSEL</c>, for <c>rendezvous</c> (#455): the Mission's Target Vessel (ADR 0013) as the Game Snapshot
        /// reads it live. Gives <c>error</c> "", <c>name</c> and <c>body</c> (the body it orbits); or only <c>error</c>: there is none,
        /// or it is landed, or its orbit is missing. A landed or splashed one with its position read keeps that error, so rendezvous
        /// and dock still refuse it, and adds <c>name</c>, <c>body</c> (the body it is on) and <c>lat</c>, <c>lng</c>, <c>alt</c>
        /// for <c>land</c>'s <c>"vessel"</c> target (#662).
        /// </summary>
        public static IDictionary<string, object> TargetVessel(GameSnapshot snapshot)
        {
            if (snapshot == null) return Error("no Game Snapshot");
            string why;
            var vessel = VesselTarget.Read(snapshot, new BodySystem(snapshot.Bodies), null, out why);
            var entry = snapshot.Target;
            if (vessel == null && entry != null && entry.IsDown && entry.Position != null)
                return new Dictionary<string, object>
                {
                    { "error", why }, { "name", entry.Name }, { "body", entry.Body },
                    { "lat", entry.Position.Latitude }, { "lng", entry.Position.Longitude }, { "alt", entry.Position.Altitude },
                };
            if (vessel == null) return Error(why);
            return new Dictionary<string, object> { { "error", "" }, { "name", vessel.Name }, { "body", vessel.Body.Name } };
        }

        /// <summary>CORRECTIONBURN's Course Correction to the Target Vessel's closest approach (<c>target_distance</c>).</summary>
        private static IDictionary<string, object> ApproachCorrection(Dictionary<string, object> ask, GameSnapshot snapshot)
        {
            foreach (var entry in ask)
            {
                var key = entry.Key.ToLowerInvariant();
                if (key == "target")
                {
                    if (!(entry.Value is string)) return Error(Tools.CorrectionBurn.Fail("target must be the Target Vessel's name").Error);
                }
                else if (key == "target_distance" || key == "earliest_ut" || key == "latest_ut" || key == "distance_tolerance")
                {
                    if (!(entry.Value is double)) return Error(Tools.CorrectionBurn.Fail(key + " must be a number").Error);
                }
                else
                    return Error(Tools.CorrectionBurn.Fail("unknown input " + entry.Key + ": a correction to target_distance takes target, earliest_ut, latest_ut and distance_tolerance").Error);
            }
            var earliest = Number(ask, "earliest_ut", snapshot.Ut);
            object target;
            ask.TryGetValue("target", out target);
            var plan = Tools.CorrectionBurn.Find(snapshot, new CorrectionAsk
            {
                Target = target as string,
                TargetDistance = (double)ask["target_distance"],
                EarliestUt = earliest,
                LatestUt = Number(ask, "latest_ut", earliest),
                DistanceTolerance = Number(ask, "distance_tolerance", 10),
            });
            if (!plan.HasBurn) return Error(plan.Error);
            return new Dictionary<string, object>
            {
                { "error", plan.Error ?? "" },
                { "ut", plan.Ut },
                { "prograde", plan.Prograde },
                { "normal", plan.Normal },
                { "radial", plan.Radial },
                { "dv", plan.DeltaV },
                { "distance_predicted", plan.DistancePredicted },
                { "relative_speed_predicted", plan.RelativeSpeedPredicted },
                { "approach_ut", plan.ApproachUt },
            };
        }

        /// <summary>CORRECTIONBURN's match_velocities: solve_burn's goal at <c>at</c> (default closest_approach) on the craft's orbit as it is now.</summary>
        private static IDictionary<string, object> MatchVelocities(Dictionary<string, object> ask, GameSnapshot snapshot)
        {
            var json = new JsonWriter().BeginObject().Field("goal", "match_velocities");
            foreach (var entry in ask)
            {
                var key = entry.Key.ToLowerInvariant();
                var text = entry.Value as string;
                switch (key)
                {
                    case "match_velocities":
                        if (!(entry.Value is bool) || !(bool)entry.Value) return Error(Tools.CorrectionBurn.Fail("match_velocities must be true").Error);
                        break;
                    case "target":
                        if (text == null) return Error(Tools.CorrectionBurn.Fail("target must be the Target Vessel's name").Error);
                        json.Field(key, text);
                        break;
                    case "at":
                        if (text != null) json.Field(key, text);
                        else if (entry.Value is double && !double.IsNaN((double)entry.Value) && !double.IsInfinity((double)entry.Value)) json.Field(key, (double)entry.Value);
                        else return Error(Tools.CorrectionBurn.Fail("at must be a word or a finite UT, not " + Convert.ToString(entry.Value, CultureInfo.InvariantCulture)).Error);
                        break;
                    default:
                        return Error(Tools.CorrectionBurn.Fail("unknown input " + entry.Key + ": match_velocities takes at and target").Error);
                }
            }
            var solved = new SolvedBurn();
            var outcome = new SolveBurn().Solve(JsonReader.Parse(json.EndObject().ToString()), snapshot, solved);
            if (outcome.IsError) return Error(Tools.CorrectionBurn.Fail(outcome.Summary).Error);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "ut", solved.Ut },
                { "prograde", solved.Prograde },
                { "normal", solved.Normal },
                { "radial", solved.Radial },
                { "dv", solved.DeltaV },
                { "distance_predicted", solved.Distance ?? -1 },
                { "relative_speed_predicted", solved.RelativeSpeed ?? -1 },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:RETURNBURN</c>, for <c>return</c> (#450): the ejection from the moon the craft orbits to the planet that
        /// moon orbits, the smallest burn arriving there at <c>target_pe</c> (m) and, if given, at <c>target_inc</c> (degrees), by
        /// <see cref="Tools.ReturnBurn"/>. <c>earliest_ut</c> (UT; now if missing or past) and <c>latest_ut</c> (UT; one orbit of the craft
        /// later if missing) are the burn's window. <c>pe_tolerance</c> (m, default 1000) and <c>inc_tolerance</c> (degrees,
        /// default 0.5) are how close the answer must be. Gives <c>error</c> "", <c>planet</c>, <c>ut</c>, <c>prograde</c>,
        /// <c>normal</c>, <c>radial</c> (m/s, maneuver node components), <c>dv</c>, <c>pe_predicted</c> (m), <c>inc_predicted</c>
        /// (degrees), <c>soi_ut</c> (UT the craft leaves the moon), <c>from_grid</c> (the analytic seed was undefined) and
        /// <c>finished_by</c> (what finished a solve the SQP stopped short, "" for none); a best
        /// burn outside the tolerances gives its <c>error</c> with the rest; a solve that could not start, only <c>error</c>,
        /// "found no return: &lt;why&gt;". Given <c>lat</c> and <c>lng</c> (#792), <see cref="ReturnAim"/> picks the window within
        /// <c>max_wait</c> (s; "auto" or missing is one turn of the landing's longitude) and <c>max_duration</c> (s from <c>start_ut</c>, when a number) and times the
        /// burn to come down there, by the reentry predictor with <paramref name="body"/>, <paramref name="air"/> (the planet's) and
        /// <paramref name="craft"/>; <c>ground_alt</c> (m) and <c>isp</c> (s; 0 or missing leaves the mass as it is) are as for
        /// <see cref="DeorbitBurn"/>. It adds <c>lat</c>, <c>lng</c>, <c>landing_ut</c> and <c>error_km</c> when the predictor can say,
        /// else the window's least burn stands with <c>error_km</c> -1. <c>aim_dv</c> (m/s over the window's least burn) and
        /// <c>spread_km</c> (what <see cref="ReturnAim.ResidualMs"/> of error in the burn moves the landing; -1 unknown) are always there.
        /// </summary>
        public static IDictionary<string, object> ReturnBurn(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body = null,
            IReentryAir air = null, IReentryCraft craft = null)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return Error(Tools.ReturnBurn.Fail("no Game Snapshot").Error);
            var pe = NumberOrNull(ask, "target_pe");
            if (!pe.HasValue) return Error(Tools.ReturnBurn.Fail("target_pe is required").Error);
            foreach (var key in new[] { "target_inc", "earliest_ut", "latest_ut", "pe_tolerance", "inc_tolerance" })
                if (ask.ContainsKey(key) && !(ask[key] is double)) return Error(Tools.ReturnBurn.Fail(key + " must be a number").Error);
            var returnAsk = new ReturnAsk
            {
                TargetPe = pe.Value,
                TargetInc = NumberOrNull(ask, "target_inc"),
                EarliestUt = Number(ask, "earliest_ut", snapshot.Ut),
                LatestUt = NumberOrNull(ask, "latest_ut"),
                PeTolerance = Number(ask, "pe_tolerance", 1000),
                IncTolerance = Number(ask, "inc_tolerance", 0.5),
            };
            if (ask.ContainsKey("lat") || ask.ContainsKey("lng"))
            {
                var problem = ReturnLandingOf(ask, snapshot, returnAsk, body, air, craft);
                if (problem != null) return Error(Tools.ReturnBurn.Fail(problem).Error);
            }
            var plan = Tools.ReturnBurn.Find(snapshot, returnAsk);
            if (!plan.HasBurn) return Error(plan.Error);
            var result = new Dictionary<string, object>
            {
                { "error", plan.Error ?? "" },
                { "planet", plan.Planet },
                { "ut", plan.Ut },
                { "prograde", plan.Prograde },
                { "normal", plan.Normal },
                { "radial", plan.Radial },
                { "dv", plan.DeltaV },
                { "pe_predicted", plan.PePredicted },
                { "inc_predicted", plan.IncPredicted },
                { "soi_ut", plan.SoiUt },
                { "from_grid", plan.FromGrid },
                { "finished_by", plan.FinishedBy ?? "" },
                { "aim_dv", plan.AimDv },
                { "spread_km", plan.SpreadKm },
                { "error_km", plan.ErrorKm },
                { "plan_log", BurnPlanLog.Arrival("RETURNBURN", plan.Ut, plan.PePredicted, plan.IncPredicted, ask.ContainsKey("lat") && ask.ContainsKey("lng"), Number(ask, "lat", 0), Number(ask, "lng", 0)) },
            };
            if (plan.Landing != null)
                foreach (var key in Landing(plan.Landing, plan.ErrorKm)) result[key.Key] = key.Value;
            return result;
        }

        /// <summary>Fills in the landing <c>RETURNBURN</c> was asked for: why it can't be read, or null. Round no moon, the solve says why.</summary>
        private static string ReturnLandingOf(IDictionary<string, object> ask, GameSnapshot snapshot, ReturnAsk into, IPlanningBody body, IReentryAir air, IReentryCraft craft)
        {
            var lat = NumberOrNull(ask, "lat");
            var lng = NumberOrNull(ask, "lng");
            if (!lat.HasValue || !lng.HasValue) return "lat and lng must both be numbers";
            object wait;
            ask.TryGetValue("max_wait", out wait);
            if (wait is double && (double)wait > 0) into.MaxWait = (double)wait;
            else if (wait != null && !string.Equals(wait as string, "auto", StringComparison.OrdinalIgnoreCase))
                return "max_wait must be \"auto\" or seconds above 0, not " + Convert.ToString(wait, CultureInfo.InvariantCulture);
            object limit;
            if (ask.TryGetValue("max_duration", out limit) && limit is double)
                into.LatestUt = Number(ask, "start_ut", snapshot.Ut) + (double)limit - ReturnAim.AfterBurnS;
            DropBurn payer;
            var craftProblem = EntryCraftOf(ask, out payer);
            if (craftProblem != null) return craftProblem;
            if (body == null || air == null || craft == null) return "the landing needs the planet's air and the craft";
            string problem;
            into.Landing = ReturnAim.LandingOn(snapshot, lat.Value, lng.Value, Number(ask, "ground_alt", 0), Number(ask, "isp", 0), body, air, craft, out problem);
            if (into.Landing != null) into.Landing.Payer = payer;
            return problem;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:RETURNMISS</c>, for <c>return</c> (#759, ADR 0014): whether the path flown is the return asked, from
        /// <c>target_pe</c> and <c>pe</c> (m), and <c>target_inc</c> and <c>inc</c> (degrees; <c>inc</c> counts only with a
        /// <c>target_inc</c>), within <c>pe_tolerance</c> (m, default 1000) and <c>inc_tolerance</c> (degrees, default 0.5). Gives
        /// <c>error</c>: "" when it is, else why, with the figures. Pure.
        /// </summary>
        public static IDictionary<string, object> ReturnMiss(IDictionary<string, object> input)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var targetPe = NumberOrNull(ask, "target_pe");
            var pe = NumberOrNull(ask, "pe");
            if (!targetPe.HasValue || !pe.HasValue) return Error("the return's check: target_pe and pe must be numbers");
            return Error(Tools.ReturnBurn.Miss(pe.Value, Number(ask, "inc", 0), targetPe.Value, NumberOrNull(ask, "target_inc"),
                Number(ask, "pe_tolerance", 1000), Number(ask, "inc_tolerance", 0.5)) ?? "");
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ORBITBURN</c>, for <c>change_orbit</c> (#444): <c>solve_burn</c>'s solve (<see cref="SolveBurn"/>) on the
        /// craft's orbit as it is at the call, so the node a block flies is solved when it starts, not when Claudia planned. Takes
        /// <c>solve_burn</c>'s inputs: <c>goal</c>, and as that goal needs them <c>value</c>, <c>at</c> (a word or a UT),
        /// <c>target</c> (a body's name), <c>pe</c>, <c>ap</c>, <c>of</c> and <c>direction</c>. Gives <c>error</c> "", <c>ut</c>,
        /// <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver node components), <c>dv</c>, <c>burn_time</c> (s; -1 when
        /// the craft's stages don't give it) and <c>wanted</c>, a lexicon of each element the goal sets and the figure it aims at
        /// (<see cref="SolvedBurn.Wanted"/>); or only <c>error</c>, solve_burn's reason. It refuses <c>intercept_at</c>, whose aim is
        /// an arrival, not an element. Block-internal (#691), for change_orbit's re-solve after the turn: <c>resolve_ut</c> (the
        /// node's UT) and <c>resolve_max_dv</c> (a number, or anything else for none). With <c>resolve_ut</c>, a burn more than 5 s
        /// from it, or <c>resolve_max_dv</c> and a burn over it, gives only an <c>error</c> naming that: the first node is kept.
        /// </summary>
        public static IDictionary<string, object> OrbitBurn(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return Error("no Game Snapshot");
            // change_orbit judges the elements a burn sets (wanted); an intercept aims at an arrival, which no element holds. Checked
            // before the inputs, so an intercept's own fields (arrive_ut, tof) get this reason, not "unknown input".
            object goal;
            if (ask.TryGetValue("goal", out goal) && goal as string == "intercept_at")
                return Error("ORBITBURN does not solve intercept_at: change_orbit checks the orbit elements a burn sets, and an intercept "
                    + "aims at an arrival, not an element; exec_node the node solve_burn gives");
            if (goal as string == "match_velocities" || goal as string == "course_correction")
                return Error("ORBITBURN does not solve " + goal + ": it aims at the Target Vessel, not an orbit element; CORRECTIONBURN solves it at fly time");
            var json = new JsonWriter().BeginObject();
            double? resolveUt = null, resolveMaxDv = null;
            foreach (var entry in ask)
            {
                var key = entry.Key.ToLowerInvariant();
                var text = entry.Value as string;
                if (key == "resolve_ut" || key == "resolve_max_dv")
                {
                    if (entry.Value is double && !double.IsNaN((double)entry.Value))
                    {
                        if (key == "resolve_ut") resolveUt = (double)entry.Value;
                        else resolveMaxDv = (double)entry.Value;
                    }
                    continue;
                }
                switch (key)
                {
                    case "goal":
                    case "target":
                    case "direction":
                        if (text == null) return Error(key + " must be a string, not " + Convert.ToString(entry.Value, CultureInfo.InvariantCulture));
                        json.Field(key, text);
                        break;
                    case "value":
                    case "pe":
                    case "ap":
                    case "of":
                    case "at":
                        if (key == "at" && text != null)
                        {
                            json.Field(key, text);
                            break;
                        }
                        if (!(entry.Value is double) || double.IsNaN((double)entry.Value) || double.IsInfinity((double)entry.Value))
                            return Error(key + " must be a finite number" + (key == "at" ? " (a UT) or a word" : "") + ", not " + Convert.ToString(entry.Value, CultureInfo.InvariantCulture));
                        json.Field(key, (double)entry.Value);
                        break;
                    default:
                        return Error("unknown input " + entry.Key + ": ORBITBURN takes solve_burn's goal, value, at, target, pe, ap, of and direction");
                }
            }
            var solved = new SolvedBurn();
            var outcome = new SolveBurn().Solve(JsonReader.Parse(json.EndObject().ToString()), snapshot, solved);
            if (outcome.IsError) return Error(outcome.Summary);
            if (resolveUt.HasValue && Math.Abs(solved.Ut - resolveUt.Value) > 5)
                return Error("the burn moved to UT " + Math.Round(solved.Ut, 1).ToString(CultureInfo.InvariantCulture) + " from "
                    + Math.Round(resolveUt.Value, 1).ToString(CultureInfo.InvariantCulture));
            if (resolveUt.HasValue && resolveMaxDv.HasValue && solved.DeltaV > resolveMaxDv.Value)
                return Error("needs " + Math.Round(solved.DeltaV, 1).ToString(CultureInfo.InvariantCulture) + " m/s, over max_dv "
                    + resolveMaxDv.Value.ToString(CultureInfo.InvariantCulture));
            var wanted = new Dictionary<string, object>();
            foreach (var element in solved.Wanted) wanted[element.Key] = element.Value;
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "ut", solved.Ut },
                { "prograde", solved.Prograde },
                { "normal", solved.Normal },
                { "radial", solved.Radial },
                { "dv", solved.DeltaV },
                { "burn_time", solved.BurnTime ?? -1 },
                { "wanted", wanted },
            };
        }

        /// <summary>The <c>chutes</c> a reentry call asks for (#477): true when missing, else a boolean; a problem text otherwise.</summary>
        private static string ChutesOf(IDictionary<string, object> ask, out bool chutes)
        {
            chutes = true;
            object value;
            if (!ask.TryGetValue("chutes", out value) || value == null) return null;
            if (!(value is bool)) return "chutes must be true or false";
            chutes = (bool)value;
            return null;
        }

        private static double Number(IDictionary<string, object> ask, string key, double fallback)
        {
            object value;
            if (!ask.TryGetValue(key, out value) || !(value is double)) return fallback;
            return (double)value;
        }

        private static double? NumberOrNull(IDictionary<string, object> ask, string key)
        {
            object value;
            return ask.TryGetValue(key, out value) && value is double ? (double)value : (double?)null;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:CHUTEGROUP</c>, for <c>reentry</c> after any drop and whenever its group has gone (ADR 0019): what
        /// <see cref="ChuteGroups.Choose"/> gives the Entry in hand, read fresh from the craft, with the Last Entry as for the Entry Shield
        /// (shield number 1, ADR 0018; <paramref name="legs"/> null with no plan, <paramref name="step"/> 0 first, <paramref name="child"/> "" for the craft).
        /// Takes no input. Gives <c>error</c> "", <c>last</c>, <c>stage</c> (the group's; 0 for none, and meaningless when last), <c>uids</c> and <c>titles</c> (its stowed
        /// or armed chutes, by kOS part UID) and <c>touchdown_speed</c> (m/s they leave
        /// it at, by the predictor; -1 unknown). <paramref name="uidOf"/> gives a part's kOS UID from its <c>persistentId</c>, or null when it isn't aboard.
        /// With <paramref name="ask"/> (#827, <see cref="ChuteGroups.Widen"/>): <c>none</c> (the script's <c>chutes: "none"</c>) and <c>engine</c> (the Entry lands on
        /// its engines), the group is widened to later stages when it can't land the craft and no engine will, and <c>why</c> says so ("" if not), <c>stages</c> are
        /// its stages (what the block opens, not <c>stage</c>) and <c>warning</c> is the line for the Mission window and Debrief ("" if none).
        /// </summary>
        public static IDictionary<string, object> ChuteGroupCall(GameSnapshot snapshot, IList<PlannedSequence> legs, int step, string child, Func<uint, string> uidOf, IDictionary<string, object> ask = null)
        {
            if (snapshot == null) return Error("there is no Game Snapshot of the craft");
            // With no ask the group is today's, never widened.
            var widened = ChuteGroups.Widen(snapshot, Entries.ShieldNumber(snapshot, legs, step, child) == 1, ask != null && Equals(ask.ContainsKey("none") ? ask["none"] : false, true),
                ask == null || Equals(ask.ContainsKey("engine") ? ask["engine"] : false, true));
            var entry = widened.Entry;
            var chosen = entry.Chutes.Where(c => uidOf(c.PartId) != null).ToList();
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "last", entry.Last },
                { "stage", chosen.Count == 0 ? 0.0 : chosen.Max(c => c.Stage) },
                { "stages", chosen.Select(c => (object)(double)c.Stage).Distinct().ToList() },
                { "uids", chosen.Select(c => (object)uidOf(c.PartId)).ToList() },
                { "titles", chosen.Select(c => (object)c.Title).ToList() },
                { "touchdown_speed", entry.TouchdownSpeed },
                { "why", widened.Why() },
                { "warning", widened.Warning },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENTRYDROP</c>, for <c>reentry</c> at block start (#823, ADR 0018): what this Entry drops, read
        /// fresh from the Mission Plan's <paramref name="legs"/> (null with no plan: the first shield's rule) from <paramref name="step"/>
        /// (0 first) for the vessel flying (<paramref name="child"/>; "" for the craft). Takes <c>home</c> (the kOS UID of the crew's part).
        /// Gives <c>error</c> "", <c>number</c> (n), <c>shield</c> (the Entry Shield's kOS UID; "" for none),
        /// <c>decouplers</c> and <c>gone</c> (kOS UIDs), <c>names</c> (the stack's part names, each once) and <c>stuck</c> (those with no decoupler).
        /// </summary>
        public static IDictionary<string, object> EntryDropCall(IDictionary<string, object> ask, GameSnapshot snapshot, IList<PlannedSequence> legs, int step, string child, Func<uint, string> uidOf)
        {
            if (snapshot == null) return Error("there is no Game Snapshot of the craft");
            var number = Entries.ShieldNumber(snapshot, legs, step, child);
            var stack = EntryStack.Of(snapshot.Craft.Parts, Home(ask, snapshot, uidOf), number);
            Func<IEnumerable<SnapshotPart>, List<object>> uids = parts => parts.Select(p => uidOf(p.Id)).Where(u => u != null).Cast<object>().ToList();
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "number", (double)number },
                { "shield", stack.Shield == null ? "" : uidOf(stack.Shield.Id) ?? "" },
                { "decouplers", uids(stack.Decouplers) },
                { "gone", uids(stack.Gone) },
                { "names", EntryStack.Names(stack.Parts).Cast<object>().ToList() },
                { "stuck", EntryStack.Names(stack.Stuck).Cast<object>().ToList() },
            };
        }

        /// <summary>The part whose kOS UID is <c>home</c> in <paramref name="ask"/>; null when none is.</summary>
        private static uint? Home(IDictionary<string, object> ask, GameSnapshot snapshot, Func<uint, string> uidOf)
        {
            var uid = ask.ContainsKey("home") ? ask["home"] as string : null;
            var home = snapshot.Craft.Parts.FirstOrDefault(p => uid != null && uidOf(p.Id) == uid);
            return home == null ? (uint?)null : home.Id;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENTRYFINISH</c> (#824), for <c>reentry</c> at block start: <see cref="EntryFinish.Decide"/> for the Entry in hand, its drop
        /// and Last Entry as <see cref="EntryDropCall"/> and <see cref="ChuteGroupCall"/> have them. Takes <c>home</c>, <c>chutes</c> (false: none will open) and
        /// <c>drop</c> (false: the block keeps its stack). Gives <c>error</c> "", <c>powered</c>, <c>speed</c> (m/s with that drop and those chutes; -1 unknown),
        /// <c>kept_speed</c> (m/s for the craft as kept; -1 on chutes), <c>engines</c> and <c>spare</c> (kOS UIDs) and <c>why</c> for the log.
        /// </summary>
        public static IDictionary<string, object> EntryFinishCall(IDictionary<string, object> ask, GameSnapshot snapshot, IList<PlannedSequence> legs, int step, string child, Func<uint, string> uidOf,
            string aimedKey = "", string aimed = "")
        {
            if (snapshot == null) return Error("there is no Game Snapshot of the craft");
            bool chutes;
            var unreadable = ChutesOf(ask, out chutes);
            if (unreadable != null) return Error(unreadable);
            var number = Entries.ShieldNumber(snapshot, legs, step, child);
            var drop = !ask.ContainsKey("drop") || Equals(ask["drop"], true);
            var stack = EntryStack.Of(snapshot.Craft.Parts, Home(ask, snapshot, uidOf), number);
            var finish = EntryFinish.Decide(snapshot, drop ? stack.Gone.Select(p => p.Id).ToList() : new List<uint>(), number == 1, chutes);
            // The craft it flies, against the one a return or deorbit aimed with (#814): a changed plan or fuel can make them differ.
            var flown = EntryCraft.From(snapshot, null, stack, finish, drop);
            var why = finish.Why() + (string.IsNullOrEmpty(aimedKey) || aimedKey == flown.Key ? "" : "; the landing was aimed for the craft " + aimed + ", it flies " + flown.Describe());
            Func<IEnumerable<SnapshotPart>, List<object>> uids = parts => parts.Select(p => uidOf(p.Id)).Where(u => u != null).Cast<object>().ToList();
            return new Dictionary<string, object>
            {
                { "error", "" }, { "powered", finish.Powered }, { "speed", finish.Speed }, { "kept_speed", finish.KeptSpeed },
                { "engines", uids(finish.Engines) }, { "spare", uids(finish.Spare) }, { "why", why },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENTRYCRAFT</c> (#814), for <c>return</c> and <c>deorbit</c> at block start: the craft the Entry ahead will fly
        /// (<see cref="EntryCraft"/>), read once and passed on as it is in each landing prediction's <c>craft</c>. The Entry Shield and
        /// Last Entry are as <see cref="EntryDropCall"/> has them; <c>reentry</c>'s <c>separate</c> is taken as "auto" and its chutes as opening.
        /// Takes <c>home</c> (the kOS UID of the crew's part), <c>leave_out</c> (kOS UIDs the block drops itself first) and <c>body</c> (where the
        /// Entry is, when not where the craft is: a return's planet). Gives <c>error</c> "", <c>leave_out</c> and <c>shield_drop</c> (kOS UIDs, as
        /// <see cref="LeftOut"/> and <see cref="ShieldDrop"/>), <c>whole</c>, <c>kept</c> and <c>gone_prop</c> (t) and <c>burners_kept</c> (who pays a
        /// burn, <see cref="DropGuard.MassAfterBurn"/>), <c>key</c> and <c>why</c> (the craft in words); <paramref name="aimed"/> receives the key and words.
        /// </summary>
        public static IDictionary<string, object> EntryCraftCall(IDictionary<string, object> ask, GameSnapshot snapshot, IList<PlannedSequence> legs, int step, string child, Func<uint, string> uidOf,
            Action<string, string> aimed = null)
        {
            if (snapshot == null) return Error("there is no Game Snapshot of the craft");
            var input = new Dictionary<string, object>(ask ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var at = input.ContainsKey("body") ? input["body"] as string : null;
            if (!string.IsNullOrEmpty(at) && !string.Equals(at, Entries.At(snapshot), StringComparison.OrdinalIgnoreCase))
            {
                // A return reads it at the moon: the Entry, its shield number and its chutes are the planet's.
                snapshot = snapshot.At(snapshot.Ut);
                snapshot.Body = at;
                snapshot.Orbit = new SnapshotOrbit { Body = at };
            }
            var dropped = new HashSet<string>(Uids(input, "leave_out"));
            var own = snapshot.Craft.Parts.Where(p => dropped.Contains(uidOf(p.Id) ?? "")).Select(p => p.Id).ToList();
            var craft = EntryCraft.Of(snapshot, own, Home(input, snapshot, uidOf), Entries.ShieldNumber(snapshot, legs, step, child), true, true);
            if (aimed != null) aimed(craft.Key, craft.Describe());
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "leave_out", craft.LeftOut.Select(p => uidOf(p.Id)).Where(u => u != null).Cast<object>().ToList() },
                { "shield_drop", craft.ShieldDropped == null || uidOf(craft.ShieldDropped.Id) == null ? new List<object>() : new List<object> { uidOf(craft.ShieldDropped.Id) } },
                { "whole", craft.WholeT }, { "kept", craft.KeptT }, { "gone_prop", craft.GonePropellantT }, { "burners_kept", (double)craft.BurnersKept },
                { "key", craft.Key }, { "why", craft.Describe() },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DROPCHECK</c> (#825, ADR 0014), for <c>claudia_drop_refusal</c>: <see cref="DropGuard.Check"/> on the Mission
        /// Plan (<paramref name="plan"/>, null with none) and the step in hand. Takes <c>before</c> and <c>left</c>, the craft's delta-v
        /// (m/s) before the drop and after it, and optionally a burn still to fly first (#835), as <see cref="DropBurn"/>: <c>spent</c> (m/s), <c>whole</c>, <c>kept</c>, <c>prop</c>, <c>gone_prop</c> (t), <c>isp</c> (s) and <c>burners_kept</c>. Gives <c>error</c> "", <c>refused</c>, <c>reason</c> (names what the drop takes, what is left
        /// and what the plan needs; "" when not refused), <c>taken</c> and <c>need</c>.
        /// </summary>
        public static IDictionary<string, object> DropCheck(IDictionary<string, object> input, GameSnapshot snapshot, IList<PlannedSequence> plan, int step)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "before", "left" })
                if (!NumberOrNull(ask, key).HasValue) return Error(key + " is required");
            var verdict = DropGuard.Check(snapshot, plan, step, NumberOrNull(ask, "before").Value, NumberOrNull(ask, "left").Value, new DropBurn
            {
                Spent = Number(ask, "spent", 0), Whole = Number(ask, "whole", 0), Kept = Number(ask, "kept", 0), KeptPropellant = Number(ask, "prop", 0),
                Isp = Number(ask, "isp", 0), GonePropellant = Number(ask, "gone_prop", 0), BurnersKept = Number(ask, "burners_kept", 0),
            });
            return new Dictionary<string, object>
            {
                { "error", "" }, { "refused", verdict.Refused }, { "reason", verdict.Reason }, { "taken", verdict.Taken }, { "need", verdict.Need },
            };
        }

        private static IDictionary<string, object> Error(string reason)
        {
            return new Dictionary<string, object> { { "error", reason } };
        }
    }
}
