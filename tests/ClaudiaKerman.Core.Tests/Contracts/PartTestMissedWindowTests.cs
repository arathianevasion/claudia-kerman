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
    /// #1161, seam 1 (the Mission in Core, Claudia stubbed): a part-test Contract whose band is missed, from the window passing unrun
    /// (#1029) through the end of the run to the Debrief request (#1025). The Script Result carries the Requirement as not met with the
    /// window's gap, and the Debrief offers a retry or dropping the Contract.
    /// </summary>
    public sealed class PartTestMissedWindowTests : IDisposable
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
        public void ATestWindowThatPassesUnrunReachesTheDebriefAsNotMetWithTheGapAndOffersRetryOrDrop()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            var watch = mission.View.PartTests;
            Assert.True(watch.Pending("launch"));

            // The ascent goes through 51–56 km at 400 m/s, above the 100–260 m/s band, and out the top: no tick had every condition hold.
            Assert.Equal("", watch.Tick("launch", Craft(40000, 380)).How);
            var through = watch.Tick("launch", Craft(52000, 400));
            Assert.Equal("", through.How);
            var passed = watch.Tick("launch", Craft(58000, 420));
            Assert.Equal("", passed.How);
            var note = Assert.Single(passed.Notes);
            Assert.Contains("passed unrun", note);
            Assert.False(watch.Ran(Key));
            var gap = watch.Gap(Key);
            Assert.False(string.IsNullOrEmpty(gap));
            Assert.Contains("400", gap);

            mission.Send(Snapshot(EndedAt));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            DrainUntil(mission, v => MissionRequests(transport) >= 4);

            // No hold: the test never ran, so the craft is not seen meeting it.
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("Holding for the game"));
            var debrief = DebriefText(transport);
            Assert.Contains("Contract Requirements this sequence served", debrief);
            Assert.Contains("\"contract\":\"PartTest\"", debrief);
            Assert.Contains("\"state\":\"not met\"", debrief);
            Assert.Contains(gap, debrief);
            Assert.Contains("For a Requirement not met, propose a trim burn or a retry leg (priced at the plan proposal), or dropping its Contract, with the reason", debrief);
        }

        [Fact]
        public void ATestThatRanInItsWindowIsNotCarriedAsAMiss()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            var watch = mission.View.PartTests;

            Assert.Equal("run", watch.Tick("launch", Craft(52000, 200)).How);
            mission.Send(Snapshot(EndedAt));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(new UtAdvanced(EndedAt + 60));
            DrainUntil(mission, v => MissionRequests(transport) >= 4);

            Assert.DoesNotContain("the window", DebriefText(transport));
            Assert.Null(watch.Gap(Key));
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

        private static GameSnapshot Snapshot(double ut)
        {
            var snapshot = ContractCheckTests.With(Booster());
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
