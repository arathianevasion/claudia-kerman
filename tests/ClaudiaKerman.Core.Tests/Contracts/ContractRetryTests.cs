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
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1306, at the Mission Control seam (the Mission in Core, Claudia stubbed): a Contract Requirement missed and still open at the body
    /// the craft is at is retried before any leg leaves it. The next Sequence Plan request asks for the retry; the Margin check, the Spending
    /// Cap and the Contract's deadline each turn it into a request for a revised Mission Plan that drops the Contract. From the second miss
    /// the chat names the Requirement. The booster part test (FLYING over Kerbin) stands for every kind.
    /// </summary>
    public sealed class ContractRetryTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const double EndedAt = 448444;
        private const string Part = "liquidEngine.v2";
        private const string Key = "c-test/0";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly DateTime _now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void AMissedRequirementOpenAtTheCraftsBodyIsRetriedByTheNextSequencePlan()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Missed(mission, transport, start, Snapshot(EndedAt));

            var next = MissionRequest(transport, 4);
            Assert.Contains(ContractRetry.RetryWords, next.Text);
            Assert.Contains("PartTest", next.Text);
            Assert.Contains("plan_step 1", next.Text);
            Assert.Contains("Propose the Sequence Plan for the next Maneuver Sequence", next.Notice);
        }

        [Fact]
        public void WhenTheMarginCheckFindsNoRoomForAnotherRetryTheRequestIsForARevisedPlanThatDropsTheContract()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            // 150 m/s left: the 100 m/s still to fly fits, another 100 m/s retry on top doesn't.
            var ended = Snapshot(EndedAt);
            for (var i = 0; i < ended.Stages.Count; i++) ended.Stages[i].DeltaVVac = i == 0 ? 150 : 0;

            Missed(mission, transport, start, ended);

            AssertStopped(MissionRequest(transport, 4), "the Margin check finds the rest of the plan plus another retry won't fit");
        }

        [Fact]
        public void WhenTheSpendingCapHasNoRoomForAnotherRetryTheRequestIsForARevisedPlanThatDropsTheContract()
        {
            var config = new PlayerConfig { CheckDurationPercent = 1000 };
            var transport = Transport();
            // Prices rise as the Debrief goes out: it is sent, but another retry's worst case no longer fits the $25 cap.
            var price = config.Prices.PriceOf(Sonnet);
            var rising = new OnMissionRequest(transport, 3, () => config.Prices.Set(Sonnet, new ModelPrice(price.InputPerMTok * 40, price.OutputPerMTok * 40)));
            var mission = Running(transport, out var start, config, rising);

            Missed(mission, transport, start, Snapshot(EndedAt));

            AssertStopped(MissionRequest(transport, 4), "the Spending Cap ($25.00) has no room for another retry");
        }

        [Fact]
        public void WhenTheContractsDeadlineWouldPassTheRequestIsForARevisedPlanThatDropsTheContract()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Missed(mission, transport, start, Snapshot(EndedAt, c => c.DeadlineUt = EndedAt + 600));

            AssertStopped(MissionRequest(transport, 4), "the Contract's deadline would pass");
        }

        [Fact]
        public void UnderAutoModeARevisedPlanThatDropsTheContractWaitsForThePlayer()
        {
            var transport = Transport(
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson("Return to Kerbin"))),
                ContractWatchTests.MissionPlanKind());
            var mission = Running(transport, out var start);
            mission.Send(new SetAutoMode(true));

            Missed(mission, transport, start, Snapshot(EndedAt, c => c.DeadlineUt = EndedAt + 600));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new UtAdvanced(EndedAt + 120));

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal("Auto Mode is waiting for you: a revised Mission Plan that drops a Contract.", mission.View.AutoModeStop);
            Assert.Contains("Drops: PartTest", mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text);
        }

        [Fact]
        public void FromTheSecondMissTheChatNamesTheRequirementAndAutoModeCarriesOn()
        {
            var transport = Transport(ScriptedResponse.Fixture("mission-sequence-plan.sse"), ScriptedResponse.Fixture("mission-debrief-next.sse"));
            var mission = Running(transport, out var start);
            mission.Send(new SetAutoMode(true));

            Missed(mission, transport, start, Snapshot(EndedAt));
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Contract Requirement missed again"));
            // The retry's Sequence Plan, approved by Auto Mode like any other, misses again.
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var retry = mission.TakeCommands().OfType<StartScript>().Last();
            Missed(mission, transport, retry, Snapshot(EndedAt + 90), next: 6);

            var notice = mission.View.Log.Single(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Contract Requirement missed again"));
            Assert.Equal("Contract Requirement missed again (2 times): \"test liquidEngine.v2 through 51,000–56,000 m at 100–260 m/s over Kerbin\" (PartTest). The flight carries on.", notice.Text);
            Assert.Contains(ContractRetry.RetryWords, MissionRequest(transport, 6).Text);
            Assert.Equal("", mission.View.AutoModeStop);
        }

        private static void AssertStopped(Request request, string why)
        {
            Assert.DoesNotContain(ContractRetry.RetryWords, request.Text);
            Assert.Contains(ContractRetry.StopWords + ": " + why, request.Text);
            Assert.Contains("\"PartTest\"", request.Text.Replace("(PartTest)", "\"PartTest\""));
            Assert.Contains("drop its Contract", request.Notice);
            Assert.Contains("under Auto Mode too", request.Notice);
        }

        /// <summary>Runs <paramref name="act"/> as the <paramref name="index"/>th Mission request (0-based) goes out, then sends it on.</summary>
        private sealed class OnMissionRequest : IHttpTransport
        {
            private readonly StreamingFakeTransport _inner;
            private readonly int _index;
            private readonly Action _act;
            private int _seen;

            public OnMissionRequest(StreamingFakeTransport inner, int index, Action act)
            {
                _inner = inner;
                _index = index;
                _act = act;
            }

            public HttpResponseData Send(HttpRequestSpec request)
            {
                return _inner.Send(request);
            }

            public IHttpStream Open(HttpRequestSpec request, CancellationToken cancel)
            {
                if (request.Body.Contains(ProposedPlans.ToolName) && _seen++ == _index) _act();
                return _inner.Open(request, cancel);
            }
        }

        // ---- Helpers ----

        /// <summary>The run ends with the Requirement missed; returns once the request after its Debrief (Mission request <paramref name="next"/>, 0-based) is sent.</summary>
        private void Missed(MissionControl mission, StreamingFakeTransport transport, StartScript start, GameSnapshot ended, int next = 4)
        {
            // The booster test never run: its window passed.
            mission.View.PartTests.Tick("launch", Craft(58000, 420));
            mission.Send(ended);
            mission.Send(new KosRunEnded(Finished(), start.RunId, ended.Ut));
            mission.Send(new UtAdvanced(ended.Ut + 60));
            DrainUntil(mission, v => transport.RequestsSoFar().Count(r => r.Body.Contains(ProposedPlans.ToolName)) > next);
        }

        private static TestCraft Craft(double altitude, double speed)
        {
            return new TestCraft
            {
                Body = "Kerbin", Altitude = altitude, Speed = speed, Situation = "FLYING",
                Parts = new Dictionary<string, TestPart> { { Part, new TestPart { Uid = "42", CanRun = true } } },
            };
        }

        private static SnapshotContract Booster()
        {
            var contract = ContractCheckTests.BoosterTest("c-test");
            contract.Requirements[0].Key = Key;
            contract.Requirements[0].State = "Incomplete";
            return contract;
        }

        private static GameSnapshot Snapshot(double ut, Action<SnapshotContract> change = null)
        {
            var booster = Booster();
            if (change != null) change(booster);
            var snapshot = ContractCheckTests.With(booster);
            snapshot.Ut = ut;
            snapshot.Body = "Kerbin";
            snapshot.Situation = "SUB_ORBITAL";
            return snapshot;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private sealed class Request
        {
            public string Text = "";
            public string Notice = "";
        }

        /// <summary>The <paramref name="index"/>th Mission request (0-based): its last user text, and its whole body, where the mod's notice is.</summary>
        private static Request MissionRequest(StreamingFakeTransport transport, int index)
        {
            var requests = transport.RequestsSoFar().Where(r => r.Body.Contains(ProposedPlans.ToolName)).ToList();
            Assert.True(requests.Count > index, "only " + requests.Count + " Mission requests");
            using var doc = JsonDocument.Parse(requests[index].Body);
            var texts = new List<string>();
            foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user"))
            {
                var content = message.GetProperty("content");
                texts.Add(content.ValueKind == JsonValueKind.String ? content.GetString()
                    : string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString())));
            }
            return new Request { Text = texts.Last(), Notice = requests[index].Body };
        }

        private static StreamingFakeTransport Transport(params ScriptedResponse[] more)
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson("Hop straight up through 51-56 km over Kerbin", "Return to Kerbin"))),
                ContractWatchTests.MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"));
            foreach (var response in more) transport.Add(response);
            return transport;
        }

        private MissionControl Running(StreamingFakeTransport transport, out StartScript start, PlayerConfig config = null, IHttpTransport wrapped = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(wrapped ?? transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => _now;
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(ContractCheckTests.With(Booster()));
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            start = mission.TakeCommands().OfType<StartScript>().Single();
            return mission;
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
