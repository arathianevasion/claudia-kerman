// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Ui;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Target Vessel (ADR 0013, #643): chosen when the Mission starts, by the same choice the start form shows, and changed
    /// or cleared between sequences (#661); the adapter reads it live by its id, and only a Mission with one has its entry in
    /// the Game Snapshot.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The Target Vessel changed since the pending plan was offered ("now Tug", "cleared"), so Approve is Re-plan; "" otherwise. Saved.</summary>
            public string TargetChange = "";
        }

        /// <summary>The start form's goal and pick as it last reported them (#664); null until it has.</summary>
        private StartFormChanged _form;

        private GameSnapshot _targetFactsFrom;
        private TargetFacts _targetFacts = new TargetFacts();

        /// <summary>What the Target Vessel is chosen from, as of the latest snapshot: one object per snapshot, so the form can cache its choice.</summary>
        private TargetFacts CurrentTargetFacts()
        {
            if (!ReferenceEquals(_targetFactsFrom, _snapshot))
            {
                _targetFactsFrom = _snapshot;
                _targetFacts = TargetFacts.Of(_snapshot);
            }
            return _targetFacts;
        }

        /// <summary>The Target Vessel for this goal and the form's pick (#643): the same choice the start form shows.</summary>
        private TargetChoice ChooseTarget(string goal, TargetPick pick)
        {
            return TargetVessel.Choose((goal ?? "").Trim(), pick, CurrentTargetFacts());
        }

        /// <summary>The Mission takes the target the Craft Check judged, and the log says how it was found or why there is none.</summary>
        private void TakeTarget(Mission mission, TargetChoice choice)
        {
            mission.Target = choice.Target;
            if (choice.LogLine.Length > 0) Note(LogKind.Notice, choice.LogLine);
            if (_snapshot != null) FitTarget(_snapshot);
        }

        /// <summary>The snapshot keeps a target entry only for the Mission in play's Target Vessel, so a Mission without one pays nothing.</summary>
        private void FitTarget(GameSnapshot snapshot)
        {
            var target = MissionOver ? null : _mission.Target;
            if (target == null) snapshot.Target = null;
            else if (snapshot.Target == null || snapshot.Target.Id != target.Id)
                snapshot.Target = snapshot.KspTarget != null && snapshot.KspTarget.Id == target.Id ? snapshot.KspTarget : null;
        }

        /// <summary>
        /// The Mission has a Target Vessel the snapshot names among the game's vessels but hasn't read yet (a snapshot taken before
        /// the Mission started): the Mission Plan waits a snapshot for it.
        /// </summary>
        private static bool TargetUnread(Mission mission, GameSnapshot snapshot)
        {
            var target = mission.Target;
            return target != null && snapshot.Target == null && snapshot.Vessels.Any(v => v.Id == target.Id);
        }

        // ---- Changed between sequences (#661) ----

        /// <summary>The Mission Goal needs a Target Vessel (#643's rule), so the Mission window shows its line and, between sequences, Choose and Clear.</summary>
        private bool TargetNeeded(Mission mission)
        {
            return !MissionOver && TargetVessel.Choose(mission.Goal, TargetPick.Cleared, CurrentTargetFacts()).Needed;
        }

        /// <summary>Choose and Clear are on between sequences, as the Mission Settings are: never while Running or a request is out.</summary>
        private bool CanChangeTarget
        {
            get { return CanEditSettings && TargetNeeded(_mission); }
        }

        /// <summary>
        /// The player chose or cleared the Target Vessel between sequences, by the start form's choice function. Before the first
        /// Mission Plan nothing more is needed: it is asked for with the new target. A pending plan becomes Re-plan, and a pending
        /// Mission Plan is revised at once (<see cref="TryTargetRevision"/>).
        /// </summary>
        private void TargetChanged(TargetPick pick)
        {
            var mission = _mission;
            if (pick == null || pick.Kind == TargetPickKind.Auto || !CanChangeTarget) return;
            var choice = ChooseTarget(mission.Goal, pick);
            if (pick.Kind == TargetPickKind.Vessel && choice.Target == null)
            {
                Note(LogKind.Notice, "Target Vessel unchanged: " + choice.Reason + ".");
                return;
            }
            var before = mission.Target;
            if (choice.Target == null ? before == null : before != null && before.Id == choice.Target.Id) return;
            mission.Target = choice.Target;
            if (_snapshot != null) FitTarget(_snapshot);
            Note(LogKind.Player, choice.LogLine);
            if (choice.Note.Length > 0) Note(LogKind.Notice, choice.Target.Name + " is " + choice.Note + ".");
            Event(mission, "target_changed", choice.Target != null ? choice.Target.Name : "none");
            if (mission.Stage == MissionStage.WaitingForFlight) return;
            mission.TargetChange = choice.Target != null ? "now " + choice.Target.Name : "cleared";
            mission.ConfirmPrompt = null;
        }

        /// <summary>A pending Mission Plan is revised for the new Target Vessel as soon as the target is read; a Sequence Plan waits for Re-plan.</summary>
        private void TryTargetRevision()
        {
            var mission = _mission;
            if (MissionOver || mission.TargetChange.Length == 0 || mission.Stage != MissionStage.AwaitingPlanApproval || mission.InFlight || mission.Blackout) return;
            ReviseForTarget(mission, LogKind.Notice);
        }

        /// <summary>
        /// The revision for a changed Target Vessel, once a Game Snapshot has read it: the Craft Check's target rule runs on it (a
        /// goal that docks: a port of a size the target has free), and Claudia is asked for a revised Mission Plan with what it
        /// found. A pending Sequence Plan is dropped with the old plan. False: the target isn't read yet.
        /// </summary>
        private bool ReviseForTarget(Mission mission, LogKind kind)
        {
            if (_snapshot == null || TargetUnread(mission, _snapshot))
            {
                // The player's own press says why nothing happened; Auto Mode's comes again on the next input.
                if (kind == LogKind.Player) Note(LogKind.Notice, "Re-plan waits for the next Game Snapshot to read the new Target Vessel.");
                return false;
            }
            Note(kind, "Re-plan: the Target Vessel changed (" + mission.TargetChange + ").");
            var findings = TargetFindings(mission);
            foreach (var finding in findings) Note(LogKind.Notice, "Craft Check: " + finding.Describe() + ".");
            var turn = TargetTurn(mission, findings);
            mission.TargetChange = "";
            mission.ConfirmPrompt = null;
            mission.PlanBaseline = null;
            if (mission.Stage == MissionStage.AwaitingSequenceApproval) DropPendingSequence(mission);
            mission.Stage = MissionStage.Planning;
            Begin(turn);
            return true;
        }

        /// <summary>The Craft Check's rule that reads the Target Vessel (#643): for a goal that docks, the craft's ports against the target's free ones.</summary>
        private IList<MarginFinding> TargetFindings(Mission mission)
        {
            if (_snapshot == null || !SequenceWords.Docking.IsMatch(mission.Goal)) return new List<MarginFinding>();
            return MarginCheck.Docking(_snapshot, "The Mission Goal docks", _snapshot.Target);
        }

        /// <summary>The revised Mission Plan request for the new Target Vessel; also the probe the Auto Mode room check prices.</summary>
        private Turn TargetTurn(Mission mission, IList<MarginFinding> findings)
        {
            return new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = MissionPrompts.TargetChanged(mission.Target != null ? mission.Target.Name : null, findings.Select(f => f.Describe()).ToList()) + "\n"
                    + MissionPrompts.Facts(_ut, _snapshot) + PlanFigures(mission, mission.Goal),
                Notice = MissionPrompts.TargetNotice,
            };
        }
    }
}
