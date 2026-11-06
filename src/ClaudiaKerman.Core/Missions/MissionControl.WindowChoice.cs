// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The player picks the Transfer Window for a transfer between bodies with the same parent (#686, <see cref="WindowChoice"/>): on the
    /// start form before Start, which carries into the first Mission Plan, and again for each revised Mission Plan, whose Approval needs
    /// a pick. The pick stands in for the setting's wherever a window is taken (<see cref="Tools.TransferWindows.Pick(Tools.WindowSearch, GameSnapshot)"/>).
    /// #782: a departure no pick covers asks at its first Sequence Plan, whose Due Time and window wait for the answer.
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
            /// <summary>#782: the Due Time the Sequence Plan in hand came with, which each pick's window is worked against. Saved.</summary>
            public double? WindowOwnDue;
        }

        /// <summary>The offer last worked out, what it was worked for, and the snapshot, goal and rework period it last answered.</summary>
        private WindowOffer _offer;
        private string _offerKey;
        private GameSnapshot _offerFrom;
        private string _offerGoal;
        private double _offerPeriod;
        /// <summary>The departure last read from a plan, and the plan and snapshot it was read from.</summary>
        private SurfaceDeparture _departure;
        private SequencePlan _departurePlan;
        private GameSnapshot _departureFrom;

        /// <summary>
        /// The windows on offer for <paramref name="goal"/> from <paramref name="snapshot"/>: one object until what it depends on moves, so
        /// the view can ask on every rebuild. The start form asks of the craft it checks; a Mission of its own craft (<see cref="MissionVesselSnapshot"/>).
        /// </summary>
        private WindowOffer OfferFor(string goal, GameSnapshot snapshot, Func<double, WindowOffer> work = null)
        {
            if (snapshot == null) return WindowOffer.None;
            var now = Math.Max(_ut, snapshot.Ut);
            var period = WindowChoice.Period(now);
            if (_offer != null && ReferenceEquals(_offerFrom, snapshot) && _offerGoal == goal && _offerPeriod == period) return _offer;
            var key = WindowChoice.Key(snapshot, goal, _config.CraftCheckMarginPercent, now);
            if (_offer == null || key != _offerKey) _offer = work != null ? work(now) : WindowChoice.Work(snapshot, goal, _config.CraftCheckMarginPercent, now);
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
            if (mission.Stage == MissionStage.AwaitingSequenceApproval) return !mission.Declined && DeparturePickMissing(mission, mission.PendingSequence);
            if (!mission.WindowPickNeeded || mission.Stage != MissionStage.AwaitingPlanApproval || mission.Declined) return false;
            var snapshot = MissionVesselSnapshot(mission);
            return snapshot == null || OfferFor(PlanGoal(mission), snapshot).Needed;
        }

        /// <summary>The Mission Plan in hand offers the choice: a revised one waiting for a pick, or picked and not yet approved. The first carries its pick from the form: after Start only a revision changes it. A Sequence Plan whose departure asks offers it the same way (#782).</summary>
        private bool WindowChoiceOpen(Mission mission)
        {
            if (mission.Stage == MissionStage.AwaitingSequenceApproval) return !mission.Declined && (mission.WindowPickUnsaid || WindowPickMissing(mission));
            return (mission.WindowPickNeeded || mission.WindowPickUnsaid) && mission.Stage == MissionStage.AwaitingPlanApproval && !mission.Declined;
        }

        /// <summary>The windows the Mission's view shows; none unless the plan in hand offers the choice.</summary>
        private WindowOffer MissionOffer(Mission mission)
        {
            if (!WindowChoiceOpen(mission)) return WindowOffer.None;
            return mission.Stage == MissionStage.AwaitingSequenceApproval ? DepartureOffer(mission, mission.PendingSequence) : OfferFor(PlanGoal(mission), MissionVesselSnapshot(mission));
        }

        /// <summary>The departure <paramref name="plan"/> starts, if it is one the player picks for: the mod's own resume of a coast (#775), a Child Vessel's plan and a craft the player is away from ask nothing yet.</summary>
        private SurfaceDeparture DepartureOf(Mission mission, SequencePlan plan)
        {
            var snapshot = MissionVesselSnapshot(mission);
            if (snapshot == null || plan == null || plan.Manual || CoastPlanToken(plan) != null || !FliesCraft(mission, plan.Vessel)) return null;
            if (ReferenceEquals(plan, _departurePlan) && ReferenceEquals(snapshot, _departureFrom)) return _departure;
            _departure = WindowChoice.Departure(snapshot, plan, mission.Plan == null ? null : mission.Plan.Sequences, StepOf(mission, plan));
            _departurePlan = plan;
            _departureFrom = snapshot;
            return _departure;
        }

        /// <summary>The windows for the departure <paramref name="plan"/> starts; none when it starts none or no window was found.</summary>
        private WindowOffer DepartureOffer(Mission mission, SequencePlan plan)
        {
            var departure = DepartureOf(mission, plan);
            if (departure == null) return WindowOffer.None;
            var snapshot = MissionVesselSnapshot(mission);
            var setting = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config)).TimeVersusDeltaV;
            return OfferFor("departure:" + departure.Planet.Name + ":" + departure.TwoStep + ":" + setting, snapshot, now => WindowChoice.WorkDeparture(snapshot, departure, setting, _config.CraftCheckMarginPercent, now));
        }

        /// <summary>#782: <paramref name="plan"/> starts a departure no pick covers. A pick for another transfer (the way out, say) is none for this one.</summary>
        private bool DeparturePickMissing(Mission mission, SequencePlan plan)
        {
            var departure = DepartureOf(mission, plan);
            return departure != null && !mission.WindowPick.IsFor(departure.Planet.Name) && DepartureOffer(mission, plan).Needed;
        }

        /// <summary>The player pressed a row of a revised Mission Plan's or a departure's choice: never while a request is out, and it can change until Approval.</summary>
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
            if (mission.Stage == MissionStage.AwaitingSequenceApproval) DeparturePicked(mission, offer.RowFor(mission.WindowPick));
        }

        /// <summary>#782: the Sequence Plan takes the pick: its script's literal windows, then the Due Time and alarm from the window, on top of the plan's own Due Time.</summary>
        private void DeparturePicked(Mission mission, WindowRow row)
        {
            var plan = mission.PendingSequence;
            var hadDue = plan.DueTimeUt.HasValue;
            plan.DueTimeUt = mission.WindowOwnDue;
            plan.Script = WindowChoice.Retimed(plan.Script, row);
            // A window noted under the setting, from a manual stretch before this, is not the one picked.
            ClearWindow(mission);
            WindowWait(mission, plan);
            TransferWait(mission, plan);
            if (hadDue && !plan.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            DueTimeOffered(mission, plan);
            var card = mission.Log.LastOrDefault(e => e.Kind == LogKind.SequencePlan);
            if (card == null) return;
            card.Text = PlanCardText(mission, plan);
            card.Script = plan.Script;
        }

        /// <summary>#782: a Sequence Plan's Approval sends no request, so the next turn once the plan has gone says what the player picked; nothing while it still waits, when the pick can change.</summary>
        private static string DepartureLine(Mission mission)
        {
            return mission.AwaitingApproval ? "" : WindowPickLine(mission);
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
