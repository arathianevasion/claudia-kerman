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
    /// #1343, at the Mission Control seam (the Mission in Core, Claudia stubbed): a whole orbital survey ends within the lesser of 24 game
    /// hours from its first Sequence Plan at the body and the Contract's deadline. With waypoints still open after a Survey Plane the next
    /// Sequence Plan request asks for the next Survey Plane while its 12-hour window fits; when it doesn't, the request is for a revised
    /// Mission Plan that drops the Contract, which waits for the player under Auto Mode.
    /// </summary>
    public sealed class SurveyCapTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private static readonly double Start = ContractCheckTests.With().Ut;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly DateTime _now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void WhileAnotherWindowFitsBeforeTheCapTheNextSequencePlanFliesTheNextSurveyPlane()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Ended(mission, transport, start, Start + 20000);

            var next = MissionRequest(transport, 4);
            Assert.Contains(SurveyCap.NextPlaneWords, next.Text);
            Assert.Contains("3.0° N 10.0° E", next.Text);
            Assert.Contains("Propose the Sequence Plan for the next Maneuver Sequence", next.Notice);
        }

        [Fact]
        public void WhenTheNextWindowWouldEndAfter24GameHoursTheRequestIsForARevisedPlanThatDropsTheContract()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Ended(mission, transport, start, Start + 50000);

            AssertStopped(MissionRequest(transport, 4), "after 24 game hours from the survey's first Sequence Plan");
        }

        [Fact]
        public void WhenTheNextWindowWouldEndAfterTheContractsDeadlineTheRequestIsForARevisedPlanThatDropsTheContract()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);

            Ended(mission, transport, start, Start + 20000, Start + 30000);

            AssertStopped(MissionRequest(transport, 4), "after the Contract's deadline");
        }

        [Fact]
        public void UnderAutoModeTheRevisedPlanThatDropsTheContractWaitsForThePlayer()
        {
            var transport = Transport(
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson("Return to Kerbin"))),
                ContractWatchTests.MissionPlanKind());
            var mission = Running(transport, out var start);
            mission.Send(new SetAutoMode(true));

            Ended(mission, transport, start, Start + 50000);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.True(mission.View.Stage == MissionStage.AwaitingPlanApproval, mission.View.Stage + ": " + string.Join(" | ", mission.View.Log.Skip(8).Select(e => e.Kind + " " + e.Text)));
            mission.Send(new UtAdvanced(Start + 50120));

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains("Drops: SurveyContract", mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text);
            Assert.Equal("Auto Mode is waiting for you: a revised Mission Plan that drops a Contract.", mission.View.AutoModeStop);
        }

        private static void AssertStopped(Request request, string why)
        {
            Assert.DoesNotContain(SurveyCap.NextPlaneWords, request.Text);
            Assert.Contains(SurveyCap.StopWords, request.Text);
            Assert.Contains(why, request.Text);
            Assert.Contains("drop the survey's Contract", request.Notice);
            Assert.Contains("under Auto Mode too", request.Notice);
        }

        // ---- Helpers ----

        /// <summary>The run ends at <paramref name="ut"/> with the waypoint still open; returns once the request after its Debrief (Mission request 4) is sent.</summary>
        private void Ended(MissionControl mission, StreamingFakeTransport transport, StartScript start, double ut, double? deadline = null)
        {
            var ended = Snapshot(ut, deadline);
            mission.Send(ended);
            mission.Send(new KosRunEnded(Finished(), start.RunId, ut));
            mission.Send(new UtAdvanced(ut + 60));
            DrainUntil(mission, v => transport.RequestsSoFar().Count(r => r.Body.Contains(ProposedPlans.ToolName)) > 4);
            Assert.True(transport.RequestsSoFar().Count(r => r.Body.Contains(ProposedPlans.ToolName)) > 4, mission.View.Stage + ": " + string.Join(" | ", mission.View.Log.Skip(8).Select(e => e.Kind + " " + e.Text)));
        }

        private static GameSnapshot Snapshot(double ut, double? deadline, string body = "Mun")
        {
            var survey = ScienceSurveyContractTests.Survey("c-test", "2", "LOW", "crewReport", 30000, "3,10");
            survey.DeadlineUt = deadline;
            var snapshot = ContractCheckTests.With(survey);
            snapshot.Ut = ut;
            if (body != "Kerbin") snapshot.Body = body;
            if (body != "Kerbin") snapshot.Situation = "SUB_ORBITAL";
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
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson("Transfer to the Mun and capture into low orbit", "Run crew report science over 3.0° N 10.0° E in low orbit of the Mun", "Return to Kerbin"))),
                ContractWatchTests.MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"));
            foreach (var response in more) transport.Add(response);
            return transport;
        }

        private MissionControl Running(StreamingFakeTransport transport, out StartScript start)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 100000000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => _now;
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot(Start, null, "Kerbin"));
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.True(mission.View.Stage == MissionStage.AwaitingPlanApproval, mission.View.Stage + ": " + string.Join(" | ", mission.View.Log.Select(e => e.Text)));
            // The craft is in orbit of the Mun when the survey's first Sequence Plan is offered.
            mission.Send(new Approve());
            mission.Send(Snapshot(Start, null));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.True(mission.View.Stage == MissionStage.AwaitingSequenceApproval, mission.View.Stage + ": " + string.Join(" | ", mission.View.Log.Select(e => e.Kind + " " + e.Text)));
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
