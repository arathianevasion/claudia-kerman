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
    /// #1032, seam 1 (the Mission in Core, driven by Game Snapshots with Contracts, Claudia stubbed): a planned Manual stretch serving a
    /// Contract is marked "you fly" in the plan, and at Hand back the mod reads the Contract Requirement it was tagged with. Met, the short
    /// Debrief says so; not met, the Debrief offers a retry (a new planned Manual stretch) or a drop, and a revised plan with neither drops it.
    /// </summary>
    public sealed class ManualStretchHandBackTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Guid = "c-flag";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void TheMissionPlanMarksTheManualStretchServingTheContractYouFly()
        {
            var transport = Transport();
            var mission = InManualStretch(transport);

            var card = mission.View.Log.First(e => e.Kind == LogKind.MissionPlan).Text;
            Assert.Contains("2. Plant the flag [you fly this by hand]", card);
            Assert.Contains("serves: PlantFlag", card.Split('\n').Single(l => l.StartsWith("2. ", StringComparison.Ordinal)));
            Assert.DoesNotContain("serves", card.Split('\n').Single(l => l.StartsWith("1. ", StringComparison.Ordinal)));
        }

        [Fact]
        public void AtHandBackAMetRequirementIsReportedForTheShortDebrief()
        {
            var transport = Transport();
            var mission = InManualStretch(transport);
            mission.Send(At(1000, "Complete"));

            mission.Send(new HandBack("planted it"));
            DrainUntil(() => MissionRequests(transport) >= 6);

            var debrief = LastMissionText(transport);
            Assert.Contains("Contract Requirements this Manual stretch served", debrief);
            Assert.Contains("\"requirement\":\"plant a flag on the Mun\"", debrief);
            Assert.Contains("\"state\":\"met\"", debrief);
            Assert.Contains("say in the Debrief that it is met", debrief);
            Assert.DoesNotContain("retry", debrief);
        }

        [Fact]
        public void AtHandBackARequirementNotMetOffersARetryOrADrop()
        {
            var transport = Transport();
            var mission = InManualStretch(transport);
            mission.Send(At(1000, "Incomplete"));

            mission.Send(new HandBack(""));
            DrainUntil(() => MissionRequests(transport) >= 6);

            var debrief = LastMissionText(transport);
            Assert.Contains("\"state\":\"not met\"", debrief);
            Assert.Contains("the game hasn't marked it met after the Manual stretch", debrief);
            Assert.Contains("offer the player a retry (a new planned Manual stretch, in the revised Mission Plan) or dropping its Contract", debrief);
        }

        /// <summary>The stretch handed back is the leg in hand: a revised plan keeping it flies it again, the retry.</summary>
        [Fact]
        public void AfterARequirementNotMetARevisedPlanWithARetryStretchKeepsTheContract()
        {
            var transport = Transport(Debrief(), PlanUse(Legs("Transfer to the Mun and land", "*Plant the flag, again", "Return to Kerbin")), Kind());
            var mission = InManualStretch(transport);
            mission.Send(At(1000, "Incomplete"));

            mission.Send(new HandBack(""));
            DrainUntil(() => mission.View.Stage == MissionStage.AwaitingPlanApproval);

            var card = mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text;
            Assert.DoesNotContain("Drops:", card);
            Assert.Contains("serves: PlantFlag", card.Split('\n').Single(l => l.StartsWith("2. ", StringComparison.Ordinal)));
        }

        [Fact]
        public void AfterARequirementNotMetARevisedPlanWithNoRetryDropsTheContractWithTheReason()
        {
            var transport = Transport(Debrief(), PlanUse(Legs("Transfer to the Mun and land", "Return to Kerbin")), Kind());
            var mission = InManualStretch(transport);
            mission.Send(At(1000, "Incomplete"));

            mission.Send(new HandBack(""));
            DrainUntil(() => mission.View.Stage == MissionStage.AwaitingPlanApproval);

            var card = mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text;
            Assert.Contains("Drops: PlantFlag: \"plant a flag on the Mun\" isn't met: the game hasn't marked it met after the Manual stretch", card);
        }

        // ---- Helpers ----

        private static GameSnapshot At(double ut, string state)
        {
            var contract = ContractCheckTests.Fixture("PlantFlag.json");
            contract.Guid = Guid;
            contract.DeadlineUt = null;
            contract.Requirements[0].Key = ContractRequirement.KeyOf(Guid, new[] { 0 });
            contract.Requirements[0].State = state;
            var snapshot = ContractCheckTests.With(contract);
            snapshot.Ut = ut;
            if (ut > 0)
            {
                snapshot.Body = "Mun";
                snapshot.Situation = "LANDED";
            }
            return snapshot;
        }

        /// <summary>Leg names; one starting "*" is a Manual stretch.</summary>
        private static string[] Legs(params string[] names)
        {
            return names;
        }

        private static string PlanJson(string[] legs)
        {
            return "{\"why\":\"Because.\",\"sequences\":["
                + string.Join(",", legs.Select(n => "{\"name\":" + JsonSerializer.Serialize(n.TrimStart('*')) + ",\"intent\":\"Fly it.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":"
                    + (n.StartsWith("*", StringComparison.Ordinal) ? "true" : "false") + ",\"vessel\":\"craft\"}"))
                + "],\"total_delta_v\":" + (legs.Length * 100) + ",\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\\\"\\\"\",\"child_vessels\":[],\"goal_steps\":[]}";
        }

        private static ScriptedResponse PlanUse(string[] legs)
        {
            return ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, PlanJson(legs)));
        }

        private static ScriptedResponse Kind()
        {
            return ContractWatchTests.MissionPlanKind();
        }

        private static ScriptedResponse Sequence(int step, string name, bool manual)
        {
            return ContractWatchTests.Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":" + step + ",\"name\":" + JsonSerializer.Serialize(name) + ",\"intent\":\"Do it.\",\"why\":\"The plan.\",\"target\":\"Done\","
                + "\"tolerances\":\"none\",\"expected_delta_v\":" + (manual ? 0 : 100) + ",\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + (manual ? "\"\"" : "\"print \\\"go\\\".\\n\"") + ",\"manual\":" + (manual ? "true" : "false")
                + ",\"due_time_ut\":null,\"window_closes_ut\":null,\"late_window_dv\":null},\"debrief\":null}");
        }

        private static ScriptedResponse Debrief()
        {
            return ContractWatchTests.Reply("{\"kind\":\"debrief\",\"say\":\"Off-Nominal: no flag.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Off-Nominal\",\"why\":\"No flag yet.\",\"what_happened\":\"No flag.\",\"goal_reached\":false,"
                + "\"next\":\"Retry or drop.\",\"follow_up\":\"revised_plan\"}}");
        }

        /// <summary>The plan, its reply, the landing's Sequence Plan, its Debrief and the Manual stretch's; then <paramref name="after"/>.</summary>
        private static StreamingFakeTransport Transport(params ScriptedResponse[] after)
        {
            var responses = new List<ScriptedResponse>
            {
                PlanUse(Legs("Transfer to the Mun and land", "*Plant the flag", "Return to Kerbin")),
                Kind(),
                Sequence(1, "Mun landing", false),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
                Sequence(2, "Plant the flag", true),
            };
            responses.AddRange(after);
            return new StreamingFakeTransport(responses.ToArray());
        }

        /// <summary>The landing flown and Debriefed, and the Manual stretch approved: the player has the craft.</summary>
        private MissionControl InManualStretch(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(At(0, "Incomplete"));
            mission.Send(new StartMission("", 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntil(() => mission.View.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            mission.Send(new Approve());
            DrainUntil(() => mission.View.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(At(900, "Incomplete"));
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), start.RunId, 900));
            DrainUntil(() => mission.View.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            return mission;
        }

        private static bool IsMission(HttpRequestSpec request)
        {
            return request.Body.Contains(ProposedPlans.ToolName);
        }

        private static int MissionRequests(StreamingFakeTransport transport)
        {
            return transport.RequestsSoFar().Count(IsMission);
        }

        /// <summary>The Hand back's Debrief: the 6th Mission request, after the plan, its reply, two Sequence Plans and the landing's Debrief.</summary>
        private static string LastMissionText(StreamingFakeTransport transport)
        {
            var mission = transport.RequestsSoFar().Where(IsMission).ToList();
            Assert.True(mission.Count >= 6, "no Hand back Debrief: " + mission.Count + " Mission requests");
            using var doc = JsonDocument.Parse(mission[5].Body);
            var last = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
            var content = last.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        }

        private void DrainUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
        }
    }
}
