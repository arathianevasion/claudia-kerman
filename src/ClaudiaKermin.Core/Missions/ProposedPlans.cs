// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// <c>propose_mission_plan</c>, the Reply Tool a Mission Plan arrives through (ADR 0012): its input is the Mission Plan part,
    /// the mod answers ok or the problems to fix, and a <c>mission_plan</c> reply takes the last plan accepted in its turn. It is
    /// non-strict, so it adds nothing to the reply schema's grammar, and on every Mission request, so the cached prefix holds.
    /// </summary>
    internal static class ProposedPlans
    {
        public const string ToolName = "propose_mission_plan";
        public const string Activity = "checking the Mission Plan";

        /// <summary>The tool result of an accepted plan; reading a transcript back, a call with this result is the accepted one.</summary>
        public const string AcceptedJson = "{\"ok\":true}";

        /// <summary>The tool result of an accepted plan whose new_goal was ignored (#590); also an accepted call, and its plan carries no new goal.</summary>
        public const string GoalKeptJson = "{\"ok\":true,\"note\":\"new_goal ignored: the Mission Goal is kept, as the player did not ask to change it.\"}";

        /// <summary>
        /// The answer to every propose_mission_plan call of a reply that holds more than one (#697): none is accepted, so the
        /// plan is never whichever came last, and as none was accepted, sending one again in the next step is no repeat.
        /// </summary>
        public const string OnePerReplyJson = "{\"ok\":false,\"problems\":[\"hand in one Mission Plan per reply: send the one you mean again on its own\"]}";

        public static ToolOutcome OnePerReply()
        {
            return new ToolOutcome { Json = OnePerReplyJson, Summary = "1 problem, one plan per reply" };
        }

        public static ToolDefinition Definition()
        {
            return new ToolDefinition
            {
                Name = ToolName,
                Description = "Only for a request that asks for a Mission Plan; never a placeholder or a first step on any other request, which you reply to directly. "
                    + "Hand in your Mission Plan. The mod answers ok, or the problems to fix before you call it again. "
                    + "Then reply with kind mission_plan: the plan is the last one it accepted in this turn.",
                InputSchemaJson = MissionReply.MissionPlanSchema,
                Strict = false,
            };
        }

        /// <summary>
        /// The answer to one call: <see cref="AcceptedJson"/>, or <c>{"ok":false,"problems":[...]}</c> as check_script answers.
        /// <paramref name="leftTheCraft"/> is the vessel ids that may be Child Vessels; <paramref name="needsNewGoal"/> when the
        /// request takes a Mission Plan only for a new Mission Goal (a typed reply to a pending Sequence Plan);
        /// <paramref name="allowsNewGoal"/> when the player may change the goal (their typed Reply or Change), else a new_goal is ignored (#590).
        /// <paramref name="goalStands"/> when the mod asked for a revision of a Mission under way whose goal stands (#697): a plan
        /// with no sequences, which declines the goal, is refused then.
        /// </summary>
        public static ToolOutcome Check(ToolUseBlock use, ICollection<string> leftTheCraft, bool needsNewGoal, bool allowsNewGoal = true,
            bool goalStands = false)
        {
            var problems = new List<string>();
            var plan = use.InputIsValid ? Read(use.InputJson, problems) : null;
            if (!use.InputIsValid) problems.Add("the input is not valid JSON");
            if (plan != null)
            {
                foreach (var id in plan.ChildVessels.Where(c => c.VesselId.Length > 0 && !leftTheCraft.Contains(c.VesselId)).Select(c => c.VesselId).Distinct())
                    problems.Add("child_vessels names vessel id " + PlanningJson.Quote(id) + ", which has not left the craft: name its separation stage instead, "
                        + "or use a vessel id from a notice that said it left");
                if (needsNewGoal && plan.NewGoal == null)
                    problems.Add("new_goal is empty: while a Sequence Plan is pending, a Mission Plan is only for a new Mission Goal; otherwise reply with a sequence_plan or an answer");
                // #670's planet guard went with #459: transfer_to flies to a planet, so a planet goal is planned like any other.
                if (goalStands && plan.Sequences.Count == 0)
                    problems.Add("this is a revision of a Mission still under way: give the sequences still to fly; "
                        + "to give up on the goal, say so in the Debrief's follow-up instead");
            }
            if (problems.Count == 0)
                return plan != null && plan.NewGoal != null && !allowsNewGoal
                    ? new ToolOutcome { Json = GoalKeptJson, Summary = "ok, goal kept" }
                    : new ToolOutcome { Json = AcceptedJson, Summary = "ok" };
            var json = new JsonWriter().BeginObject().Field("ok", false).Name("problems").BeginArray();
            foreach (var problem in problems) json.String(problem);
            return new ToolOutcome
            {
                Json = json.EndArray().EndObject().ToString(),
                Summary = problems.Count + (problems.Count == 1 ? " problem" : " problems"),
            };
        }

        /// <summary>The last plan the tool accepted in <paramref name="messages"/> (one turn's), or null when none was.</summary>
        public static MissionPlan LastAccepted(IEnumerable<Message> messages)
        {
            var list = messages.ToList();
            var results = list.SelectMany(m => m.Content.OfType<ToolResultBlock>()).Where(r => !r.IsError).ToList();
            var accepted = new HashSet<string>(results.Where(r => r.Content == AcceptedJson || r.Content == GoalKeptJson).Select(r => r.ToolUseId));
            var kept = new HashSet<string>(results.Where(r => r.Content == GoalKeptJson).Select(r => r.ToolUseId));
            var use = list.SelectMany(m => m.Content.OfType<ToolUseBlock>()).LastOrDefault(u => u.Name == ToolName && u.InputIsValid && accepted.Contains(u.Id ?? ""));
            var plan = use != null ? ReadAccepted(use.InputJson) : null;
            if (plan != null && kept.Contains(use.Id ?? "")) plan.NewGoal = null;
            return plan;
        }

        /// <summary>
        /// An accepted call's plan, not checked against the schema again: it was checked when accepted, and today's schema
        /// would refuse a call from before goal_steps (#581) and lose its plan.
        /// </summary>
        private static MissionPlan ReadAccepted(string inputJson)
        {
            var reply = MissionReply.Parse("{\"mission_plan\":" + (string.IsNullOrEmpty(inputJson) ? "{}" : inputJson) + "}");
            return reply != null ? reply.MissionPlan : null;
        }

        /// <summary>The plan in a call's input, read as a reply's mission_plan part is; null, with the problem, when it doesn't fit the schema.</summary>
        private static MissionPlan Read(string inputJson, List<string> problems)
        {
            JsonNode input;
            try
            {
                input = JsonReader.Parse(string.IsNullOrEmpty(inputJson) ? "{}" : inputJson);
            }
            catch (JsonException)
            {
                problems.Add("the input is not valid JSON");
                return null;
            }
            var problem = ToolSchema.Check(JsonReader.Parse(MissionReply.MissionPlanSchema), input, "input");
            if (problem != null)
            {
                problems.Add(problem);
                return null;
            }
            var reply = MissionReply.Parse("{\"mission_plan\":" + inputJson + "}");
            return reply != null ? reply.MissionPlan : null;
        }
    }
}
