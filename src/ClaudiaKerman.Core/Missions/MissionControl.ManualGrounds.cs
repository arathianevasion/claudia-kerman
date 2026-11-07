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
            return mission.ManualAsk || ManualGrounds.ServesEva(plan, mission.EvaScience);
        }

        private void ManualRefused(Mission mission, Turn turn, SequencePlan plan)
        {
            var reason = ManualGrounds.Refusal(plan.Name);
            if (turn.ManualRetry)
            {
                // Her reply was committed, so nothing is in hand: the turn goes back in hand for the Pause, or Retry and Retry with a note have nothing to resend.
                mission.Pending = turn;
                Failed(mission, turn, "Claudia still plans a manual stretch (" + MissionPrompts.Quoted(plan.Name) + ") that nothing asked for. Retry with a note saying you will fly it by hand, or asking for a script.");
                // Retry with a note is how the player answers: it takes a Refusal pause.
                if (mission.Stage == MissionStage.Paused) mission.PauseCause = PauseCause.Refusal;
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
            if (plan == null || !ManualGrounds.LandingStep(plan.Script) || mission.PrecisionGoal) return false;
            if (!ManualGrounds.DownAlive(MissionVesselSnapshot(mission), mission.RunStartCrew)) return false;
            turn.LandingMissSentBack = true;
            // Her Failed reply stays in front of her, so "reply Failed again" has something to answer; it and the send-back are committed with the turn.
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
