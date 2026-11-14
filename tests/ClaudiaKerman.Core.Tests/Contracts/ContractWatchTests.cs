// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1025, seam 1 (the Mission in Core, driven by Game Snapshots with Contracts, Claudia stubbed): the mod watches the chosen Contracts
    /// through the flight with no request of its own. The Script Result carries each served Requirement's state; a leg serving one holds
    /// until the game marks it met, or 60 s of game time; failures, expiries and fall-backs reach the next Debrief as data, and a revised
    /// plan may drop those Contracts, never add one the player didn't ask for; a completed Contract gets the mod's own chat line.
    /// </summary>
    public sealed class ContractWatchTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const double EndedAt = 448444;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private DateTime _now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The Script Result carries the served Requirement's state ----

        [Fact]
        public void TheDebriefCarriesTheStateOfEachRequirementTheFlownLegServed()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt, "Mun", "ORBITING", "Complete"));

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);

            var debrief = LastUserText(transport.Requests.Last(IsMission));
            Assert.True(debrief.Contains("Contract Requirements this sequence served"), debrief);
            Assert.Contains("\"contract\":\"Explore the Mun\"", debrief);
            Assert.Contains("\"requirement\":\"orbit the Mun\"", debrief);
            Assert.Contains("\"state\":\"met\"", debrief);
        }

        // ---- Helpers ----

        private static bool IsMission(HttpRequestSpec request)
        {
            return request.Body.Contains(ProposedPlans.ToolName);
        }

        internal static GameSnapshot At(double ut, string body, string situation, string state, string contractState = "Active")
        {
            var snapshot = ContractCheckTests.With(Contract(state, contractState));
            snapshot.Ut = ut;
            snapshot.Body = body;
            snapshot.Situation = situation;
            return snapshot;
        }

        internal static SnapshotContract Contract(string state = "Incomplete", string contractState = "Active")
        {
            var contract = ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT");
            contract.State = contractState;
            contract.Requirements[0].State = state;
            return contract;
        }

        private MissionControl Running(StreamingFakeTransport transport, out StartScript start)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => _now;
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(ContractCheckTests.With(Contract()));
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            start = mission.TakeCommands().OfType<StartScript>().Single();
            return mission;
        }

        private static StreamingFakeTransport Transport(params ScriptedResponse[] after)
        {
            var responses = new List<ScriptedResponse>
            {
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, PlanJson("Transfer to the Mun and capture into low orbit", "Return to Kerbin"))),
                MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
            };
            responses.AddRange(after);
            return new StreamingFakeTransport(responses.ToArray());
        }

        internal static string PlanJson(params string[] names)
        {
            return "{\"why\":\"Because.\",\"sequences\":["
                + string.Join(",", names.Select(n => "{\"name\":" + JsonSerializer.Serialize(n) + ",\"intent\":\"Fly it.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}"))
                + "],\"total_delta_v\":" + (names.Length * 100) + ",\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\\\"\\\"\",\"child_vessels\":[],\"goal_steps\":[]}";
        }

        internal static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = new StringBuilder();
            sse.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3900,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n");
            sse.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            sse.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n");
            sse.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            sse.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":90,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3900,\"output_tokens\":90}]}}\n\n");
            sse.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        internal static ScriptedResponse MissionPlanKind()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Here.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}");
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using var doc = JsonDocument.Parse(request.Body);
            var last = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
            var content = last.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
        }
    }
}
