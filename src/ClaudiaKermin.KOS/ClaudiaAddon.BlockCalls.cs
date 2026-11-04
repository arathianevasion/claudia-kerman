// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Adapter;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The block-internal calls (ADR 0006, ADR 0007): the mod's calculations a Building Block makes while it flies, on the same C#
    /// as the Planning Tools (<see cref="BlockCalls"/>). The Script Check lets only the Core Library call them; a Mission Script
    /// passes a block its goal instead. Each reads a fresh Game Snapshot of the processor's vessel (TURNTIME reads only its torque
    /// and moment of inertia) and never changes the game.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddBlockCallSuffixes()
        {
            AddSuffix("ORBITBURN", new OneArgsSuffix<Lexicon, Lexicon>(OrbitBurn,
                "Core Library only (change_orbit): solve_burn's burn to goal (with value, at, target, pe, ap, of, direction as it needs) on the orbit as it is now; lexicon error, ut, prograde, normal, radial, dv, burn_time, wanted (element: figure)"));
            AddSuffix("CORRECTIONBURN", new OneArgsSuffix<Lexicon, Lexicon>(CorrectionBurn,
                "Core Library only (transfer_to, return, tune_pe): the least burn from earliest_ut to latest_ut that arrives at target (a body's name) at target_pe and, if given, target_inc; lexicon error, ut, prograde, normal, radial, dv, pe_predicted, inc_predicted"));
            AddSuffix("LAUNCHWINDOW", new OneArgsSuffix<Lexicon, Lexicon>(LaunchWindow,
                "Core Library only (launch): when the pad next passes under the plane of inc and lan (degrees); lexicon error, wait (s), ut, north, inc (negative southbound), heading"));
            AddSuffix("LANDINGSITE", new OneArgsSuffix<Lexicon, Lexicon>(LandingSite,
                "Core Library only (land): find_landing_site's search on the body orbited; gives the first site as lexicon error, lat, lng, biome, ..."));
            AddSuffix("LANDINGPASS", new OneArgsSuffix<Lexicon, Lexicon>(LandingPass,
                "Core Library only (land): the first pass over lat, lng (r_pe, earliest_ut, daylight, max_correction_dv, within_hours) land may plan for; lexicon error, burn_ut, arrive_ut, correction_dv, next_pass_ut, passes, ..."));
            AddSuffix("BRAKEPATH", new OneArgsSuffix<Lexicon, Lexicon>(BrakePath,
                "Core Library only (land): braking simulated from now onto lat, lng s m ahead on heading (alt, v_h, v_up, the craft's thrust, mass, isp, ...) on shape (a Descent Shape or auto); lexicon error, clears, descent_shape, shape_lo, shape_hi, brake_dv, predicted_clearance, path"));
            AddSuffix("DEORBITBURN", new OneArgsSuffix<Lexicon, Lexicon>(DeorbitBurn,
                "Core Library only (deorbit): the deorbit burn that lands on lat, lng by the reentry predictor; lexicon error, burn_ut, prograde, normal, radial, dv, lat, lng, error_km, ...; leave_out (part UIDs) flies the craft without them"));
            AddSuffix("REENTRYIMPACT", new OneArgsSuffix<Lexicon, Lexicon>(ReentryImpact,
                "Core Library only (deorbit, return): where the orbit as it is now, or the one given as orbit_sma, orbit_ecc, ..., comes down, held retrograde; lexicon error, lat, lng, landing_ut, error_km"));
            AddSuffix("TURNTIME", new OneArgsSuffix<Lexicon, Lexicon>(TurnTime,
                "Core Library only (a block that turns to a burn): the time to leave for a 190 degree turn on the wheels and RCS (if the craft has monoprop, on or off), given max_stopping_time; lexicon error, turn_time, turn_s, top_rate, and wheels_turn_time, wheels_turn_s on the wheels alone (-1: no wheel torque)"));
            AddSuffix("EJECTIONBURN", new OneArgsSuffix<Lexicon, Lexicon>(EjectionBurn,
                "Core Library only (transfer_to): the cheapest departure to target (a moon's name) arriving at target_pe, or to an orbit (target_sma, target_ecc, ...; mode rendezvous or intercept), capture true or false, within one synodic period from earliest_ut or window (to latest_ut, refusing over max_dv); lexicon error, ut, prograde, normal, radial, dv, arrival_dv, encounter_ut, arrival_ut, target_pe_actual, ..."));
            AddSuffix("RETURNBURN", new OneArgsSuffix<Lexicon, Lexicon>(ReturnBurn,
                "Core Library only (return): the least burn from earliest_ut to latest_ut (one orbit later if missing) that leaves the moon orbited for its planet at target_pe and, if given, target_inc; lexicon error, planet, ut, prograde, normal, radial, dv, pe_predicted, inc_predicted, soi_ut, from_grid"));
            AddSuffix("TARGETVESSEL", new NoArgsSuffix<Lexicon>(TargetVessel,
                "Core Library only (rendezvous, dock, land): the Mission's Target Vessel, read live; lexicon error, name, body, and lat, lng, alt for a landed or splashed one"));
            AddSuffix("DOCKHOLD", new OneArgsSuffix<Lexicon, Lexicon>(DockHoldStart,
                "Core Library only (dock): the mod starts its dock_hold on the Target Vessel's kOS processor, turning its port (uid) toward the craft's (toward, uid), or meets the target passive; lexicon error, state (starting or passive), reason"));
            AddSuffix("DOCKHOLDSTATE", new NoArgsSuffix<Lexicon>(DockHoldRead,
                "Core Library only (dock): the dock_hold dock started; lexicon state (none, starting, active, settled, passive, fault or docked), reason"));
            // dock_hold's own report (#711), from the mod's program on the target. Block-internal (ADR 0014 guardrail 5): the
            // Script Check refuses it in a Mission Script, and the bridge ignores it from any run but a live dock_hold.
            AddSuffix("DOCKHOLDSETTLED", new OneArgsSuffix<BooleanValue>(DockHoldSettled,
                "The mod's dock_hold only: its port settled on its aim (true) or a re-aim unsettled it (false)"));
            AddSuffix("DOCKGUIDE", new OneArgsSuffix<Lexicon, Lexicon>(DockGuide,
                "Core Library only (dock, each tick): DockGuidance's wanted velocity and coast band for the craft's port in the target port's frame; lexicon along, lat, dv_error, fire, cmd_along, cmd_lat, cmd_side"));
            AddSuffix("DOCKFUEL", new OneArgsSuffix<Lexicon, Lexicon>(DockFuel,
                "Core Library only (dock): whether the craft's RCS delta-v covers a dock from distance (m) at speed (m/s); lexicon error, dv, need"));
            AddSuffix("CHUTEALLOCATION", new NoArgsSuffix<Lexicon>(ChuteAllocationSet,
                "Core Library only (reentry): the Chute Allocation's chutes for this entry; lexicon error, all (true: keep every chute), entry, return, uids, titles, powered_finish, touchdown_speed"));
            AddSuffix("LANDGUARD", new OneArgsSuffix<Lexicon, Lexicon>(LandGuardTick,
                "Core Library only (land, each braking tick): the path guard on alt against path_alt, and near the site the straight-down fallback; lexicon verdict (fly, vertical or abort), error, why, over, and the fallback's al, cr, up, vv, ..."));
            AddSuffix("KEEPCLEAR", new OneArgsSuffix<Lexicon, Lexicon>(KeepClearCall,
                "Core Library only (land): the site lat, lng moved clear_m clear of keep_lat, keep_lng (straight away, else toward ahead_lat, ahead_lng) by up to max_divert on a body of radius; lexicon lat, lng, dist, moved"));
        }

        /// <summary><c>ADDONS:CLAUDIA:KEEPCLEAR</c> (#713, ADR 0014): land's keep-clear decision (<see cref="KeepClear.Call"/>), pure.</summary>
        private Lexicon KeepClearCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> moved = null;
            return (Lexicon)Guarded("KEEPCLEAR", () =>
            {
                moved = KeepClear.Call(KosValues.ToDictionary(input));
                return KosValues.FromPlain(moved);
            }, () => string.Format(CultureInfo.InvariantCulture, "{0:0} m from what it keeps clear of, moved {1:0} m", moved["dist"], moved["moved"]));
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDGUARD</c> (#712, ADR 0014): one braking tick of land's path guard and vertical fallback
        /// (<see cref="LandGuard.Tick"/>), pure. Called every physics tick, so its time is logged at most once a second (guardrail 6).
        /// </summary>
        private Lexicon LandGuardTick(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> tick = null;
            return (Lexicon)Guarded("LANDGUARD", () =>
            {
                tick = LandGuard.Tick(KosValues.ToDictionary(input));
                return KosValues.FromPlain(tick);
            }, () => string.Format(CultureInfo.InvariantCulture, "{0}, {1:0} m over the path{2}", tick["verdict"], tick["over"],
                (string)tick["why"] == "" ? "" : "; " + tick["why"]), 1);
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DOCKHOLD</c> (#456): the Target Vessel (the Mission's, or a block test's) gets the mod's dock_hold when it
        /// is loaded, has a kOS processor and electric charge, and passes the Child Vessel link test; otherwise dock meets it passive.
        /// Changes no vessel itself: the bridge starts the program like any run, under the runtime gates (ADR 0003).
        /// </summary>
        private Lexicon DockHoldStart(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> answer = null;
            return (Lexicon)Guarded("DOCKHOLD", () =>
            {
                answer = DockHoldCall(KosValues.ToDictionary(input));
                return KosValues.FromPlain(answer);
            }, () => answer["error"] as string != "" ? (string)answer["error"]
                : answer["state"] + ((string)answer["reason"] == "" ? "" : ": " + answer["reason"]));
        }

        private System.Collections.Generic.IDictionary<string, object> DockHoldCall(System.Collections.Generic.IDictionary<string, object> ask)
        {
            object port, toward;
            ask.TryGetValue("port", out port);
            ask.TryGetValue("toward", out toward);
            var bridge = KosBridge.Instance;
            var craft = shared.Vessel;
            if (!DockHold.IsUid(port as string) || !DockHold.IsUid(toward as string)) return DockHoldAnswer("port and toward must be the two ports' uids", "", "");
            if (bridge == null || craft == null || shared.KSPPart == null) return DockHoldAnswer("no kOS bridge in this scene", "", "");
            var id = MissionDriver.TargetVesselId ?? MissionDriver.BlockTestTargetId;
            Vessel target = null;
            if (!id.HasValue || !FlightGlobals.FindVessel(id.Value, out target) || target == null || target == craft)
                return DockHoldAnswer("the Mission has no Target Vessel in the game", "", "");
            var hasProcessor = target.loaded && target.parts.Any(p => p.FindModuleImplementing<kOS.Module.kOSProcessor>() != null);
            var charged = target.loaded && target.parts.Any(p => p.Resources.Contains("ElectricCharge") && p.Resources["ElectricCharge"].amount > 0.01);
            var linked = SnapshotReader.LinkedToClaudia(target, craft, MissionDriver.Remote);
            var passive = DockHold.PassiveReason(target.loaded, hasProcessor, charged, linked);
            var state = bridge.StartHold(shared.KSPPart.persistentId, target, passive, (string)port, (string)toward);
            string reason;
            bridge.HoldState(shared.KSPPart.persistentId, out reason);
            return DockHoldAnswer("", state, reason);
        }

        private static System.Collections.Generic.IDictionary<string, object> DockHoldAnswer(string error, string state, string reason)
        {
            return new System.Collections.Generic.Dictionary<string, object> { { "error", error }, { "state", state }, { "reason", reason } };
        }

        /// <summary><c>ADDONS:CLAUDIA:DOCKHOLDSTATE</c>, read by dock each tick: not a calculation, so not logged.</summary>
        private Lexicon DockHoldRead()
        {
            var bridge = KosBridge.Instance;
            var state = DockHoldState.None;
            var reason = "";
            if (bridge != null && shared.KSPPart != null) state = bridge.HoldState(shared.KSPPart.persistentId, out reason);
            var result = new Lexicon();
            result.Add(new StringValue("state"), new StringValue(state.ToString().ToLowerInvariant()));
            result.Add(new StringValue("reason"), new StringValue(reason ?? ""));
            return result;
        }

        /// <summary><c>ADDONS:CLAUDIA:DOCKHOLDSETTLED</c> (#711): dock_hold's settled report; the bridge ignores it from any run but a live hold.</summary>
        private void DockHoldSettled(BooleanValue settled)
        {
            var bridge = KosBridge.Instance;
            if (bridge != null && shared.KSPPart != null) bridge.HoldSettled(shared.KSPPart.persistentId, settled.Value);
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DOCKGUIDE</c> (#706, ADR 0014): one tick of dock's guidance (<see cref="DockGuidance.Guide"/>), pure. Called
        /// every physics tick, so its time is logged at most once a second (guardrail 6).
        /// </summary>
        private Lexicon DockGuide(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> tick = null;
            return (Lexicon)Guarded("DOCKGUIDE", () =>
            {
                tick = DockGuidance.Guide(KosValues.ToDictionary(input));
                return KosValues.FromPlain(tick);
            }, () => string.Format(CultureInfo.InvariantCulture, "want {0:0.###}/{1:0.###} m/s, error {2:0.###} m/s, {3}",
                tick["along"], tick["lat"], tick["dv_error"], (bool)tick["fire"] ? "translating" : "coasting"), 1);
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DOCKFUEL</c> (#706): dock's start-of-dock monopropellant check (<see cref="DockGuidance.Fuel"/>) on the
        /// craft's MonoPropellant, mass and its RCS thrusters' vacuum Isp (weighted by thrust), read live.
        /// </summary>
        private Lexicon DockFuel(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> fuel = null;
            double monoprop = 0, mass = 0;
            return (Lexicon)Guarded("DOCKFUEL", () =>
            {
                var vessel = shared.Vessel;
                var thrusts = new System.Collections.Generic.List<double>();
                var isps = new System.Collections.Generic.List<double>();
                foreach (var part in vessel.parts)
                {
                    if (part.Resources.Contains("MonoPropellant")) monoprop += part.Resources["MonoPropellant"].amount;
                    foreach (var rcs in part.FindModulesImplementing<ModuleRCS>())
                    {
                        if (!rcs.rcsEnabled) continue;
                        thrusts.Add(rcs.thrusterPower);
                        isps.Add(rcs.atmosphereCurve.Evaluate(0f));
                    }
                }
                mass = vessel.GetTotalMass();
                fuel = DockGuidance.FuelCall(KosValues.ToDictionary(input), monoprop, mass, thrusts, isps);
                return KosValues.FromPlain(fuel);
            }, () => string.Format(CultureInfo.InvariantCulture, "{0:0.0} units, Isp {1:0} s, {2:0.00} t: {3:0.0} m/s of RCS, needs {4:0.0}{5}",
                monoprop, fuel["isp"], mass, fuel["dv"], fuel["need"], fuel["error"] as string == "" ? "" : "; " + fuel["error"]));
        }

        private Lexicon ChuteAllocationSet()
        {
            System.Collections.Generic.IDictionary<string, object> set = null;
            // The set and why, so a reentry's chutes can be read back from KSP.log.
            return (Lexicon)Guarded("CHUTEALLOCATION", () =>
            {
                var vessel = shared.Vessel;
                var uids = new System.Collections.Generic.Dictionary<uint, string>();
                foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
                set = BlockCalls.ChuteAllocationCall(MissionDriver.ChuteAllocation, MissionDriver.CurrentSequence, SnapshotReader.Read(vessel),
                    id => uids.ContainsKey(id) ? uids[id] : null);
                return KosValues.FromPlain(set);
            }, () => set["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "{0}: {1}{2}{3}", (bool)set["all"] ? "every chute aboard" : "entry " + set["entry"],
                    string.Join(", ", ((System.Collections.Generic.List<object>)set["titles"]).ConvertAll(t => (string)t).ToArray()),
                    (bool)set["powered_finish"] ? ", powered finish" : "",
                    (double)set["touchdown_speed"] >= 0 ? string.Format(CultureInfo.InvariantCulture, ", {0:0.0} m/s", set["touchdown_speed"]) : "")
                : (string)set["error"]);
        }

        private Lexicon ReturnBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> node = null;
            // The solve's time against ADR 0010's budget (about 100 ms, under 200 ms from the grid), and the node.
            return (Lexicon)Guarded("RETURNBURN", () =>
            {
                node = BlockCalls.ReturnBurn(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => node.ContainsKey("dv")
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); {5} Pe {6:0} m, inclination {7:0.00}, leaving the moon at UT {8:0}{9}{10}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["planet"], node["pe_predicted"], node["inc_predicted"], node["soi_ut"],
                    (bool)node["from_grid"] ? " (from the grid)" : "", node["error"] as string == "" ? "" : "; " + node["error"])
                : (string)node["error"]);
        }

        /// <summary>
        /// The one wrapper every block-internal call goes through (#439): runs <paramref name="call"/>, logs its name and wall-clock
        /// time with <paramref name="describe"/>'s account of the result (so block tests can read it back from KSP.log), and turns an
        /// exception into <c>lexicon("error", reason)</c> so the block fails with <c>reason</c> instead of the kOS program ending.
        /// Core returns expected failures as an <c>error</c>; an exception here is a bug, logged with its stack.
        /// </summary>
        internal static Structure Guarded(string name, Func<Structure> call, Func<string> describe, double everySeconds = 0)
        {
            var watch = Stopwatch.StartNew();
            Structure result;
            try
            {
                result = call();
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " block call " + name + " failed after " + Ms(watch) + " ms: " + e);
                var error = new Lexicon();
                error.Add(new StringValue("error"), new StringValue(name + " failed: " + e.Message));
                return error;
            }
            // The log line is only a diagnostic: a failure writing it must not fail a call that worked.
            var took = Ms(watch);
            var note = "";
            if (everySeconds > 0 && !Due(name, watch.Elapsed.TotalMilliseconds, everySeconds, ref took, ref note)) return result;
            try
            {
                Debug.Log(ModInfo.LogTag + " block call " + name + " took " + took + " ms" + note + ": " + (describe == null ? "done" : describe()));
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " block call " + name + " took " + took + " ms; its log line failed: " + e.Message);
            }
            return result;
        }

        private Lexicon EjectionBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> node = null;
            // The search's time, against the minutes the old RSVP search took, and the node.
            return (Lexicon)Guarded("EJECTIONBURN", () =>
            {
                node = BlockCalls.EjectionBurn(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => node["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.0} m/s); Pe {5:0} m at UT {6:0}, arrival burn {7:0.0} m/s; synodic period {8:0} s from UT {9:0.0}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["target_pe_actual"], node["arrival_ut"], node["arrival_dv"], node["synodic"], node["search_from"])
                : (string)node["error"]);
        }

        private Lexicon OrbitBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> ask = null, node = null;
            // The goal, the node and what it should leave, so block tests can read predicted against flown from KSP.log.
            return (Lexicon)Guarded("ORBITBURN", () =>
            {
                ask = KosValues.ToDictionary(input);
                node = BlockCalls.OrbitBurn(ask, SnapshotReader.Read(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => node["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "{0} node at UT {1:0.0}, prograde {2:0.00}, normal {3:0.00}, radial {4:0.00} m/s ({5:0.00} m/s, burn {6:0.0} s); wanted {7}",
                    KosValues.Format(ask), node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["burn_time"], KosValues.Format(node["wanted"]))
                : (string)node["error"]);
        }

        private Lexicon TargetVessel()
        {
            System.Collections.Generic.IDictionary<string, object> found = null;
            return (Lexicon)Guarded("TARGETVESSEL", () =>
            {
                found = BlockCalls.TargetVessel(SnapshotReader.Read(shared.Vessel));
                var answer = (Lexicon)KosValues.FromPlain(found);
                // The vessel structure itself, found by persistentId like DOCKHOLD, so the blocks never look the target up by name.
                var id = MissionDriver.TargetVesselId ?? MissionDriver.BlockTestTargetId;
                Vessel target = null;
                if (found["error"] as string == "" && id.HasValue && FlightGlobals.FindVessel(id.Value, out target) && target != null && target != shared.Vessel)
                    answer.Add(new StringValue("vessel"), kOS.Suffixed.VesselTarget.CreateOrGetExisting(target, shared));
                return answer;
            }, () => found["error"] as string == "" ? found["name"] + " round " + found["body"] : (string)found["error"]);
        }

        private Lexicon CorrectionBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> node = null;
            return (Lexicon)Guarded("CORRECTIONBURN", () =>
            {
                node = BlockCalls.CorrectionBurn(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => !node.ContainsKey("dv") ? (string)node["error"]
                : node.ContainsKey("distance_predicted")
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); closest approach {5:0.0} m at {6:0.000} m/s{7}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["distance_predicted"], node["relative_speed_predicted"],
                    node["error"] as string == "" ? "" : "; " + node["error"])
                : string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); Pe {5:0} m, inclination {6:0.00}{7}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["pe_predicted"], node["inc_predicted"],
                    node["error"] as string == "" ? "" : "; " + node["error"]));
        }

        private Lexicon LaunchWindow(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> window = null;
            return (Lexicon)Guarded("LAUNCHWINDOW", () =>
            {
                window = BlockCalls.LaunchWindow(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel));
                return KosValues.FromPlain(window);
            }, () => window["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "window in {0:0} s at UT {1:0}, {2}bound, inclination {3:0.00}, heading {4:0.0}",
                    window["wait"], window["ut"], (bool)window["north"] ? "north" : "south", window["inc"], window["heading"])
                : (string)window["error"]);
        }

        private Lexicon TurnTime(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> turn = null;
            TurnCraft craft = null;
            // The prediction, so block tests can read it back from KSP.log against the turn the block flew.
            return (Lexicon)Guarded("TURNTIME", () =>
            {
                craft = TurnCraftReader.Read(shared.Vessel);
                turn = BlockCalls.TurnTime(KosValues.ToDictionary(input), craft);
                return KosValues.FromPlain(turn);
            }, () => string.Format(CultureInfo.InvariantCulture,
                "{0}; wheels {1:0.##}/{2:0.##} kN m, RCS {3:0.##}/{4:0.##} kN m{5}, MOI {6:0.##}/{7:0.##} t m2 (pitch/yaw)",
                turn["error"] as string == ""
                    ? string.Format(CultureInfo.InvariantCulture, "turn_time {0:0.0} s, turn {1:0.0} s at up to {2:0.0} deg/s; wheels alone {3}",
                        turn["turn_time"], turn["turn_s"], turn["top_rate"], (double)turn["wheels_turn_time"] < 0 ? "no torque"
                            : string.Format(CultureInfo.InvariantCulture, "turn_time {0:0.0} s, turn {1:0.0} s", turn["wheels_turn_time"], turn["wheels_turn_s"]))
                    : turn["error"],
                craft.WheelPitch, craft.WheelYaw, craft.RcsPitch, craft.RcsYaw, craft.RcsUsable ? "" : " (no monoprop)", craft.MoiPitch, craft.MoiYaw));
        }

        private Lexicon DeorbitBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> found = null;
            return (Lexicon)Guarded("DEORBITBURN", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                found = BlockCalls.DeorbitBurn(ask, SnapshotReader.Read(vessel), new PlanningBody(),
                    new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask)));
                return KosValues.FromPlain(found);
            }, () => Landing(found));
        }

        private Lexicon ReentryImpact(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> landing = null;
            return (Lexicon)Guarded("REENTRYIMPACT", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                landing = BlockCalls.ReentryImpact(ask, SnapshotReader.Read(vessel), new PlanningBody(),
                    new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask)));
                return KosValues.FromPlain(landing);
            }, () => Landing(landing));
        }

        /// <summary>A predicted landing for the log, so predicted against actual can be read back from KSP.log.</summary>
        private static string Landing(System.Collections.Generic.IDictionary<string, object> result)
        {
            var text = result["error"] as string ?? "";
            if (!result.ContainsKey("lat")) return text;
            // The frame the ground was read in (#326): stock's inverse-rotation angle and whether the frame turns with the body.
            return string.Format(CultureInfo.InvariantCulture, "{0}landing {1:0.0000}, {2:0.0000} at UT {3:0}, {4:0.00} km from the target{5}; inverse rotation {6:0.00} deg{7}",
                text == "" ? "" : text + "; ", result["lat"], result["lng"], result["landing_ut"], result["error_km"],
                result.ContainsKey("burn_ut")
                    ? string.Format(CultureInfo.InvariantCulture, "; burn at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00} m/s", result["burn_ut"], result["prograde"], result["normal"])
                    : "",
                Planetarium.InverseRotAngle, FlightGlobals.RefFrameIsRotating ? " (rotating frame)" : "");
        }

        private sealed class CallTally
        {
            public long LastTicks;
            public int Calls;
            public double SlowestMs;
        }

        private static readonly System.Collections.Generic.Dictionary<string, CallTally> Tallies = new System.Collections.Generic.Dictionary<string, CallTally>();

        /// <summary>
        /// For a call made every tick: whether its log line is due (at most one every <paramref name="everySeconds"/> real seconds), and
        /// if so <paramref name="took"/> becomes the slowest of the calls since the last line, and <paramref name="note"/> says so.
        /// </summary>
        private static bool Due(string name, double ms, double everySeconds, ref string took, ref string note)
        {
            CallTally tally;
            if (!Tallies.TryGetValue(name, out tally)) Tallies[name] = tally = new CallTally { LastTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3600 };
            tally.Calls++;
            tally.SlowestMs = Math.Max(tally.SlowestMs, ms);
            var now = Stopwatch.GetTimestamp();
            if (now - tally.LastTicks < everySeconds * Stopwatch.Frequency) return false;
            took = tally.SlowestMs.ToString("0.000", CultureInfo.InvariantCulture);
            note = " (the slowest of " + tally.Calls + " calls since the last line; this one's answer)";
            tally.LastTicks = now;
            tally.Calls = 0;
            tally.SlowestMs = 0;
            return true;
        }

        private static string Ms(Stopwatch watch)
        {
            return watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private Lexicon LandingSite(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> site = null;
            // The search runs on the main thread, as kOS does: its time against the frame budget is for tuning it.
            return (Lexicon)Guarded("LANDINGSITE", () =>
            {
                site = BlockCalls.LandingSite(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel), new PlanningBody());
                return KosValues.FromPlain(site);
            }, () => site["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "{0} at {1}, {2}", site["biome"], site["lat"], site["lng"])
                : (string)site["error"]);
        }

        private Lexicon BrakePath(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> path = null;
            // The prediction against the flown descent, and its time against the search's budget (#511).
            return (Lexicon)Guarded("BRAKEPATH", () =>
            {
                path = BlockCalls.BrakePathCall(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel), new PlanningBody());
                return KosValues.FromPlain(path);
            }, () => path["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "shape {0:0.###} {1}, shapes {2:0.###} to {3:0.###} clear, braking {4:0} m/s",
                    path["descent_shape"], (bool)path["clears"] ? "clears" : "doesn't clear", path["shape_lo"], path["shape_hi"],
                    path.ContainsKey("brake_dv") ? path["brake_dv"] : -1.0)
                : (string)path["error"]);
        }

        /// <summary>The snapshot with the Mission's time cap, as the Planning Tools' snapshots carry it (#543).</summary>
        private static GameSnapshot WithTimeCap(GameSnapshot snapshot)
        {
            snapshot.TimeCapHours = MissionDriver.TimeCapHours;
            return snapshot;
        }

        private Lexicon LandingPass(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> pass = null;
            return (Lexicon)Guarded("LANDINGPASS", () =>
            {
                pass = BlockCalls.LandingPass(KosValues.ToDictionary(input), WithTimeCap(SnapshotReader.Read(shared.Vessel)), new PlanningBody());
                return KosValues.FromPlain(pass);
            }, () => pass.ContainsKey("burn_ut") && pass["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "burn at UT {0:0.0}, touchdown at UT {1:0.0}, correction {2:0.0} m/s, next pass {3:0.0}; descent shape {4:0.###}, periapsis radius {5:0} m, landing {6:0} m/s",
                    pass["burn_ut"], pass["arrive_ut"], pass["correction_dv"], pass["next_pass_ut"], pass["descent_shape"], pass["r_pe"], pass["predicted_dv"])
                : (string)pass["error"]);
        }
    }
}
