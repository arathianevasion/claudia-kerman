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

        // ---- Holds ----

        [Fact]
        public void ALegMeetingItsRequirementHoldsTheDebriefUntilTheGameMarksItMet()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt, "Mun", "ORBITING", "Incomplete"));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(At(EndedAt + 20, "Mun", "ORBITING", "Incomplete"));
            _background.Drain();

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count(IsMission));
            Assert.Contains(mission.View.Log, e => e.Text.Contains("Holding for the game to mark \"orbit the Mun\" met"));

            mission.Send(At(EndedAt + 25, "Mun", "ORBITING", "Complete"));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);
            Assert.Contains("\"state\":\"met\"", LastUserText(transport.Requests.Last(IsMission)));
        }

        [Fact]
        public void AHoldEndsAfter60SecondsOfGameTimeWithTheGapInTheDebrief()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt, "Mun", "ORBITING", "Incomplete"));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(new UtAdvanced(EndedAt + 59));
            _background.Drain();
            Assert.Equal(3, transport.Requests.Count(IsMission));

            mission.Send(new UtAdvanced(EndedAt + 60));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);

            var debrief = LastUserText(transport.Requests.Last(IsMission));
            Assert.Contains("\"state\":\"not met\"", debrief);
            Assert.Contains("for 60 s of game time, and the game still hasn't marked it met", debrief);
            Assert.Contains("For a Requirement not met, propose a trim burn or a retry leg (priced at the plan proposal), or dropping its Contract, with the reason", debrief);
        }

        [Fact]
        public void ALegThatDidntGetThereDoesntHoldAndTheDebriefGivesTheGap()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt, "Kerbin", "SUB_ORBITAL", "Incomplete"));

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);

            var debrief = LastUserText(transport.Requests.Last(IsMission));
            Assert.Contains("\"state\":\"not met\"", debrief);
            Assert.Contains("the craft is SUB_ORBITAL at Kerbin, and the Requirement asks it to orbit the Mun", debrief);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("Holding for the game"));
        }

        // ---- In flight: the completion line, failures and fall-backs, with no request and no Alert ----

        [Fact]
        public void ACompletionMidFlightGetsTheModsOwnLineWithNoRequestAndReachesTheNextDebrief()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt - 2, "Mun", "ORBITING", "Complete"));

            mission.Send(Finished(EndedAt - 1, "Mun", "ORBITING", "Completed"));
            _background.Drain();

            Assert.Contains(mission.View.Log, e => e.Text == "Contract complete: Explore the Mun · +10,000 √");
            Assert.Equal(3, transport.Requests.Count(IsMission));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Empty(mission.TakeCommands());

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(Finished(EndedAt, "Mun", "ORBITING", "Completed"));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);
            var debrief = LastUserText(transport.Requests.Last(IsMission));
            Assert.Contains("\"state\":\"completed\"", debrief);
            Assert.Contains("\"Explore the Mun\" completed: the game paid +10,000 funds", debrief);
            Assert.Single(mission.View.Log, e => e.Text.StartsWith("Contract complete:", StringComparison.Ordinal));
        }

        [Fact]
        public void AFailedContractNeverRaisesAnAlertOrTouchesTheFlightAndTheNextDebriefProposesTheDrop()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt - 2, "Kerbin", "ORBITING", "Incomplete"));
            var logged = mission.View.Log.Count;

            mission.Send(Finished(EndedAt - 1, "Kerbin", "ORBITING", "Failed"));
            _background.Drain();

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count(IsMission));
            Assert.Empty(mission.TakeCommands());
            // Not an Alert, not a Telemetry Check, not even a line: it waits for the Debrief.
            Assert.Equal(logged, mission.View.Log.Count);

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(Finished(EndedAt, "Kerbin", "ORBITING", "Failed"));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);
            var debrief = LastUserText(transport.Requests.Last(IsMission));
            Assert.Contains("\"Explore the Mun\" can't complete: the game failed it", debrief);
            Assert.Contains("propose dropping it, with the reason", debrief);
        }

        [Fact]
        public void ARequirementFallingBackToUnmetReachesTheNextDebrief()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt - 2, "Mun", "ORBITING", "Complete"));
            mission.Send(At(EndedAt - 1, "Mun", "ORBITING", "Incomplete"));
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(At(EndedAt, "Mun", "ORBITING", "Incomplete"));
            mission.Send(new UtAdvanced(EndedAt + 61));
            DrainUntil(mission, v => transport.Requests.Count(IsMission) >= 4);
            Assert.Contains("\"Explore the Mun\": \"orbit the Mun\" fell back to unmet", LastUserText(transport.Requests.Last(IsMission)));
        }

        // ---- The replan drops the Contract with the reason, for Approval ----

        [Fact]
        public void TheRevisedPlanDropsTheFailedContractWithTheReasonOnItsCardAndApprovalDropsIt()
        {
            var transport = Transport(OffNominalRevise(),
                ToolUseStreams.ToolUse("home", (ProposedPlans.ToolName, PlanJson("Transfer to the Mun and capture into low orbit", "Return to Kerbin"))),
                MissionPlanKind());
            var mission = Running(transport, out var start);
            mission.Send(At(EndedAt - 2, "Kerbin", "ORBITING", "Incomplete"));
            mission.Send(Finished(EndedAt - 1, "Kerbin", "ORBITING", "Failed"));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(Finished(EndedAt, "Kerbin", "ORBITING", "Failed"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            var card = mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan);
            Assert.Contains("Drops: Explore the Mun: the game failed it", card.Text);

            mission.Send(new Approve());
            Assert.Contains(mission.View.Log, e => e.Text == "Contract dropped from the Mission: Explore the Mun: the game failed it.");
        }

        private static ScriptedResponse OffNominalRevise()
        {
            return Reply("{\"kind\":\"debrief\",\"say\":\"Off-Nominal: the Contract failed.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Off-Nominal\",\"why\":\"The game failed the Contract.\",\"what_happened\":\"It failed.\",\"goal_reached\":false,"
                + "\"next\":\"Drop it and come home.\",\"follow_up\":\"revised_plan\"}}");
        }

        // ---- Helpers ----

        /// <summary>The chosen Contract gone from the accepted list, finished in <paramref name="state"/>.</summary>
        private static GameSnapshot Finished(double ut, string body, string situation, string state)
        {
            var snapshot = At(ut, body, situation, "Complete");
            snapshot.Contracts.Accepted.Clear();
            snapshot.Contracts.Finished.Add(new FinishedContract { Guid = "c1", State = state });
            return snapshot;
        }

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

        /// <summary>The plan, its reply and the Sequence Plan; then <paramref name="after"/>, or the fixture's Nominal Debrief with none.</summary>
        private static StreamingFakeTransport Transport(params ScriptedResponse[] after)
        {
            var responses = new List<ScriptedResponse>
            {
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, PlanJson("Transfer to the Mun and capture into low orbit", "Return to Kerbin"))),
                MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
            };
            if (after.Length == 0) responses.Add(ScriptedResponse.Fixture("mission-debrief-next.sse"));
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
