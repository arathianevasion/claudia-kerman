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
        /// <paramref name="snapshot"/> and <paramref name="goal"/>, copied before the tool step, are for the planet guard (#670).
        /// </summary>
        public static ToolOutcome Check(ToolUseBlock use, ICollection<string> leftTheCraft, bool needsNewGoal, bool allowsNewGoal = true,
            GameSnapshot snapshot = null, string goal = null)
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
                // The planet guard (#670): until #459's transfer_to flies to planets, a planet goal is declined. #459 removes it.
                var planet = plan.Sequences.Count > 0 ? PlanetGoal(snapshot, allowsNewGoal && plan.NewGoal != null ? plan.NewGoal : goal) : null;
                if (planet != null)
                    problems.Add("no Building Block flies to a planet yet, and " + planet + " needs a transfer to another planet: "
                        + "hand in a Mission Plan with no sequences, and say so in say and why");
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

        /// <summary>
        /// The body <paramref name="goal"/> names that needs a transfer to another planet (#670): one the craft's planet transfers to,
        /// as <see cref="WindowWaits.Planet"/> reads the goal, or a moon of one (Ike, Laythe); null for none. Goes with #459.
        /// </summary>
        internal static string PlanetGoal(GameSnapshot snapshot, string goal)
        {
            if (snapshot == null || string.IsNullOrEmpty(goal)) return null;
            var body = MissionPrompts.GoalBodies(snapshot, goal).FirstOrDefault(b => TransferWindows.IsPlanetTransfer(snapshot, b.Name)
                || (!string.IsNullOrEmpty(b.Parent) && TransferWindows.IsPlanetTransfer(snapshot, b.Parent)));
            return body != null ? body.Name : null;
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
