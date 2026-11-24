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
    /// #1321, at the Mission Control seam: the player chose a collect-science-in-space Contract near the Mun. The capture leg was flown, the
    /// science leg after it ran, and the game did not mark the Requirement met. The Debrief after the science leg, not the capture, names it.
    /// </summary>
    public sealed class CollectScienceWatchTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Guid = "c-sci";
        private const double TransferEnded = 448444;
        private const double ScienceEnded = TransferEnded + 500;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void AfterTheScienceLegTheDebriefNamesTheCollectScienceRequirementTheGameStillShowsOpen()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson(
                    "Transfer to the Mun and capture into low orbit", "Collect science in low Mun orbit", "Return to Kerbin"))),
                ContractWatchTests.MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
                ContractWatchTests.Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: the science.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                    + "\"sequence_plan\":{\"plan_step\":2,\"name\":\"Science\",\"intent\":\"Collect science in low Mun orbit.\",\"why\":\"The Contract.\",\"target\":\"One reading\","
                    + "\"tolerances\":\"In orbit\",\"expected_delta_v\":0,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                    + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"scan\\\".\\n\",\"due_time_ut\":null,\"window_closes_ut\":null,\"late_window_dv\":null},\"debrief\":null}"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"));
            var mission = Started(transport);

            var transfer = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(At(TransferEnded));
            mission.Send(new KosRunEnded(Finished(), transfer.RunId, TransferEnded));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && MissionRequests(transport) >= 5);
            Assert.DoesNotContain("Contract Requirements this sequence served", MissionRequest(transport, 3));

            mission.Send(new Approve());
            var science = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(At(ScienceEnded));
            mission.Send(new KosRunEnded(Finished(), science.RunId, ScienceEnded));
            DrainUntil(mission, v => MissionRequests(transport) >= 6);

            var one = Assert.Single(Served(MissionRequest(transport, 5)));
            Assert.Equal("not met", one.State);
            Assert.Contains("collect science in space near the Mun", one.Requirement);
            Assert.StartsWith("the game hasn't marked it met: it needs science collected in space near the Mun", one.Gap);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("Holding for the game"));
        }

        // ---- Helpers ----

        private sealed class Reading
        {
            public string Requirement = "";
            public string State = "";
            public string Gap = "";
        }

        /// <summary>The Debrief's "Contract Requirements this sequence served" JSON, read back.</summary>
        private static List<Reading> Served(string debrief)
        {
            const string Lead = "Contract Requirements this sequence served (the mod's reading of the game, JSON data): ";
            var at = debrief.IndexOf(Lead, StringComparison.Ordinal);
            Assert.True(at >= 0, "no served line in the Debrief: " + debrief);
            var line = debrief.Substring(at + Lead.Length).Split('\n')[0];
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.EnumerateArray().Select(e => new Reading
            {
                Requirement = e.GetProperty("requirement").GetString(),
                State = e.GetProperty("state").GetString(),
                Gap = e.TryGetProperty("gap", out var gap) ? gap.GetString() : "",
            }).ToList();
        }

        /// <summary>The craft in low Mun orbit at <paramref name="ut"/>, the Requirement not marked met.</summary>
        private static GameSnapshot At(double ut)
        {
            var snapshot = ContractCheckTests.With(ScienceSurveyContractTests.Science(Guid, "2", "Space"));
            snapshot.Ut = ut;
            snapshot.Body = "Mun";
            snapshot.Situation = "ORBITING";
            return snapshot;
        }

        private MissionControl Started(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(ContractCheckTests.With(ScienceSurveyContractTests.Science(Guid, "2", "Space")));
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static bool IsMission(HttpRequestSpec request)
        {
            return request.Body.Contains(ProposedPlans.ToolName);
        }

        private static int MissionRequests(StreamingFakeTransport transport)
        {
            return transport.RequestsSoFar().Count(IsMission);
        }

        /// <summary>The last user text of the Mission request at <paramref name="index"/>, by position.</summary>
        private static string MissionRequest(StreamingFakeTransport transport, int index)
        {
            var mission = transport.RequestsSoFar().Where(IsMission).ToList();
            Assert.True(mission.Count > index, "only " + mission.Count + " Mission requests");
            using var doc = JsonDocument.Parse(mission[index].Body);
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
