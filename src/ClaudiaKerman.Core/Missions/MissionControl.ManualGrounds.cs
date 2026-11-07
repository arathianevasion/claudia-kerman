// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #818: a manual stretch with no grounds (<see cref="ManualGrounds"/>) is refused before the player sees it, and goes back to
    /// Claudia once with the reason, Auto Mode staying on. A second one Pauses the Mission with a request to the player. A Failed
    /// Debrief that is only a landing miss is sent back once the same way.
    /// </summary>
    public sealed partial class MissionControl
    {
        private static bool ManualHasGrounds(Mission mission, SequencePlan plan)
        {
            if (ManualGrounds.Asked(mission.Goal, ManualGrounds.TypedWords(mission.Log), mission.TookManual)) return true;
            if (ManualGrounds.ServesEva(plan, mission.EvaScience)) return true;
            // The player approved a Mission Plan whose step is itself a manual stretch.
            var steps = mission.Plan == null ? null : mission.Plan.Sequences;
            return steps != null && plan.PlanStep.HasValue && plan.PlanStep.Value >= 1 && plan.PlanStep.Value <= steps.Count && steps[plan.PlanStep.Value - 1].Manual;
        }

        private void ManualRefused(Mission mission, Turn turn, SequencePlan plan)
        {
            var reason = ManualGrounds.Refusal(plan.Name);
            if (turn.ManualRetry)
            {
                Failed(mission, turn, "Claudia still plans a manual stretch (" + MissionPrompts.Quoted(plan.Name) + ") that nothing asked for. Reply that you will fly it by hand, or Retry with a note asking for a script.");
                return;
            }
            Note(LogKind.Notice, "The Sequence Plan " + MissionPrompts.Quoted(plan.Name) + " was a manual stretch nothing asked for; Claudia is revising it.");
            Begin(new Turn
            {
                Purpose = turn.Purpose,
                ManualRetry = true,
                Waits = turn.Waits,
                GoalStands = turn.GoalStands,
                ScriptRewrite = turn.ScriptRewrite,
                Stats = turn.Stats,
                PlayerText = turn.PlayerText,
                // The refused turn was never committed, so what it asked goes again with the reason.
                UserText = turn.UserText + "\n" + reason,
                Notice = turn.Notice,
            });
        }

        /// <summary>
        /// A Failed Debrief for a craft down with its crew alive, on a landing step, with no precision goal: sent back once. True when
        /// it was; the reply is then not committed.
        /// </summary>
        private bool LandingMissSentBack(Mission mission, Turn turn, MissionReply reply, AssistantMessage message)
        {
            if (turn.Purpose != Purpose.Debrief || reply.Debrief == null || reply.Debrief.Outcome != "Failed" || turn.LandingMissSentBack || mission.DeferredRestore != null) return false;
            var plan = mission.PendingSequence;
            if (plan == null || !ManualGrounds.LandingStep(plan.Script) || ManualGrounds.PrecisionGoal(mission.Goal)) return false;
            if (!ManualGrounds.DownAlive(MissionVesselSnapshot(mission), mission.RunStartCrew)) return false;
            turn.LandingMissSentBack = true;
            // Her Failed reply stays in front of her, so "reply Failed again" has something to answer; none of it is committed unless she stands by it.
            var before = turn.Messages.Count;
            turn.Messages.Add(message.ToMessage());
            turn.Messages.Add(Message.SystemText(ManualGrounds.SendBack));
            if (!HasCapRoom(mission, turn))
            {
                turn.Messages.RemoveRange(before, turn.Messages.Count - before);
                return false;
            }
            Event(mission, "debrief_retry", "Failed on a landing step with the craft down and the crew alive");
            mission.Activity = Thinking(turn);
            Send(mission, turn);
            return true;
        }
    }
}
