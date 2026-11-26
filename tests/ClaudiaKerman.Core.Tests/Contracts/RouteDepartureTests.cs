// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
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
    /// #1380 (ADR 0022, ADR 0009), at the Mission Control seam: a Mission Plan that leaves the Contract Route and names the departure and why
    /// (route_departure) isn't sent back. Without Auto Mode it waits for the player's Approval; under Auto Mode it is approved at once and the
    /// departure is posted in the Mission log and kept in the Mission's event log. A departure with no reason is sent back either way.
    /// </summary>
    public sealed class RouteDepartureTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const double LaunchedAt = 448444;
        private const string Reason = "Minmus first: its window opens now and the Mun's comes round again before Minmus is done";
        private static readonly string[] FirstPlan = { "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" };
        private static readonly string[] MinmusFirst = { "Transfer to Minmus and capture into low orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin" };
        private static readonly string[] MunFirst = { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" };
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private StreamingFakeTransport _transport;

        public void Dispose()
        {
            _background.Drain();
            _save.Dispose();
        }

        private static string PlanJson(string departure, params string[] names)
        {
            return "{\"why\":\"Because.\",\"sequences\":["
                + string.Join(",", names.Select(n => "{\"name\":" + JsonSerializer.Serialize(n) + ",\"intent\":\"Fly it.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}"))
                + "],\"total_delta_v\":" + (names.Length * 100) + ",\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\",\"child_vessels\":[],\"goal_steps\":[]"
                + (departure == null ? "" : ",\"route_departure\":" + JsonSerializer.Serialize(departure)) + "}";
        }

        private static GameSnapshot Exploration(double ut)
        {
            var contract = ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT", "Minmus:ORBIT");
            contract.State = "Active";
            var snapshot = ContractCheckTests.With(contract);
            snapshot.Ut = ut;
            return snapshot;
        }

        private MissionControl Mission(IEnumerable<ScriptedResponse> responses)
        {
            _transport = new StreamingFakeTransport(responses.ToArray());
            var client = new ClaudeClient(new CraftCheckRouter(_transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Exploration(0));
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            return mission;
        }

        /// <summary>Claudia proposes each first plan in turn; returns once one is offered.</summary>
        private MissionControl Proposing(params string[] plans)
        {
            var responses = plans.Select((p, i) => ToolUseStreams.ToolUse("p" + i, (ProposedPlans.ToolName, p))).ToList();
            responses.Add(ContractWatchTests.MissionPlanKind());
            var mission = Mission(responses);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            return mission;
        }

        /// <summary>
        /// The first plan's launch flown (nothing met), then a Debrief asking for a revised plan; Claudia proposes each revision in turn,
        /// then a Sequence Plan for whatever is approved. Auto Mode is ticked before the launch when <paramref name="autoMode"/>.
        /// </summary>
        private MissionControl Revising(bool autoMode, params string[] revisions)
        {
            var responses = new List<ScriptedResponse>
            {
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, PlanJson(null, FirstPlan))),
                ContractWatchTests.MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ContractWatchTests.Reply("{\"kind\":\"debrief\",\"say\":\"Off-Nominal.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"alert\":null,"
                    + "\"debrief\":{\"outcome\":\"Off-Nominal\",\"why\":\"Plan again.\",\"what_happened\":\"In orbit.\",\"goal_reached\":false,\"next\":\"Revise.\",\"follow_up\":\"revised_plan\"}}"),
            };
            responses.AddRange(revisions.Select((r, i) => ToolUseStreams.ToolUse("r" + i, (ProposedPlans.ToolName, r))));
            responses.Add(ContractWatchTests.MissionPlanKind());
            responses.Add(ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Mission(responses);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            if (autoMode) mission.Send(new SetAutoMode(true));
            var inOrbit = Exploration(LaunchedAt);
            inOrbit.Situation = "ORBITING";
            mission.Send(inOrbit);
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), start.RunId, LaunchedAt));
            mission.Send(new UtAdvanced(LaunchedAt + 61));
            DrainUntil(mission, v => _transport.Requests.Count >= 5 + revisions.Length && !v.Busy);
            mission.Send(new UtAdvanced(LaunchedAt + 120));
            return mission;
        }

        private string Answers
        {
            get { return string.Join("\n", _transport.Requests.Select(r => r.Body)).Replace("\\\\\\\"", "\"").Replace("\\\"", "\""); }
        }

        private static string Offered(MissionControl mission)
        {
            return mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text;
        }

        private string EventLog
        {
            get { return string.Join("\n", Directory.GetFiles(_save.Path, MissionStore.EventsFile, SearchOption.AllDirectories).Select(File.ReadAllText)); }
        }

        [Fact]
        public void WithoutAutoModeAFirstPlanThatLeavesTheRouteWithAReasonIsOfferedAndWaitsForApproval()
        {
            var mission = Proposing(PlanJson(Reason, new[] { "Launch to low Kerbin orbit" }.Concat(MinmusFirst).ToArray()));
            mission.Send(new UtAdvanced(120));

            Assert.DoesNotContain("before the Mun", Answers);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains("Leaves the Contract Route: " + Reason, Offered(mission));
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
        }
    }
}
