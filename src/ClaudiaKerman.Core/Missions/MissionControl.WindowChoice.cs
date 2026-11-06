// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The player picks the Transfer Window for a transfer between bodies with the same parent (#686, <see cref="WindowChoice"/>): on the
    /// start form before Start, which carries into the first Mission Plan, and again for each revised Mission Plan, whose Approval needs
    /// a pick. The pick stands in for the setting's wherever a window is taken (<see cref="Tools.TransferWindows.Pick(Tools.WindowSearch, GameSnapshot)"/>).
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The window the player last picked, kept while a revised plan waits for a new one; None for none. Saved.</summary>
            public WindowPick WindowPick = WindowPick.None;
            /// <summary>The Mission Plan in hand needs a fresh pick before Approval. Saved.</summary>
            public bool WindowPickNeeded;
            /// <summary>The player picked after the plan's request was sent, so Claudia hears it with the Approval. Saved.</summary>
            public bool WindowPickUnsaid;
        }

        /// <summary>The offer last worked out, what it was worked for, and the snapshot, goal and rework period it last answered.</summary>
        private WindowOffer _offer;
        private string _offerKey;
        private GameSnapshot _offerFrom;
        private string _offerGoal;
        private double _offerPeriod;

        /// <summary>
        /// The windows on offer for <paramref name="goal"/> from <paramref name="snapshot"/>: one object until what it depends on moves, so
        /// the view can ask on every rebuild. The start form asks of the craft it checks; a Mission of its own craft (<see cref="MissionVesselSnapshot"/>).
        /// </summary>
        private WindowOffer OfferFor(string goal, GameSnapshot snapshot)
        {
            if (snapshot == null) return WindowOffer.None;
            var now = Math.Max(_ut, snapshot.Ut);
            var period = WindowChoice.Period(now);
            if (_offer != null && ReferenceEquals(_offerFrom, snapshot) && _offerGoal == goal && _offerPeriod == period) return _offer;
            var key = WindowChoice.Key(snapshot, goal, _config.CraftCheckMarginPercent, now);
            if (_offer == null || key != _offerKey) _offer = WindowChoice.Work(snapshot, goal, _config.CraftCheckMarginPercent, now);
            _offerKey = key;
            _offerFrom = snapshot;
            _offerGoal = goal;
            _offerPeriod = period;
            return _offer;
        }

        /// <summary>The goal the Mission Plan in hand flies: a new one it carries, or the Mission's.</summary>
        private static string PlanGoal(Mission mission)
        {
            return mission.Plan != null && mission.Plan.NewGoal != null ? mission.Plan.NewGoal : mission.Goal;
        }

        /// <summary>
        /// A Mission Plan is offered: the windows are worked out again for the craft as it is. The first one carries the start form's
        /// pick; any other waits for a fresh one. With the player away from the craft it can't be worked out yet, so a Mission that has
        /// had a pick waits for them to come back; one that never had one has no such transfer.
        /// </summary>
        private void AskForWindow(Mission mission, bool first)
        {
            _offer = null;
            mission.WindowPickUnsaid = false;
            var snapshot = MissionVesselSnapshot(mission);
            if (snapshot == null)
            {
                mission.WindowPickNeeded = !first && !mission.WindowPick.IsNone;
                return;
            }
            var offer = OfferFor(PlanGoal(mission), snapshot);
            mission.WindowPickNeeded = offer.Needed && !(first && !WindowChoice.Held(mission.WindowPick, offer).IsNone);
        }

        /// <summary>The Mission Plan in hand can't be approved yet: it waits for the player's pick, under Auto Mode too.</summary>
        private bool WindowPickMissing(Mission mission)
        {
            if (!mission.WindowPickNeeded || mission.Stage != MissionStage.AwaitingPlanApproval || mission.Declined) return false;
            var snapshot = MissionVesselSnapshot(mission);
            return snapshot == null || OfferFor(PlanGoal(mission), snapshot).Needed;
        }

        /// <summary>The Mission Plan in hand offers the choice: a revised one waiting for a pick, or picked and not yet approved. The first carries its pick from the form: after Start only a revision changes it.</summary>
        private bool WindowChoiceOpen(Mission mission)
        {
            return (mission.WindowPickNeeded || mission.WindowPickUnsaid) && mission.Stage == MissionStage.AwaitingPlanApproval && !mission.Declined;
        }

        /// <summary>The windows the Mission's view shows; none unless the plan in hand offers the choice.</summary>
        private WindowOffer MissionOffer(Mission mission)
        {
            return WindowChoiceOpen(mission) ? OfferFor(PlanGoal(mission), MissionVesselSnapshot(mission)) : WindowOffer.None;
        }

        /// <summary>The player pressed a row of a revised Mission Plan's choice: never while a request is out, and it can change until Approval.</summary>
        private void WindowChosen(WindowKind kind)
        {
            var mission = _mission;
            if (!CanAnswerPlan || !WindowChoiceOpen(mission)) return;
            var offer = MissionOffer(mission);
            if (!offer.Needed) return;
            mission.WindowPick = new WindowPick(offer.Body, kind);
            mission.WindowPickNeeded = false;
            mission.WindowPickUnsaid = true;
            mission.ConfirmPrompt = null;
            Note(LogKind.Player, "Transfer Window to " + offer.Body + ": " + kind + ".");
            Event(mission, "window_picked", mission.WindowPick.Key);
        }

        /// <summary>The line for the Approval's turn when the player picked after the plan's request was sent; "" otherwise.</summary>
        private static string WindowPickLine(Mission mission)
        {
            if (!mission.WindowPickUnsaid || mission.WindowPick.IsNone) return "";
            mission.WindowPickUnsaid = false;
            return MissionPrompts.WindowPicked(mission.WindowPick) + "\n";
        }
    }
}
