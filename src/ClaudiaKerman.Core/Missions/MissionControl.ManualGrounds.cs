// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Tools;
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
            RefusedAtApply(mission, turn, turn.ManualRetry, reason,
                "Claudia still plans a manual stretch (" + MissionPrompts.Quoted(plan.Name) + ") that nothing asked for. Retry with a note saying you will fly it by hand, or asking for a script.",
                "The Sequence Plan " + MissionPrompts.Quoted(plan.Name) + " was a manual stretch nothing asked for; Claudia is revising it.",
                () => new Turn
                {
                    Purpose = turn.Purpose,
                    ManualRetry = true,
                    Waits = turn.Waits,
                    GoalStands = turn.GoalStands,
                    ScriptRewrite = turn.ScriptRewrite,
                    Stats = turn.Stats,
                    PlayerText = turn.PlayerText,
                    // The refused turn is committed with her reply: only the reason goes, not what it asked again.
                    UserText = reason,
                    Notice = turn.Notice,
                });
        }

        /// <summary>A Kerbal other than Claudia died (#874): kept with the UT, so #818's crew check reads deaths and not who is still aboard.</summary>
        private void CrewDeathSeen(string name)
        {
            var mission = _mission;
            if (mission == null || MissionOver || name.Length == 0) return;
            mission.CrewDeaths[name] = _ut;
        }

        private static string CrewDeathsJson(Mission mission)
        {
            if (mission.CrewDeaths.Count == 0) return "";
            var json = new JsonWriter().BeginArray();
            foreach (var d in mission.CrewDeaths) json.BeginObject().Field("name", d.Key).Field("ut", d.Value).EndObject();
            return json.EndArray().ToString();
        }

        private static void RestoreCrewDeaths(Mission mission, string text)
        {
            mission.CrewDeaths.Clear();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                foreach (var node in JsonReader.Parse(text).Items.Where(n => n.Kind == JsonKind.Object))
                {
                    var name = JsonNode.Str(node["name"]);
                    if (!string.IsNullOrEmpty(name)) mission.CrewDeaths[name] = JsonNode.Double(node["ut"]) ?? 0;
                }
            }
            catch (JsonException)
            {
                mission.CrewDeaths.Clear();
            }
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
            if (!ManualGrounds.DownAlive(MissionVesselSnapshot(mission), mission.RunStartCrew, mission.CrewDeaths, mission.RunStartUt)) return false;
            turn.LandingMissSentBack = true;
            // Her Failed reply stays in front of her, so "reply Failed again" has something to answer; it and the send-back are committed with the turn.
            // The send-back is a user message (#953): the API refuses a system message straight after an assistant one.
            var before = turn.Messages.Count;
            turn.Messages.Add(message.ToMessage());
            turn.Messages.Add(Message.UserText(ManualGrounds.SendBack));
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
