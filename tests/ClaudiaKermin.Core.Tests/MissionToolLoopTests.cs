// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The tool loop (#274) through <see cref="MissionControl"/> with scripted tool_use replies on the streaming fake: the tools
    /// offered per request kind, the Alert error, the one commit, byte-exact steps, the call and step limits, the Spending Cap,
    /// and what a refusal, a cut-off, a fallback, a save, a load and a Pause do to a loop mid-way.
    /// </summary>
    public sealed class MissionToolLoopTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Vacuum = "{\"situation\":\"vacuum\"}";
        private const string CutOffNote = "Your last attempt ran out of room while thinking. Decide with what you have and keep your reasoning short.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly List<ManualResetEventSlim> _gates = new List<ManualResetEventSlim>();

        public void Dispose()
        {
            foreach (var gate in _gates) gate.Set();
            _save.Dispose();
        }

        // ---- Setup ----

        private MissionControl Control(IHttpTransport transport, PlanningTools tools = null, bool route = true, IScriptCheck check = null)
        {
            var client = new ClaudeClient(route ? new CraftCheckRouter(transport) : transport, () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            // Always, so every Debrief sends the Margin recheck whose requests these tests look at.
            var mission = new MissionControl(client, _background, new PlayerConfig { MarginRecheck = MarginRecheckMode.Always }, () => true, check ?? new PassingScriptCheck());
            if (tools != null) mission.Tools = tools;
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private void Start(MissionControl mission, decimal cap = 25m)
        {
            mission.Send(new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        private static ScriptedResponse Fixture(string name)
        {
            return ScriptedResponse.Fixture(name);
        }

        private static ScriptedResponse StagePerformance(string tag, string input = Vacuum)
        {
            return ToolUseStreams.ToolUse(tag, ("stage_performance", input));
        }

        /// <summary>A tool for the loop tests: it answers at once, or waits on a gate.</summary>
        private sealed class TestTool : IPlanningTool
        {
            public string Name { get; set; } = "fake";
            public string Description { get { return "A test tool."; } }
            /// <summary>An optional <c>n</c>, so calls can differ: an identical repeat in a turn is refused without running (#508).</summary>
            public string InputSchemaJson { get { return "{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\"}},\"required\":[],\"additionalProperties\":false}"; } }
            public bool OnCraftCheck { get; set; }
            public string Activity { get; set; } = "faking it";
            public Func<ToolOutcome> Answer = () => new ToolOutcome { Json = "{\"ok\":true}", Summary = "ok" };

            public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
            {
                return Answer();
            }
        }

        private static PlanningTools With(params IPlanningTool[] extra)
        {
            return new PlanningTools(new IPlanningTool[] { new StagePerformance() }.Concat(extra));
        }

        // ---- Reading requests ----

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body)) return doc.RootElement.Clone();
        }

        private static List<string> Messages(HttpRequestSpec request)
        {
            return Body(request).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        }

        private static string ToolsJson(HttpRequestSpec request)
        {
            return Body(request).GetProperty("tools").GetRawText();
        }

        private static string[] ToolNames(HttpRequestSpec request)
        {
            return Body(request).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        }

        /// <summary>The tool_result blocks of a request's last message.</summary>
        private static List<JsonElement> Results(HttpRequestSpec request)
        {
            var last = Body(request).GetProperty("messages").EnumerateArray().Last();
            Assert.Equal("user", last.GetProperty("role").GetString());
            return last.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "tool_result").ToList();
        }

        private static string ErrorOf(JsonElement result)
        {
            Assert.True(result.GetProperty("is_error").GetBoolean());
            using (var doc = JsonDocument.Parse(result.GetProperty("content").GetString())) return doc.RootElement.GetProperty("error").GetString();
        }

        private static int Count(string text, string part)
        {
            return text.Split(new[] { part }, StringSplitOptions.None).Length - 1;
        }

        private static string[] Efforts(HttpRequestSpec request)
        {
            return Body(request).GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).ToArray();
        }

        private string Folder(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId);
        }

        private List<JsonElement> PlansLog(MissionControl mission)
        {
            return File.ReadAllLines(Path.Combine(Folder(mission), PlanLog.FileName)).Where(l => l.Length > 0)
                .Select(l => { using (var doc = JsonDocument.Parse(l)) return doc.RootElement.Clone(); }).ToList();
        }

        /// <summary>A Spending Cap measured from a dry run of the same two steps with a roomy cap.</summary>
        private decimal CapWithRoomForTheFirstStepButNotTheNext()
        {
            using (var probe = new MissionToolLoopTests())
            {
                var mission = probe.Control(new StreamingFakeTransport(ToolUseStreams.ToolUsePriced("cap", 100000, ("stage_performance", Vacuum)), Fixture("mission-plan.sse")));
                probe.Start(mission);
                probe.DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
                var log = File.ReadAllText(Path.Combine(probe.Folder(mission), "cost-log.jsonl"));
                var worst = CostLogGap.Amounts(log, "pending");
                var paid = CostLogGap.Amounts(log, "paid");
                return CostLogGap.Between(worst[0], paid[0] + worst[1], "fits the first step but not the next after its real cost");
            }
        }

        private string Events(MissionControl mission)
        {
            var path = Path.Combine(Folder(mission), MissionStore.EventsFile);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static List<LogEntry> ToolLines(MissionControl mission)
        {
            return mission.View.Log.Where(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Planning Tool ")).ToList();
        }

        // ---- Tools offered per request kind ----

        private static string DeltaVCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"delta_v_used\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Nothing should burn.\"}],";
        }

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace("\\\"script\\\":", fields.Replace("\"", "\\\"") + "\\\"script\\\":");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>From Start Mission to a crossed Telemetry Check's Alert, with whatever replies follow the Sequence Plan.</summary>
        private StartScript ToRunning(MissionControl mission)
        {
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot Burned()
        {
            var snapshot = OnPad();
            snapshot.Stages[0].DeltaVVac = 3100;
            return snapshot;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        [Fact]
        public void EveryMissionRequestCarriesTheSameToolsListAndTheCraftCheckAndMarginRecheckOnlyStagePerformance()
        {
            var transport = new StreamingFakeTransport(
                Fixture("mission-plan.sse"),
                Fixture("mission-answer.sse"),
                WithSequenceFields(Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                Fixture("mission-alert-carry-on-plain.sse"),
                Fixture("mission-debrief.sse"));
            var router = new CraftCheckRouter(transport);
            var mission = Control(router, With(new TestTool { Name = "predict_burn" }), route: false);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 2);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(Burned());
            mission.Send(Burned());
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // Mission Plan, answer, Sequence Plan, Alert and Debrief: one tools list, byte for byte, and tool_choice auto on all.
            Assert.Equal(5, transport.Requests.Count);
            Assert.Contains("Alert: a Telemetry Check", transport.Requests[3].Body);
            // The Planning Tools, then the Reply Tool (ADR 0012).
            Assert.Equal(new[] { "stage_performance", "predict_burn", "propose_mission_plan" }, ToolNames(transport.Requests[0]));
            foreach (var request in transport.Requests)
            {
                Assert.Equal(ToolsJson(transport.Requests[0]), ToolsJson(request));
                Assert.Equal("{\"type\":\"auto\"}", Body(request).GetProperty("tool_choice").GetRawText());
                // Non-strict: the module checks the input, so the schemas take none of the reply schema's grammar budget.
                Assert.All(Body(request).GetProperty("tools").EnumerateArray(), t => Assert.False(t.TryGetProperty("strict", out _)));
            }
            // The Alert's turn says no tool is available.
            Assert.Contains("No Planning Tool is available during an Alert", transport.Requests[3].Body);

            // The start check and the Debrief's Margin recheck: stage_performance only.
            Assert.Equal(2, router.CheckRequests.Count);
            Assert.Contains("Margin recheck after a Maneuver Sequence", router.CheckRequests[1].Body);
            foreach (var check in router.CheckRequests)
            {
                Assert.Equal(new[] { "stage_performance" }, ToolNames(check));
                Assert.Equal("{\"type\":\"auto\"}", Body(check).GetProperty("tool_choice").GetRawText());
            }
        }

        [Fact]
        public void AToolCallDuringAnAlertIsAnsweredWithAnErrorAndTheAlertIsStillJudged()
        {
            var transport = new StreamingFakeTransport(
                Fixture("mission-plan.sse"),
                WithSequenceFields(Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                StagePerformance("alert"),
                Fixture("mission-alert-carry-on-plain.sse"));
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(Burned());
            mission.Send(Burned());
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);

            var result = Assert.Single(Results(transport.Requests[3]));
            Assert.Equal("toolu_alert_0", result.GetProperty("tool_use_id").GetString());
            Assert.Equal("not available during an Alert", ErrorOf(result));
            Assert.Equal("{\"error\":\"not available during an Alert\"}", result.GetProperty("content").GetString());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.StartsWith("Alert, delta-v used: carry on.", mission.View.LatestLine);
            Assert.Contains(ToolLines(mission), e => e.Text.EndsWith("error: not available during an Alert"));
        }

        // ---- check_script ----

        private sealed class QuicksaveCheck : IScriptCheck
        {
            public int Calls;

            public ScriptCheckResult Check(string script)
            {
                Calls++;
                return script.ToUpperInvariant().Contains("QUICKSAVE")
                    ? new ScriptCheckResult(null, new List<string> { "line 1: :QUICKSAVE (quicksave)" })
                    : ScriptCheckResult.Pass();
            }
        }

        [Fact]
        public void AScriptThatPassedCheckScriptIsStillCheckedOnTheReply()
        {
            var check = new QuicksaveCheck();
            var transport = new StreamingFakeTransport(
                Fixture("mission-plan.sse"),
                ToolUseStreams.ToolUse("cs", ("check_script", "{\"script\":\"print 1.\"}")),
                Fixture("mission-sequence-plan-unsafe.sse"),
                Fixture("mission-sequence-plan-rewritten.sse"));
            var mission = Control(transport, check: check);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            // The tool said ok, and the reply with the quicksave was refused anyway and rewritten.
            Assert.Equal(4, transport.Requests.Count);
            Assert.Equal("{\"ok\":true}", Assert.Single(Results(transport.Requests[2])).GetProperty("content").GetString());
            Assert.Contains("line 1: :QUICKSAVE (quicksave)", transport.Requests[3].Body);
            Assert.DoesNotContain(mission.View.Log, e => e.Script.Contains("QUICKSAVE"));
            // The tool's check, the unsafe reply's, the rewrite's.
            Assert.Equal(3, check.Calls);
        }

        // ---- One commit, byte-exact steps ----

        [Fact]
        public void EachStepIsTheLastRequestPlusTwoMessagesAndTheLoopCommitsOnceWithTheAcceptedReply()
        {
            var transport = new StreamingFakeTransport(
                StagePerformance("a"),
                StagePerformance("b", "{\"situation\":\"surface\",\"body\":\"Mun\"}"),
                Fixture("mission-plan.sse"),
                Fixture("mission-answer.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(3, transport.Requests.Count);

            for (var k = 0; k < 2; k++)
            {
                var before = Messages(transport.Requests[k]);
                var after = Messages(transport.Requests[k + 1]);
                Assert.Equal(before.Count + 2, after.Count);
                Assert.Equal(before, after.Take(before.Count).ToList());
                // The prefix is the same bytes in the request body itself, not just the same JSON.
                Assert.Contains(string.Join(",", before), transport.Requests[k + 1].Body);
            }
            // Claude's tool_use turn goes back exactly: its thinking signature and its input as streamed.
            var echoed = Messages(transport.Requests[1])[Messages(transport.Requests[1]).Count - 2];
            Assert.Contains("\"signature\":\"c2lnLXRvb2wta\"", echoed);
            Assert.Contains("\"input\":{\"situation\":\"vacuum\"}", echoed);
            var result = Assert.Single(Results(transport.Requests[1]));
            Assert.Equal("toolu_a_0", result.GetProperty("tool_use_id").GetString());
            Assert.False(result.TryGetProperty("is_error", out _));
            Assert.Equal(PlanningTools.Default().Run("stage_performance", Vacuum, OnPad(), null).Json, result.GetProperty("content").GetString());
            Assert.Contains("\"body\":\"Mun\"", Results(transport.Requests[2]).Single().GetProperty("content").GetString());
            // One effort for the whole loop.
            Assert.Equal(new[] { "high" }, Efforts(transport.Requests[2]));

            // The loop joined the conversation once, with the plan: the next turn builds on all of it, once.
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            var next = Messages(transport.Requests[3]);
            var loop = Messages(transport.Requests[2]);
            Assert.Equal(loop, next.Take(loop.Count).ToList());
            Assert.Equal(1, Count(transport.Requests[3].Body, "\"id\":\"toolu_a_0\""));
            Assert.Equal(1, Count(transport.Requests[3].Body, "\"id\":\"toolu_b_0\""));
            var transcript = File.ReadAllText(Path.Combine(Folder(mission), MissionFolder.TranscriptFile));
            Assert.Equal(1, Count(transcript, "toolu_a_0\",\"name\""));
        }

        [Fact]
        public void TheCapcomStripNamesTheToolWhileItRunsAndTheMissionLogHasOneLinePerCall()
        {
            var gate = new ManualResetEventSlim();
            _gates.Add(gate);
            var tool = new TestTool { Activity = "checking stage performance", Answer = () => { gate.Wait(TimeSpan.FromSeconds(5)); return new ToolOutcome { Json = "{\"ok\":true}", Summary = "ok" }; } };
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("cap", ("fake", "{}"), ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(tool));
            Start(mission);
            DrainUntil(mission, v => v.Activity == "checking stage performance");
            Assert.Equal("Claudia is checking stage performance…", MissionWindowRules.BusyLine(mission.View));
            gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var lines = ToolLines(mission);
            Assert.Equal(2, lines.Count);
            Assert.Equal("Planning Tool fake ({}): ok", lines[0].Text);
            Assert.Equal("Planning Tool stage_performance ({\"situation\":\"vacuum\"}): 1 stage, 3200 m/s in all in vacuum at Kerbin", lines[1].Text);
            Assert.Equal(2, Count(Events(mission), "\"tool\""));
        }

        // ---- Limits ----

        [Fact]
        public void UpToSixCallsOfAStepRunTogetherAndAnyMoreAreAnsweredWithAnError()
        {
            var started = new CountdownEvent(6);
            var tool = new TestTool
            {
                Answer = () =>
                {
                    started.Signal();
                    // Only six calls running at once get past this.
                    var together = started.Wait(TimeSpan.FromSeconds(3));
                    return new ToolOutcome { Json = "{\"together\":" + (together ? "true" : "false") + "}", Summary = "ok" };
                },
            };
            var calls = Enumerable.Range(0, 7).Select(i => ("fake", "{\"n\":" + i + "}")).ToArray();
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("par", calls), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(tool));
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            // Every call's result in one user message, in the order of the calls.
            var results = Results(transport.Requests[1]);
            Assert.Equal(Enumerable.Range(0, 7).Select(i => "toolu_par_" + i), results.Select(r => r.GetProperty("tool_use_id").GetString()));
            Assert.All(results.Take(6), r => Assert.Equal("{\"together\":true}", r.GetProperty("content").GetString()));
            Assert.Equal("at most 6 calls run in one step; call this again in the next", ErrorOf(results[6]));
        }

        [Fact]
        public void AfterEightStepsTheNextRequestIsTheLastWithToolChoiceNoneAndTheSameToolsAndTheReplyIsAccepted()
        {
            var transport = new StreamingFakeTransport();
            for (var i = 0; i < 8; i++) transport.Add(ToolUseStreams.ToolUse("s" + i, ("fake", "{\"n\":" + (2 * i) + "}"), ("fake", "{\"n\":" + (2 * i + 1) + "}")));
            transport.Add(Fixture("mission-plan.sse"));
            var mission = Control(transport, With(new TestTool()));
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(9, transport.Requests.Count);
            for (var step = 1; step <= 8; step++)
            {
                Assert.All(Results(transport.Requests[step]), r => Assert.False(r.TryGetProperty("is_error", out _)));
                Assert.Equal(step == 8 ? "{\"type\":\"none\"}" : "{\"type\":\"auto\"}", Body(transport.Requests[step]).GetProperty("tool_choice").GetRawText());
                Assert.Equal(ToolsJson(transport.Requests[0]), ToolsJson(transport.Requests[step]));
                Assert.Equal(Body(transport.Requests[0]).GetProperty("system").GetRawText(), Body(transport.Requests[step]).GetProperty("system").GetRawText());
            }
            Assert.Contains("tool_limit_final", Events(mission));
            Assert.Equal(16, ToolLines(mission).Count(l => !l.Text.Contains("error")));
        }

        [Fact]
        public void AReplyThatStillCallsToolsOnTheToollessRequestFailsTheTurnAndRetrySendsItFromScratch()
        {
            var transport = new StreamingFakeTransport();
            for (var i = 0; i < 9; i++) transport.Add(StagePerformance("s" + i, "{\"situation\":\"surface\",\"n\":" + i + "}"));
            transport.Add(Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(9, transport.Requests.Count);
            Assert.Equal("Claudia kept calling Planning Tools after the limit of 8 steps.", mission.View.PauseReason);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(transport.Requests[0].Body, transport.Requests[9].Body);
        }

        [Fact]
        public void TheToollessRequestAtTheStepLimitIsBlockedByTheSpendingCapAsAnyStepIs()
        {
            Func<ScriptedResponse[]> steps = () => Enumerable.Range(0, 8)
                .Select(i => i == 7 ? ToolUseStreams.ToolUsePriced("cap", 100000, ("stage_performance", Vacuum)) : StagePerformance("s" + i, "{\"situation\":\"surface\",\"n\":" + i + "}"))
                .Concat(new[] { Fixture("mission-plan.sse") }).ToArray();
            decimal cap;
            using (var probe = new MissionToolLoopTests())
            {
                var mission = probe.Control(new StreamingFakeTransport(steps()));
                probe.Start(mission);
                probe.DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
                var log = File.ReadAllText(Path.Combine(probe.Folder(mission), "cost-log.jsonl"));
                var worst = CostLogGap.Amounts(log, "pending");
                var paid = CostLogGap.Amounts(log, "paid");
                decimal low = 0, committed = 0;
                for (var k = 0; k < 8; k++)
                {
                    low = Math.Max(low, committed + worst[k]);
                    committed += paid[k];
                }
                cap = CostLogGap.Between(low, committed + worst[8], "fits the eight steps but not the last request");
            }

            var transport = new StreamingFakeTransport(steps());
            var run = Control(transport);
            Start(run, cap);
            DrainUntil(run, v => v.Stage == MissionStage.Paused);

            Assert.Equal(8, transport.Requests.Count);
            Assert.Contains("Spending Cap", run.View.PauseReason);
            Assert.Contains("tool_loop_discarded", Events(run));
            Assert.DoesNotContain("tool_limit_final", Events(run));
        }

        [Fact]
        public void ACallOverItsTimeLimitIsAnsweredWithAnError()
        {
            var gate = new ManualResetEventSlim();
            _gates.Add(gate);
            var tool = new TestTool { Answer = () => { gate.Wait(TimeSpan.FromSeconds(5)); return new ToolOutcome { Json = "{}" }; } };
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("slow", ("fake", "{}"), ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(tool));
            mission.ToolCallTimeout = TimeSpan.FromMilliseconds(100);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var results = Results(transport.Requests[1]);
            Assert.Equal("the calculation took longer than 0.1 s", ErrorOf(results[0]));
            Assert.False(results[1].TryGetProperty("is_error", out _));
            Assert.Equal(TimeSpan.FromSeconds(5), ToolStep.CallTimeout);
        }

        // ---- The Spending Cap ----

        [Fact]
        public void AStepWithNoRoomUnderTheSpendingCapPausesTheMissionAndDiscardsTheLoop()
        {
            // The first request's worst case fits the cap; after the tool step's real cost the next step's no longer does.
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUsePriced("cap", 100000, ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission, CapWithRoomForTheFirstStepButNotTheNext());
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Single(transport.Requests);
            Assert.Contains("Spending Cap", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies.ToArray());
            Assert.Contains("tool_loop_discarded", Events(mission));

            // The discarded loop's counts are in plans.jsonl, marked discarded (#514).
            var discarded = Assert.Single(PlansLog(mission));
            Assert.Equal("mission_plan", discarded.GetProperty("event").GetString());
            Assert.True(discarded.GetProperty("discarded").GetBoolean());
            Assert.Equal(1, discarded.GetProperty("tool_calls").GetInt32());
            Assert.Equal(1, discarded.GetProperty("tool_steps").GetInt32());
            Assert.Equal(0, discarded.GetProperty("refused_calls").GetInt32());
            Assert.Equal(0, discarded.GetProperty("repeat_calls").GetInt32());

            mission.Send(new RaiseCap(25m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var accepted = PlansLog(mission).Last();
            Assert.False(accepted.GetProperty("discarded").GetBoolean());
            Assert.Equal(0, accepted.GetProperty("tool_steps").GetInt32());
            // Sent again from scratch: the request is the turn's first, with no tool step in it.
            Assert.Equal(transport.Requests[0].Body, transport.Requests[1].Body);
        }

        // ---- Mid-loop failures ----

        [Fact]
        public void ARefusalMidLoopPausesAsTodayAndRetrySendsTheTurnFromScratch()
        {
            var transport = new StreamingFakeTransport(StagePerformance("r"), Fixture("refusal.sse"), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal("Claudia declined to answer that. Retry, Retry with a note, or End Mission.", mission.View.PauseReason);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(transport.Requests[0].Body, transport.Requests[2].Body);
        }

        [Fact]
        public void ACutOffMidLoopGoesToTheAutomaticRetryWithTheLoopDiscarded()
        {
            var transport = new StreamingFakeTransport(StagePerformance("c"), Fixture("max-tokens-tool.sse"), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(3, transport.Requests.Count);
            var retry = transport.Requests[2];
            Assert.Equal(Messages(transport.Requests[0]).Count, Messages(retry).Count);
            Assert.DoesNotContain("tool_result", retry.Body);
            Assert.Equal(new[] { "medium" }, Efforts(retry));
            Assert.Equal(1, Count(retry.Body, CutOffNote));
        }

        [Fact]
        public void AServerSideFallbackMidLoopCarriesOnAndIsLogged()
        {
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUseAfterFallback("f", ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Single(Results(transport.Requests[1]));
            Assert.Contains("tool_loop_fallback", Events(mission));
            Assert.Contains("served by claude-opus-5-5", Events(mission));
        }

        // ---- Save, load and quit mid-loop ----

        [Fact]
        public void ASaveMidLoopHoldsOnlyTheTurnSoAfterAQuitItIsSentFromScratch()
        {
            var plan = ScriptedResponse.Fixture("mission-plan.sse", 64);
            plan.PauseAt = 100;
            _gates.Add(plan.Gate);
            var transport = new StreamingFakeTransport(StagePerformance("q"), plan);
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => transport.Requests.Count == 2);
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();

            var after = new StreamingFakeTransport(Fixture("mission-plan.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Single(after.Requests);
            Assert.DoesNotContain("tool_use", after.Requests[0].Body);
            Assert.Equal(Messages(transport.Requests[0]).Count, Messages(after.Requests[0]).Count);
        }

        [Fact]
        public void ALoadMidLoopRunsNoToolsAndTheTurnIsSentFromScratch()
        {
            var step = StagePerformance("l");
            step.ChunkSize = 64;
            step.PauseAt = 100;
            _gates.Add(step.Gate);
            var transport = new StreamingFakeTransport(step, Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => transport.Requests.Count == 1);
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();

            mission.Send(new MissionsRestored(_save.Path, saved));
            step.Gate.Set();
            // The tool_use reply lands after the load: the save's state comes back, and the craft's next snapshot carries it on.
            DrainUntil(mission, v => !v.Busy);
            Assert.Single(transport.Requests);
            mission.Send(OnPad());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, transport.Requests.Count);
            Assert.DoesNotContain("tool_use", transport.Requests[1].Body);
            Assert.Empty(ToolLines(mission));
        }

        // ---- The Craft Check and the Margin recheck ----

        [Fact]
        public void TheCraftCheckRunsItsOwnToolLoopAndEachStepIsChargedToTheMission()
        {
            // No router: the Craft Check's requests come first on the one transport.
            var transport = new StreamingFakeTransport(StagePerformance("cc"), Fixture("craft-check-pass-free.sse"), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(new TestTool { Name = "predict_burn" }), route: false);
            Start(mission);
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(new[] { "stage_performance" }, ToolNames(transport.Requests[0]));
            var result = Assert.Single(Results(transport.Requests[1]));
            Assert.Contains("\"total_dv\":3200", result.GetProperty("content").GetString());
            Assert.Equal(new[] { "stage_performance", "predict_burn", "propose_mission_plan" }, ToolNames(transport.Requests[2]));
            var costLog = File.ReadAllText(Path.Combine(Folder(mission), MissionFolder.CostLogFile));
            Assert.Equal(2, Count(costLog, "\"craft_check_pending\""));
            Assert.Contains("\"tool_use\"", costLog);
        }

        [Fact]
        public void TheMarginRecheckRunsStagePerformanceAndItsFindingsStillReachTheDebrief()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"), Fixture("mission-debrief.sse"));
            var router = new CraftCheckRouter(transport);
            router.Rechecks.Enqueue(StagePerformance("m", "{\"situation\":\"surface\"}"));
            var mission = Control(router, route: false);
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // The start check, then the recheck's two steps.
            Assert.Equal(3, router.CheckRequests.Count);
            var result = Assert.Single(Results(router.CheckRequests[2]));
            Assert.Contains("\"situation\":\"surface\"", result.GetProperty("content").GetString());
            Assert.Contains("The mod's margin recheck (a Craft Check", transport.Requests[2].Body);
            Assert.Contains(ToolLines(mission), e => e.Text.StartsWith("Planning Tool stage_performance for the margin recheck"));
        }

        // ---- The Reply Tool propose_mission_plan (ADR 0012) ----

        private static string ProposedPlan(string name, string childVessels = "[]")
        {
            return "{\"why\":\"Prove the link.\",\"sequences\":[{\"name\":\"" + name + "\",\"intent\":\"Report from the pad.\",\"delta_v\":0,\"timing\":\"right away\","
                + "\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}],\"total_delta_v\":0,\"expected_duration\":\"a minute\",\"mission_time_s\":-1,"
                + "\"new_goal\":\"\",\"child_vessels\":" + childVessels + "}";
        }

        private static ScriptedResponse Propose(string tag, string plan)
        {
            return ToolUseStreams.ToolUse(tag, (ProposedPlans.ToolName, plan));
        }

        /// <summary>A final text reply carrying <paramref name="json"/>, as the Mission schema allows it.</summary>
        private static ScriptedResponse Text(string json)
        {
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(json) + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        private static ScriptedResponse Kind(string kind, string intent = "none")
        {
            return Text("{\"kind\":\"" + kind + "\",\"say\":\"Done.\",\"reply_intent\":\"" + intent + "\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}");
        }

        private string CostLog(MissionControl mission)
        {
            return File.ReadAllText(Path.Combine(Folder(mission), MissionFolder.CostLogFile));
        }

        [Fact]
        public void EveryKindOfMissionRequestSendsTheSameSchemaToolsAndToolChoice()
        {
            var transport = new StreamingFakeTransport(
                Propose("p", ProposedPlan("Link check")), Kind("mission_plan"),
                Kind("answer", "question"),
                Kind("answer"),
                Propose("s", ProposedPlan("Link check again")), Kind("mission_plan"),
                Kind("answer"),
                WithSequenceFields(Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                Fixture("mission-alert-carry-on-plain.sse"),
                Fixture("mission-debrief.sse"));
            var router = new CraftCheckRouter(transport);
            var mission = Control(router, route: false);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            mission.Send(new Reply("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 3);
            mission.Send(new ChangeSomething("Make it faster."));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            mission.Send(new EditSettings(new MissionSettingsValues { PhysicsWarp = false }));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy && transport.Requests.Count == 6);
            Assert.Equal(new[] { "Link check again" }, mission.View.PlanSequences);
            mission.Send(new AskQuestion("Ready?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 7);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(Burned());
            mission.Send(Burned());
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 9);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // Mission Plan (two steps), Reply, Change, Settings (two steps), Question, Sequence Plan, Alert and Debrief: one
            // schema, one tools list and tool_choice auto, byte for byte, so no request rewrites the cache (#534). effort is
            // not pinned: the cache probe of 2026-10-31 showed changing it keeps the cache read (ADR 0012).
            Assert.Equal(10, transport.Requests.Count);
            Assert.Contains("typed a reply", transport.Requests[2].Body);
            Assert.Contains("wants a change", transport.Requests[3].Body);
            Assert.Contains("changed the Mission Settings", transport.Requests[4].Body);
            Assert.Contains("Alert: a Telemetry Check", transport.Requests[8].Body);
            var first = Body(transport.Requests[0]);
            Assert.Equal(MissionReply.MissionSchemaJson, first.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetRawText());
            foreach (var request in transport.Requests)
            {
                var body = Body(request);
                Assert.Equal(first.GetProperty("output_config").GetProperty("format").GetRawText(), body.GetProperty("output_config").GetProperty("format").GetRawText());
                Assert.Equal(first.GetProperty("tools").GetRawText(), body.GetProperty("tools").GetRawText());
                Assert.Equal("{\"type\":\"auto\"}", body.GetProperty("tool_choice").GetRawText());
            }
        }

        [Fact]
        public void APlanHandedInThroughTheToolIsTheOneAppliedByTheMissionPlanReply()
        {
            var transport = new StreamingFakeTransport(Propose("p", ProposedPlan("Link check")), Kind("mission_plan"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            var result = Assert.Single(Results(transport.Requests[1]));
            Assert.False(result.TryGetProperty("is_error", out _));
            Assert.Equal(ProposedPlans.AcceptedJson, result.GetProperty("content").GetString());
            Assert.Equal(new[] { "Link check" }, mission.View.PlanSequences);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Contains("\"outcome\":\"completed\"", CostLog(mission));
            Assert.DoesNotContain("off_schema", CostLog(mission));
        }

        [Fact]
        public void AnInvalidPlanGetsItsProblemsBackAndTheCorrectedCallIsTheOneApplied()
        {
            var transport = new StreamingFakeTransport(
                Propose("bad", ProposedPlan("First try", "[{\"name\":\"Probe\",\"separation_stage\":-1,\"vessel_id\":\"ghost\"}]")),
                Propose("good", ProposedPlan("Second try")),
                Kind("mission_plan"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            // check_script's way: not an error, ok false and the problems, so she can fix it in the same turn.
            var refused = Assert.Single(Results(transport.Requests[1]));
            Assert.False(refused.TryGetProperty("is_error", out _));
            using (var answer = JsonDocument.Parse(refused.GetProperty("content").GetString()))
            {
                Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());
                var problem = Assert.Single(answer.RootElement.GetProperty("problems").EnumerateArray()).GetString();
                Assert.Contains("\"ghost\"", problem);
                Assert.Contains("has not left the craft", problem);
            }
            Assert.Equal(ProposedPlans.AcceptedJson, Assert.Single(Results(transport.Requests[2])).GetProperty("content").GetString());
            Assert.Equal(new[] { "Second try" }, mission.View.PlanSequences);
        }

        [Fact]
        public void APlanThatDoesNotFitTheSchemaIsRefusedWithTheField()
        {
            var transport = new StreamingFakeTransport(Propose("bad", ProposedPlan("Link check").Replace(",\"new_goal\":\"\"", "")), Propose("good", ProposedPlan("Link check")), Kind("mission_plan"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            Assert.Contains("new_goal", Assert.Single(Results(transport.Requests[1])).GetProperty("content").GetString());
            Assert.Equal(new[] { "Link check" }, mission.View.PlanSequences);
        }

        [Fact]
        public void ProposeMissionPlanOnARequestThatDoesNotAskForAPlanIsAnErrorAndChangesNoPlan()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Propose("seq", ProposedPlan("Something else")), Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var before = mission.View.PlanSequences.ToArray();
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("doesn't ask for a Mission Plan", ErrorOf(Assert.Single(Results(transport.Requests[2]))));
            Assert.Equal(before, mission.View.PlanSequences);
            Assert.DoesNotContain("Something else", string.Join(" ", mission.View.PlanSequences));
        }

        [Fact]
        public void AMissionPlanReplyWithNoAcceptedPlanIsRefusedOffSchema()
        {
            var transport = new StreamingFakeTransport(
                Propose("bad", ProposedPlan("Link check", "[{\"name\":\"Probe\",\"separation_stage\":-1,\"vessel_id\":\"ghost\"}]")), Kind("mission_plan"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Contains("propose_mission_plan", mission.View.PauseReason);
            Assert.Empty(mission.View.PlanSequences);
            Assert.Contains("\"outcome\":\"off_schema\"", CostLog(mission));
        }

        [Theory]
        [InlineData("mission-debrief.sse", false)]
        [InlineData("mission-alert-carry-on-plain.sse", true)]
        public void ATypedReplyDuringAFlightThatComesBackAsADebriefOrAnAlertIsRefused(string fixture, bool change)
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"), Fixture(fixture));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(change ? (MissionInput)new ChangeSomething("Burn later.") : new Reply("Why that much?"));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Contains("didn't fit", mission.View.PauseReason);
            Assert.Contains("\"outcome\":\"off_schema\"", CostLog(mission));
        }

        [Fact]
        public void TheStepBeforeTheToolLimitAsksForTheMissionPlanNow()
        {
            var steps = Enumerable.Range(0, ToolStep.MaxSteps - 1).Select(i => ToolUseStreams.ToolUse("f" + i, ("fake", "{\"n\":" + i + "}")));
            var transport = new StreamingFakeTransport(steps.Concat(new[] { Propose("p", ProposedPlan("Link check")), Kind("mission_plan") }).ToArray());
            var mission = Control(transport, With(new TestTool()));
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            // Only the step whose next request is the last that may call a tool (#509) carries it.
            for (var i = 1; i <= ToolStep.MaxSteps; i++)
            {
                var last = Messages(transport.Requests[i]).Last();
                if (i == ToolStep.MaxSteps - 1) Assert.Contains(MissionPrompts.ProposePlanNow, last);
                else Assert.DoesNotContain(MissionPrompts.ProposePlanNow, last);
            }
            Assert.Equal("{\"type\":\"none\"}", Body(transport.Requests[ToolStep.MaxSteps]).GetProperty("tool_choice").GetRawText());
            Assert.Equal(new[] { "Link check" }, mission.View.PlanSequences);
        }

        [Fact]
        public void AQuickloadOfASaveMadeDuringTheToolLoopOffersThePlanTheToolAcceptedWithoutAskingAgain()
        {
            var reply = Kind("mission_plan");
            reply.PauseAt = 100;
            _gates.Add(reply.Gate);
            var transport = new StreamingFakeTransport(Propose("p", ProposedPlan("Link check")), reply);
            var mission = Control(transport);
            mission.Send(new StartMission(Goal, 25m, Sonnet));
            DrainUntil(mission, v => transport.Requests.Count == 2);
            // Saved while the reply after the tool step was out.
            var saved = mission.ExportStates();
            Assert.Equal(MissionStage.Planning, saved[0].Stage);

            reply.Gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            mission.Send(new MissionsRestored(_save.Path, saved));
            DrainUntil(mission, v => !v.Busy);

            // The turn joined the transcript with its tool loop: the plan is read from the accepted call, and nothing is asked again.
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(new[] { "Link check" }, mission.View.PlanSequences);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("already been answered"));
        }

        [Fact]
        public void TheRebuiltLogShowsTheMissionPlanHandedInThroughTheTool()
        {
            var transport = new StreamingFakeTransport(Propose("p", ProposedPlan("Link check")), Kind("mission_plan"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            // The transcript as written: the tool loop, then the mission_plan reply with no part.
            var log = MissionLogRebuild.From(MissionStore.ReadTranscript(Folder(mission)), Goal, "Craft", new PlayerConfig());

            Assert.Contains(log, e => e.Kind == LogKind.MissionPlan && e.Text.Contains("Link check"));
        }
    }
}
