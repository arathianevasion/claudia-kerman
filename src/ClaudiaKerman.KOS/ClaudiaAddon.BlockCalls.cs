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
                "Core Library only (transfer_to, return, tune_pe): the least burn from earliest_ut to latest_ut that arrives at target (a body's name) at target_pe and, if given, target_inc or a landing at lat, lng; lexicon error, ut, prograde, normal, radial, dv, pe_predicted, inc_predicted, and for a landing lat, lng, landing_ut, error_km"));
            AddSuffix("LAUNCHWINDOW", new OneArgsSuffix<Lexicon, Lexicon>(LaunchWindow,
                "Core Library only (launch): when the pad next passes under the plane of inc and lan (degrees); lexicon error, wait (s), ut, north, inc (negative southbound), heading"));
            AddSuffix("LAUNCHHEADING", new OneArgsSuffix<Lexicon, Lexicon>(LaunchHeading,
                "Core Library only (launch): the heading into the plane of inc (negative southbound) from lat, with gm, radius and rotation of the body and target_ap (m); lexicon error, heading"));
            AddSuffix("BURNTHROUGH", new OneArgsSuffix<Lexicon, Lexicon>(BurnThrough,
                "Core Library only (launch, at its first cutoff and each tick of a burn-through): whether the stage is too weak to coast to apoapsis and circularize there, from ap, sma, gm, radius, eta_ap, altitude, target_ap, atm_height, angle, stages, turn and burning; lexicon error, burn_through, dv, half_burn, turn_time, angle, eta_ap, climb, time_left"));
            AddSuffix("BURNTIME", new OneArgsSuffix<Lexicon, Lexicon>(BurnTimeCall,
                "Core Library only (claudia_burn_timing): the seconds to burn dv (m/s) over stages (claudia_burn_stages' list) and to burn half of it; lexicon error, burn_time, half_burn (each -1 with no thrust)"));
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
            AddSuffix("GROUNDSETTLE", new OneArgsSuffix<Lexicon, Lexicon>(GroundSettleCall,
                "Core Library only (reentry, each tick after touchdown): whether the craft has stayed on the ground for 2 s (or 30 s have passed since first touch), from on_ground, now, since, first; lexicon since, first, done"));
            AddSuffix("TURNTIME",new OneArgsSuffix<Lexicon, Lexicon>(TurnTime,
                "Core Library only (a block that turns to a burn): the time to leave for a 190 degree turn on the wheels and RCS (if the craft has monoprop, on or off), given max_stopping_time; lexicon error, turn_time, turn_s, top_rate, and wheels_turn_time, wheels_turn_s on the wheels alone (-1: no wheel torque)"));
            AddSuffix("LOWCHARGE", new OneArgsSuffix<Lexicon, Lexicon>(LowChargeCall,
                "Core Library only (reentry, each tick): what low charge does with the periapsis in the air, from ec_pct, min_ec_pct, pe, air_top; lexicon error, carry_on, step (0 nothing, 1 steering released, 2 chutes armed), abort_pct (the charge min_ec_pct is to judge)"));
            AddSuffix("BRAKEHEIGHTS", new OneArgsSuffix<Lexicon, Lexicon>(BrakeHeightsCall,
                "Core Library only (reentry, every 0.25 s of a powered fall): the heights it brakes by, from v_down, a_up, g, ks, radar, soft, end; lexicon error, brake_h, last_h (-1 for a fall the engines can't stop)"));
            AddSuffix("BRAKETICK", new OneArgsSuffix<Lexicon, Lexicon>(BrakeTickCall,
                "Core Library only (reentry, each tick of a powered finish): whether braking may start, the guarded throttle and the cut for the final burn, from radar, speed, brake_h, last_h, uids, the shield limits, throttle, off_deg, rate_deg, up_deg, v_down, a_up, g, soft, end, drogues_called, mains_called, brake_why; lexicon error, start, why, open, chutes, throttle, zero_why, rate_deg, cut, cut_why, trace"));
            AddSuffix("ENTRYLOSS", new OneArgsSuffix<Lexicon, Lexicon>(EntryLossCall,
                "Core Library only (reentry, a part count that fell): whether it carries on through a part loss in the air, from lost, altitude, air_top, on_ground, powered, thrust, uids, chute_mode, mode, entry, group; lexicon error, carry_on, powered, to_engines, regroup, light, chute_mode, finish, group, why"));
            AddSuffix("ENTRYSKIP", new OneArgsSuffix<Lexicon, Lexicon>(EntrySkipCall,
                "Core Library only (reentry, after a skip-out): whether it flies another pass, from passes, pe, air_top, entry_in, auto, max_duration, elapsed; lexicon error, refusal, max_duration"));
            AddSuffix("TURNPLAN", new OneArgsSuffix<Lexicon, Lexicon>(TurnPlanCall,
                "Core Library only (RCS Use, once per turn): the burst turn through angle (deg) with left_s to the deadline (-1: none), or after a stop burst with corrections taken; lexicon error, rcs, wheels_s, coast_rate, spin_s, coast_s, stop_s, stop_deg, wheels_deg, turn_s, alpha, and spin, coast, stop (kOS's t, kp, ki)"));
            AddSuffix("BURSTTURN", new TwoArgsSuffix<Lexicon, Lexicon, Lexicon>(BurstTurnCall,
                "Core Library only (RCS Use, each tick of a burst turn): the turn (TURNPLAN's plan, then this call's answer) stepped on ut, error, rate, left_s, aligned, monoprop; lexicon phase, rcs_on, done, and tuning (t, kp, ki) as a phase begins, report as the wheels take over"));
            AddSuffix("EJECTIONBURN", new OneArgsSuffix<Lexicon, Lexicon>(EjectionBurn,
                "Core Library only (transfer_to): the cheapest departure to target (a moon's name) arriving at target_pe, or to an orbit (target_sma, target_ecc, ...; mode rendezvous or intercept), capture true or false, within one synodic period from earliest_ut or window (to latest_ut, refusing over max_dv); lexicon error, ut, prograde, normal, radial, dv, arrival_dv, encounter_ut, arrival_ut, target_pe_actual, ..."));
            AddSuffix("TRANSFERSTEP", new OneArgsSuffix<Lexicon, Lexicon>(TransferStep,
                "Core Library only (transfer_to): its next step from phase (start first) with its options and what the last step carried; lexicon error, do (burn, wait or done), kind, ut, prograde, normal, radial, max_residual, fail_prefix, keep_going, report, max_duration, watch, carry"));
            AddSuffix("CAPTURESTEP", new OneArgsSuffix<Lexicon, Lexicon>(CaptureStep,
                "Core Library only (capture): its next step from phase (start first) with its options and what the last step carried; lexicon as TRANSFERSTEP's"));
            AddSuffix("ENCOUNTERWATCH", new OneArgsSuffix<Lexicon>(EncounterWatchArm,
                "Core Library only (transfer_to): arms the Encounter Watch for a wait with TRANSFERSTEP's watch; an empty lexicon watches nothing"));
            AddSuffix("ENCOUNTERWATCHDROPPED", new Suffix<BooleanValue>(EncounterWatchPoll,
                "Core Library only (transfer_to, each tick of a wait): the Encounter Watch's check when one is due; whether the wait should end early"));
            AddSuffix("LANDINGTARGET", new OneArgsSuffix<Lexicon, Lexicon>(LandingTargetCall,
                "What a return's or deorbit's \"auto\" target stands for: ksc or none (#790). Internal to the Core Library."));
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
            AddSuffix("CHUTEGROUP", new OneArgsSuffix<Lexicon, Lexicon>(ChuteGroupSet,
                "Core Library only (reentry): this entry's Chute Group, read fresh, from none and engine; lexicon error, last (true: every chute aboard), stage, stages, uids, titles, touchdown_speed, why, warning"));
            AddSuffix("ENTRYFINISH", new OneArgsSuffix<Lexicon, Lexicon>(EntryFinishSet,
                "Core Library only (reentry): whether this entry lands on its engines, from home, chutes, drop and core; lexicon error, powered, keeps_stack, speed, kept_speed, engines, spare, why"));
            AddSuffix("ENTRYDROP", new OneArgsSuffix<Lexicon, Lexicon>(EntryDrop,
                "Core Library only (reentry): what this Entry drops, from home, separate, chutes, core and drop_error (error: a stack it refuses); lexicon error, number, shield, jettison, jettison_first, decouplers, gone, names, stuck, lead"));
            AddSuffix("ENTRYTRIM", new OneArgsSuffix<Lexicon, Lexicon>(EntryTrimCall,
                "Core Library only (reentry): at most one late trim toward the Mission's landing target, from to_air, home, dv_left, isp and has_node; lexicon as CORRECTIONBURN's, dv 0 and aim_note when none flies"));
            AddSuffix("ENTRYCRAFT", new OneArgsSuffix<Lexicon, Lexicon>(EntryCraft,
                "Core Library only (return, deorbit): the craft the Entry ahead flies, from home, core, leave_out and body, to pass on as craft; lexicon error, leave_out, shield_drop, whole, kept, gone_prop, burners_kept, key, why"));
            AddSuffix("LIFTOFFCLEAR", new OneArgsSuffix<Lexicon, Lexicon>(LiftoffClearCall,
                "Core Library only (launch, ascend): the decouplers under the engines that give thrust, from core and home; lexicon error, decouplers, gone, stuck"));
            AddSuffix("DROPCHECK", new OneArgsSuffix<Lexicon, Lexicon>(DropCheckCall,
                "Core Library only (drop_parts, deorbit, reentry): whether a drop taking the craft from before to left m/s of delta-v takes what the Mission Plan still needs; lexicon error, refused, reason, taken, need"));
            AddSuffix("LANDGUARD", new OneArgsSuffix<Lexicon, Lexicon>(LandGuardTick,
                "Core Library only (land, each braking tick): the path guard on alt against path_alt, and near the site the straight-down fallback; lexicon verdict (fly, vertical or abort), error, why, over, and the fallback's al, cr, up, vv, ..."));
            AddSuffix("LANDSTEER", new OneArgsSuffix<Lexicon, Lexicon>(LandSteerTick,
                "Core Library only (land, each braking tick): the direction and throttle for the demand want, with the final descent's lean cap and settle; lexicon al, cr, up, mag, throttle, error, lean, settling, settle_s, ..."));
            AddSuffix("KEEPCLEAR", new OneArgsSuffix<Lexicon, Lexicon>(KeepClearCall,
                "Core Library only (land): the site lat, lng moved clear_m clear of keep_lat, keep_lng (straight away, else toward ahead_lat, ahead_lng) by up to max_divert on a body of radius; lexicon lat, lng, dist, moved"));
            AddSuffix("BURNWORTH", new OneArgsSuffix<Lexicon, Lexicon>(BurnWorthCall,
                "Core Library only (circularize, tune_pe, a Course Correction): whether a burn is worth its turn when the tolerance was defaulted, and tune_pe's default tolerance; for a correction, its max_throttle and done_dv; lexicon safe_height, tolerance, skip, note, ..."));
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

        /// <summary><c>ADDONS:CLAUDIA:ENCOUNTERWATCH</c> (#770, ADR 0014): a wait of transfer_to's arms its watch; the Script Check refuses the call in a Mission Script.</summary>
        private void EncounterWatchArm(Lexicon plan)
        {
            var watcher = EncounterWatcher.Current;
            if (watcher != null) watcher.Arm(KosValues.ToDictionary(plan), shared.Vessel);
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENCOUNTERWATCHDROPPED</c> (#770): the wait's stop, every tick. Most ticks only compare a time; the time of
        /// those that read the orbit is logged at most every 10 s (guardrail 6). A drop's line goes in the block trace.
        /// </summary>
        private BooleanValue EncounterWatchPoll()
        {
            var watcher = EncounterWatcher.Current;
            if (watcher == null) return false;
            var dropped = false;
            Guarded("ENCOUNTERWATCHDROPPED", () =>
            {
                dropped = watcher.Poll(shared.Vessel);
                var line = watcher.TakeLine();
                var bridge = KosBridge.Instance;
                if (line.Length > 0 && shared.KSPPart != null && bridge != null)
                    bridge.OnBlockTrace(shared.KSPPart.persistentId, "transfer_to", new System.Collections.Generic.Dictionary<string, object> { { "encounter_watch", line } });
                return new BooleanValue(dropped);
            }, watcher.Describe, 10);
            return dropped;
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
            }, () => (bool)worth["skip"] ? (string)worth["note"] : worth.ContainsKey("max_throttle")
                ? string.Format(CultureInfo.InvariantCulture, "fly it: throttle at most {0:0.###}, stopping within {1:0.###} m/s", worth["max_throttle"], worth["done_dv"])
                : string.Format(CultureInfo.InvariantCulture, "no skip, tolerance {0:0.###}", worth["tolerance"]));
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

        /// <summary><c>ADDONS:CLAUDIA:GROUNDSETTLE</c> (#968, ADR 0014): reentry's wait on the ground (<see cref="GroundSettle.Call"/>), pure.</summary>
        private Lexicon GroundSettleCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> settled = null;
            return (Lexicon)Guarded("GROUNDSETTLE", () =>
            {
                settled = GroundSettle.Call(KosValues.ToDictionary(input));
                return KosValues.FromPlain(settled);
            }, () => string.Format(CultureInfo.InvariantCulture, "settled {0}", settled["done"]), 1);
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
        /// <c>ADDONS:CLAUDIA:LANDSTEER</c> (#865, ADR 0014): one braking tick of land's steering and throttle (<see cref="LandSteer.Tick"/>),
        /// pure. Called every physics tick, so its time is logged at most once a second (guardrail 6).
        /// </summary>
        private Lexicon LandSteerTick(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> tick = null;
            return (Lexicon)Guarded("LANDSTEER", () =>
            {
                tick = LandSteer.Tick(KosValues.ToDictionary(input));
                return KosValues.FromPlain(tick);
            }, () => string.Format(CultureInfo.InvariantCulture, "throttle {0:0.00}, {1:0.0} deg off, lean {2:0.0} deg{3}", tick["throttle"], tick["error"],
                tick["lean"], (bool)tick["settling"] ? ", settling" : ""), 1);
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

        private Lexicon ChuteGroupSet(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> set = null;
            // The group and why, so a reentry's chutes can be read back from KSP.log.
            return (Lexicon)Guarded("CHUTEGROUP", () =>
            {
                set = ReadChuteGroup(KosValues.ToDictionary(input));
                return KosValues.FromPlain(set);
            }, () => set["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "{0}: {1}{2}", (bool)set["last"] ? "Last Entry, every chute aboard" : "stage " + set["stage"],
                    string.Join(", ", ((System.Collections.Generic.List<object>)set["titles"]).ConvertAll(t => (string)t).ToArray()),
                    (double)set["touchdown_speed"] >= 0 ? string.Format(CultureInfo.InvariantCulture, ", {0:0.0} m/s", set["touchdown_speed"]) : "")
                : (string)set["error"]);
        }

        /// <summary><c>ADDONS:CLAUDIA:ENTRYFINISH</c> (#824): whether this Entry lands on its engines (<see cref="BlockCalls.EntryFinishCall"/>); the log line is the decision, the speeds and the engine.</summary>
        private Lexicon EntryFinishSet(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> finish = null;
            return (Lexicon)Guarded("ENTRYFINISH", () =>
            {
                finish = ReadEntryFinish(KosValues.ToDictionary(input));
                return KosValues.FromPlain(finish);
            }, () => finish["error"] as string == "" ? (string)finish["why"] : (string)finish["error"]);
        }

        /// <summary><see cref="BlockCalls.ChuteGroupCall"/> for the craft as it now is, and any warning sent on; for <c>CHUTEGROUP</c> and <c>ENTRYLOSS</c>.</summary>
        private System.Collections.Generic.IDictionary<string, object> ReadChuteGroup(System.Collections.Generic.IDictionary<string, object> ask)
        {
            var vessel = shared.Vessel;
            var uids = new System.Collections.Generic.Dictionary<uint, string>();
            foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
            var set = BlockCalls.ChuteGroupCall(SnapshotReader.ReadForMission(vessel), MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence, MissionDriver.RunningChild,
                id => uids.ContainsKey(id) ? uids[id] : null, ask);
            // Nothing can land the craft, said above the air (#827): the Mission window's events and the Debrief, never an Alert.
            var warning = set["error"] as string == "" ? (string)set["warning"] : "";
            if (warning.Length > 0 && MissionDriver.Current != null) MissionDriver.Current.Send(new ChuteWarning(warning));
            return set;
        }

        /// <summary><see cref="BlockCalls.EntryFinishCall"/> for the craft as it now is; for <c>ENTRYFINISH</c> and <c>ENTRYLOSS</c>.</summary>
        private System.Collections.Generic.IDictionary<string, object> ReadEntryFinish(System.Collections.Generic.IDictionary<string, object> ask)
        {
            var vessel = shared.Vessel;
            var uids = new System.Collections.Generic.Dictionary<uint, string>();
            foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
            var snapshot = SnapshotReader.ReadForMission(vessel);
            SnapshotReader.ReadEngineFuel(snapshot, vessel);
            // Compared once, and only with this vessel's own aim: a later Entry with no aim of its own has nothing to compare.
            string aimed;
            var aimedKey = MissionDriver.AimedCraft.Take(vessel.persistentId, out aimed);
            return BlockCalls.EntryFinishCall(ask, snapshot, MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence, MissionDriver.RunningChild,
                id => uids.ContainsKey(id) ? uids[id] : null, aimedKey, aimed);
        }

        /// <summary><c>ADDONS:CLAUDIA:ENTRYLOSS</c> (#958, ADR 0014): <see cref="BlockCalls.EntryPartLoss"/> with the craft read now. Called only on a tick the part count fell. A failure (logged by Guarded) still answers, carrying on with what the block had: a falling craft must not abort on a failed read.</summary>
        private Lexicon EntryLossCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> loss = null;
            var answer = Guarded("ENTRYLOSS", () =>
            {
                var ask = KosValues.ToDictionary(input);
                var uids = ask.ContainsKey("uids") && ask["uids"] is System.Collections.IEnumerable ? ((System.Collections.IEnumerable)ask["uids"]).Cast<object>().Select(u => Convert.ToString(u, CultureInfo.InvariantCulture)).ToList() : new System.Collections.Generic.List<string>();
                ask["chutes"] = ReentryCraft.ChuteStates(shared.Vessel, uids);
                loss = BlockCalls.EntryPartLoss(ask, ReadEntryFinish, ReadChuteGroup);
                // Gone over to its engines (#963): BRAKETICK starts fresh, so a canopy already out is waited for from now, never on what an earlier flight left.
                if (loss.ContainsKey("to_engines") && (bool)loss["to_engines"]) HoldBrake(BlockCalls.EmptyBrakeHold());
                return KosValues.FromPlain(loss);
            }, () => string.Format(CultureInfo.InvariantCulture, "carry on {0}, {1}", loss["carry_on"], loss.ContainsKey("why") ? loss["why"] : ""));
            return loss != null ? (Lexicon)answer : (Lexicon)KosValues.FromPlain(BlockCalls.EntryPartLoss(KosValues.ToDictionary(input), null, null));
        }

        /// <summary><c>ADDONS:CLAUDIA:ENTRYSKIP</c> (#958, ADR 0014): <see cref="BlockCalls.EntrySkip"/>, pure. A failure refuses nothing and caps nothing, so the block flies on.</summary>
        private Lexicon EntrySkipCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> skip = null;
            var answer = Guarded("ENTRYSKIP", () =>
            {
                skip = BlockCalls.EntrySkip(KosValues.ToDictionary(input));
                return KosValues.FromPlain(skip);
            }, () => skip["refusal"] as string);
            return skip != null ? (Lexicon)answer : (Lexicon)KosValues.FromPlain(BlockCalls.EntrySkip(null));
        }

        /// <summary><c>ADDONS:CLAUDIA:ENTRYCRAFT</c> (#814): the craft the Entry ahead flies (<see cref="BlockCalls.EntryCraftCall"/>), recorded for <c>reentry</c> to compare.</summary>
        private Lexicon EntryCraft(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> craft = null;
            return (Lexicon)Guarded("ENTRYCRAFT", () =>
            {
                var vessel = shared.Vessel;
                var uids = new System.Collections.Generic.Dictionary<uint, string>();
                foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
                var snapshot = SnapshotReader.ReadForMission(vessel);
                SnapshotReader.ReadEngineFuel(snapshot, vessel);
                craft = BlockCalls.EntryCraftCall(KosValues.ToDictionary(input), snapshot, MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence, MissionDriver.RunningChild,
                    id => uids.ContainsKey(id) ? uids[id] : null, (key, why) => MissionDriver.AimedCraft.Aimed(vessel.persistentId, key, why));
                return KosValues.FromPlain(craft);
            }, () => craft["error"] as string == "" ? (string)craft["why"] : (string)craft["error"]);
        }

        /// <summary><c>ADDONS:CLAUDIA:ENTRYDROP</c> (#823, ADR 0018): what this Entry drops (<see cref="BlockCalls.EntryDropCall"/>), read fresh at block start.</summary>
        private Lexicon EntryDrop(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> drop = null;
            // n and the Entry Shield, so a reentry's drop can be read back from KSP.log.
            return (Lexicon)Guarded("ENTRYDROP", () =>
            {
                var vessel = shared.Vessel;
                var uids = new System.Collections.Generic.Dictionary<uint, string>();
                foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
                var snapshot = SnapshotReader.ReadForMission(vessel);
                // Its refusal (#815) asks whether the Entry lands on its engines, as ENTRYFINISH does.
                SnapshotReader.ReadEngineFuel(snapshot, vessel);
                drop = BlockCalls.EntryDropCall(KosValues.ToDictionary(input), snapshot, MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence, MissionDriver.RunningChild,
                    id => uids.ContainsKey(id) ? uids[id] : null);
                return KosValues.FromPlain(drop);
            }, () => drop["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "shield number {0}: {1}", drop["number"], drop["shield"] as string == "" ? "none, nothing drops" : "part " + drop["shield"])
                : (string)drop["error"]);
        }

        /// <summary><c>ADDONS:CLAUDIA:LIFTOFFCLEAR</c> (#945, ADR 0014): what is still under an engine giving thrust (<see cref="BlockCalls.LiftoffClearCall"/>), read fresh.</summary>
        private Lexicon LiftoffClearCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> found = null;
            return (Lexicon)Guarded("LIFTOFFCLEAR", () =>
            {
                var vessel = shared.Vessel;
                var uids = new System.Collections.Generic.Dictionary<uint, string>();
                foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
                found = BlockCalls.LiftoffClearCall(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(vessel), id => uids.ContainsKey(id) ? uids[id] : null);
                return KosValues.FromPlain(found);
            }, () => found["error"] as string == "" ? ((System.Collections.IList)found["decouplers"]).Count + " decoupler(s) under the engines" : (string)found["error"]);
        }

        /// <summary><c>ADDONS:CLAUDIA:DROPCHECK</c> (#825, ADR 0014): the Mission Plan's need against a drop (<see cref="BlockCalls.DropCheck"/>).</summary>
        private Lexicon DropCheckCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> verdict = null;
            return (Lexicon)Guarded("DROPCHECK", () =>
            {
                verdict = BlockCalls.DropCheck(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel),
                    MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence);
                return KosValues.FromPlain(verdict);
            }, () => verdict["error"] as string == "" ? (bool)verdict["refused"] ? "refused: " + verdict["reason"] : "allowed" : (string)verdict["error"]);
        }

        /// <summary><c>ADDONS:CLAUDIA:LANDINGTARGET</c> (#790, ADR 0014): the landing target default (<see cref="BlockCalls.LandingTarget"/>).</summary>
        private Lexicon LandingTargetCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> found = null;
            return (Lexicon)Guarded("LANDINGTARGET", () =>
            {
                found = BlockCalls.LandingTarget(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
                return KosValues.FromPlain(found);
            }, () => "auto target: " + found["target"]);
        }

        private Lexicon ReturnBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> node = null;
            // The solve's time against ADR 0010's budget (about 100 ms, under 200 ms from the grid), and the node.
            return (Lexicon)Guarded("RETURNBURN", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                // Aimed at a landing (#792), the predictor flies the planet's air: the craft is still at the moon.
                node = ask.ContainsKey("lat")
                    ? BlockCalls.ReturnBurn(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody(), new ReentryAir(vessel.mainBody.referenceBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask), CoreId()))
                    : BlockCalls.ReturnBurn(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody()); // #888: the body reads the planet's frame for the turn direction
                // A return aimed at no landing clears the last one (#892): a later reentry mustn't trim toward it.
                MissionDriver.RecordLandingTarget(BlockCalls.LandingTargetOf(ask, vessel.mainBody.referenceBody.bodyName), vessel);
                return KosValues.FromPlain(node);
            }, () => node.ContainsKey("dv")
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); {5} Pe {6:0} m, inclination {7:0.00}, leaving the moon at UT {8:0}{9}{10}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["planet"], node["pe_predicted"], node["inc_predicted"], node["soi_ut"],
                    ((bool)node["from_grid"] ? " (from the grid)" : "") + (node["finished_by"] as string == "" ? "" : " (the SQP stopped short: finished by " + node["finished_by"] + ")")
                        + ((double)node["error_km"] < 0 ? "" : string.Format(CultureInfo.InvariantCulture, "; aimed {0:0.00} km from the target, +{1:0.00} m/s, spread {2:0} km", node["error_km"], node["aim_dv"], node["spread_km"])),
                    node["error"] as string == "" ? "" : "; " + node["error"]) + PlanLine(node)
                : (string)node["error"]);
        }

        /// <summary>The planned-orbit line Core wrote for the call (#791), after the log's own account; "" when the call gave none.</summary>
        private static string PlanLine(System.Collections.Generic.IDictionary<string, object> result)
        {
            object line;
            return result.TryGetValue("plan_log", out line) ? " " + line : "";
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
                var ask = KosValues.ToDictionary(input);
                var vessel = shared.Vessel;
                var snapshot = SnapshotReader.ReadForMission(vessel);
                object target;
                ask.TryGetValue("target", out target);
                var planet = BodyNamed(target as string);
                // #915: a trip home with a landing target aims its correction there: the air of the planet aimed at and the craft reentry flies, read as ENTRYTRIM reads them.
                if (planet != null && BlockCalls.AimsHomeArrival(ask, snapshot))
                {
                    var uids = new System.Collections.Generic.Dictionary<uint, string>();
                    foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
                    SnapshotReader.ReadEngineFuel(snapshot, vessel);
                    ask["craft"] = BlockCalls.EntryCraftCall(new System.Collections.Generic.Dictionary<string, object> { { "core", ask.ContainsKey("core") ? ask["core"] : "" }, { "body", planet.bodyName }, { "stage_limit", ask.ContainsKey("stage_limit") ? ask["stage_limit"] : 0.0 } }, snapshot,
                        MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence, MissionDriver.RunningChild, id => uids.ContainsKey(id) ? uids[id] : null, (key, why) => { });
                    step = BlockCalls.TransferStep(ask, snapshot, new PlanningBody(), new ReentryAir(planet), new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask), CoreId()));
                }
                else step = BlockCalls.TransferStep(ask, snapshot);
                // Core's record of the plane it aims for, for capture (#788). A timing ask aims for nothing.
                if (step.ContainsKey("aimed_plane")) MissionDriver.RecordAimedPlane((string)step["aimed_plane"]);
                // #880: a trip home from another planet aims at the default landing, for the Late Trim.
                if (step.ContainsKey("lat")) MissionDriver.RecordLandingTarget(BlockCalls.LandingTargetOf(step, (string)step["landing_body"]), vessel);
                return KosValues.FromPlain(step);
            }, () => (string)step["error"] != "" ? (string)step["error"]
                : string.Format(CultureInfo.InvariantCulture, "{0} {1} at UT {2:0.0}, prograde {3:0.00}, normal {4:0.00}, radial {5:0.00} m/s; next {6}; {7}",
                    step["do"], step["kind"], step["ut"], step["prograde"], step["normal"], step["radial"],
                    ((System.Collections.Generic.IDictionary<string, object>)step["carry"])["phase"], KosValues.Format(step["report"])) + PlanLine(step));
        }

        private Lexicon CaptureStep(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> step = null;
            return (Lexicon)Guarded("CAPTURESTEP", () =>
            {
                step = BlockCalls.CaptureStep(KosValues.ToDictionary(input), SnapshotReader.ReadForMission(shared.Vessel));
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

        /// <summary>The body of that name, or null.</summary>
        private static CelestialBody BodyNamed(string name)
        {
            return FlightGlobals.Bodies.FirstOrDefault(b => string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase));
        }

        private Lexicon CorrectionBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> node = null;
            return (Lexicon)Guarded("CORRECTIONBURN", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                // Aimed at a landing (#793), the predictor flies the air of the planet aimed at: the craft may still be at the moon, or round the star. No such body: Core says why.
                object target;
                ask.TryGetValue("target", out target);
                var planet = BodyNamed(target as string);
                node = ask.ContainsKey("lat") && planet != null
                    ? BlockCalls.CorrectionBurn(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody(), new ReentryAir(planet), new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask), CoreId()))
                    : BlockCalls.CorrectionBurn(ask, SnapshotReader.ReadForMission(vessel));
                if (ask.ContainsKey("lat") && planet != null) MissionDriver.RecordLandingTarget(BlockCalls.LandingTargetOf(ask, planet.bodyName), vessel);
                return KosValues.FromPlain(node);
            }, () => (!node.ContainsKey("dv") ? (string)node["error"]
                : node.ContainsKey("distance_predicted")
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); closest approach {5:0.0} m at {6:0.000} m/s{7}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["distance_predicted"], node["relative_speed_predicted"],
                    node["error"] as string == "" ? "" : "; " + node["error"])
                : string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); Pe {5:0} m, inclination {6:0.00}{7}{8}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["pe_predicted"], node["inc_predicted"],
                    node.ContainsKey("error_km") ? string.Format(CultureInfo.InvariantCulture, "; aimed {0:0.00} km from the target", node["error_km"]) : "",
                    node["error"] as string == "" ? "" : "; " + node["error"])) + AimLine(node) + PlanLine(node));
        }

        /// <summary><c>ADDONS:CLAUDIA:ENTRYTRIM</c> (#892): <see cref="BlockCalls.EntryTrim"/> with this planet's air and the craft reentry flies; the log line is the decision.</summary>
        private Lexicon EntryTrimCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> fix = null;
            return (Lexicon)Guarded("ENTRYTRIM", () =>
            {
                try
                {
                    var vessel = shared.Vessel;
                    var ask = KosValues.ToDictionary(input);
                    var uids = new System.Collections.Generic.Dictionary<uint, string>();
                    foreach (var part in vessel.parts) uids[part.persistentId] = part.flightID.ToString(CultureInfo.InvariantCulture);
                    var snapshot = SnapshotReader.ReadForMission(vessel);
                    SnapshotReader.ReadEngineFuel(snapshot, vessel);
                    // The craft this Entry flies, read here and not through ENTRYCRAFT's call: that records the craft aimed with, which ENTRYFINISH compares this one against (#814).
                    var entryCraft = BlockCalls.EntryCraftCall(new System.Collections.Generic.Dictionary<string, object> { { "home", ask.ContainsKey("home") ? ask["home"] : "" }, { "core", ask.ContainsKey("core") ? ask["core"] : "" }, { "body", vessel.mainBody.bodyName }, { "stage_limit", ask.ContainsKey("stage_limit") ? ask["stage_limit"] : 0.0 } }, snapshot,
                        MissionDriver.BlockTestPlan ?? MissionDriver.PlanLegs, MissionDriver.CurrentSequence, MissionDriver.RunningChild, id => uids.ContainsKey(id) ? uids[id] : null, (key, why) => { });
                    ask["craft"] = entryCraft;
                    fix = BlockCalls.EntryTrim(ask, snapshot, new PlanningBody(), new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask), CoreId()));
                    return KosValues.FromPlain(fix);
                }
                finally
                {
                    if (fix == null || !(Convert.ToDouble(fix["wait"], CultureInfo.InvariantCulture) > 0)) BlockTestAim.Clear(); // the one reentry trim has read it, or failed before it did (#892); a "wait" asks again (#952)
                }
            }, () => string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.00} m/s); Pe {5:0} m{6}",
                fix["ut"], fix["prograde"], fix["normal"], fix["radial"], fix["dv"], fix["pe_predicted"],
                fix.ContainsKey("error_km") ? string.Format(CultureInfo.InvariantCulture, "; aimed {0:0.00} km from the target", fix["error_km"]) : "") + AimLine(fix) + PlanLine(fix));
        }

        /// <summary>#817: why an aimed correction stopped where it did (its aim_note, with the miss and the delta-v left), on the block call line; "" when it says nothing.</summary>
        private static string AimLine(System.Collections.Generic.IDictionary<string, object> node)
        {
            object note;
            return node.TryGetValue("aim_note", out note) && note as string != "" ? "; " + note : "";
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

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BURNTHROUGH</c> (#932, ADR 0014): launch's burn-through decision (<see cref="LaunchBurnThrough.Call"/>), pure, called
        /// at the first cutoff and on every tick of a burn-through. KSP.log has a line for the first call, one every 30 s of re-checks with the slowest call's time (ADR 0014 guardrail 6), and one for the re-check that ends it. A failure (logged by Guarded) coasts, as a launch always did.
        /// </summary>
        private Lexicon BurnThrough(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> decision = null;
            var burning = new StringValue("burning");
            var rechecking = input != null && input.ContainsKey(burning) && input[burning] is BooleanValue && ((BooleanValue)input[burning]).Value;
            Func<string> describe = () => string.Format(CultureInfo.InvariantCulture, "burn_through {0}: half burn {1:0.0} s for {2:0.0} m/s + turn {3:0.0} s for {6:0.0} deg against {7:0.0} s left as launch ends ({4:0.0} s to apoapsis less {8:0.0} s climb){5}",
                decision["burn_through"], decision["half_burn"], decision["dv"], decision["turn_time"], decision["eta_ap"],
                decision["error"] as string == "" ? "" : " (" + decision["error"] + ")", decision["angle"], decision["time_left"], decision["climb"]);
            var answer = Guarded("BURNTHROUGH", () =>
            {
                decision = LaunchBurnThrough.Call(KosValues.ToDictionary(input));
                return KosValues.FromPlain(decision);
            }, describe, rechecking ? 30 : 0);
            // The re-check that ends the burn-through is always logged, however Guarded's 30 s falls.
            if (rechecking && decision != null && !(bool)decision["burn_through"])
            {
                // The periapsis and fell-back ends are not a coast: no half burn or time left to show (half burn is -1 there).
                var fail = decision["fail"] as string;
                var ended = !(bool)decision["done"] ? "the coast works again: " + describe()
                    : fail != "" ? "fell back, launch fails: " + fail
                    : "periapsis clear of the air, launch ends";
                Debug.Log(ModInfo.LogTag + " block call BURNTHROUGH ended it: " + ended);
            }
            return decision != null ? (Lexicon)answer : (Lexicon)KosValues.FromPlain(LaunchBurnThrough.Call(null));
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BURNTIME</c> (#935, ADR 0014): the one burn-timing rule (<see cref="StagedBurnTime.Call"/>), pure, asked a few times a burn
        /// by <c>claudia_burn_timing</c>, never per tick; the line's a diagnostic of the figures and the call's time (Guarded).
        /// </summary>
        private Lexicon BurnTimeCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> timing = null;
            var answer = Guarded("BURNTIME", () =>
            {
                timing = StagedBurnTime.Call(KosValues.ToDictionary(input));
                return KosValues.FromPlain(timing);
            }, () => string.Format(CultureInfo.InvariantCulture, "burn_time {0:0.0} s, half burn {1:0.0} s{2}",
                timing["burn_time"], timing["half_burn"], timing["error"] as string == "" ? "" : " (" + timing["error"] + ")"));
            // A failure (logged by Guarded) has no figures: no thrust, as burn_time -1 says.
            return timing != null ? (Lexicon)answer : (Lexicon)KosValues.FromPlain(StagedBurnTime.Call(null));
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
        /// <c>ADDONS:CLAUDIA:LOWCHARGE</c> (#942, ADR 0014): what low charge does to a <c>reentry</c> with the periapsis in the air (<see cref="BlockCalls.LowCharge"/>),
        /// pure. Called every physics tick, so its time is logged at most once a second (guardrail 6). The block reads every key: a failure (logged by Guarded) answers as full charge, so it neither acts nor aborts.
        /// </summary>
        private Lexicon LowChargeCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> low = null;
            var answer = Guarded("LOWCHARGE", () =>
            {
                low = BlockCalls.LowCharge(KosValues.ToDictionary(input));
                return KosValues.FromPlain(low);
            }, () => string.Format(CultureInfo.InvariantCulture, "carry on {0}, step {1}", low["carry_on"], low["step"]), 1);
            return low != null ? (Lexicon)answer : (Lexicon)KosValues.FromPlain(BlockCalls.LowCharge(null));
        }

        /// <summary><c>ADDONS:CLAUDIA:BRAKEHEIGHTS</c> (#954, ADR 0014): <see cref="BlockCalls.BrakeHeights"/>, sampled 4 times a second, so its time is logged at most once a second (guardrail 6). A failure answers -1 for both heights, as the block reads both.</summary>
        private Lexicon BrakeHeightsCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> heights = null;
            var answer = Guarded("BRAKEHEIGHTS", () =>
            {
                heights = BlockCalls.BrakeHeights(KosValues.ToDictionary(input));
                return KosValues.FromPlain(heights);
            }, () => string.Format(CultureInfo.InvariantCulture, "brake at {0:0} m, last resort at {1:0} m", heights["brake_h"], heights["last_h"]), 1);
            return heights != null ? (Lexicon)answer : (Lexicon)KosValues.FromPlain(BlockCalls.BrakeHeights(null));
        }

        /// <summary>The time (UT, s) this processor's powered finish first read its called chutes all fully open, for <see cref="BrakeTickCall"/>; -1 while one is really opening or none is open, and kept through a called chute still packed.</summary>
        private double brakeOpenUt = -1;

        /// <summary>How many of this processor's chutes read fully open last tick, for <see cref="BrakeTickCall"/>: one more starts <see cref="brakeOpenUt"/> again.</summary>
        private double brakeOpenCount;

        /// <summary>This processor's powered finish has read its open chutes settled, for <see cref="BrakeTickCall"/>; held until a chute is really opening again.</summary>
        private bool brakeSettled;

        /// <summary>The open time and settled reading set aside while a chute reads semi, for <see cref="BrakeTickCall"/>: a chute that is then cut gives them back (#960).</summary>
        private double brakeKeptUt = -1;
        private bool brakeKeptSettled;
        private double brakeKeptDrogues, brakeKeptMains;

        /// <summary>The time (UT, s) each set of this processor's powered finish was first called, for <see cref="BrakeTickCall"/>; -1 while it isn't.</summary>
        private double brakeDrogueCallUt = -1, brakeMainCallUt = -1;

        /// <summary>The last sample of the fall's speed for <see cref="BrakeTickCall"/> (UT, s; -1 for none, and m/s) and the slowing worked out from it, m/s².</summary>
        private double brakeSampleUt = -1, brakeSampleSpeed, brakeDecel;

        /// <summary>When this processor's last resort first burned, when its chute states first failed to read and when the burn's next trace line is due (UT, s; -1 for none), for <see cref="BrakeTickCall"/>.</summary>
        private double brakeLastResortUt = -1, brakeUnreadUt = -1, brakeTraceUt = -1;

        /// <summary>When this processor's chutes all first read fully open (UT, s; -1 while one isn't), and whether its braking has begun the final wait under them, for <see cref="BrakeTickCall"/> (#969).</summary>
        private double brakeAllOpenUt = -1;
        private bool brakeFinal;

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BRAKETICK</c> (#954, ADR 0014): <see cref="BlockCalls.BrakeTick"/> with the live state of the Chute Group's parachutes (<c>uids</c>), read here as Core has no game.
        /// Called every physics tick, so its time is logged at most once a second (guardrail 6). A failure asks Core with <c>chutes_unread</c>: no burn and no thrust until it has failed for a second, then the chutes that are out are cut and the landing goes on the engine (#954; there is no braking by radar under a canopy).
        /// The times the chutes first read fully open and each set was first called are held here between ticks; each goes back to -1 on any tick it no longer holds, so no flight inherits it from the last.
        /// </summary>
        private Lexicon BrakeTickCall(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> tick = null;
            var answer = Guarded("BRAKETICK", () =>
            {
                var ask = BrakeAsk(input);
                var uids = ask.ContainsKey("uids") ? ((System.Collections.Generic.IEnumerable<object>)ask["uids"]).Select(u => Convert.ToString(u, CultureInfo.InvariantCulture)).ToList() : new System.Collections.Generic.List<string>();
                ask["chutes"] = ReentryCraft.ChuteStates(shared.Vessel, uids);
                tick = BlockCalls.BrakeTick(ask);
                HoldBrake(tick);
                NoteBrake(tick);
                return KosValues.FromPlain(tick);
            }, () => string.Format(CultureInfo.InvariantCulture, "start {0} ({1}), chutes {2}, throttle {3:0.00} (zero: {4}), rate {5:0.0} deg/s", tick["start"], tick["why"], tick["chutes"], tick["throttle"], tick["zero_why"], tick["rate_deg"]), 1);
            if (tick != null) return (Lexicon)answer;
            var blind = BrakeAsk(input);
            blind["chutes_unread"] = true;
            tick = BlockCalls.BrakeTick(blind);
            HoldBrake(tick);
            return (Lexicon)KosValues.FromPlain(tick);
        }

        /// <summary>The block's ask with the time and what this processor holds between ticks, for <see cref="BrakeTickCall"/>.</summary>
        private System.Collections.Generic.IDictionary<string, object> BrakeAsk(Lexicon input)
        {
            var ask = KosValues.ToDictionary(input);
            ask["now"] = Planetarium.GetUniversalTime();
            ask["open_ut"] = brakeOpenUt;
            ask["open_count"] = brakeOpenCount;
            ask["settled"] = brakeSettled;
            ask["kept_ut"] = brakeKeptUt;
            ask["kept_settled"] = brakeKeptSettled;
            ask["kept_drogues"] = brakeKeptDrogues;
            ask["kept_mains"] = brakeKeptMains;
            ask["drogue_call_ut"] = brakeDrogueCallUt;
            ask["main_call_ut"] = brakeMainCallUt;
            ask["sample_ut"] = brakeSampleUt;
            ask["sample_speed"] = brakeSampleSpeed;
            ask["decel"] = brakeDecel;
            ask["last_resort_ut"] = brakeLastResortUt;
            ask["unread_ut"] = brakeUnreadUt;
            ask["trace_ut"] = brakeTraceUt;
            ask["all_open_ut"] = brakeAllOpenUt;
            ask["final"] = brakeFinal;
            return ask;
        }

        /// <summary>Keeps the times and readings Core handed back for the next tick, for <see cref="BrakeTickCall"/>.</summary>
        private void HoldBrake(System.Collections.Generic.IDictionary<string, object> tick)
        {
            brakeOpenUt = (double)tick["open_ut"];
            brakeOpenCount = (double)tick["open_count"];
            brakeSettled = (bool)tick["settled"];
            brakeKeptUt = (double)tick["kept_ut"];
            brakeKeptSettled = (bool)tick["kept_settled"];
            brakeKeptDrogues = (double)tick["kept_drogues"];
            brakeKeptMains = (double)tick["kept_mains"];
            brakeDrogueCallUt = (double)tick["drogue_call_ut"];
            brakeMainCallUt = (double)tick["main_call_ut"];
            brakeSampleUt = (double)tick["sample_ut"];
            brakeSampleSpeed = (double)tick["sample_speed"];
            brakeDecel = (double)tick["decel"];
            brakeLastResortUt = (double)tick["last_resort_ut"];
            brakeUnreadUt = (double)tick["unread_ut"];
            brakeTraceUt = (double)tick["trace_ut"];
            brakeAllOpenUt = (double)tick["all_open_ut"];
            brakeFinal = (bool)tick["final"];
        }

        /// <summary>Notes for <c>reentry</c> what Core says changed this tick (#969: the thrust stopped for the final burn), as <c>BLOCKNOTE</c> would: KSP.log only. Written here, so the block spends no line on it.</summary>
        private void NoteBrake(System.Collections.Generic.IDictionary<string, object> tick)
        {
            var note = tick["note"] as System.Collections.Generic.IDictionary<string, object>;
            var bridge = KosBridge.Instance;
            if (note != null && note.Count > 0 && shared.KSPPart != null && bridge != null) bridge.OnBlockTrace(shared.KSPPart.persistentId, "reentry", note, false);
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
            ReentryCraft craft = null;
            return (Lexicon)Guarded("DEORBITBURN", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                craft = new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask), CoreId());
                found = BlockCalls.DeorbitBurn(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody(), new ReentryAir(vessel.mainBody), craft);
                MissionDriver.RecordLandingTarget(BlockCalls.LandingTargetOf(ask, vessel.mainBody.bodyName), vessel);
                // #974: another pass flown than find_landing_site's is the block's note too, as the block itself would write it.
                object passNote;
                if (found.TryGetValue("pass_note", out passNote) && shared.KSPPart != null && KosBridge.Instance != null)
                    KosBridge.Instance.OnBlockTrace(shared.KSPPart.persistentId, "deorbit", new System.Collections.Generic.Dictionary<string, object> { { "phase", "pass" }, { "note", passNote } }, false);
                return KosValues.FromPlain(found);
            }, () => Landing(found) + CraftLine(craft) + PlanLine(found));
        }

        /// <summary>The part running this script, whose side of a docking is the Mission's own (#926); null when the call has no processor.</summary>
        private uint? CoreId()
        {
            return shared.KSPPart == null ? (uint?)null : shared.KSPPart.persistentId;
        }

        private Lexicon ReentryImpact(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> landing = null;
            ReentryCraft craft = null;
            return (Lexicon)Guarded("REENTRYIMPACT", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                // Given a path round another body (return's, with the craft still at the moon), that body's air (#793).
                object named;
                ask.TryGetValue("orbit_body", out named);
                craft = new ReentryCraft(vessel, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask), CoreId());
                UnityEngine.Debug.Log(ClaudiaKerman.Core.ModInfo.LogTag + " REENTRYIMPACT read: " + craft.Diagnosis());
                landing = BlockCalls.ReentryImpact(ask, SnapshotReader.ReadForMission(vessel), new PlanningBody(),
                    new ReentryAir(BodyNamed(named as string) ?? vessel.mainBody), craft);
                return KosValues.FromPlain(landing);
            }, () => Landing(landing) + CraftLine(craft) + PlanLine(landing));
        }

        /// <summary>The craft the prediction flew, for the log (#868): its mass, drag area held retrograde at Mach 2, and the Ship Systems out now that it read stowed.</summary>
        private static string CraftLine(ReentryCraft craft)
        {
            if (craft == null) return "";
            return string.Format(CultureInfo.InvariantCulture, "; craft {0:0.000} t, drag area {1:0.00} m² at Mach 2, {2} Ship Systems read stowed",
                craft.MassKg / 1000, craft.DragArea(2, 1000), craft.StowedCount);
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
