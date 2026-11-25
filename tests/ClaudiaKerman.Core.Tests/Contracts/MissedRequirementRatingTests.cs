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
    /// #1305, seam 1 (the Mission in Core, Claudia stubbed): a leg meant to meet a Requirement the game shows unmet can't be rated
    /// Nominal, as a standing Telemetry Check crossing can't. The request tells Claudia the rule and names the Requirement; a Nominal
    /// reply is held to Off-Nominal. A leg whose Requirements are all met can still be Nominal. A part test stands for every kind.
    /// </summary>
    public sealed class MissedRequirementRatingTests : IDisposable
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
        public void ANominalReplyForALegThatMissedARequirementIsHeldToOffNominalAndTheRequestNamesTheRule()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Debrief(mission, transport, start, "Incomplete");

            var request = DebriefText(transport);
            Assert.Contains(ContractWatch.NominalRule, request);
            Assert.Contains("Not met: \"", request);
            Assert.Contains("PartTest", request);
            var card = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", card.Outcome);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Rated Off-Nominal, not Nominal: a Contract Requirement"));
        }

        [Fact]
        public void ALegWhoseRequirementsAreAllMetCanStillBeNominal()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Debrief(mission, transport, start, "Complete");

            Assert.DoesNotContain(ContractWatch.NominalRule, DebriefText(transport));
            Assert.Equal("Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Rated Off-Nominal"));
        }

        private void Debrief(MissionControl mission, StreamingFakeTransport transport, StartScript start, string state)
        {
            // The flight ends with the booster test never run (a part test the mod times: its window passed), or marked complete.
            if (state != "Complete") mission.View.PartTests.Tick("launch", Craft(58000, 420));
            else mission.View.PartTests.Tick("launch", Craft(52000, 200));
            mission.Send(Snapshot(EndedAt, state));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(new UtAdvanced(EndedAt + 60));
            DrainUntil(mission, v => v.Log.Any(e => e.Kind == LogKind.Debrief));
        }

        // ---- Helpers ----

        private static TestCraft Craft(double altitude, double speed)
        {
            return new TestCraft
            {
                Body = "Kerbin", Altitude = altitude, Speed = speed, Situation = "FLYING",
                Parts = new Dictionary<string, TestPart> { { Part, new TestPart { Uid = "42", CanRun = true } } },
            };
        }

        /// <summary>#1048's booster PartTest (FLYING over Kerbin, 51,000–56,000 m at 100–260 m/s), its Requirement keyed under the Contract's GUID.</summary>
        private static SnapshotContract Booster()
        {
            var contract = ContractCheckTests.BoosterTest("c-test");
            contract.Requirements[0].Key = Key;
            contract.Requirements[0].State = "Incomplete";
            return contract;
        }

        private static GameSnapshot Snapshot(double ut, string state = "Incomplete")
        {
            var booster = Booster();
            booster.Requirements[0].State = state;
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

        private static int MissionRequests(StreamingFakeTransport transport)
        {
            return transport.RequestsSoFar().Count(r => r.Body.Contains(ProposedPlans.ToolName));
        }

        /// <summary>The Debrief: the 4th Mission request, after the plan, its reply and the Sequence Plan.</summary>
        private static string DebriefText(StreamingFakeTransport transport)
        {
            var requests = transport.RequestsSoFar().Where(r => r.Body.Contains(ProposedPlans.ToolName)).ToList();
            Assert.True(requests.Count >= 4, "no Debrief request: " + requests.Count + " Mission requests");
            using var doc = JsonDocument.Parse(requests[3].Body);
            var last = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
            var content = last.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        }

        private static StreamingFakeTransport Transport()
        {
            return new StreamingFakeTransport(
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson("Hop straight up through 51-56 km over Kerbin", "Return to Kerbin"))),
                ContractWatchTests.MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"));
        }

        private MissionControl Running(StreamingFakeTransport transport, out StartScript start)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
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
