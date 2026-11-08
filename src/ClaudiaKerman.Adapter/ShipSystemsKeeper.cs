// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// The game side of Ship Systems (#756): reads the craft Claudia is flying for <see cref="ShipSystemsRule"/>, moves the parts the
    /// rule's transitions ask for, and records each in the Mission's events and in KSP.log. Only the active vessel while it is the
    /// Mission's craft (or the Child Vessel a run flies), never in Manual Control; when that stops being true the rule starts over.
    /// </summary>
    public sealed class ShipSystemsKeeper
    {
        private const float ReadSeconds = 0.2f;

        /// <summary>The keeper of this game session, for the block cue <c>ADDONS:CLAUDIA:SHIPSYSTEMS</c>.</summary>
        public static ShipSystemsKeeper Current { get; private set; }

        private readonly Action<MissionInput> _send;
        private readonly Func<MissionView> _view;
        private readonly Func<bool> _managed;
        private ShipSystemsRule _rule;
        private Vessel _flown;
        private float _nextRead;
        private bool _cued;
        // A transition that met a part on its way, to ask again once nothing is moving.
        private ShipSystemsTransition _again;
        // The block's stow wait: when its lift-off cue came, until its parts are in or the cap; null with no wait in hand.
        private double? _stowFrom;

        public ShipSystemsKeeper(Action<MissionInput> send, Func<MissionView> view, Func<bool> managed)
        {
            _send = send;
            _view = view;
            _managed = managed;
            Current = this;
        }

        /// <summary>A save was loaded: what the rule remembers belongs to another timeline.</summary>
        public void Reset()
        {
            _rule = null;
            _flown = null;
            _cued = false;
            _again = null;
            _stowFrom = null;
        }

        /// <summary>Call each frame in any scene.</summary>
        public void Update(bool flight)
        {
            var vessel = flight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (!Flown(vessel))
            {
                Reset();
                return;
            }
            if (_rule == null || !ReferenceEquals(vessel, _flown))
            {
                Reset();
                _rule = new ShipSystemsRule();
                _flown = vessel;
            }
            if (!_cued && Time.realtimeSinceStartup < _nextRead) return;
            _nextRead = Time.realtimeSinceStartup + ReadSeconds;
            _cued = false;
            // Nothing here may stop the flight: the rule has latched, so a transition that fails is logged and lost.
            try
            {
                var transition = _rule.Step(Read(vessel), _managed());
                if (transition == null && _again != null && !ReadParts(vessel, null).Any(p => p.State == ShipSystemState.Moving && p.Kind != ShipSystemKind.Light)) transition = _again;
                if (transition != null) Carry(transition, vessel);
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " Ship Systems failed: " + e);
            }
        }

        private bool Flown(Vessel vessel)
        {
            var view = _view();
            return vessel != null && view.CanEndMission && view.Stage != MissionStage.ManualControl
                && RunEventFilter.Accepts(vessel.id.ToString("N"), view.CraftVesselId, view.RunningChildVesselId);
        }

        /// <summary>
        /// A block's cue from <paramref name="vessel"/>: "liftoff", "final_descent" or "entry", or "stowing" to ask after the lift-off
        /// stow. What it moves happens at the next <see cref="Update"/>. The answer is for the block's stow wait, done at once when no
        /// stow is in hand. Any other vessel, no Mission or the setting off is no action.
        /// </summary>
        public ShipSystemsStowAnswer Cue(string cue, Vessel vessel)
        {
            if (!Manages(vessel)) return ShipSystemsStowAnswer.Nothing;
            var ut = Planetarium.GetUniversalTime();
            switch (cue)
            {
                case "liftoff":
                    _rule.Cue(ShipSystemsCue.LiftOff);
                    _cued = true;
                    if (!ShipSystemsRule.StowsAtLiftOff(Read(vessel))) return ShipSystemsStowAnswer.Nothing;
                    _stowFrom = ut;
                    return ShipSystemsStowWait.Step(ut, ut, 1);
                case "stowing":
                    if (!_stowFrom.HasValue) return ShipSystemsStowAnswer.Nothing;
                    // While the cue waits for the next Update, it counts as one still out.
                    var answer = ShipSystemsStowWait.Step(_stowFrom.Value, ut, _cued ? 1 : StillOut(vessel));
                    if (answer.Done) _stowFrom = null;
                    return answer;
                case "final_descent":
                    _rule.Cue(ShipSystemsCue.FinalDescent);
                    _cued = true;
                    return ShipSystemsStowAnswer.Nothing;
                case "entry":
                    _rule.Cue(ShipSystemsCue.EntryStart);
                    _cued = true;
                    return ShipSystemsStowAnswer.Nothing;
                default:
                    Debug.LogWarning(ModInfo.LogTag + " unknown Ship Systems cue '" + cue + "'");
                    return ShipSystemsStowAnswer.Nothing;
            }
        }

        /// <summary>
        /// The persistentIds of <paramref name="vessel"/>'s parts its entry meets the air with stowed (#868, <see cref="ShipSystemsMoves.StowedForEntry"/>):
        /// none unless this keeper would act on the block's entry cue for it.
        /// </summary>
        public static ICollection<uint> StowedForEntry(Vessel vessel)
        {
            var keeper = Current;
            return ShipSystemsMoves.StowedForEntry(ReadParts(vessel, null), keeper != null && keeper.Manages(vessel));
        }

        /// <summary>Whether this keeper acts on a block's cue from <paramref name="vessel"/>: the craft it flies, with the setting on.</summary>
        private bool Manages(Vessel vessel)
        {
            return _rule != null && ReferenceEquals(vessel, _flown) && _managed();
        }

        private static ShipSystemsReading Read(Vessel vessel)
        {
            var body = vessel.mainBody;
            var orbit = vessel.orbit;
            var top = body.atmosphere ? body.atmosphereDepth : 0;
            return new ShipSystemsReading
            {
                Ut = Planetarium.GetUniversalTime(),
                Situation = Situation(vessel.situation),
                Body = body.bodyName,
                AtmosphereTop = top,
                Periapsis = orbit.PeA,
                Altitude = vessel.altitude,
                Radar = vessel.radarAltitude,
                VerticalSpeed = vessel.verticalSpeed,
                SurfaceSpeed = vessel.srfSpeed,
                SecondsToAir = top > 0
                    ? ShipSystemsRule.SecondsToAir(body.gravParameter, orbit.semiMajorAxis, orbit.eccentricity, body.Radius + vessel.altitude, vessel.verticalSpeed, body.Radius + top)
                    : null,
                EnginesOn = EnginesOn(vessel),
            };
        }

        private static ShipSystemsSituation Situation(Vessel.Situations situation)
        {
            switch (situation)
            {
                case Vessel.Situations.PRELAUNCH: return ShipSystemsSituation.Prelaunch;
                case Vessel.Situations.LANDED: return ShipSystemsSituation.Landed;
                case Vessel.Situations.SPLASHED: return ShipSystemsSituation.Splashed;
                case Vessel.Situations.FLYING: return ShipSystemsSituation.Flying;
                case Vessel.Situations.SUB_ORBITAL: return ShipSystemsSituation.SubOrbital;
                case Vessel.Situations.ORBITING: return ShipSystemsSituation.Orbiting;
                case Vessel.Situations.ESCAPING: return ShipSystemsSituation.Escaping;
                default: return ShipSystemsSituation.Other;
            }
        }

        private static bool EnginesOn(Vessel vessel)
        {
            foreach (var part in vessel.parts)
                foreach (var module in part.Modules)
                {
                    var engine = module as ModuleEngines;
                    if (engine != null && engine.EngineIgnited && engine.finalThrust > 0) return true;
                }
            return false;
        }

        private void Carry(ShipSystemsTransition transition, Vessel vessel)
        {
            var modules = new Dictionary<ShipSystemPart, PartModule>();
            var parts = ReadParts(vessel, modules);
            var hold = ShipSystemsHold.None;
            if (transition.Deployables == ShipSystemsDeployables.Extend && parts.Any(p => !p.CanRetract))
            {
                var view = _view();
                hold = ShipSystemsHold.Find(SnapshotReader.ReadForMission(vessel), view.PlanLegs, Math.Max(0, view.CurrentSequence), view.Remote, view.ProcessorPartId, transition.Landed);
            }
            var moves = ShipSystemsMoves.Of(transition, parts, hold);
            _again = moves.Unsettled ? new ShipSystemsTransition { Deployables = transition.Deployables, Why = transition.Why, Landed = transition.Landed } : null;
            foreach (var part in moves.Extend) Move(modules[part], true);
            foreach (var part in moves.Retract) Move(modules[part], false);
            if (transition.Lights != ShipSystemsLights.None) Lights(vessel, moves, modules, transition.Lights == ShipSystemsLights.On);
            if (moves.Text.Length == 0) return;
            Debug.Log(ModInfo.LogTag + " " + moves.Text + " at UT " + Planetarium.GetUniversalTime().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            _send(new ShipSystemsMoved(moves.Text));
        }

        /// <summary>A stuck part must not stop the others, or the flight.</summary>
        private static void Move(PartModule module, bool extend)
        {
            try
            {
                var deployable = (ModuleDeployablePart)module;
                if (extend) deployable.Extend();
                else deployable.Retract();
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't move " + module.part.partInfo.title + ": " + e.Message);
            }
        }

        /// <summary>The Lights group keeps the player's L key in step; a light it didn't reach is switched itself.</summary>
        private static void Lights(Vessel vessel, ShipSystemsMoves moves, Dictionary<ShipSystemPart, PartModule> modules, bool on)
        {
            vessel.ActionGroups.SetGroup(KSPActionGroup.Light, on);
            foreach (var part in on ? moves.LightsOn : moves.LightsOff)
            {
                var light = (ModuleLight)modules[part];
                if (light.isOn == on) continue;
                if (on) light.LightsOn();
                else light.LightsOff();
            }
        }

        private static int StillOut(Vessel vessel)
        {
            return ShipSystemsStowWait.StillOut(ReadParts(vessel, null));
        }

        private static List<ShipSystemPart> ReadParts(Vessel vessel, Dictionary<ShipSystemPart, PartModule> modules)
        {
            var parts = new List<ShipSystemPart>();
            foreach (var part in vessel.parts)
                foreach (var module in part.Modules)
                {
                    var read = ReadModule(part, module);
                    if (read == null) continue;
                    parts.Add(read);
                    if (modules != null) modules[read] = module;
                }
            return parts;
        }

        private static ShipSystemPart ReadModule(Part part, PartModule module)
        {
            var light = module as ModuleLight;
            if (light != null) return new ShipSystemPart { Id = part.persistentId, Kind = ShipSystemKind.Light, State = light.isOn ? ShipSystemState.Out : ShipSystemState.Stowed };
            var deployable = module as ModuleDeployablePart;
            if (!Managed(deployable)) return null;
            // A part shielded in a fairing or bay can't deploy: the line mustn't count it.
            var state = part.ShieldedFromAirstream ? ShipSystemState.Broken : State(deployable.deployState);
            return new ShipSystemPart { Id = part.persistentId, Kind = Kind(deployable), State = state, CanRetract = deployable.retractable };
        }

        /// <summary>A panel, antenna or radiator that moves: a fixed panel has no animation and nothing to extend. Nothing else is a Ship System (#756, scope).</summary>
        private static bool Managed(ModuleDeployablePart module)
        {
            return module != null && module.useAnimation
                && (module is ModuleDeployableSolarPanel || module is ModuleDeployableAntenna || module is ModuleDeployableRadiator);
        }

        /// <summary>For the Game Snapshot: this module is a Ship System that extends and can never retract.</summary>
        public static bool CannotRetract(PartModule module)
        {
            var deployable = module as ModuleDeployablePart;
            return Managed(deployable) && !deployable.retractable;
        }

        private static ShipSystemKind Kind(ModuleDeployablePart module)
        {
            if (module is ModuleDeployableSolarPanel) return ShipSystemKind.Panel;
            return module is ModuleDeployableAntenna ? ShipSystemKind.Antenna : ShipSystemKind.Radiator;
        }

        private static ShipSystemState State(ModuleDeployablePart.DeployState state)
        {
            switch (state)
            {
                case ModuleDeployablePart.DeployState.RETRACTED: return ShipSystemState.Stowed;
                case ModuleDeployablePart.DeployState.EXTENDED: return ShipSystemState.Out;
                case ModuleDeployablePart.DeployState.BROKEN: return ShipSystemState.Broken;
                default: return ShipSystemState.Moving;
            }
        }
    }
}
