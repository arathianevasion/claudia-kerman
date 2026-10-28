// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The block-internal calls (ADR 0006, ADR 0007): the mod's calculations a Building Block makes while it flies, through
    /// <c>ADDONS:CLAUDIA</c>: the site search, the reentry predictor with the deorbit burn search on it, the turn time, the
    /// ejection burn to a moon, the launch window, the Course Correction and solve_burn's orbit changes. They run the same C#
    /// as the Planning Tools, on a Game Snapshot read at the call (the reentry calls also read the air and the craft's drag cubes
    /// live, through <see cref="IReentryAir"/> and <see cref="IReentryCraft"/>), and only read. The turn time is the exception:
    /// it needs no Snapshot, only the craft's torque and moment of inertia, read live into a <see cref="TurnCraft"/>. The
    /// Script Check lets the Core Library call them and refuses them in a Mission Script. Inputs and results are plain values, as
    /// <c>KosValues</c> converts a kOS lexicon.
    /// </summary>
    public static class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDINGSITE</c>, for <c>land</c> with a biome target: <c>find_landing_site</c>'s search on the body the
        /// craft orbits (any <c>body</c> given is ignored), taking its other inputs (<c>biome</c>, <c>daylight</c>,
        /// <c>max_slope_deg</c>, <c>within_hours</c>, <c>near</c>). Gives the first site, the soonest pass or the nearest the
        /// <c>near</c> point: <c>error</c> "", <c>lat</c>, <c>lng</c>, <c>biome</c>, <c>terrain_altitude</c>, <c>slope_deg</c>,
        /// <c>pass_ut</c>, <c>sun_elevation_deg</c>, <c>near_edge</c> (true if nothing deeper in the biome was reachable). With no site, or a search that could not run, only <c>error</c>, saying why.
        /// </summary>
        public static IDictionary<string, object> LandingSite(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var orbited = snapshot == null ? "" : snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            ask["body"] = orbited ?? "";
            var outcome = PlanningTools.Default().Run("find_landing_site", ToolTestGate.InputJson(ask), snapshot, body);
            var result = JsonReader.Parse(outcome.Json);
            if (outcome.IsError) return Error(JsonNode.Str(result["error"]));

            var sites = result["sites"];
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
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DEORBITBURN</c>, for <c>deorbit</c>: the deorbit burn search (<see cref="DeorbitBurn"/>) from the craft's
        /// orbit, by the reentry predictor with <paramref name="air"/> and <paramref name="craft"/>. Takes <c>lat</c>, <c>lng</c>
        /// (the target), <c>earliest_ut</c>, <c>max_wait</c> (s), <c>ground_alt</c> (m), <c>accuracy_km</c> and <c>isp</c> (s; 0 or
        /// missing for none); <c>leave_out</c> (<see cref="LeftOut"/>) is for the adapter's <paramref name="craft"/>. Gives <c>error</c> "", <c>burn_ut</c>, <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver
        /// node components), <c>dv</c>, and the landing: <c>lat</c>, <c>lng</c>, <c>error_km</c>, <c>landing_ut</c>. A best burn
        /// outside the accuracy gives its <c>error</c> with the landing; a search that could not run, only <c>error</c>.
        /// </summary>
        public static IDictionary<string, object> DeorbitBurn(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body,
            IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
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
            }, air, craft, frame);
            if (plan.Landing == null || plan.Landing.Error != null) return Error(plan.Error ?? plan.Landing.Error);
            var result = Landing(plan.Landing, plan.ErrorKm);
            result["error"] = plan.Error ?? "";
            result["burn_ut"] = plan.BurnUt;
            result["prograde"] = plan.Prograde;
            result["normal"] = plan.Normal;
            result["radial"] = 0.0;
            result["dv"] = plan.DeltaV;
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:REENTRYIMPACT</c>, for <c>deorbit</c> once it has burned: where the craft's orbit as it is now comes
        /// down, by the reentry predictor. Takes <c>ground_alt</c> (m) and, for the distance, <c>lat</c> and <c>lng</c>. Given
        /// <c>orbit_sma</c>, <c>orbit_ecc</c>, <c>orbit_inc</c>, <c>orbit_lan</c>, <c>orbit_argpe</c>, <c>orbit_mna</c> (degrees, as kOS
        /// gives them), <c>orbit_epoch</c> (UT) and <c>orbit_body</c> (name), it predicts that orbit instead of the craft's: <c>return</c>
        /// asks it of its planned path round Kerbin before the craft is there, with <c>from_ut</c> (UT, default now) the UT that
        /// path begins, so the fall is not taken from an earlier pass of the same conic. Gives
        /// <c>error</c> "", <c>lat</c>, <c>lng</c>, <c>landing_ut</c> and <c>error_km</c> (-1 without a target); or only
        /// <c>error</c>, saying why there is no landing point.
        /// </summary>
        public static IDictionary<string, object> ReentryImpact(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body,
            IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            SnapshotBody at;
            Conic orbit;
            BodyFrame frame;
            var problem = Reentry(snapshot, body, ask, out at, out orbit, out frame);
            if (problem != null) return Error(problem);
            var landing = ReentryPredictor.Predict(at, orbit, Number(ask, "from_ut", snapshot.Ut), Number(ask, "ground_alt", 0), craft.MassKg, air, craft, frame);
            if (landing.Error != null) return Error(landing.Error);
            var km = ask.ContainsKey("lat") && ask.ContainsKey("lng")
                ? LandingSiteSearch.ArcDegrees(landing.Latitude, landing.Longitude, Number(ask, "lat", 0), Number(ask, "lng", 0)) * Math.PI / 180 * at.Radius / 1000
                : -1;
            var result = Landing(landing, km);
            result["error"] = "";
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:TURNTIME</c>, for a block that turns to a burn: how long to leave for the turn (<see cref="Tools.TurnTime"/>),
        /// from the torque and moment of inertia the adapter reads into <paramref name="craft"/>. Takes <c>max_stopping_time</c> (s,
        /// kOS's <c>steeringmanager:maxstoppingtime</c>; 2 if missing). Gives <c>error</c> "", <c>turn_time</c> (s, at least 30),
        /// <c>turn_s</c> (the 190° turn alone), <c>top_rate</c> (deg/s) and <c>alpha</c> (deg/s², the weaker axis's angular
        /// acceleration), on the wheels and RCS (if the craft has MonoPropellant, whatever the switch; where RCS adds torque the
        /// figures follow the RCS Use override, #422, and ignore <c>max_stopping_time</c>), and <c>wheels_turn_time</c> and <c>wheels_turn_s</c>, the same on the wheels alone (-1 when the
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
        /// optional, for the correction after an ejection that missed (#363). Gives <c>error</c> "", <c>ut</c>, <c>prograde</c>,
        /// <c>normal</c>, <c>radial</c> (m/s, maneuver node components), <c>dv</c>, <c>arrival_dv</c> (m/s: the circular capture at a
        /// body's periapsis, or matching an orbit's velocity), <c>encounter_ut</c>, <c>arrival_ut</c> (periapsis at a body; for an
        /// orbit both are the arrival), <c>target_pe_actual</c> (m; -1 for an orbit), <c>search_from</c> (UT) and <c>synodic</c>
        /// (s); or only <c>error</c>, "found no transfer: &lt;why&gt;".
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
                Capture = capture as bool? ?? true,
                TargetPe = Number(ask, "target_pe", 0),
                EarliestUt = Number(ask, "earliest_ut", snapshot.Ut),
                WindowUt = window as double?,
                LatestUt = NumberOrNull(ask, "latest_ut"),
                MaxDv = NumberOrNull(ask, "max_dv"),
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
            };
        }

        /// <summary>
        /// The parts a reentry call leaves out of the craft (#326): <c>leave_out</c>, the kOS UIDs of the parts <c>deorbit</c> drops
        /// after its burn, so the predictor flies only what stays. One UID may be given bare. The adapter builds the craft from it.
        /// </summary>
        public static IList<string> LeftOut(IDictionary<string, object> input)
        {
            return Uids(input, "leave_out");
        }

        /// <summary>
        /// The parts the heat shield's drop takes (#462): <c>shield_drop</c>, kOS UIDs, as <see cref="LeftOut"/>. The adapter builds
        /// the craft after the drop from it; none (the default) keeps the heat shield to the ground in the prediction.
        /// </summary>
        public static IList<string> ShieldDrop(IDictionary<string, object> input)
        {
            return Uids(input, "shield_drop");
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
        /// a southbound window) and <c>heading</c> (degrees); or only <c>error</c>, saying why the pad never passes under the plane.
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
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:CORRECTIONBURN</c>, for <c>transfer_to</c> and <c>return</c> (#449): the Course Correction, the smallest
        /// burn that brings the craft's trajectory to <c>target</c> (a body's name) at <c>target_pe</c> (m) and, if given, at
        /// <c>target_inc</c> (degrees), by <see cref="Tools.CorrectionBurn"/>. <c>earliest_ut</c> (UT; now if missing) and
        /// <c>latest_ut</c> (UT; earliest_ut if missing) are the burn's window: the same UT burns then, a wider window lets the
        /// solver choose the time. <c>pe_tolerance</c> (m, default 1000) and <c>inc_tolerance</c> (degrees, default 0.5) are how
        /// close the answer must be. Gives <c>error</c> "", <c>ut</c>, <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver
        /// node components), <c>dv</c>, <c>pe_predicted</c> (m) and <c>inc_predicted</c> (degrees); a best burn outside the
        /// tolerances gives its <c>error</c> with the rest; a solve that could not start, only <c>error</c>, "found no correction: &lt;why&gt;".
        /// </summary>
        public static IDictionary<string, object> CorrectionBurn(IDictionary<string, object> input, GameSnapshot snapshot)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return Error(Tools.CorrectionBurn.Fail("no Game Snapshot").Error);
            object target;
            ask.TryGetValue("target", out target);
            if (!(target is string)) return Error(Tools.CorrectionBurn.Fail("target must be a body's name").Error);
            var pe = NumberOrNull(ask, "target_pe");
            if (!pe.HasValue) return Error(Tools.CorrectionBurn.Fail("target_pe is required").Error);
            if (ask.ContainsKey("target_inc") && !(ask["target_inc"] is double))
                return Error(Tools.CorrectionBurn.Fail("target_inc must be a number of degrees").Error);
            var earliest = Number(ask, "earliest_ut", snapshot.Ut);
            var plan = Tools.CorrectionBurn.Find(snapshot, new CorrectionAsk
            {
                Target = (string)target,
                TargetPe = pe.Value,
                TargetInc = NumberOrNull(ask, "target_inc"),
                EarliestUt = earliest,
                LatestUt = Number(ask, "latest_ut", earliest),
                PeTolerance = Number(ask, "pe_tolerance", 1000),
                IncTolerance = Number(ask, "inc_tolerance", 0.5),
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
                { "pe_predicted", plan.PePredicted },
                { "inc_predicted", plan.IncPredicted },
            };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ORBITBURN</c>, for <c>change_orbit</c> (#444): <c>solve_burn</c>'s solve (<see cref="SolveBurn"/>) on the
        /// craft's orbit as it is at the call, so the node a block flies is solved when it starts, not when Claudia planned. Takes
        /// <c>solve_burn</c>'s inputs: <c>goal</c>, and as that goal needs them <c>value</c>, <c>at</c> (a word or a UT),
        /// <c>target</c> (a body's name), <c>pe</c>, <c>ap</c>, <c>of</c> and <c>direction</c>. Gives <c>error</c> "", <c>ut</c>,
        /// <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver node components), <c>dv</c>, <c>burn_time</c> (s; -1 when
        /// the craft's stages don't give it) and <c>wanted</c>, a lexicon of each element the goal sets and the figure it aims at
        /// (<see cref="SolvedBurn.Wanted"/>); or only <c>error</c>, solve_burn's reason. It refuses <c>intercept_at</c>, whose aim is
        /// an arrival, not an element.
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
            var json = new JsonWriter().BeginObject();
            foreach (var entry in ask)
            {
                var key = entry.Key.ToLowerInvariant();
                var text = entry.Value as string;
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

        private static IDictionary<string, object> Error(string reason)
        {
            return new Dictionary<string, object> { { "error", reason } };
        }
    }
}
