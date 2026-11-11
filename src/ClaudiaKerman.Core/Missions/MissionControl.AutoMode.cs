// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Approval given in advance (spec #327, ADR 0009). The player gives it in a Blackout, where their Approval of the
    /// Mission Plan stands for each sequence (MissionControl.Blackout.cs), and under Auto Mode, a per-Mission switch
    /// (not a Mission Setting: flipping it asks Claudia for nothing) under which every plan after the first Mission Plan
    /// is approved the moment it arrives, while the Spending Cap has room for the Debrief and the next plan request that
    /// follow. One check decides both, so the rules for what may fly without the player live in one place.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The player's Approval is given in advance (Auto Mode); saved with the Mission.</summary>
            public bool AutoMode;
            /// <summary>
            /// The player has approved a Mission Plan (#661): a revision is never the first Mission Plan, even when a dropped Sequence
            /// Plan took <c>SequencesProposed</c> back to 0. Saved; false in older saves, where that count alone decides.
            /// </summary>
            public bool MissionPlanApproved;
            /// <summary>The plan the Auto Mode room check last held for the player, so its blocked charge is logged once (not saved).</summary>
            public object AutoModeHeldFor;
            /// <summary>
            /// The plan last approved in advance: if <see cref="Approval"/> refused it (no processor to run it on, an unsafe
            /// script), it waits for the player rather than being approved again on every input; in a Blackout it is tried
            /// again once a processor is there, and once more when the link returns (not saved).
            /// </summary>
            public object AutoModeApprovedFor;
            /// <summary>The plan whose run an Approval in advance last started: one kOS refused and that starts again is not logged or listed twice (not saved).</summary>
            public object AutoModeStartedFor;
            /// <summary>
            /// Auto Mode re-planned an unsafe script, and no plan has passed the Script Check since: a second failure waits
            /// for the player (not saved).
            /// </summary>
            public bool AutoModeReplanned;
            /// <summary>Why Auto Mode is waiting for the player on the plan in hand ("" when it isn't); see <see cref="AutoModeStopLine"/> (not saved).</summary>
            public string AutoModeStop = "";
            /// <summary>The no-room stop line the last failed room check built, with the amounts it was built from (not saved).</summary>
            public string AutoModeRoomStop = "";
            /// <summary>The stop line last written to the log, so a wait that lasts through many inputs is logged once (not saved).</summary>
            public string AutoModeStopLogged = "";
        }

        /// <summary>What a plan in hand waits for, though the player's Approval was given in advance.</summary>
        private enum AdvanceWait
        {
            /// <summary>The very first Mission Plan: the player never approved one, so there is nothing to stand for it.</summary>
            FirstMissionPlan,
            /// <summary>Only the player flies a manual stretch.</summary>
            ManualStretch,
            /// <summary>
            /// A Due Time is a wait that needs the player (ADR 0004), even once it is reached; under Auto Mode, in a Blackout too, only while they
            /// are away from the craft or once its approval there was refused (#967), and a coast checkpoint's only once they have left (#775).
            /// </summary>
            DueTime,
            /// <summary>Auto Mode only: the Spending Cap has no room for the Debrief and next plan request that follow.</summary>
            NoRoom,
            /// <summary>Auto Mode only: the script failed the Script Check again after Auto Mode's re-plan.</summary>
            ScriptFailedAgain,
            /// <summary>A revised Mission Plan's or a departure's Transfer Window is the player's to pick (#686, #782): nothing stands in for them.</summary>
            WindowPick,
            /// <summary>The plan waits in an orbit that crosses a moon (#867): its Blocker holds until it is re-planned or the orbit fixed.</summary>
            MoonCrossing,
        }

        /// <summary>The Auto Mode checkbox, in any stage of a Mission in play. Ticking it lets the check below approve a plan that waits.</summary>
        private void AutoModeSet(bool on)
        {
            var mission = _mission;
            if (MissionOver || mission.AutoMode == on) return;
            mission.AutoMode = on;
            mission.AutoModeHeldFor = null;
            mission.AutoModeApprovedFor = null;
            mission.AutoModeReplanned = false;
            Event(mission, "auto_mode", on ? "on" : "off");
        }

        /// <summary>
        /// After every input and each reply: whether the plan in hand already has the player's Approval, given in a Blackout
        /// or under Auto Mode. Either way the first Mission Plan, a manual stretch and a plan with a Due Time wait for the
        /// player (but for a reached Due Time under Auto Mode, #967), and a stale plan is re-planned. Under Auto Mode (and
        /// no Blackout, whose own rules stand) an approval or re-plan also needs room in the Spending Cap (<see cref="AutoModeRoom"/>). The Approval itself is
        /// <see cref="Approval"/>, so the Script Check, the late-window and the Due Time rules hold as for the player's button.
        /// Auto Mode also answers what Blackout leaves for the player (<see cref="AutoModeAnswers"/>): an Abort
        /// recommendation, a reduced goal and an unsafe script.
        /// </summary>
        private void ApproveInAdvance(Mission mission)
        {
            // A request in flight keeps the wait as it was (a question asked meanwhile doesn't lift it).
            if (!mission.InFlight) mission.AutoModeStop = "";
            ApproveOrWait(mission);
            var stop = AutoModeStopLine(mission);
            if (stop != mission.AutoModeStopLogged)
            {
                mission.AutoModeStopLogged = stop;
                if (stop.Length > 0) Note(LogKind.Notice, stop);
            }
        }

        /// <summary>
        /// The line for a hand-back under Auto Mode (spec #327, Wording): the log's line, the view's <c>AutoModeStop</c> and
        /// the text of the attention it raises. "" when Auto Mode is off, in a Blackout (whose own lines speak) or not waiting.
        /// </summary>
        private static string AutoModeStopLine(Mission mission)
        {
            if (!mission.AutoMode || mission.Blackout) return "";
            if (mission.Stage == MissionStage.Paused && !mission.InFlight) return AutoModeWaitsFor("the Mission is Paused");
            return mission.AutoModeStop;
        }

        private static string AutoModeWaitsFor(string what)
        {
            return "Auto Mode is waiting for you: " + what + ".";
        }

        private void ApproveOrWait(Mission mission)
        {
            if (!mission.Blackout && !mission.AutoMode) return;
            if (MissionOver) return;
            if (mission.AutoMode && AutoModeAnswers(mission)) return;
            if (!CanAnswerPlan || mission.Declined) return;
            // A plan waits for Approval, so it passed the Script Check on arrival: the next unsafe script gets its own re-plan.
            mission.AutoModeReplanned = false;
            // ADR 0002: typed words never start anything, so a pending "approve?" is moot once Approval stands.
            if (mission.Blackout) mission.ConfirmPrompt = null;
            var missionPlan = mission.Stage == MissionStage.AwaitingPlanApproval;
            var plan = mission.PendingSequence;
            var atDue = false;
            if (missionPlan)
            {
                if (mission.SequencesProposed == 0 && !mission.MissionPlanApproved)
                {
                    WaitsForPlayer(mission, AdvanceWait.FirstMissionPlan, "");
                    return;
                }
                if (WindowPickMissing(mission))
                {
                    WaitsForPlayer(mission, AdvanceWait.WindowPick, "");
                    return;
                }
            }
            else
            {
                if (plan == null) return;
                if (plan.Manual)
                {
                    WaitsForPlayer(mission, AdvanceWait.ManualStretch, plan.Name);
                    return;
                }
                if (WindowPickMissing(mission))
                {
                    WaitsForPlayer(mission, AdvanceWait.WindowPick, plan.Name);
                    return;
                }
                if (CrossingAwaitsRead(mission, plan)) return;
                // Back in the craft at a coast checkpoint: its check, later in this input, decides (#775).
                if (CoastCheckRunsNow(mission, plan)) return;
                if (CrossingHeld(mission, plan))
                {
                    WaitsForPlayer(mission, AdvanceWait.MoonCrossing, plan.Name);
                    return;
                }
                // The exceptions: Auto Mode may stand in at a coast checkpoint's Due Time with the player still in the craft, in a Blackout too (#775, #967), and at any Due Time once it is reached (#967).
                if (plan.DueTimeUt.HasValue && !(mission.AutoMode && SoftDueTime(mission, plan)))
                {
                    // #967: Auto Mode on, the player in the craft: Auto Mode approves once the Due Time is reached, in a Blackout too.
                    if (!AutoModeTakesDue(mission, plan))
                    {
                        WaitsForPlayer(mission, AdvanceWait.DueTime, plan.Name);
                        return;
                    }
                    if (_ut < plan.DueTimeUt.Value) return;
                    atDue = true;
                }
            }
            object inHand = missionPlan ? (object)mission.Plan : plan;
            if (mission.AutoModeApprovedFor == inHand)
            {
                // Approved in advance before and back in hand: Approval refused it (no processor to run it on), or kOS refused
                // the script. Linked, it waits for the player, called at a Due Time Auto Mode took.
                if (!mission.Blackout)
                {
                    if (atDue) WaitsForPlayer(mission, AdvanceWait.DueTime, plan.Name);
                    return;
                }
                // In a Blackout it is tried again only once a processor is there to run it, so the refusal is logged once.
                string refusal;
                if (!missionPlan && !ProcessorOrRefusal(mission, plan, out refusal).HasValue) return;
            }
            var stale = StaleBecause(mission);
            if (!mission.Blackout && !AutoModeRoom(mission, inHand, stale.Length > 0 ? ReplanTurn(stale, missionPlan) : null))
            {
                WaitsForPlayer(mission, AdvanceWait.NoRoom, missionPlan ? "" : plan.Name);
                return;
            }
            mission.ConfirmPrompt = null;
            if (stale.Length > 0)
            {
                Replanned(inAdvance: true);
                return;
            }
            mission.AutoModeApprovedFor = inHand;
            if (!mission.Blackout) RetractPlanNews();
            // Before Approval's own lines, and taken back if it didn't start the run. A Blackout says so only at a Due Time Auto Mode took.
            var note = mission.Blackout && !atDue ? null : Note(LogKind.Notice, atDue ? AutoModeApprovedAtDueNote : AutoModeApprovedNote);
            var dueBefore = plan == null ? null : plan.DueTimeUt;
            Approval();
            var started = missionPlan ? mission.Stage != MissionStage.AwaitingPlanApproval : mission.Stage == MissionStage.Running;
            // Started again after kOS refused the script: it was logged and listed the first time.
            var again = started && mission.AutoModeStartedFor == inHand;
            if (started) mission.AutoModeStartedFor = inHand;
            if (!started || again)
            {
                if (note != null) mission.Log.Remove(note);
                // Not refused: Approval moved the Due Time to the next window, or sent a re-plan or an Alert. Auto Mode takes the plan again at its new Due Time.
                if (!started && (mission.InFlight || plan != null && plan.DueTimeUt != dueBefore))
                {
                    mission.AutoModeApprovedFor = null;
                    return;
                }
                // Refused, and still in hand: nothing says it was approved or flown, and at a Due Time Auto Mode took the player is called.
                if (!started && atDue && !mission.Blackout && mission.Stage == MissionStage.AwaitingSequenceApproval && mission.PendingSequence == plan)
                    WaitsForPlayer(mission, AdvanceWait.DueTime, plan.Name);
                return;
            }
            if (mission.Blackout)
                mission.BlackoutRevisions.Add(missionPlan
                    ? "Mission Plan revised: " + mission.Plan.Why
                    : "Sequence Plan " + MissionPrompts.Quoted(plan.Name) + " flown without Approval: " + plan.Why);
        }

        /// <summary>
        /// #967, the player's ruling: Auto Mode stands in at a reached Due Time when it is on at that moment and they are in the craft,
        /// landed, orbiting or escaping (away from it, Go to the vessel comes first). Nothing hangs on a warp to it, on when Auto Mode was
        /// ticked, or on the Due Time having moved. A Blackout doesn't stop it: there every plan is approved, so it takes the Blackout's
        /// Approval path (no room check, the plan listed for when the link returns). A manual stretch waits, and a coast checkpoint's
        /// resume waits for its check on return (#775), which approves it when clean.
        /// </summary>
        private bool AutoModeTakesDue(Mission mission, SequencePlan plan)
        {
            return mission.AutoMode && plan != null && plan == mission.PendingSequence && !plan.Manual && CraftActive
                && !(mission.CheckPending && CoastPlanToken(plan) != null);
        }

        /// <summary>#967: Warp to Due Time pressed with Auto Mode on: the log says Auto Mode carries on there (<see cref="ApproveOrWait"/>).</summary>
        private void DueWarpPressed(Mission mission, SequencePlan plan)
        {
            if (!mission.AutoMode || plan.Manual) return;
            Note(LogKind.Notice, DueWarpNote);
        }

        /// <summary>
        /// What Auto Mode answers beyond Blackout's rules (spec #327, "One approve-in-advance path" and "Abort"). An Abort
        /// recommendation is taken as if the player pressed Abort, in a Blackout too and with no room check: stopping
        /// costs nothing. A reduced goal on offer is approved, and an unsafe script re-planned once per plan, each subject
        /// to the room check with the request it starts priced in (no room check in a Blackout, as for any plan there).
        /// True: the stage was Auto Mode's to answer, whether it answered or left it waiting.
        /// </summary>
        private bool AutoModeAnswers(Mission mission)
        {
            if (mission.AbortRecommendation.Length > 0)
            {
                AbortTaken(byAutoMode: true);
                // Not taken: the run is already being stopped, or there is nothing to abort it on. Either way it is moot, and
                // left standing it would hold the script's warp and show itself if Auto Mode were unticked.
                mission.AbortRecommendation = "";
            }
            if (mission.InFlight) return false;
            if (mission.Stage == MissionStage.AwaitingReducedGoal && mission.ReducedGoal.Length > 0)
            {
                if (!mission.Blackout && !AutoModeRoom(mission, mission.ReducedGoal, ReducedGoalTurn(mission)))
                {
                    WaitsForPlayer(mission, AdvanceWait.NoRoom, "");
                    return true;
                }
                ReducedGoalAccepted(inAdvance: true);
                return true;
            }
            if (mission.Stage != MissionStage.UnsafeScript) return false;
            if (mission.AutoModeReplanned)
            {
                WaitsForPlayer(mission, AdvanceWait.ScriptFailedAgain, "");
                return true;
            }
            if (!mission.Blackout && !AutoModeRoom(mission, mission.UnsafeProblems, ScriptRetryTurn(mission, null, replan: true)))
            {
                WaitsForPlayer(mission, AdvanceWait.NoRoom, "");
                return true;
            }
            mission.AutoModeReplanned = true;
            RetractPlanNews();
            Note(LogKind.Notice, AutoModeReplannedNote);
            ScriptRetried(null, replan: true);
            return true;
        }

        /// <summary>
        /// The plan waits for the player as any plan does, with the usual quick replies and attention. In a Blackout the log
        /// says so once; under Auto Mode the stop line is set (<see cref="AutoModeStopLine"/>), the one place the wording is used.
        /// </summary>
        private void WaitsForPlayer(Mission mission, AdvanceWait wait, string planName)
        {
            if (mission.Blackout)
            {
                WaitsForLink(mission, wait, planName);
                return;
            }
            // Under Auto Mode the player is called with the stop line; the first Mission Plan is their go-ahead, not a stop.
            switch (wait)
            {
                case AdvanceWait.ManualStretch: mission.AutoModeStop = AutoModeWaitsFor("a Manual stretch"); break;
                case AdvanceWait.DueTime: mission.AutoModeStop = AutoModeWaitsFor("a Due Time"); break;
                case AdvanceWait.ScriptFailedAgain: mission.AutoModeStop = AutoModeWaitsFor("the script failed again after a re-plan"); break;
                case AdvanceWait.NoRoom: mission.AutoModeStop = mission.AutoModeRoomStop; break;
                case AdvanceWait.WindowPick: mission.AutoModeStop = AutoModeWaitsFor("a Transfer Window to pick"); break;
                case AdvanceWait.MoonCrossing: mission.AutoModeStop = AutoModeWaitsFor("a Blocker: " + mission.Crossing.Reason); break;
            }
        }

        /// <summary>
        /// The room check: Committed plus the worst case of the Debrief after the plan and of the Sequence Plan request after
        /// that (and of <paramref name="starts"/>, the request an approval or re-plan starts itself: a stale plan's re-plan,
        /// an unsafe script's, a reduced goal's Mission Plan) fits the Spending Cap. Worst cases by the formula
        /// <see cref="MaySend"/> uses, never past costs, so Auto Mode can't approve a plan whose Debrief would be refused
        /// (ADR 0009). A tool loop charges per step, so <see cref="MaySend"/> stays the backstop.
        /// </summary>
        private bool AutoModeRoom(Mission mission, object inHand, Turn starts)
        {
            var worst = AutoModeReserve(mission);
            if (starts != null) worst += RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, starts));
            if (mission.Committed + worst <= mission.SpendingCap) return true;
            mission.AutoModeRoomStop = NoRoomStop(mission, worst);
            if (mission.AutoModeHeldFor != inHand)
            {
                mission.AutoModeHeldFor = inHand;
                LogCost(mission, "blocked", worst, "auto_mode_room");
            }
            return false;
        }

        /// <summary>
        /// What the Debrief after the plan in hand and the Sequence Plan request after it could cost at worst. The Debrief is
        /// <see cref="DebriefReserve"/>'s probe of a run that reported nothing; the next plan is a probe built as
        /// <see cref="BeginAfterDebrief"/> builds it, behind that Debrief, its full reply and the same slack.
        /// </summary>
        private decimal AutoModeReserve(Mission mission)
        {
            var ran = mission.PendingSequence ?? new SequencePlan { Name = "" };
            var debrief = new Turn
            {
                Purpose = Purpose.Debrief,
                UserText = MissionPrompts.RunReport(ran, new RunOutcome(RunOutcomeKind.Finished, null, "", "", false), "") + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.DebriefNotice,
            };
            var next = BuildRequest(mission, NextSequenceTurn(offNominal: true));
            next.Messages.Insert(next.Messages.Count - 1, Message.UserText(debrief.UserText));
            next.Messages.Insert(next.Messages.Count - 1, Message.SystemText(debrief.Notice));
            var nextInput = (next.ToJson().Length + ProbeSlack + 2) / 3 + MissionPrompts.MaxTokens;
            return DebriefReserve(mission, debrief) + _config.Prices.WorstCase(mission.Model, nextInput, next.MaxTokens);
        }

        /// <summary>The screen message for the plan just offered, still unsent: Auto Mode takes it back if it approves the plan in the same input (not saved).</summary>
        private AdapterCommand _planNews;

        /// <summary>Claudia's news that a plan waits for Approval (<see cref="HerNews"/>), kept so an automatic approval can retract it.</summary>
        private void PlanNews(string text)
        {
            var before = _commands.Count;
            HerNews(text);
            _planNews = _commands.Count > before ? _commands[_commands.Count - 1] : null;
        }

        /// <summary>An automatic approval or re-plan raises no screen message: the plan is never one that waits.</summary>
        private void RetractPlanNews()
        {
            if (_planNews != null) _commands.Remove(_planNews);
            _planNews = null;
        }

        /// <summary>
        /// Auto Mode stopped because the Spending Cap has no room for the Debrief and next plan request after the plan: what
        /// is spent, what is held, the reserve for those requests' worst case, and the cap that would let it carry on (to the cent).
        /// </summary>
        private static string NoRoomStop(Mission mission, decimal reserve)
        {
            var needed = System.Math.Ceiling((mission.Committed + reserve) * 100m) / 100m;
            return "Auto Mode stopped: the Spending Cap has no room for this plan's Debrief and the next plan. Spent $" + Dollars(mission.Spent)
                + ", held $" + Dollars(mission.Held + mission.CompactionCharge) + ", reserve $" + Dollars(reserve)
                + " for those two requests' worst case: Raise cap to $" + Dollars(needed) + " to carry on, or approve the plan yourself.";
        }
        /// <summary>Logged after a plan's card when Auto Mode approves it.</summary>
        private const string AutoModeApprovedNote = "Approved by Auto Mode.";
        /// <summary>Logged instead when Auto Mode approves at a reached Due Time (#967).</summary>
        private const string AutoModeApprovedAtDueNote = "Approved by Auto Mode at the Due Time.";
        /// <summary>Logged when the player presses Warp to Due Time with Auto Mode on (#967).</summary>
        private const string DueWarpNote = "Warping to the Due Time: Auto Mode approves it there if you're in the craft.";
        /// <summary>Logged after the "Couldn't produce a safe script" card when Auto Mode re-plans it.</summary>
        private const string AutoModeReplannedNote = "Re-planned by Auto Mode.";
    }

    /// <summary>The Auto Mode checkbox: the player's Approval given in advance for this Mission, or taken back. Accepted in any stage of a Mission in play.</summary>
    public sealed class SetAutoMode : MissionInput
    {
        public readonly bool On;
        public SetAutoMode(bool on) { On = on; }
    }
}
