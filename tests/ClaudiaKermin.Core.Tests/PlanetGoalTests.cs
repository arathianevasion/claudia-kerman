// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Until transfer_to flies to planets (#459), a Mission Goal that needs a transfer to another planet, or to a moon of one, is
    /// declined (#670): propose_mission_plan refuses a plan with sequences for it, and plan_transfer's planet answer says so.
    /// </summary>
    public sealed class PlanetGoalTests : IDisposable
    {
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", name));
        }

        /// <summary>The stock body table, with the craft in a 100 km circular orbit of Kerbin.</summary>
        private static GameSnapshot Parked()
        {
            var snapshot = GameSnapshot.Parse(Fixture("mun-shot-v2-kerbin-orbit.json"));
            snapshot.Ut = 0;
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = 700000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000 };
            return snapshot;
        }

        /// <summary>The craft on the pad with the stock body table (Duna in it).</summary>
        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(Fixture("on-pad.json"));
            snapshot.Bodies = GameSnapshot.Parse(Fixture("mun-shot-v2-kerbin-orbit.json")).Bodies;
            snapshot.Stages[0].DeltaVVac = 9000;
            return snapshot;
        }

        private static string Plan(bool withSequence, string newGoal = "")
        {
            var sequences = withSequence
                ? "[{\"name\":\"Ejection\",\"intent\":\"Burn for the target.\",\"delta_v\":1100,\"timing\":\"at the window\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}]"
                : "[]";
            return "{\"why\":\"No Building Block flies to a planet yet.\",\"sequences\":" + sequences + ",\"total_delta_v\":0,\"expected_duration\":\"none\",\"mission_time_s\":-1,"
                + "\"new_goal\":" + JsonSerializer.Serialize(newGoal) + ",\"child_vessels\":[],\"goal_steps\":[\"Decline\"]}";
        }

        private static ToolOutcome Check(string goal, bool withSequence, string newGoal = "", bool allowsNewGoal = false)
        {
            var use = new ToolUseBlock { Id = "u", Name = ProposedPlans.ToolName, InputJson = Plan(withSequence, newGoal), InputIsValid = true };
            return ProposedPlans.Check(use, new List<string>(), false, allowsNewGoal, Parked(), goal);
        }

        private static string Problem(ToolOutcome outcome)
        {
            using (var answer = JsonDocument.Parse(outcome.Json))
            {
                Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());
                return Assert.Single(answer.RootElement.GetProperty("problems").EnumerateArray()).GetString();
            }
        }

        // ---- propose_mission_plan ----

        [Theory]
        [InlineData("Fly to Duna orbit", "Duna")]
        [InlineData("Land on Ike", "Ike")]
        [InlineData("Orbit Laythe, then come home", "Laythe")]
        public void APlanWithSequencesForAPlanetOrItsMoonIsRefusedAndAnEmptyOneIsAccepted(string goal, string body)
        {
            var problem = Problem(Check(goal, true));
            Assert.Contains("no Building Block flies to a planet yet", problem);
            Assert.Contains(body, problem);
            Assert.Contains("no sequences", problem);
            Assert.Equal(ProposedPlans.AcceptedJson, Check(goal, false).Json);
        }

        [Theory]
        [InlineData("Land on the Mun")]
        [InlineData("Orbit Minmus")]
        [InlineData("Reach a 100 km orbit of Kerbin")]
        [InlineData("Prove the kOS link")]
        public void APlanWithSequencesForKerbinOrItsMoonsIsAccepted(string goal)
        {
            Assert.Equal(ProposedPlans.AcceptedJson, Check(goal, true).Json);
        }

        [Fact]
        public void ANewGoalNamingDunaIsRefusedWhereTheRequestAllowsOne()
        {
            Assert.Contains("no Building Block flies to a planet yet", Problem(Check("Land on the Mun", true, "Fly to Duna orbit", true)));
            Assert.Equal(ProposedPlans.AcceptedJson, Check("Land on the Mun", false, "Fly to Duna orbit", true).Json);
            // A new goal away from Duna is flown; one the request ignores leaves the Mission Goal to decide.
            Assert.Equal(ProposedPlans.AcceptedJson, Check("Fly to Duna orbit", true, "Land on the Mun", true).Json);
            Assert.Equal(ProposedPlans.GoalKeptJson, Check("Land on the Mun", true, "Fly to Duna orbit", false).Json);
        }

        [Fact]
        public void WithNoSnapshotThereIsNoPlanetCheck()
        {
            var use = new ToolUseBlock { Id = "u", Name = ProposedPlans.ToolName, InputJson = Plan(true), InputIsValid = true };
            Assert.Equal(ProposedPlans.AcceptedJson, ProposedPlans.Check(use, new List<string>(), false, false, null, "Fly to Duna orbit").Json);
        }

        // ---- plan_transfer ----

        [Fact]
        public void PlanTransfersPlanetAnswerSaysToDeclineAndItsMoonAnswerDoesNot()
        {
            var tools = PlanningTools.Default();
            var duna = tools.Run("plan_transfer", "{\"target\":\"Duna\",\"target_pe\":100000}", Parked(), new NoPlanningBody());
            Assert.False(duna.IsError, duna.Json);
            using (var doc = JsonDocument.Parse(duna.Json))
                Assert.Contains("no Building Block flies to a planet yet", doc.RootElement.GetProperty("decline").GetString());
            var mun = tools.Run("plan_transfer", "{\"target\":\"Mun\",\"target_pe\":30000}", Parked(), new NoPlanningBody());
            Assert.False(mun.IsError, mun.Json);
            using (var doc = JsonDocument.Parse(mun.Json))
                Assert.False(doc.RootElement.TryGetProperty("decline", out _));
        }

        [Fact]
        public void PlanTransfersDescriptionPromisesNoBurnForAPlanetAndStaysAsShort()
        {
            var description = PlanningTools.Default().Definitions(ToolOffer.Mission).Single(t => t.Name == "plan_transfer").Description;
            Assert.InRange(description.Length, 1, 457);
            Assert.Contains("Its block is transfer_to", description);
            Assert.Contains("a planet: soonest and optimal dates and delta-v, no burn", description);
        }

        // ---- Through the Mission seam ----

        private static ScriptedResponse Text(string json)
        {
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(json) + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        private static List<string> Results(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var last = doc.RootElement.GetProperty("messages").EnumerateArray().Last();
                return last.GetProperty("content").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "tool_result")
                    .Select(c => c.GetProperty("content").GetString()).ToList();
            }
        }

        [Fact]
        public void ADunaGoalIsRefusedWithSequencesAndEndsDeclinedWithHerLine()
        {
            const string say = "0 m/s, no sequences: no Building Block flies to Duna yet";
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("bad", (ProposedPlans.ToolName, Plan(true))),
                ToolUseStreams.ToolUse("empty", (ProposedPlans.ToolName, Plan(false))),
                Text("{\"kind\":\"mission_plan\",\"say\":\"" + say + "\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}"));
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Fly to Duna orbit", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => (v.Stage == MissionStage.AwaitingPlanApproval || v.Stage == MissionStage.Paused) && !v.Busy);

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("no Building Block flies to a planet yet", Assert.Single(Results(transport.Requests[1])));
            Assert.Equal(ProposedPlans.AcceptedJson, Assert.Single(Results(transport.Requests[2])));
            Assert.Empty(mission.View.PlanSequences);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, mission.View.QuickReplies);
            var line = Assert.Single(mission.View.Log, e => e.Kind == LogKind.Claudia);
            Assert.Contains(say, line.Text);
            Assert.Contains("No Building Block flies to a planet yet.", line.Text);
        }
    }
}
