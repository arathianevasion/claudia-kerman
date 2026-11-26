// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1378 (ADR 0022), at the Mission Control seam: with Chosen Contracts, Claudia's proposed Mission Plan is held to the Contract Route's
    /// Body Visits. A plan that visits a body twice, or flies the bodies in another order than the Route's, is sent back with the problem
    /// named; a plan that follows the Route is offered; a Mission with no Chosen Contract is checked as before.
    /// </summary>
    public sealed class RouteHeldPlanTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private StreamingFakeTransport _transport;

        public void Dispose()
        {
            _background.Drain();
            _save.Dispose();
        }

        /// <summary>The Game Snapshot recorded with the Ferry Contract of Mission m20261126-040610-a4b7: its Route flies the Mun, then Minmus, then home.</summary>
        private static GameSnapshot TheFerry()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", "TourismContract-1fbf2657-ut545547.json")));
        }

        private static readonly string[] FollowsTheRoute =
        {
            "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin",
        };

        /// <summary>A Mission on <paramref name="snapshot"/> whose Claudia proposes each plan in turn, then says the plan is hers.</summary>
        private MissionControl Proposing(GameSnapshot snapshot, params string[][] plans)
        {
            var responses = plans.Select((p, i) => ToolUseStreams.ToolUse("p" + i, (ProposedPlans.ToolName, PlanJson(p)))).ToList();
            responses.Add(MissionPlanKind());
            _transport = new StreamingFakeTransport(responses.ToArray());
            var client = new ClaudeClient(new CraftCheckRouter(_transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(snapshot);
            return mission;
        }

        private void DrainUntilOffered(MissionControl mission)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.Stage != MissionStage.AwaitingPlanApproval && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
        }

        /// <summary>What the mod answered Claudia's proposals with, across every request, its quotes unescaped (a tool result is JSON inside the request's JSON).</summary>
        private string Answers
        {
            get { return string.Join("\n", _transport.Requests.Select(r => r.Body)).Replace("\\\\\\\"", "\"").Replace("\\\"", "\""); }
        }

        private static string PlanJson(params string[] names)
        {
            return "{\"why\":\"Because.\",\"sequences\":["
                + string.Join(",", names.Select(n => "{\"name\":" + JsonSerializer.Serialize(n) + ",\"intent\":\"Fly it.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}"))
                + "],\"total_delta_v\":" + (names.Length * 100) + ",\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\\\"\\\"\",\"child_vessels\":[],\"goal_steps\":[]}";
        }

        private static ScriptedResponse MissionPlanKind()
        {
            var text = JsonSerializer.Serialize("{\"kind\":\"mission_plan\",\"say\":\"Here.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}");
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

        private static string Offered(MissionControl mission)
        {
            return mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text;
        }

        [Fact]
        public void ThePlanOfMissionA4b7_MunMinmusMunHome_IsSentBackNamingTheSecondMunVisit()
        {
            var mission = Proposing(TheFerry(),
                new[] { "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Transfer to the Mun and capture into low orbit again", "Return to Kerbin" },
                FollowsTheRoute);
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.Contains("leg 4 \"Transfer to the Mun and capture into low orbit again\" visits the Mun a second time", Answers);
            Assert.DoesNotContain("capture into low orbit again", Offered(mission));
        }

        [Fact]
        public void APlanFlyingMinmusBeforeTheMunIsSentBackNamingTheOrder()
        {
            var mission = Proposing(TheFerry(),
                new[] { "Launch to low Kerbin orbit", "Transfer to Minmus and capture into low orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin" },
                FollowsTheRoute);
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.Contains("the plan flies Minmus before the Mun, but the Contract Route flies the Mun first", Answers);
            Assert.Contains("Transfer to the Mun and capture into low orbit: ", Offered(mission));
        }

        [Fact]
        public void APlanFollowingTheRoutesBodyVisitsIsOfferedAsBefore()
        {
            var mission = Proposing(TheFerry(), FollowsTheRoute);
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.Equal(2, _transport.Requests.Count);
            Assert.Contains("Transfer to Minmus and capture into low orbit", Offered(mission));
        }

        [Fact]
        public void PassingABodyAgainOnAFlybyIsNotASecondVisit()
        {
            var mission = Proposing(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Mun:FLYBY", "Minmus:ORBIT")),
                new[] { "Launch to low Kerbin orbit", "Transfer to the Mun for a flyby", "Transfer to Minmus and capture into low orbit", "Transfer to the Mun for a flyby on the way home", "Return to Kerbin" });
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.DoesNotContain("a second time", Answers);
            Assert.Contains("Transfer to the Mun for a flyby on the way home", Offered(mission));
        }

        private const string BackToTheMun = "Orbit the Mun, then orbit Minmus, then orbit the Mun again, and come home.";

        private static readonly string[] MunMinmusMunHome =
        {
            "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Transfer back to the Mun and capture into low orbit", "Return to Kerbin",
        };

        [Fact]
        public void WordsAskingToGoBackToTheMun_GiveTwoMunVisitsWithTheContractWorkAtTheFirst_AndThePlanPasses()
        {
            var mission = Proposing(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT")), MunMinmusMunHome);
            mission.Send(new StartMission(BackToTheMun, 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            var request = _transport.Requests[0].Body.Replace("\\\"", "\"");
            Assert.Matches(@"1\. the Mun: [^\\]*orbit[^\\]*open", request);
            Assert.Contains("2. Minmus: no Contract Requirement", request);
            Assert.Contains("3. the Mun, again for the player's words: no Contract Requirement", request);
            Assert.Equal(2, _transport.Requests.Count);
            Assert.Contains("Transfer back to the Mun and capture into low orbit", Offered(mission));
        }

        [Fact]
        public void TheRepeatTheWordsAskForServesTheWords_EvenAtABodyWithNoContractWork()
        {
            var mission = Proposing(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Minmus:ORBIT")), MunMinmusMunHome);
            mission.Send(new StartMission(BackToTheMun, 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.DoesNotContain("serves no Requirement", Answers);
            Assert.Contains("Transfer back to the Mun and capture into low orbit", Offered(mission));
        }

        [Fact]
        public void WithoutWordsAskingForIt_TheSameRepeatIsSentBack()
        {
            var mission = Proposing(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT")), MunMinmusMunHome,
                new[] { "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" });
            mission.Send(new StartMission("Orbit the Mun, then orbit Minmus, and come home.", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.Contains("leg 4 \"Transfer back to the Mun and capture into low orbit\" visits the Mun a second time", Answers);
            Assert.DoesNotContain("Transfer back to the Mun", Offered(mission));
        }

        [Fact]
        public void AFlybyOfABodyOnTheWayToTheNextBodyVisitIsAcceptedAndIsNotAVisit()
        {
            var mission = Proposing(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Minmus:ORBIT")),
                new[] { "Launch to low Kerbin orbit", "Transfer to the Mun for a flyby", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" });
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntilOffered(mission);

            Assert.DoesNotContain("serves no Requirement", Answers);
            Assert.DoesNotContain("a second time", Answers);
            Assert.Contains("Transfer to the Mun for a flyby", Offered(mission));
        }

        [Fact]
        public void AMissionWithNoChosenContractAcceptsAPlanThatRepeatsABody()
        {
            var mission = Proposing(ContractCheckTests.Pad(),
                new[] { "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Transfer to the Mun and capture into low orbit again", "Return to Kerbin" });
            mission.Send(new StartMission("Orbit the Mun, then Minmus, then the Mun again, and come home.", 25m, Sonnet));
            DrainUntilOffered(mission);

            Assert.Equal(2, _transport.Requests.Count);
            Assert.Contains("Transfer to the Mun and capture into low orbit again", Offered(mission));
        }
    }
}
