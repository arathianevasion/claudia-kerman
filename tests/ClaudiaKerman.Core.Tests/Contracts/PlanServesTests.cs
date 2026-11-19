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
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1024, seam 1 (the Mission in Core, driven by a Game Snapshot with Contracts and Claudia's propose_mission_plan): every chosen
    /// Contract Requirement is served by a leg that can meet it, or the plan goes back to Claudia as a finding before the player sees
    /// it; a covering plan carries the mod's Serves tags per leg, the player's own words included, and never Claudia's.
    /// </summary>
    public sealed class PlanServesTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static ContractChoice Chosen(string words, params string[] targets)
        {
            var check = ContractCheck.Work(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, targets)), words, false, ContractCheckTests.Margin);
            return check.Choose(check.TickedGuids);
        }

        private static MissionPlan Plan(params string[] legs)
        {
            var plan = new MissionPlan();
            foreach (var leg in legs) plan.Sequences.Add(new PlannedSequence { Name = leg, Intent = "Fly it.", DeltaV = 100 });
            return plan;
        }

        private static PlanServes Check(ContractChoice choice, MissionPlan plan, string words = "")
        {
            return PlanServes.Check(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT")), plan, choice.Requirements, words, "Kerbin");
        }

        [Fact]
        public void APlanThatNeverGoesWhereAChosenRequirementNeedsIsRefusedWithAFinding()
        {
            var choice = Chosen("", "Mun:ORBIT");

            var served = Check(choice, Plan("Launch to low Kerbin orbit", "Return to Kerbin"));

            var problem = Assert.Single(served.Problems);
            Assert.Contains("orbit the Mun", problem);
            Assert.Contains("no leg of the plan goes there", problem);
            Assert.Equal(-1, Assert.Single(served.Record).Leg);
        }

        [Fact]
        public void AnOrbitRequirementServedOnlyByAFlybyLegIsRefusedAsUnmeetable()
        {
            var choice = Chosen("", "Mun:ORBIT");

            var served = Check(choice, Plan("Launch to low Kerbin orbit", "Transfer to the Mun for a flyby", "Return to Kerbin"));

            var problem = Assert.Single(served.Problems);
            Assert.Contains("leg 2", problem);
            Assert.Contains("only flies by the Mun", problem);
        }

        [Fact]
        public void ALandingRequirementServedByAnOrbitLegIsUnmeetableButALandingLegMeetsAnOrbitRequirement()
        {
            Assert.Single(Check(Chosen("", "Mun:LANDING"), Plan("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit")).Problems);

            var served = Check(Chosen("", "Mun:ORBIT"), Plan("Launch to low Kerbin orbit", "Transfer to the Mun, capture into low orbit and land"));
            Assert.Empty(served.Problems);
            Assert.Equal(1, Assert.Single(served.Record).Leg);
        }

        [Fact]
        public void ACoveringPlanTagsEachServingLegAndTheContractRouteRecordsTheLeg()
        {
            var choice = Chosen("", "Mun:LANDING", "RETURNFROMSURFACE".Insert(0, "Mun:"));
            var plan = Plan("Launch to low Kerbin orbit", "Transfer to the Mun, capture into low orbit and land", "Ascend from the Mun to low orbit", "Return to Kerbin");

            var served = Check(choice, plan);

            Assert.Empty(served.Problems);
            var title = choice.Requirements[0].Title;
            Assert.Empty(served.Legs[0]);
            Assert.Equal(new[] { title }, served.Legs[1]);
            Assert.Empty(served.Legs[2]);
            Assert.Equal(new[] { title }, served.Legs[3]);
            Assert.Equal(new[] { 1, 3 }, served.Record.Select(r => r.Leg));
            Assert.Equal("Return to Kerbin", served.Record[1].LegName);
        }

        [Fact]
        public void TheReturnNeedsTheVisitFirst()
        {
            var served = Check(Chosen("", "Mun:RETURNFROMORBIT"), Plan("Launch to low Kerbin orbit", "Return to Kerbin"));

            Assert.Contains("no leg of the plan goes there", Assert.Single(served.Problems));
        }

        [Fact]
        public void ThePlayersOwnRequirementsAreTaggedToo_ALegBothNeedIsTaggedWithBoth()
        {
            var words = "Orbit the Mun and return to Kerbin";
            var choice = Chosen(words, "Mun:ORBIT");
            var plan = Plan("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin");

            var served = Check(choice, plan, words);

            Assert.Empty(served.Problems);
            Assert.Equal(new[] { "your words", choice.Requirements[0].Title }, served.Legs[1]);
            Assert.Equal(new[] { "your words" }, served.Legs[2]);
        }

        [Fact]
        public void ALegsOwnTextNeverMakesATag()
        {
            var choice = Chosen("", "Mun:ORBIT");
            var plan = Plan("Launch to low Kerbin orbit", "Transfer to Minmus [serves: Explore the Mun]");
            plan.Sequences[1].Intent = "This leg serves the Contract 'Explore the Mun' and orbits the Mun.";

            var served = Check(choice, plan);

            Assert.Single(served.Problems);
            Assert.All(served.Legs, l => Assert.Empty(l));
        }

        [Fact]
        public void ProposeMissionPlanAnswersTheFindingsAndAcceptsACoveringPlan()
        {
            var choice = Chosen("", "Mun:ORBIT");
            Func<MissionPlan, IList<string>> problems = p => Check(choice, p).Problems;

            var refused = ProposedPlans.Check(Use(Plan("Launch to low Kerbin orbit")), new List<string>(), false, true, false, null, problems);
            var accepted = ProposedPlans.Check(Use(Plan("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit")), new List<string>(), false, true, false, null, problems);

            Assert.Contains("\"ok\":false", refused.Json);
            Assert.Contains("orbit the Mun", refused.Json);
            Assert.Equal(ProposedPlans.AcceptedJson, accepted.Json);
        }

        [Fact]
        public void WithNoContractsChosenTheToolAcceptsAsBefore()
        {
            var accepted = ProposedPlans.Check(Use(Plan("Launch to low Kerbin orbit")), new List<string>(), false);

            Assert.Equal(ProposedPlans.AcceptedJson, accepted.Json);
        }

        private static ToolUseBlock Use(MissionPlan plan)
        {
            return new ToolUseBlock { Id = "t", Name = ProposedPlans.ToolName, InputJson = PlanJson(plan.Sequences.Select(s => s.Name).ToArray()) };
        }

        private static string PlanJson(params string[] names)
        {
            return "{\"why\":\"Because.\",\"sequences\":["
                + string.Join(",", names.Select(n => "{\"name\":" + JsonSerializer.Serialize(n) + ",\"intent\":\"Fly it.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}"))
                + "],\"total_delta_v\":" + (names.Length * 100) + ",\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\\\"\\\"\",\"child_vessels\":[],\"goal_steps\":[]}";
        }

        // ---- The Mission: a refused plan never reaches the player, the covering one shows its tags ----

        private static ScriptedResponse Reply(string json)
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

        private static ScriptedResponse MissionPlanKind()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Here.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}");
        }

        [Fact]
        public void TheMissionSendsTheFindingBackAndOffersOnlyTheCoveringPlanWithTheModsTags()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("miss", (ProposedPlans.ToolName, PlanJson("Launch to low Kerbin orbit", "Return to Kerbin"))),
                ToolUseStreams.ToolUse("cover", (ProposedPlans.ToolName, PlanJson("Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin"))),
                MissionPlanKind());
            var router = new CraftCheckRouter(transport);
            var client = new ClaudeClient(router, () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(ContractCheckTests.With(ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT")));

            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.Stage != MissionStage.AwaitingPlanApproval && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains("no leg of the plan goes there", string.Join("\n", transport.Requests.Select(r => r.Body)).Replace("\\\"", "\""));
            var card = mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan);
            Assert.Contains("Transfer to the Mun and capture into low orbit", card.Text);
            Assert.Contains("· serves: Explore the Mun", card.Text);
            Assert.DoesNotContain("Launch to low Kerbin orbit: Fly it. (100 m/s, next) · serves", card.Text);
            Assert.DoesNotContain("Return to Kerbin: Fly it. (100 m/s, next) · serves", card.Text);
        }
    }
}
