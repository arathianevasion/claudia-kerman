// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The tool loop (#265 "Tool loop"): Claudia calls Planning Tools during a Mission turn and plans from their answers. The
    /// loop lives in the pending turn: each step appends her tool_use reply and the tool results to <c>Turn.Messages</c>
    /// exactly, so each request is the last one plus two messages and the cache holds, and the turn joins the conversation once,
    /// with the accepted reply. Every Mission request carries the same tools list (offering never breaks the cache); which tools
    /// a turn may call is said in its notice and enforced here, so a call during an Alert is answered with an error.
    /// </summary>
    public sealed partial class MissionControl
    {
        private PlanningTools _tools = PlanningTools.Default();
        private IPlanningBody _body = new NoPlanningBody();

        /// <summary>The Planning Tools the Mission and its Craft Checks offer; tests replace them.</summary>
        internal PlanningTools Tools
        {
            get { return _tools; }
            set
            {
                _tools = value;
                _craftCheck.Tools = value;
            }
        }

        private TimeSpan _toolCallTimeout = ToolStep.CallTimeout;

        /// <summary>Each tool call's cap; tests shorten it.</summary>
        internal TimeSpan ToolCallTimeout
        {
            get { return _toolCallTimeout; }
            set
            {
                _toolCallTimeout = value;
                _craftCheck.ToolCallTimeout = value;
            }
        }

        /// <summary>The live game's terrain, biome and sun for the tools; until the adapter passes one, tools that need it say so.</summary>
        public IPlanningBody PlanningBody
        {
            set
            {
                _body = value ?? new NoPlanningBody();
                _craftCheck.Body = _body;
            }
        }

        /// <summary>
        /// Every Mission request's tools list (ADR 0012): the Planning Tools, then the Reply Tool propose_mission_plan. The same
        /// on every request, whatever the turn may call, so the cached prefix never changes.
        /// </summary>
        private IList<ToolDefinition> MissionTools()
        {
            var tools = new List<ToolDefinition>(_tools.Definitions(ToolOffer.Mission));
            tools.Add(ProposedPlans.Definition());
            return tools;
        }

        /// <summary>
        /// The requests that may come back as a Mission Plan, and so may call propose_mission_plan: the first or a revised plan,
        /// a reduced goal, the Mission Settings, and the player's typed reply or change (to a pending Mission Plan, or a new goal).
        /// </summary>
        private static bool TakesMissionPlan(Purpose purpose)
        {
            return purpose == Purpose.MissionPlan || purpose == Purpose.Settings || purpose == Purpose.Reply || purpose == Purpose.Change;
        }

        /// <summary>
        /// Why a turn may not call <paramref name="tool"/>, or null when it may: no tool during an Alert; on the ground, none of
        /// the tools that need an orbit (#508); no check_script on a request for a Mission Plan (new, or revised for the Mission
        /// Settings), which has no script; propose_mission_plan only on a request that takes a Mission Plan.
        /// </summary>
        private static string ToolRefusal(Purpose purpose, GameSnapshot snapshot, string tool)
        {
            if (purpose == Purpose.Alert) return "not available during an Alert";
            if (tool == ProposedPlans.ToolName && !TakesMissionPlan(purpose))
                return "this request doesn't ask for a Mission Plan; reply with the kind the mod asked for";
            var where = snapshot != null ? Grounded(snapshot.Situation) : null;
            if (where != null)
            {
                var launch = snapshot.Situation == "PRELAUNCH"
                    ? ": plan the launch with plan_ascent (the stage table is in the Mission request), and this burn once in orbit"
                    : ": check the ascent with plan_ascent or stage_performance, and plan this burn once in orbit";
                if (tool == "predict_burn") return "the craft is " + where + ", with no orbit to burn from" + launch;
                if (tool == "solve_burn") return "the craft is " + where + ", with no orbit to solve a burn from" + launch;
                if (tool == "find_landing_site") return "the craft is " + where + ", with no orbit and so no ground track to search: plan the landing site once in orbit round the body";
            }
            if (tool == "check_script" && (purpose == Purpose.MissionPlan || purpose == Purpose.Settings))
                return "the Mission Plan needs no script check; the Sequence Plan's script is checked when you send it";
            return null;
        }

        /// <summary>Where a craft on the ground is, in words ("on the launch pad"), or null when it is not on the ground.</summary>
        internal static string Grounded(string situation)
        {
            switch (situation)
            {
                case "PRELAUNCH": return "on the launch pad";
                case "LANDED": return "landed";
                case "SPLASHED": return "splashed down";
                default: return null;
            }
        }

        /// <summary>A step's refusals: <see cref="ToolRefusal"/> first, then an identical repeat of a call made earlier in the turn (<see cref="ToolRepeats"/>).</summary>
        private static Func<ToolUseBlock, string> ToolRefusals(Purpose purpose, GameSnapshot snapshot, Turn turn, AssistantMessage message)
        {
            var uses = message.Content.OfType<ToolUseBlock>().ToList();
            var repeats = ToolRepeats.Of(turn.Messages, message);
            return use => ToolRefusal(purpose, snapshot, use.Name) ?? repeats[uses.IndexOf(use)];
        }

        /// <summary>
        /// Claudia's reply asked for tools: they run on a worker against the Game Snapshot the turn was built with, while the
        /// Capcom strip names them. After the step limit the next request goes with tool_choice none (<see cref="NextStep"/>); a reply that still
        /// calls tools to that fails the turn, so a loop can never run away.
        /// </summary>
        private void ToolsCalled(Mission mission, Turn turn, AssistantMessage message)
        {
            if (message.FallbackRan)
                Event(mission, "tool_loop_fallback", turn.Purpose + " step " + (turn.ToolSteps + 1) + " was served by " + (message.Model ?? "a fallback model") + "; the loop carries on");
            // A save loaded while this ran: its state comes back (MissionControl.Finished), and the turn is sent again from scratch.
            if (mission.DeferredRestore != null)
            {
                mission.Pending = null;
                return;
            }
            if (turn.ToolLimitSent)
            {
                Failed(mission, turn, "Claudia kept calling Planning Tools after the limit of " + ToolStep.MaxSteps + " steps.");
                return;
            }
            var atLimit = false;
            var names = message.Content.OfType<ToolUseBlock>().Select(t => t.Name == ProposedPlans.ToolName ? ProposedPlans.Activity : _tools.Activity(t.Name)).Distinct().ToArray();
            mission.InFlight = true;
            mission.Activity = names.Length > 0 ? string.Join(" and ", names) : "calculating";
            turn.Cancel = new CancellationTokenSource();
            var tools = _tools;
            var body = _body;
            var snapshot = turn.ToolSnapshot;
            if (snapshot != null)
            {
                var settings = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
                snapshot.DaylightLandingDefault = settings.DaylightLanding;
                snapshot.TimeCapHours = settings.TimeCapHours;
            }
            var purpose = turn.Purpose;
            var timeout = _toolCallTimeout;
            var refusal = ToolRefusals(purpose, snapshot, turn, message);
            // The Reply Tool is answered by the mod's own plan checks, from copies taken here: the worker never reads the Mission.
            var left = new HashSet<string>(mission.LeftTheCraft);
            var needsNewGoal = mission.Stage != MissionStage.AwaitingPlanApproval && (purpose == Purpose.Reply || purpose == Purpose.Change);
            Func<ToolUseBlock, ToolOutcome> answer = use => use.Name == ProposedPlans.ToolName ? ProposedPlans.Check(use, left, needsNewGoal) : null;
            _background.Run(
                () => ToolStep.Run(tools, message, refusal, atLimit, snapshot, body, timeout, answer),
                step => ToolsAnswered(mission, turn, message, step, atLimit),
                error => ToolsAnswered(mission, turn, message, null, atLimit));
        }

        private void ToolsAnswered(Mission mission, Turn turn, AssistantMessage message, ToolStepResult step, bool atLimit)
        {
            if (mission.Pending != turn || !mission.InFlight) return;
            mission.InFlight = false;
            mission.Activity = "";
            turn.Cancel.Dispose();
            if (mission != _mission) return;
            try
            {
                if (mission.Stage == MissionStage.Lost || mission.DeferredRestore != null)
                {
                    mission.Pending = null;
                    return;
                }
                if (step == null)
                {
                    Failed(mission, turn, "A Planning Tool failed; see KSP.log.");
                    return;
                }
                var repeats = ToolRepeats.Of(turn.Messages, message);
                // Byte for byte as Claude sent it, then the results: the next request is this one plus these two messages.
                turn.Messages.Add(message.ToMessage());
                turn.Messages.Add(step.Results);
                // The next request is the last that may call a tool (#509): a Mission Plan not handed in yet is asked for now.
                if (turn.ToolSteps + 1 == ToolStep.MaxSteps - 1 && IsMissionPlanTurn(mission, turn) && ProposedPlans.LastAccepted(turn.Messages) == null)
                    step.Results.Content.Add(new TextBlock { Text = MissionPrompts.ProposePlanNow });
                turn.ToolSteps++;
                for (var i = 0; i < step.Calls.Count; i++)
                {
                    var call = step.Calls[i];
                    turn.Stats.Called(call.Name);
                    Note(LogKind.Notice, "Planning Tool " + call.Name + " (" + Shorten(call.InputJson) + "): " + (call.IsError ? "error: " : "") + call.Summary);
                    Event(mission, "tool", call.Name + " " + call.InputJson + " -> " + (call.IsError ? "error: " : "") + call.Summary);
                    if (call.Refusal.Length == 0) continue;
                    turn.Stats.Refused(call.Refusal == repeats[i]);
                    Event(mission, "tool_refused", call.Name + " " + call.InputJson + " -> " + call.Refusal + " (this turn: " + turn.Stats.RefusedCalls + " refused, "
                        + turn.Stats.RepeatCalls + (turn.Stats.RepeatCalls == 1 ? " repeat)" : " repeats)"));
                }
                // Paused while the tools ran (at the Spending Cap after the step's charge): the loop goes, the turn waits for Retry.
                if (mission.Stage == MissionStage.Paused)
                {
                    DiscardLoop(mission, turn);
                    return;
                }
                NextStep(mission, turn);
            }
            finally
            {
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
            }
        }

        /// <summary>The next step goes only if the Spending Cap has room for its worst case; with none, the loop is discarded and the Mission Pauses (or the call waits, as any waiting turn does).</summary>
        private void NextStep(Mission mission, Turn turn)
        {
            // At the step limit the next request is the last: tool_choice none, the tools list and system prompt unchanged (#509). The cap check below prices it like any step.
            turn.ToolLimitSent = turn.ToolSteps >= ToolStep.MaxSteps;
            var worst = RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, turn));
            if (mission.Committed + worst <= mission.SpendingCap)
            {
                if (turn.ToolLimitSent) Event(mission, "tool_limit_final", turn.Purpose + " after " + turn.ToolSteps + " steps; the reply is asked for with tool_choice none");
                Send(mission, turn);
                return;
            }
            LogCost(mission, "blocked", worst, "over_cap");
            DiscardLoop(mission, turn);
            if (turn.Waits || IsRunning(mission) || mission.Blackout) WaitForCap(mission);
            else PauseForCap(mission, "Claudia's next calculation step could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). Raise cap to carry on, or End Mission.");
        }

        /// <summary>
        /// Drops a turn's tool loop: the turn goes back to its first form and is sent again from scratch, with the Game Snapshot
        /// of that moment (<see cref="ResendFromScratch"/>). Nothing for a turn that has had no tool step.
        /// </summary>
        private void DiscardLoop(Mission mission, Turn turn)
        {
            if (turn.ToolSteps == 0) return;
            if (IsMissionPlanTurn(mission, turn)) LogMissionPlan(mission, turn, true);
            Event(mission, "tool_loop_discarded", turn.Purpose + " after " + turn.ToolSteps + " step" + (turn.ToolSteps == 1 ? "" : "s"));
            ResendFromScratch(mission, turn, null);
        }

        private static string Shorten(string input)
        {
            input = input ?? "";
            return input.Length <= 160 ? input : input.Substring(0, 157) + "...";
        }
    }
}
