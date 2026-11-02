// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Watches the game for the notable events of a run (a stage fired, a part destroyed or broken off, a part close to its
    /// heat limit, science collected) and reports each to Mission Control as a <see cref="RunEventSeen"/>, for the Debrief.
    /// Reports only while a sequence is Running and only for the Mission craft (the vessel the run flies, decided by
    /// <see cref="RunEventFilter"/>), so staging or science on another vessel after a switch is left out; the core keeps the log.
    /// </summary>
    public sealed class RunEventWatcher
    {
        private const float HeatSeconds = 0.5f;

        private readonly Action<MissionInput> _send;
        private readonly Func<bool> _running;
        private readonly Func<string> _craft;
        private readonly Func<string> _child;
        private readonly HashSet<uint> _hot = new HashSet<uint>();
        private float _nextHeat;
        private readonly List<JointCandidate> _candidates = new List<JointCandidate>();
        private double _lastExplained = double.NegativeInfinity;

        /// <summary>A joint break waiting for its re-check on a later frame.</summary>
        private struct JointCandidate
        {
            public Part Part;
            public int Frame;
            public double At;
        }

        public RunEventWatcher(Action<MissionInput> send, Func<bool> running, Func<string> craftVesselId, Func<string> runningChildVesselId)
        {
            _send = send;
            _running = running;
            _craft = craftVesselId;
            _child = runningChildVesselId;
            GameEvents.onStageActivate.Add(OnStage);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartJointBreak.Add(OnJointBreak);
            GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupled);
            GameEvents.OnExperimentDeployed.Add(OnExperiment);
        }

        /// <summary>Call each frame: parts near their heat limit are reported once per run.</summary>
        public void Update()
        {
            if (!_running())
            {
                _hot.Clear();
                _candidates.Clear();
                return;
            }
            ResolveCandidates();
            if (Time.realtimeSinceStartup < _nextHeat) return;
            _nextHeat = Time.realtimeSinceStartup + HeatSeconds;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || !IsCraft(vessel)) return;
            foreach (var part in vessel.Parts)
            {
                var percent = PartHeat.Percent(part.temperature, part.maxTemp, part.skinTemperature, part.skinMaxTemp);
                if (percent < RunEventLog.OverheatPercent || !_hot.Add(part.persistentId)) continue;
                Report(RunEventKind.Overheating, part.partInfo.title + " at " + percent.ToString("0") + "% of its heat limit");
            }
        }

        private bool IsCraft(Vessel vessel)
        {
            return vessel != null && RunEventFilter.Accepts(vessel.id.ToString("N"), _craft(), _child());
        }

        private bool Flying(Part part)
        {
            return _running() && part != null && IsCraft(part.vessel);
        }

        private void Report(RunEventKind kind, string text)
        {
            if (_running()) _send(new RunEventSeen(kind, text, Planetarium.GetUniversalTime()));
        }

        private void OnStage(int stage)
        {
            // Staging acts on the vessel the player is flying, so that is the vessel the stage belongs to.
            if (!IsCraft(FlightGlobals.ActiveVessel)) return;
            Report(RunEventKind.Staging, "stage " + stage + " fired");
            Separated(FlightGlobals.ActiveVessel);
            _lastExplained = Time.realtimeSinceStartup;
        }

        private void OnDecoupled(Vessel original, Vessel made)
        {
            // A decoupler fired by a block (drop_parts, drop_empty) doesn't stage, but drops delta-v all the same (#605).
            if (IsCraft(original)) Separated(original);
            _lastExplained = Time.realtimeSinceStartup;
        }

        /// <summary>A stage or decoupler fired on the craft: Mission Control measures what stock's delta-v left loses across it (#605).</summary>
        private void Separated(Vessel vessel)
        {
            // The throttle as it is now (#615), not the last snapshot's: a script can cut it and stage in the same moment.
            if (_running()) _send(new CraftSeparated(vessel.ctrlState.mainThrottle));
        }

        private void OnPartDie(Part part)
        {
            if (Flying(part)) Report(RunEventKind.PartLost, part.partInfo.title + " was destroyed");
        }

        private void OnJointBreak(PartJoint joint, float breakForce)
        {
            // Only a candidate: the part may stay attached, or leave because a decoupler fired. Re-checked on a later frame.
            if (joint != null && Flying(joint.Child) && !_candidates.Exists(c => c.Part == joint.Child))
                _candidates.Add(new JointCandidate { Part = joint.Child, Frame = Time.frameCount, At = Time.realtimeSinceStartup });
        }

        private void ResolveCandidates()
        {
            for (var i = _candidates.Count - 1; i >= 0; i--)
            {
                var c = _candidates[i];
                if (Time.frameCount <= c.Frame) continue;
                _candidates.RemoveAt(i);
                if (c.Part == null) continue; // destroyed: onPartDie reports it
                var stillOnCraft = c.Part.vessel != null && IsCraft(c.Part.vessel);
                if (PartLossRule.BrokeOff(stillOnCraft, PartLossRule.Explains(_lastExplained, c.At)))
                    Report(RunEventKind.PartLost, c.Part.partInfo.title + " broke off");
            }
        }

        private void OnExperiment(ScienceData data)
        {
            // One line per result: the deployed experiment is logged here and the science-received event for the same result is not
            // watched. The event carries no vessel; an experiment runs on the vessel the player is flying.
            if (data != null && IsCraft(FlightGlobals.ActiveVessel)) Report(RunEventKind.Science, data.title + " collected (" + data.dataAmount.ToString("0.#") + " Mits)");
        }
    }
}
