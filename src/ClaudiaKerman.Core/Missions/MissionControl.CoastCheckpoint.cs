// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Coast Checkpoints (#775, GLOSSARY, the ADR 0004 amendment): a long coast of <c>transfer_to</c> or <c>return</c> may stop so the player can
    /// fly elsewhere. The block ends Nominal at one, or a far switch makes one while it only warps; the mod writes the Nominal Debrief
    /// and the resumed Sequence Plan, with no request, and the plan's soft Due Time and its alarm call the player back before the
    /// next correction. Back on the craft, the mod checks the trajectory: clean, the resume stands; drifted or lost, Claudia plans
    /// from the check's figures. A player who stays gets the resume at once (Auto Mode) or on one Approve.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The coast the running block is warping on (from its Encounter Watch), which a far switch can make a checkpoint; null otherwise. Not saved: a run never survives a load.</summary>
            public CoastToken Coast;
            /// <summary>The player has left the craft at a checkpoint and the check on return hasn't run. Not saved: a load checks again.</summary>
            public bool CheckPending;
            /// <summary>The last far-switch answer and the snapshot it was read from, so each view doesn't work out the path again.</summary>
            public GameSnapshot LeaveSnapshot;
            public string CannotLeave = "";
            /// <summary>What stops the running script's long coast from checkpointing (<see cref="CoastCheckpoints.LongCoastBlocker"/>, #778); "" when nothing does. Set at each run's start; not saved.</summary>
            public string CoastBlocked = "";
        }

        /// <summary>#778: the clause that says why this plan's long coast can't checkpoint, "" for a plan that can, a short coast, a manual stretch, a Child Vessel's run or the mod's own resume.</summary>
        private string CoastBlock(Mission mission, SequencePlan plan)
        {
            if (plan.Manual || !FliesCraft(mission, plan.Vessel) || CoastPlanToken(plan) != null) return "";
            return CoastCheckpoints.LongCoastBlocker(plan.Script, MissionVesselSnapshot(mission));
        }

        /// <summary>The Sequence Plan card's text: the plan, then the mod's line when its long coast can't checkpoint.</summary>
        private string PlanCardText(Mission mission, SequencePlan plan)
        {
            var text = MissionCards.SequencePlanBody(plan, mission.VesselName, _ut, _config);
            var blocked = CoastBlock(mission, plan);
            if (blocked.Length > 0) text += "\n" + CoastCheckpoints.BlockerLine(blocked);
            // #867: the orbit it waits in crosses a moon.
            return CrossingHeld(mission, plan) ? text + "\n" + mission.Crossing.Blocker : text;
        }

        /// <summary>The block's wait armed its Encounter Watch: the coast it is on. An empty token: nothing a switch could checkpoint.</summary>
        private void CoastArmedSeen(CoastArmed armed)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission)) return;
            mission.Coast = CoastToken.Parse(armed.Token);
            mission.LeaveSnapshot = null;
        }

        /// <summary>The token of the plan in hand when it is the mod's own at a checkpoint; null for every other plan.</summary>
        private static CoastToken CoastPlanToken(SequencePlan plan)
        {
            return plan == null ? null : CoastToken.Parse(plan.CoastCheckpoint);
        }

        /// <summary>
        /// The one Due Time that doesn't hold the player or Auto Mode back (#775): a coast checkpoint's, with the player still in the craft
        /// (the script warps there on rails). Left, or not yet checked since, it holds as any Due Time does.
        /// </summary>
        private bool SoftDueTime(Mission mission, SequencePlan plan)
        {
            return CoastPlanToken(plan) != null && !mission.CheckPending && CraftActive;
        }

        // ---- At a checkpoint ----

        /// <summary>The run ended with its block at a checkpoint: the mod's Debrief and resume are owed, once no request is out. True: taken. A Paused run's Debrief is Claudia's, on Resume.</summary>
        private bool RunEndedAtCheckpoint(Mission mission, RunOutcome outcome)
        {
            if (mission.Stage != MissionStage.Running || CoastCheckpoints.Of(outcome) == null) return false;
            mission.Stage = MissionStage.Debriefing;
            // The player is already elsewhere nearby: the mod's resume waits for them, and the check with it.
            mission.CheckPending = ElsewhereInFlight;
            mission.Owed = Owed.CoastCheckpoint;
            mission.OwedOutcome = outcome;
            return true;
        }

        /// <summary>The owed checkpoint: clean, the mod writes it; with a Telemetry Check standing crossed, the Debrief is Claudia's as for any run. False: not yet.</summary>
        private bool TakeOwedCheckpoint(Mission mission)
        {
            if (mission.Queue.Count > 0 || mission.Compacting != null) return false;
            var outcome = mission.OwedOutcome;
            mission.Owed = Owed.None;
            mission.OwedOutcome = null;
            var token = CoastCheckpoints.Of(outcome);
            var plan = mission.PendingSequence;
            var resumed = token == null || plan == null ? null : CoastCheckpoints.Resumed(plan, token, _ut);
            if (resumed == null || MissionPrompts.RulesOutNominal(CrossingsText(mission)))
            {
                BeginRunDebrief(mission, outcome);
                return true;
            }
            AtCheckpoint(mission, token, plan, resumed, outcome);
            return true;
        }

        /// <summary>The Nominal Debrief the mod writes and the resumed Sequence Plan for the same step, its Due Time and alarm set; then Auto Mode may start it at once.</summary>
        private void AtCheckpoint(Mission mission, CoastToken token, SequencePlan plan, SequencePlan resumed, RunOutcome outcome)
        {
            var what = CoastCheckpoints.WhatHappened(token);
            var report = outcome == null ? "" : MissionPrompts.RunReport(plan, new RunOutcome(outcome.Kind, ReportWithoutToken(outcome.Report), outcome.ErrorText, outcome.Reason, outcome.WasAborted), "");
            // The fact goes into the transcript first, so a quit before the plan is approved still rebuilds the card.
            mission.Conversation.Add(Message.UserText(CoastCheckpoints.Fact(plan.Name, what, report)));
            WriteTranscript(mission);
            Event(mission, "mod_debrief", plan.Name + ": " + what);
            mission.Crossings.Clear();

            var debrief = CoastCheckpoints.Debrief(what);
            var snapshot = MissionVesselSnapshot(mission);
            if (snapshot != null)
            {
                var check = MarginCheckOf(mission, snapshot);
                var gate = new DebriefGate { Done = true, RecheckSummary = check.Line() };
                gate.RecheckFindings.AddRange(check.FindingLines());
                mission.Gate = gate;
            }
            var entry = AddCard(mission, LogKind.Debrief, "Debrief: " + debrief.Outcome + TakeStopReason(mission), MissionCards.DebriefBody(debrief, ""), CoastCheckpoints.CardWhy);
            entry.Outcome = debrief.Outcome;
            entry.Debrief = DetailOf(mission, debrief);
            LogModDebrief(mission, debrief);

            ClearWindow(mission);
            // The same step goes on: restored, so no window or ejection wait is worked out for it, and SequenceCounted stands.
            OfferSequencePlan(mission, resumed, true);
            LogPlan(mission, new Turn { Purpose = Purpose.SequencePlan }, resumed, null);
            Note(LogKind.Notice, "Coast Checkpoint: the next correction is due " + KerbinTime.Date(token.PointUt) + ", so " + plan.Name + " is due " + KerbinTime.Date(token.DueUt)
                + ". Fly other Missions meanwhile; a KSP alarm calls you back.");
            if (mission == _mission) ApproveInAdvance(mission);
        }

        private static IDictionary<string, object> ReportWithoutToken(IDictionary<string, object> report)
        {
            if (report == null) return null;
            return report.Where(p => p.Key != CoastCheckpoints.TokenKey).ToDictionary(p => p.Key, p => p.Value);
        }

        // ---- A far switch ----

        /// <summary>
        /// "" when a far switch now would be a clean checkpoint; otherwise why not, which the prompt warns of; null when the run isn't on a
        /// coast it could checkpoint at all (a burn, a turn, a block with no token), where the prompt is as it always was.
        /// </summary>
        private string CannotLeaveCraft(Mission mission)
        {
            if (mission.Stage != MissionStage.Running || !mission.RunId.HasValue) return null;
            // #778: a script that can't checkpoint says why, in place of the silence.
            if (mission.Coast == null || _ut >= mission.Coast.WaitUt) return mission.CoastBlocked.Length > 0 ? "this script " + mission.CoastBlocked : null;
            if (mission.Pending != null || mission.InFlight || mission.Queue.Count > 0 || mission.Compacting != null) return "a request to Claudia is out";
            // The mod's Debrief is Nominal: a run with a crossing is Claudia's to judge.
            if (mission.Crossings.Count > 0) return "a Telemetry Check was crossed during this run";
            var snapshot = MissionVesselSnapshot(mission);
            if (mission.LeaveSnapshot != null && ReferenceEquals(mission.LeaveSnapshot, snapshot)) return mission.CannotLeave;
            mission.LeaveSnapshot = snapshot;
            mission.CannotLeave = CoastCheckpoints.Resumed(mission.PendingSequence ?? new SequencePlan(), mission.Coast, _ut) == null
                ? "the script can't be resumed"
                : CoastCheckpoints.CannotLeave(snapshot, mission.Coast, _ut);
            return mission.CannotLeave;
        }

        /// <summary>The player chose to leave a coast that passed the safety test: the block stops, and it is a checkpoint like the block's own.</summary>
        private void LeaveAtCheckpoint(Mission mission)
        {
            var token = mission.Coast;
            var plan = mission.PendingSequence;
            var resumed = CoastCheckpoints.Resumed(plan, token, _ut);
            mission.SwitchingAway = true;
            mission.CheckPending = true;
            if (mission.RunningProcessor.HasValue) _commands.Add(new AbortScript(mission.RunningProcessor.Value, "left at a Coast Checkpoint", mission.RunId.Value));
            mission.RunId = null;
            mission.RunDeadlineUt = null;
            mission.RcsUse.Close(_ut);
            EndLive(mission);
            mission.Stage = MissionStage.Debriefing;
            AtCheckpoint(mission, token, plan, resumed, null);
        }

        // ---- The check on return ----

        /// <summary>
        /// After every input: the player leaving the craft at a checkpoint makes the check pending; back in the craft, with a Game Snapshot of it, the
        /// check runs, with no Claude call. Clean, the resume stands (Auto Mode starts it, a late one with its lateness in the log). Otherwise
        /// the resume is dropped and Claudia plans from the figures, for the same step.
        /// </summary>
        private void CheckCoastReturn()
        {
            var mission = _mission;
            if (mission == null || MissionOver || mission.Stage != MissionStage.AwaitingSequenceApproval) return;
            var token = CoastPlanToken(mission.PendingSequence);
            if (token == null) return;
            if (!mission.CheckPending)
            {
                if (ElsewhereInFlight || _scene != GameScene.Flight) mission.CheckPending = true;
                return;
            }
            if (!CraftActive || mission.SwitchingAway || mission.InFlight || mission.Pending != null) return;
            mission.CheckPending = false;
            var verdict = CoastCheckpoints.Judge(_snapshot, token, _ut);
            Event(mission, "coast_check", verdict.Kind + ": " + verdict.Line);
            if (verdict.Kind == CoastVerdictKind.Kept)
            {
                // Coasting past a sphere of influence isn't a changed craft.
                mission.PlanBaseline = _snapshot;
                Note(LogKind.Notice, CoastCheckpoints.CleanLine(verdict, token));
                ApproveInAdvance(mission);
                return;
            }
            Note(LogKind.Notice, "Checked on return: " + verdict.Line + ". Claudia is planning the next sequence from the figures.");
            _commands.Add(new ClearDueTimeAlarm(mission.Id));
            mission.PendingSequence = null;
            mission.ConfirmPrompt = null;
            mission.Stage = MissionStage.Sequencing;
            // SequenceCounted stands: it is the same step asked for again.
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                UserText = CoastCheckpoints.CheckReport(verdict, token) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.SequenceNotice,
            });
        }
    }

    /// <summary>The running block's wait armed its Encounter Watch (#775): the coast's resume token, "" for none.</summary>
    public sealed class CoastArmed : MissionInput
    {
        /// <summary>The Script Result key that holds a checkpoint's resume token, and the block option and watch key that carry it.</summary>
        public const string ResultKey = "checkpoint";

        public readonly string Token;
        public CoastArmed(string token) { Token = token ?? ""; }
    }
}
