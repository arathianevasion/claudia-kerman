// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// #1022, seam 1: Start Mission with accepted exploration Contracts posts Mission Control's Contract check card, with no request to
    /// Claude; "Plan these" starts the Mission and its first request carries the Contract Route section with the player's words.
    /// </summary>
    public sealed class ContractCheckCardTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private StreamingFakeTransport _transport;
        private CraftCheckRouter _router;

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(GameSnapshot snapshot, bool route = true, params ScriptedResponse[] responses)
        {
            _transport = new StreamingFakeTransport(responses.Length > 0 ? responses : new[] { ScriptedResponse.Fixture("mission-plan.sse") });
            _router = route ? new CraftCheckRouter(_transport) : null;
            var client = new ClaudeClient(route ? (IHttpTransport)_router : _transport, () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            // A crewed Mission started in Flight: the Mun Shot's crew is Claudia, aboard already.
            mission.Send(snapshot);
            return mission;
        }

        /// <summary>Every request sent to Claude, the Craft Check's included.</summary>
        private int Requests
        {
            get { return _transport.Requests.Count + (_router != null ? _router.CheckRequests.Count : 0); }
        }

        private static StartMission Start(string words)
        {
            return new StartMission(words, 25m, Sonnet);
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason + " " + mission.View.StartForm.Refusal + " check " + mission.View.CraftCheck.State + " " + mission.View.CraftCheck.Verdict + " " + mission.View.CraftCheck.Message + " " + string.Join(",", mission.View.CraftCheck.Findings.Select(f => f.Code)));
        }

        /// <summary>Some time for anything that was sent to come back.</summary>
        private void Settle()
        {
            for (var i = 0; i < 20; i++)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
        }

        private static string Text(HttpRequestSpec request)
        {
            var texts = new List<string>();
            using (var doc = JsonDocument.Parse(request.Body))
                foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
                {
                    var content = message.GetProperty("content");
                    if (content.ValueKind == JsonValueKind.String) texts.Add(content.GetString());
                    else foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("text", out var text)) texts.Add(text.GetString());
                }
            return string.Join("\n", texts);
        }

        private static GameSnapshot TwoAtTheMunAndAPartTest()
        {
            return ContractCheckTests.With(
                ContractCheckTests.Fixture("ExplorationContract.json"),
                ContractCheckTests.Exploration("c-land", 30000, "Mun:LANDING"),
                ContractCheckTests.Fixture("PartTest.json"));
        }

        [Fact]
        public void StartMissionWithAcceptedContractsPostsTheCardAndAsksClaudeNothing()
        {
            var mission = Control(TwoAtTheMunAndAPartTest());

            mission.Send(Start(""));
            Settle();

            var card = mission.View.ContractCheck;
            Assert.NotNull(card);
            Assert.False(card.Folded);
            Assert.Equal(3, card.Accepted);
            Assert.Equal(2, card.Rows.Count);
            Assert.All(card.Rows, r => Assert.True(r.Ticked));
            Assert.All(card.Rows, r => Assert.Equal("Mun", r.Phase));
            Assert.All(card.Rows, r => Assert.Matches(@"^Mun · \+[0-9,]+ m/s · no deadline$", r.Detail(mission.View.Ut)));
            Assert.True(card.RouteDeltaV > 0);
            Assert.True(card.CraftDeltaV > card.RouteDeltaV + card.Reserve);
            Assert.Equal(15, card.ReservePercent);
            var left = Assert.Single(card.NotThisFlight);
            Assert.StartsWith("not yet: ", left.Reason);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.StartsWith("2 of 3 Contracts · route ", card.Headline);
            Assert.Equal("Plan these 2", card.PlanLabel);
            Assert.True(card.CanPlan);
            Assert.Equal(0, Requests);
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        [Fact]
        public void PlanTheseSendsOneStartMissionRequestWithTheContractRouteAndThePlayersWords()
        {
            var mission = Control(TwoAtTheMunAndAPartTest());
            mission.Send(Start("Also run a crew report in Mun orbit."));
            Settle();
            Assert.Equal(0, Requests);

            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var first = Assert.Single(_transport.Requests);
            var text = Text(first);
            Assert.Contains("Contract Route", text);
            Assert.Contains("Also run a crew report in Mun orbit.", text);
            Assert.Contains("land on the Mun", text);
            Assert.Contains("(phase: Mun)", text);
            Assert.Contains("not yet: part tests aren't planned yet", text);
            // The card folds to its one line, which stays in the Mission's transcript.
            Assert.Null(mission.View.ContractCheck);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Mission Control · Contract check: 2 of 3 Contracts chosen", StringComparison.Ordinal));
        }

        [Fact]
        public void WithNothingTypedTheMissionPlansFromTheContractsAlone()
        {
            var mission = Control(TwoAtTheMunAndAPartTest());
            mission.Send(Start(""));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var text = Text(Assert.Single(_transport.Requests));
            Assert.Contains("Contract Route", text);
            Assert.Contains("No words typed", text);
            Assert.Contains("land on the Mun", mission.View.Goal);
        }

        [Fact]
        public void StartMissionAgainReplacesTheCardWithTheNewWords()
        {
            var mission = Control(TwoAtTheMunAndAPartTest());
            mission.Send(Start(""));
            Assert.False(mission.View.ContractCheck.CanPlanWithout);

            mission.Send(Start("Fly to Minmus and capture into orbit."));

            Assert.Equal("Fly to Minmus and capture into orbit.", mission.View.ContractCheck.Words);
            Assert.True(mission.View.ContractCheck.CanPlanWithout);
            Assert.Equal(0, Requests);
        }

        [Fact]
        public void WithTheCraftsDeltaVUnknownTheContractsArentDroppedSilently()
        {
            var snapshot = TwoAtTheMunAndAPartTest();
            snapshot.DeltaVSimulator = false;
            var mission = Control(snapshot);

            mission.Send(Start("Fly to Minmus and capture into orbit."));
            Settle();

            Assert.Null(mission.View.ContractCheck);
            Assert.Contains("can't be priced", mission.View.StartForm.Refusal);
            Assert.Equal(0, Requests);
        }

        [Fact]
        public void PlanWithoutContractsPlansThePlayersWordsAlone()
        {
            var mission = Control(TwoAtTheMunAndAPartTest());
            mission.Send(Start("Fly to Minmus and capture into orbit."));
            Assert.True(mission.View.ContractCheck.CanPlanWithout);
            mission.Send(new PlanWithoutContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var text = Text(Assert.Single(_transport.Requests));
            Assert.Contains("Fly to Minmus and capture into orbit.", text);
            Assert.DoesNotContain("Contract Route", text);
            Assert.Equal("Fly to Minmus and capture into orbit.", mission.View.Goal);
        }

        [Fact]
        public void PlanWithoutContractsNeedsWords()
        {
            var mission = Control(TwoAtTheMunAndAPartTest());
            mission.Send(Start(""));

            Assert.False(mission.View.ContractCheck.CanPlanWithout);
            mission.Send(new PlanWithoutContracts());
            Settle();
            Assert.Equal(0, Requests);
            Assert.NotNull(mission.View.ContractCheck);
        }

        [Fact]
        public void WithNoAcceptedContractsStartMissionIsUnchanged()
        {
            var snapshot = ContractCheckTests.Pad();
            var mission = Control(snapshot);

            mission.Send(Start("Fly to Minmus and capture into orbit."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Null(mission.View.ContractCheck);
            Assert.Single(_router.CheckRequests);
            var text = Text(Assert.Single(_transport.Requests));
            Assert.DoesNotContain("Contract Route", text);
        }

        [Fact]
        public void WhenTheTypedWordsAloneDontFitTheCraftCheckGivesItsBlockerAndNoContractIsSuggested()
        {
            var snapshot = TwoAtTheMunAndAPartTest();
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= 0.25;
            var mission = Control(snapshot, false, ScriptedResponse.Fixture("craft-check-fail.sse"));

            mission.Send(Start("Fly to Minmus, capture into orbit and land, then return to Kerbin."));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            Assert.Null(mission.View.ContractCheck);
            Assert.Equal(CraftVerdict.Fail, mission.View.CraftCheck.Verdict);
            Assert.Contains(mission.View.CraftCheck.Findings, f => f.Code == "delta_v_short");
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }
    }
}
