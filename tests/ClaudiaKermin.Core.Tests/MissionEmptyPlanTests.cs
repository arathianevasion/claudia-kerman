// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// A Mission Plan with no sequences is how Claudia declines a goal that can't be flown (issue #117):
    /// a valid reply, kept and paid for, with Change something and End Mission but nothing to Approve.
    /// Driven through the Mission seam with hand-written streams.
    /// </summary>
    public sealed class MissionEmptyPlanTests : IDisposable
    {
        private const string Goal = "Quicksave the game with a kOS script, then report.";
        private const string Say = "0 m/s, no sequences: I can't quicksave from a script";
        private const string Why = "A quicksave from kOS is on the forbidden list, so no script can fly this goal.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        /// <summary>Start in Flight and run until the first reply has been applied (or the Mission paused).</summary>
        private void ToFirstReply(MissionControl mission)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval || v.Stage == MissionStage.Paused);
        }

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                return string.Join("\n", messages.Select(m => m.GetRawText()));
            }
        }

        [Fact]
        public void AMissionPlanWithNoSequencesIsClaudiasAnswerNotAFormatError()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Empty(mission.View.PauseReason);
            var claudia = mission.View.Log.Where(e => e.Kind == LogKind.Claudia).ToArray();
            var line = Assert.Single(claudia);
            Assert.Contains(Say, line.Text);
            Assert.Contains(Why, line.Text);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.StartsWith(Say, mission.View.LatestLine);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, mission.View.QuickReplies);
        }

        [Fact]
        public void ThereIsNothingToApproveWhenThePlanHasNoSequences()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new Approve());
            mission.Send(new ConfirmApproval(true));
            _background.Drain();

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void ChangeSomethingPlansAgainWithThePlayersTextAndANormalPlanCanBeApproved()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-empty.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new ChangeSomething("Just prove the kOS link instead."));
            DrainUntil(mission, v => transport.Requests.Count == 2 && v.Stage == MissionStage.AwaitingPlanApproval && v.PlanSequences.Count == 1);
            Assert.Contains("Just prove the kOS link instead.", LastUserText(transport.Requests[1]));
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        [Fact]
        public void TheReplyIsPaidForAndLoggedAsCompletedNotOffSchema()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.True(mission.View.Spent > 0m);
            var log = CostLog(mission);
            Assert.Contains("\"event\":\"paid\"", log);
            Assert.Contains("\"outcome\":\"completed\"", log);
            Assert.DoesNotContain("off_schema", log);
        }

        [Fact]
        public void AReplyWithNoMissionPlanStillPausesAsNotFitting()
        {
            // mission-answer.sse is a well-formed reply with no mission_plan object.
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains("didn't fit: no Mission Plan", mission.View.PauseReason);
            Assert.Contains("\"outcome\":\"off_schema\"", CostLog(mission));
        }

        [Fact]
        public void TheWaitSurvivesASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);
            var saved = mission.ExportStates();

            mission.Send(new MissionsRestored(_save.Path, saved));

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Empty(mission.View.PlanSequences);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Claudia && e.Text.Contains(Say));
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Single(transport.Requests);

            // A fresh game over the same save comes back to the same place.
            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.AwaitingPlanApproval, reloaded.View.Stage);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, reloaded.View.QuickReplies);
        }

        // ---- Typed words at the decline (#698) ----

        private static ScriptedResponse Answer(string intent)
        {
            var text = System.Text.Json.JsonSerializer.Serialize("{\"kind\":\"answer\",\"say\":\"Approved: the plan goes ahead.\",\"reply_intent\":\"" + intent
                + "\",\"mission_plan\":null,\"sequence_plan\":null,\"debrief\":null}");
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = System.Text.Encoding.UTF8.GetBytes(sse) };
        }

        private const string NoPlanNotice = "There was no plan to approve: Claudia is making a new one.";

        [Theory]
        [InlineData("approve")]
        [InlineData("unclear")]
        public void ATypedReplyReadAsApproveOrUnclearAtTheDeclineAsksForANewPlanOnceWithThePlayersWords(string intent)
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"), Answer(intent), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new Reply("continue"));
            DrainUntil(mission, v => transport.Requests.Count == 3 && !v.Busy && v.PlanSequences.Count == 1);

            Assert.Contains("continue", LastUserText(transport.Requests[2]));
            Assert.Contains("wants a change", LastUserText(transport.Requests[2]));
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == NoPlanNotice);
            Assert.Null(mission.View.ConfirmPrompt);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            // Nothing flew: the new plan waits for the player's Approval (ADR 0002).
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
        }

        [Fact]
        public void ATypedReplyReadAsAQuestionAtTheDeclineBeginsNothing()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"), Answer("question"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new Reply("why not?"));
            DrainUntil(mission, v => transport.Requests.Count == 2 && !v.Busy);

            Assert.Equal(2, transport.Requests.Count);
            Assert.DoesNotContain(mission.View.Log, e => e.Text == NoPlanNotice);
            Assert.Empty(mission.View.PlanSequences);
        }

        [Fact]
        public void AQuestionReadAsApproveAtTheDeclineAsksForANewPlanToo()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"), Answer("approve"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new AskQuestion("go on then"));
            DrainUntil(mission, v => transport.Requests.Count == 3 && !v.Busy && v.PlanSequences.Count == 1);

            Assert.Contains("go on then", LastUserText(transport.Requests[2]));
            Assert.Single(mission.View.Log, e => e.Text == NoPlanNotice);
        }

        [Fact]
        public void TheDeclineLineIsInTheTypedReplyAndQuestionRequestsOnlyWhileThePlanIsADecline()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"), Answer("question"), Answer("question"),
                ScriptedResponse.Fixture("mission-plan.sse"), Answer("question"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new Reply("why?"));
            DrainUntil(mission, v => transport.Requests.Count == 2 && !v.Busy);
            mission.Send(new AskQuestion("why?"));
            DrainUntil(mission, v => transport.Requests.Count == 3 && !v.Busy);
            Assert.Contains(MissionPrompts.DeclinedPlanLine, transport.Requests[1].Body);
            Assert.Contains(MissionPrompts.DeclinedPlanLine, transport.Requests[2].Body);

            mission.Send(new ChangeSomething("Just prove the kOS link instead."));
            DrainUntil(mission, v => transport.Requests.Count == 4 && !v.Busy && v.PlanSequences.Count == 1);
            mission.Send(new Reply("why?"));
            DrainUntil(mission, v => transport.Requests.Count == 5 && !v.Busy);
            // Earlier turns keep their own notice in the history; only the last message is this request's.
            using (var doc = JsonDocument.Parse(transport.Requests[4].Body))
                Assert.DoesNotContain(MissionPrompts.DeclinedPlanLine, doc.RootElement.GetProperty("messages").EnumerateArray().Last().GetRawText());
        }

        [Fact]
        public void TheStripNamesTheDeclinedState()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.True(mission.View.PlanDeclined);
            Assert.Equal("Claudia declined the goal: Change something or End Mission", ClaudiaKermin.Core.Ui.MissionWindowRules.NeedsYou(mission.View));
        }

        [Fact]
        public void AutoModeStillNeverApprovesADeclinedPlan()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new SetAutoMode(true));
            _background.Drain();

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void EndMissionWorksWhileWaitingHere()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new EndMission());
            Assert.False(mission.View.CanEndMission);
            Assert.True(mission.View.StartForm.CanStart);
        }
    }
}
