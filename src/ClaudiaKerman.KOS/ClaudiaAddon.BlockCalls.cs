// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Adapter;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using Debug = UnityEngine.Debug;

namespace ClaudiaKerman.KOS
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
            AddSuffix("LAUNCHHEADING", new OneArgsSuffix<Lexicon, Lexicon>(LaunchHeading,
                "Core Library only (launch): the heading into the plane of inc (negative southbound) from lat, with gm, radius and rotation of the body and target_ap (m); lexicon error, heading"));
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
            AddSuffix("PREDICTIONMISS", new OneArgsSuffix<Lexicon, Lexicon>(PredictionMissCall,
                "Core Library only (reentry): the great-circle km on a body of radius (m) between lat, lng and actual_lat, actual_lng; lexicon km"));
            AddSuffix("TURNTIME", new OneArgsSuffix<Lexicon, Lexicon>(TurnTime,
                "Core Library only (a block that turns to a burn): the time to leave for a 190 degree turn on the wheels and RCS (if the craft has monoprop, on or off), given max_stopping_time; lexicon error, turn_time, turn_s, top_rate, and wheels_turn_time, wheels_turn_s on the wheels alone (-1: no wheel torque)"));
            AddSuffix("TURNPLAN", new OneArgsSuffix<Lexicon, Lexicon>(TurnPlanCall,
                "Core Library only (RCS Use, once per turn): the burst turn through angle (deg) with left_s to the deadline (-1: none), or after a stop burst with corrections taken; lexicon error, rcs, wheels_s, coast_rate, spin_s, coast_s, stop_s, stop_deg, wheels_deg, turn_s, alpha, and spin, coast, stop (kOS's t, kp, ki)"));
            AddSuffix("BURSTTURN", new TwoArgsSuffix<Lexicon, Lexicon, Lexicon>(BurstTurnCall,
                "Core Library only (RCS Use, each tick of a burst turn): the turn (TURNPLAN's plan, then this call's answer) stepped on ut, error, rate, left_s, aligned, monoprop; lexicon phase, rcs_on, done, and tuning (t, kp, ki) as a phase begins, report as the wheels take over"));
            AddSuffix("EJECTIONBURN", new OneArgsSuffix<Lexicon, Lexicon>(EjectionBurn,
                "Core Library only (transfer_to): the cheapest departure to target (a moon's name) arriving at target_pe, or to an orbit (target_sma, target_ecc, ...; mode rendezvous or intercept), capture true or false, within one synodic period from earliest_ut or window (to latest_ut, refusing over max_dv); lexicon error, ut, prograde, normal, radial, dv, arrival_dv, encounter_ut, arrival_ut, target_pe_actual, ..."));
            AddSuffix("TRANSFERSTEP", new OneArgsSuffix<Lexicon, Lexicon>(TransferStep,
                "Core Library only (transfer_to): its next step from phase (start first) with its options and what the last step carried; lexicon error, do (burn, wait or done), kind, ut, prograde, normal, radial, max_residual, fail_prefix, keep_going, report, max_duration, carry"));
            AddSuffix("RETURNBURN", new OneArgsSuffix<Lexicon, Lexicon>(ReturnBurn,
                "Core Library only (return): the least burn from earliest_ut to latest_ut (one orbit later if missing) that leaves the moon orbited for its planet at target_pe and, if given, target_inc; lexicon error, planet, ut, prograde, normal, radial, dv, pe_predicted, inc_predicted, soi_ut, from_grid, finished_by"));
            AddSuffix("RETURNMISS", new OneArgsSuffix<Lexicon, Lexicon>(ReturnMissCall,
                "Core Library only (return): whether the path flown (pe, inc) is the return asked (target_pe and, if given, target_inc, within pe_tolerance and inc_tolerance); lexicon error, empty when it is, else why with the figures"));
            AddSuffix("TARGETVESSEL", new NoArgsSuffix<Lexicon>(TargetVessel,
                "Core Library only (rendezvous, dock, land): the Mission's Target Vessel, read live; lexicon error, name, body, and lat, lng, alt for a landed or splashed one"));
            AddSuffix("DOCKHOLD", new OneArgsSuffix<Lexicon, Lexicon>(DockHoldStart,
                "Core Library only (dock): the mod starts its dock_hold on the Target Vessel's kOS processor, turning its port (uid) toward the craft's (toward, uid), or meets the target passive; lexicon error, state (starting or passive), reason"));
            AddSuffix("DOCKHOLDSTATE", new NoArgsSuffix<Lexicon>(DockHoldRead,
                "Core Library only (dock): the dock_hold dock started; lexicon state (none, starting, active, near, settled, passive, fault or docked), reason"));
            // dock_hold's own report (#711), from the mod's program on the target. Block-internal (ADR 0014 guardrail 5): the
            // Script Check refuses it in a Mission Script, and the bridge ignores it from any run but a live dock_hold.
            AddSuffix("DOCKHOLDSETTLED", new OneArgsSuffix<StringValue>(DockHoldSettled,
                "The mod's dock_hold only: its port settled on its aim (settled), loosely (near), or a re-aim unsettled it (empty)"));
            AddSuffix("DOCKHOLDLEVEL", new OneArgsSuffix<Lexicon, Lexicon>(DockHoldLevel,
                "The mod's dock_hold only: DockGuidance's level rule, one tick; lexicon level, changed and the clocks to pass back"));
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
            AddSuffix("BURNWORTH", new OneArgsSuffix<Lexicon, Lexicon>(BurnWorthCall,
                "Core Library only (circularize, tune_pe): whether a burn is worth its turn when the tolerance was defaulted, and tune_pe's default tolerance; lexicon safe_height, tolerance, skip, note"));
            AddSuffix("SHIPSYSTEMS", new OneArgsSuffix<Lexicon, StringValue>(ShipSystemsCall,
                "Core Library only (launch, ascend, land, deorbit, reentry): a Ship Systems cue (liftoff, final_descent, entry) or stowing to ask after the lift-off stow; lexicon done, capped, note"));
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:SHIPSYSTEMS</c> (#756, ADR 0014): the block's one-line cue for the moments only it knows. The mod's rule
        /// decides (<see cref="ShipSystemsRule"/>) and the mod moves the parts, the craft's own, from Update; the Script Check refuses
        /// the call in a Mission Script. A block's stow wait polls it, so its time is logged at most once a second (guardrail 6).
        /// </summary>
        private Lexicon ShipSystemsCall(StringValue cue)
        {
            ShipSystemsStowAnswer answer = null;
            return (Lexicon)Guarded("SHIPSYSTEMS", () =>
            {
                // A failure here must not stop the block (it reads the answer's keys unguarded): no action, and the block goes on.
                var keeper = ShipSystemsKeeper.Current;
                try { answer = keeper == null ? ShipSystemsStowAnswer.Nothing : keeper.Cue(cue.ToString(), shared.Vessel); }
                catch (Exception e)
                {
                    Debug.LogWarning(ModInfo.LogTag + " Ship Systems cue " + cue + " failed: " + e);
                    answer = ShipSystemsStowAnswer.Nothing;
                }
                return KosValues.FromPlain(answer.Lexicon());
            }, () => cue + ": " + (answer.Done ? "done" : "waiting") + (answer.Capped ? ", capped" : ""), 1);
        }

        /// <summary><c>ADDONS:CLAUDIA:BURNWORTH</c> (#715, ADR 0014): circularize's and tune_pe's skip rule (<see cref="BurnWorth.Call"/>), pure.</summary>
        private Lexicon BurnWorthCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> worth = null;
            return (Lexicon)Guarded("BURNWORTH", () =>
            {
                // The craft's periapsis and body are read live, unless the block names its own (the hint's encounter body).
                var ask = KosValues.ToDictionary(input);
                var vessel = shared.Vessel;
                if (!ask.ContainsKey("body"))
                {
                    ask["body"] = vessel.mainBody.bodyName;
                    ask["has_atm"] = vessel.mainBody.atmosphere;
                    ask["atm_height"] = vessel.mainBody.atmosphereDepth;
                    ask["pe"] = vessel.orbit.PeA;
                }
                worth = BurnWorth.Call(ask);
                return KosValues.FromPlain(worth);
            }, () => (bool)worth["skip"] ? (string)worth["note"] : string.Format(CultureInfo.InvariantCulture, "no skip, tolerance {0:0.###}", worth["tolerance"]));
        }

        /// <summary><c>ADDONS:CLAUDIA:KEEPCLEAR</c> (#713, ADR 0014): land's keep-clear decision (<see cref="KeepClear.Call"/>), pure.</summary>
        /// <summary><c>ADDONS:CLAUDIA:PREDICTIONMISS</c> (#478, ADR 0014): reentry's predicted-against-actual landing distance (<see cref="PredictionMiss.Call"/>), pure.</summary>
        private Lexicon PredictionMissCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> miss = null;
            return (Lexicon)Guarded("PREDICTIONMISS", () =>
            {
                miss = PredictionMiss.Call(KosValues.ToDictionary(input));
                return KosValues.FromPlain(miss);
            }, () => string.Format(CultureInfo.InvariantCulture, "{0:0.00} km between the predicted and the actual landing", miss["km"]));
        }

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
        private void DockHoldSettled(StringValue level)
        {
            var bridge = KosBridge.Instance;
            if (bridge != null && shared.KSPPart != null) bridge.HoldSettled(shared.KSPPart.persistentId, level.ToString());
        }

        /// <summary><c>ADDONS:CLAUDIA:DOCKHOLDLEVEL</c> (#722): dock_hold's level rule (<see cref="DockGuidance.HoldLevel"/>), pure, each tick, so not logged.</summary>
        private Lexicon DockHoldLevel(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> tick = null;
            return (Lexicon)Guarded("DOCKHOLDLEVEL", () =>
            {
                tick = DockGuidance.HoldLevel(KosValues.ToDictionary(input));
                return KosValues.FromPlain(tick);
            }, () => "level \"" + tick["level"] + "\"", 1);
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
                set = BlockCalls.ChuteAllocationCall(MissionDriver.ChuteAllocation, MissionDriver.CurrentSequence, SnapshotReader.ReadForMission(vessel),
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
                node = BlockCalls.ReturnBurn(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => node.ContainsKey("dv")
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); {5} Pe {6:0} m, inclination {7:0.00}, leaving the moon at UT {8:0}{9}{10}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["planet"], node["pe_predicted"], node["inc_predicted"], node["soi_ut"],
                    ((bool)node["from_grid"] ? " (from the grid)" : "") + (node["finished_by"] as string == "" ? "" : " (the SQP stopped short: finished by " + node["finished_by"] + ")"),
                    node["error"] as string == "" ? "" : "; " + node["error"])
                : (string)node["error"]);
        }

        /// <summary><c>ADDONS:CLAUDIA:RETURNMISS</c> (#759, ADR 0014): whether the path flown is the return asked (<see cref="BlockCalls.ReturnMiss"/>), pure.</summary>
        private Lexicon ReturnMissCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> miss = null;
            return (Lexicon)Guarded("RETURNMISS", () =>
            {
                miss = BlockCalls.ReturnMiss(KosValues.ToDictionary(input));
                return KosValues.FromPlain(miss);
            }, () => miss["error"] as string == "" ? "within the tolerances" : (string)miss["error"]);
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
                node = BlockCalls.EjectionBurn(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => node["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.0} m/s); Pe {5:0} m at UT {6:0}, arrival burn {7:0.0} m/s; synodic period {8:0} s from UT {9:0.0}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["target_pe_actual"], node["arrival_ut"], node["arrival_dv"], node["synodic"], node["search_from"])
                : (string)node["error"]);
        }

        private Lexicon TransferStep(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> step = null;
            // The step's time (the ejection's window search is the long one) and what it asks the block to do.
            return (Lexicon)Guarded("TRANSFERSTEP", () =>
            {
                step = BlockCalls.TransferStep(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
                return KosValues.FromPlain(step);
            }, () => (string)step["error"] != "" ? (string)step["error"]
                : string.Format(CultureInfo.InvariantCulture, "{0} {1} at UT {2:0.0}, prograde {3:0.00}, normal {4:0.00}, radial {5:0.00} m/s; next {6}; {7}",
                    step["do"], step["kind"], step["ut"], step["prograde"], step["normal"], step["radial"],
                    ((System.Collections.Generic.IDictionary<string, object>)step["carry"])["phase"], KosValues.Format(step["report"])));
        }

        private Lexicon OrbitBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> ask = null, node = null;
            // The goal, the node and what it should leave, so block tests can read predicted against flown from KSP.log.
            return (Lexicon)Guarded("ORBITBURN", () =>
            {
                ask = KosValues.ToDictionary(input);
                node = BlockCalls.OrbitBurn(ask, SnapshotReader.ReadForMission(shared.Vessel));
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
                found = BlockCalls.TargetVessel(SnapshotReader.ReadForMission(shared.Vessel));
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
                node = BlockCalls.CorrectionBurn(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
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
                window = BlockCalls.LaunchWindow(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
                return KosValues.FromPlain(window);
            }, () => window["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "window in {0:0} s at UT {1:0}, {2}bound, inclination {3:0.00}, heading {4:0.0}",
                    window["wait"], window["ut"], (bool)window["north"] ? "north" : "south", window["inc"], window["heading"])
                : (string)window["error"]);
        }

        private Lexicon LaunchHeading(Lexicon input)
        {
            // Called every tick on the climb, so its log line is throttled (every 2 s), as the other per-tick calls'.
            System.Collections.Generic.IDictionary<string, object> heading = null;
            return (Lexicon)Guarded("LAUNCHHEADING", () =>
            {
                heading = BlockCalls.LaunchHeading(KosValues.ToDictionary(input));
                return KosValues.FromPlain(heading);
            }, () => heading["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "heading {0:0.00}", heading["heading"])
                : (string)heading["error"], 2);
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

        /// <summary>
        /// <c>ADDONS:CLAUDIA:TURNPLAN</c> (#692, ADR 0014): RCS Use's burst turn and correction rule (<see cref="TurnPlan.Call"/>) on
        /// the torque and moment of inertia read now. Called at the on-gate (at most once a second) and after each stop burst, so its
        /// time is logged at most once a second (guardrail 6).
        /// </summary>
        private Lexicon TurnPlanCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> plan = null;
            var answer = Guarded("TURNPLAN", () =>
            {
                plan = TurnPlan.Call(KosValues.ToDictionary(input), TurnCraftReader.Read(shared.Vessel));
                return KosValues.FromPlain(plan);
            }, () => (string)plan["error"] != "" ? (string)plan["error"] : string.Format(CultureInfo.InvariantCulture,
                "rcs {0}, wheels {1:0.0} s; coast {2:0.00} deg/s, bursts {3:0.0} s, coast {4:0.0} s, stop at {5:0.0} deg, turn {6:0.0} s",
                plan["rcs"], plan["wheels_s"], plan["coast_rate"], plan["spin_s"], plan["coast_s"], plan["stop_deg"], plan["turn_s"]), 1);
            if (plan != null) return (Lexicon)answer;
            // RCS Use reads every key: a failure (logged by Guarded) plans no burst, and wheels that are never late (0 s), so it
            // brings no continuous RCS; a big turn then gets RCS on as before the burst turn.
            var none = TurnPlan.Call(null, new TurnCraft());
            none["wheels_s"] = 0.0;
            return (Lexicon)KosValues.FromPlain(none);
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BURSTTURN</c> (#692, ADR 0014): one tick of a burst turn (<see cref="TurnPlan.Step"/>), with the
        /// orbit's energy and speed read now; the craft's torque is read only when a stop burst ends. Called every tick of a
        /// turn, so its time is logged at most once a second (guardrail 6). A failure ends the turn with RCS off.
        /// </summary>
        private Lexicon BurstTurnCall(Lexicon turn, Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> step = null;
            var answer = Guarded("BURSTTURN", () =>
            {
                var vessel = shared.Vessel;
                var now = KosValues.ToDictionary(input);
                now["energy"] = vessel.orbit.orbitalEnergy;
                now["speed"] = vessel.obt_speed;
                step = TurnPlan.Step(KosValues.ToDictionary(turn), now, () => TurnCraftReader.Read(vessel));
                return KosValues.FromPlain(step);
            }, () => string.Format(CultureInfo.InvariantCulture, "{0}, RCS {1}{2}", step["phase"], (bool)step["rcs_on"] ? "on" : "off",
                (bool)step["done"] ? ", done" : ""), 1);
            if (step != null) return (Lexicon)answer;
            return (Lexicon)KosValues.FromPlain(new System.Collections.Generic.Dictionary<string, object>
            {
                { "phase", "settle" }, { "done", true }, { "rcs_on", false },
                { "tuning", new System.Collections.Generic.Dictionary<string, object> { { "t", 2.0 }, { "kp", 1.0 }, { "ki", 0.1 } } },
            });
        }

        private Lexicon DeorbitBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> found = null;
            return (Lexicon)Guarded("DEORBITBURN", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                found = BlockCalls.DeorbitBurn(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody(),
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
                landing = BlockCalls.ReentryImpact(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody(),
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
                site = BlockCalls.LandingSite(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel), new PlanningBody());
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
                path = BlockCalls.BrakePathCall(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel), new PlanningBody());
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
                pass = BlockCalls.LandingPass(KosValues.ToDictionary(input), WithTimeCap(SnapshotReader.ReadForMission(shared.Vessel)), new PlanningBody());
                return KosValues.FromPlain(pass);
            }, () => pass.ContainsKey("burn_ut") && pass["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "burn at UT {0:0.0}, touchdown at UT {1:0.0}, correction {2:0.0} m/s, next pass {3:0.0}; descent shape {4:0.###}, periapsis radius {5:0} m, landing {6:0} m/s",
                    pass["burn_ut"], pass["arrive_ut"], pass["correction_dv"], pass["next_pass_ut"], pass["descent_shape"], pass["r_pe"], pass["predicted_dv"])
                : (string)pass["error"]);
        }
    }
}
