// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The Mission Goal's steps (#581): propose_mission_plan defines them once as goal_steps, each Debrief ticks them by number
    /// in goal_steps_done, and the Mission log rebuilt from a transcript has them as the loop had them, an old inline goal_checklist too.
    /// </summary>
    public sealed class MissionGoalStepsTests
    {
        private static readonly string[] Five = { "Reach orbit", "Transfer to the Mun", "Land on the Mun", "Run 3 experiments", "Return to Kerbin" };

        // ---- The rules ----

        [Fact]
        public void ADebriefTicksTheStepsItNamesAndIgnoresANumberOutsideTheList()
        {
            var steps = GoalSteps.Replace(new List<GoalItem>(), Five);
            GoalSteps.Tick(steps, new[] { 1, 3, 9, 0, -2 });
            Assert.Equal(new[] { true, false, true, false, false }, steps.Select(s => s.Done));
        }

        [Fact]
        public void ATickStaysWhenALaterDebriefLeavesItsStepOut()
        {
            var steps = GoalSteps.Replace(new List<GoalItem>(), Five);
            GoalSteps.Tick(steps, new[] { 1 });
            GoalSteps.Tick(steps, new[] { 2 });
            Assert.Equal(new[] { true, true, false, false, false }, steps.Select(s => s.Done));
        }

        [Fact]
        public void ARevisedPlanReplacesTheListAndAStepWhoseTextIsUnchangedKeepsItsTick()
        {
            var steps = GoalSteps.Replace(new List<GoalItem>(), Five);
            GoalSteps.Tick(steps, new[] { 1, 2 });
            var revised = GoalSteps.Replace(steps, new[] { "Reach orbit", "Transfer to Minmus", "Land on Minmus", "Return to Kerbin" });
            Assert.Equal(new[] { "Reach orbit", "Transfer to Minmus", "Land on Minmus", "Return to Kerbin" }, revised.Select(s => s.Item));
            Assert.Equal(new[] { true, false, false, false }, revised.Select(s => s.Done));
        }

        [Fact]
        public void APlanWithNoStepsLeavesTheListAsItIs()
        {
            var steps = GoalSteps.Replace(new List<GoalItem>(), Five);
            GoalSteps.Tick(steps, new[] { 1 });
            Assert.Same(steps, GoalSteps.Replace(steps, new string[0]));
        }

        [Fact]
        public void AMissionWithNoStepsTakesADebriefsNumbersWithoutComplaint()
        {
            var steps = new List<GoalItem>();
            GoalSteps.Tick(steps, new[] { 1, 2 });
            Assert.Empty(steps);
        }

        // ---- The parts ----

        [Fact]
        public void TheProposedPlansGoalStepsAreRead()
        {
            var plan = MissionReply.Parse("{\"mission_plan\":" + Plan("\"goal_steps\":[\" Reach orbit \",\"\",\"Land on the Mun\"]") + "}").MissionPlan;
            Assert.Equal(new[] { "Reach orbit", "Land on the Mun" }, plan.GoalSteps);
        }

        [Fact]
        public void ADebriefsGoalStepsDoneAreRead()
        {
            var debrief = MissionReply.Parse(Debrief("Nominal", "\"goal_steps_done\":[1,3]")).Debrief;
            Assert.Equal(new[] { 1, 3 }, debrief.GoalStepsDone);
            Assert.Empty(debrief.Checklist);
        }

        [Fact]
        public void TheToolTakesAPlanWithGoalStepsAndAsksForThemWhenMissing()
        {
            var ok = ProposedPlans.Check(Use(Plan("\"goal_steps\":[\"Reach orbit\"]")), new List<string>(), false);
            Assert.Equal(ProposedPlans.AcceptedJson, ok.Json);
            var missing = ProposedPlans.Check(Use(Plan(null)), new List<string>(), false);
            Assert.Contains("goal_steps", missing.Json);
        }

        [Fact]
        public void AnAcceptedPlanFromBeforeGoalStepsStillReadsBack()
        {
            // A transcript from before #581: the tool accepted the plan without goal_steps.
            var use = Use(Plan(null));
            var messages = new List<Message> { Assistant(use), ToolResult(use.Id, ProposedPlans.AcceptedJson) };
            var plan = ProposedPlans.LastAccepted(messages);
            Assert.NotNull(plan);
            Assert.Empty(plan.GoalSteps);
        }

        [Fact]
        public void TheDebriefPartTakesGoalStepsDoneInPlaceOfGoalChecklist()
        {
            var debrief = JsonReader.Parse(MissionReply.MissionSchemaJson)["properties"]["debrief"]["anyOf"].Items.First();
            var required = debrief["required"].Items.Select(JsonNode.Str).ToList();
            Assert.Contains("goal_steps_done", required);
            Assert.DoesNotContain("goal_checklist", required);
            Assert.Equal("integer", JsonNode.Str(debrief["properties"]["goal_steps_done"]["items"]["type"]));
        }

        // ---- The rebuild ----

        [Fact]
        public void TheRebuildTicksTheStepsAsTheLoopDidAndTheCardCountsThem()
        {
            List<GoalItem> steps;
            var log = MissionLogRebuild.From(Transcript(
                PlanTurn("p1", Plan("\"goal_steps\":" + Json(Five))),
                Debrief("Nominal", "\"goal_steps_done\":[1]"),
                Debrief("Nominal", "\"goal_steps_done\":[2,9]")), "Mun", "Craft", new PlayerConfig(), null, out steps);

            Assert.Equal(new[] { true, true, false, false, false }, steps.Select(s => s.Done));
            var cards = log.Where(e => e.Kind == LogKind.Debrief).Select(e => e.Debrief).ToList();
            Assert.Equal(new[] { 1, 2 }, cards.Select(c => c.ChecklistDone));
            Assert.All(cards, c => Assert.Equal(5, c.Checklist.Count));
        }

        [Fact]
        public void TheRebuildTakesARevisedPlansStepsKeepingTicksOnUnchangedText()
        {
            List<GoalItem> steps;
            MissionLogRebuild.From(Transcript(
                PlanTurn("p1", Plan("\"goal_steps\":" + Json(Five))),
                Debrief("Nominal", "\"goal_steps_done\":[1]"),
                PlanTurn("p2", Plan("\"goal_steps\":[\"Reach orbit\",\"Land on Minmus\"]"))), "Mun", "Craft", new PlayerConfig(), null, out steps);
            Assert.Equal(new[] { "Reach orbit", "Land on Minmus" }, steps.Select(s => s.Item));
            Assert.Equal(new[] { true, false }, steps.Select(s => s.Done));
        }

        [Fact]
        public void AnOldTranscriptsInlineGoalChecklistRebuildsItsCardButGivesTheMissionNoSteps()
        {
            List<GoalItem> steps;
            var log = MissionLogRebuild.From(Transcript(
                PlanTurn("p1", Plan(null)),
                Debrief("Off-Nominal", "\"goal_checklist\":[{\"item\":\"Reach orbit\",\"done\":true},{\"item\":\"Land on the Mun\",\"done\":false}]"),
                Debrief("Nominal", "\"goal_steps_done\":[2]")), "Mun", "Craft", new PlayerConfig(), null, out steps);

            var cards = log.Where(e => e.Kind == LogKind.Debrief).Select(e => e.Debrief).ToList();
            Assert.Equal(1, cards[0].ChecklistDone);
            Assert.Equal(new[] { "Reach orbit", "Land on the Mun" }, cards[0].Checklist.Select(s => s.Item));
            // Until a Mission Plan supplies goal_steps, the Mission has none, so a later Debrief's numbers tick nothing.
            Assert.False(cards[1].HasChecklist);
            Assert.Empty(steps);
        }

        [Fact]
        public void AMissionWithNoStepsHasNoChecklistOnItsCards()
        {
            List<GoalItem> steps;
            var log = MissionLogRebuild.From(Transcript(PlanTurn("p1", Plan(null)), Debrief("Nominal", "\"goal_steps_done\":[1]")),
                "Mun", "Craft", new PlayerConfig(), null, out steps);
            Assert.Empty(steps);
            Assert.False(log.Single(e => e.Kind == LogKind.Debrief).Debrief.HasChecklist);
        }

        [Fact]
        public void ALeanNominalDebriefsCardHasNoEmptyLineBeforeNext()
        {
            var log = MissionLogRebuild.From(Transcript(Debrief("Nominal", "\"what_happened\":\"\"")), "", "Craft", new PlayerConfig());
            Assert.Equal("Next: Fly on.", log.Single(e => e.Kind == LogKind.Debrief).Text);
        }

        // ---- Helpers ----

        private static string Plan(string goalSteps)
        {
            return "{\"why\":\"Prove the link.\",\"sequences\":[{\"name\":\"Launch\",\"intent\":\"Climb.\",\"delta_v\":3400,\"timing\":\"now\","
                + "\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}],\"total_delta_v\":3400,\"expected_duration\":\"an hour\",\"mission_time_s\":-1,"
                + "\"new_goal\":\"\",\"child_vessels\":[]" + (goalSteps != null ? "," + goalSteps : "") + "}";
        }

        private static string Debrief(string outcome, string fields)
        {
            return "{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"" + outcome + "\",\"why\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\","
                + "\"follow_up\":\"none\",\"follow_up_text\":\"\"," + fields + "}}";
        }

        private static string Json(IEnumerable<string> items)
        {
            return "[" + string.Join(",", items.Select(i => "\"" + i + "\"")) + "]";
        }

        private static ToolUseBlock Use(string input, string id = "p")
        {
            return new ToolUseBlock { Id = id, Name = ProposedPlans.ToolName, InputJson = input, InputIsValid = true };
        }

        private static Message Assistant(params ContentBlock[] blocks)
        {
            var message = new Message { Role = Role.Assistant };
            message.Content.AddRange(blocks);
            return message;
        }

        private static Message ToolResult(string id, string json)
        {
            var message = new Message { Role = Role.User };
            message.Content.Add(new ToolResultBlock { ToolUseId = id, Content = json });
            return message;
        }

        /// <summary>A plan turn: the tool call, its ok, and the mission_plan reply.</summary>
        private static Message[] PlanTurn(string id, string plan)
        {
            var use = Use(plan, id);
            return new[]
            {
                Message.UserText("Plan it."),
                Assistant(use),
                ToolResult(id, ProposedPlans.AcceptedJson),
                Assistant(new TextBlock { Text = "{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}" }),
            };
        }

        private static StoredTranscript Transcript(params object[] parts)
        {
            var transcript = new StoredTranscript();
            foreach (var part in parts)
            {
                if (part is Message[] turn) transcript.Messages.AddRange(turn);
                else
                {
                    transcript.Messages.Add(Message.UserText("The Maneuver Sequence has ended."));
                    transcript.Messages.Add(Assistant(new TextBlock { Text = (string)part }));
                }
            }
            return transcript;
        }
    }
}
