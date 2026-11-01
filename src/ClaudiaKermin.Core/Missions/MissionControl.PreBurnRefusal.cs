// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// A run refused before any burn (<see cref="PreBurnRefusal"/>, #579) goes straight to the next Sequence Plan. The mod writes
    /// the Debrief card itself (Off-Nominal, the block's reason, the margin bar and the mod's Margin check from the snapshot,
    /// the Mission Goal's steps as they stand), logs it in <c>plans.jsonl</c> with <c>by: "mod"</c>, and commits a short
    /// user-side fact to the transcript, from which <see cref="MissionLogRebuild"/> builds the card again. No margin recheck and
    /// no Debrief request: the next request is the Sequence Plan, whose text carries the run report and says the craft is
    /// unchanged. The config's <c>mod_debriefs_refusals = false</c> sends every run to Claudia's Debrief as before.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The Mission's craft at the current run's start; null when there was no snapshot of it. Not saved: a run never survives a load.</summary>
            public OrbitFix RunStartOrbit;
        }

        /// <summary>The run just ended: true when it was a pre-burn refusal the mod debriefed itself, and the next Sequence Plan is asked for.</summary>
        private bool DebriefedRefusal(Mission mission, RunOutcome outcome)
        {
            // A Paused run's Debrief waits for Resume, a hand-back's is of a manual stretch, and a Child Vessel's is judged from its own report.
            if (!_config.ModDebriefsRefusals || mission.Stage != MissionStage.Running || mission.HandBack || mission.RunningChild.Length > 0) return false;
            // The fact joins the conversation now, so no other turn may be out or waiting: it would commit after the fact it was sent without.
            if (mission.Pending != null || mission.Queue.Count > 0 || mission.Compacting != null) return false;
            var plan = mission.PendingSequence;
            var snapshot = MissionVesselSnapshot(mission);
            if (plan == null || snapshot == null) return false;
            // The real start: a stage dropped at zero throttle (#605) still changed the craft.
            var reason = PreBurnRefusal.Reason(outcome, mission.RunStartOrbit, OrbitFix.Of(snapshot), mission.RunStartDeltaVLeft + mission.RunDroppedDeltaV,
                LiveReadings.DeltaVLeftOf(snapshot), mission.Crossings.Count > 0);
            if (reason == null) return false;

            // The fact goes into the transcript first, so a quit before the next plan comes back still rebuilds the card.
            mission.Conversation.Add(Message.UserText(PreBurnRefusal.Fact(plan.Name, reason)));
            WriteTranscript(mission);
            Event(mission, "mod_debrief", plan.Name + ": " + reason);

            var debrief = PreBurnRefusal.Debrief(reason);
            var check = MarginCheckOf(mission, snapshot);
            var gate = new DebriefGate { Done = true, RecheckSummary = check.Line() };
            gate.RecheckFindings.AddRange(check.FindingLines());
            if (_config.MarginRecheck == MarginRecheckMode.Always || (_config.MarginRecheck == MarginRecheckMode.Auto && check.Level != MarginLevel.Enough))
                gate.RecheckSkipped = PreBurnRefusal.RecheckSkipped;
            mission.Gate = gate;
            var entry = AddCard(mission, LogKind.Debrief, "Debrief: " + debrief.Outcome + TakeStopReason(mission), MissionCards.DebriefBody(debrief, ""), PreBurnRefusal.CardWhy);
            entry.Outcome = debrief.Outcome;
            entry.Debrief = DetailOf(mission, debrief);
            LogModDebrief(mission, debrief);

            mission.Stage = MissionStage.Sequencing;
            mission.SequenceCounted = false;
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                UserText = PreBurnRefusal.NextTurnLead + MissionPrompts.RunReport(plan, outcome, "", mission.Blocks) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.SequenceNotice,
            });
            return true;
        }

        /// <summary>The mod's Debrief on the latest plan's line, with no request and nothing spent.</summary>
        private void LogModDebrief(Mission mission, Debrief debrief)
        {
            try
            {
                PlanLog.SetDebrief(mission.Folder, debrief.Outcome, new TurnStats(), "", null, false, PlanLog.ByMod, mission.Blocks.Json(null));
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The log is best effort; the Mission goes on.
            }
        }
    }
}
