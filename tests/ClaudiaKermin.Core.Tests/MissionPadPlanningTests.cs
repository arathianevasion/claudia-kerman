// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Pre-launch planning (#508): the Mission request carries the stage table and the pad wording, and Planning Tool calls that
    /// can't help (the three orbit tools on the pad, check_script on a Mission Plan request, an identical repeat) are refused
    /// with a reason, logged and counted, while the tools list stays byte for byte what it was.
    /// </summary>
    public sealed class MissionPadPlanningTests : IDisposable
    {
        private const string Goal = "Land on the Mun, then return to Kerbin.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Vacuum = "{\"situation\":\"vacuum\"}";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Setup ----

        private MissionControl Control(IHttpTransport transport, GameSnapshot snapshot = null, bool route = true)
        {
            var client = new ClaudeClient(route ? new CraftCheckRouter(transport) : transport, () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(snapshot ?? Snapshot("on-pad.json"));
            return mission;
        }

        private static GameSnapshot Snapshot(string name)
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", name)));
        }

        /// <summary>The pad craft said to be in orbit: the same crew and stages, so only the situation differs.</summary>
        private static GameSnapshot InOrbit()
        {
            var snapshot = Snapshot("on-pad.json");
            snapshot.Situation = "ORBITING";
            return snapshot;
        }

        private void Plan(MissionControl mission)
        {
            mission.Send(new StartMission(Goal, 25m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
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

        private static ScriptedResponse Plan()
        {
            return ScriptedResponse.Fixture("mission-plan.sse");
        }

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body)) return doc.RootElement.Clone();
        }

        /// <summary>Every text block of the request's messages, decoded.</summary>
        private static string Text(HttpRequestSpec request)
        {
            var texts = new List<string>();
            foreach (var message in Body(request).GetProperty("messages").EnumerateArray())
            {
                var content = message.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) texts.Add(content.GetString());
                else foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("text", out var text)) texts.Add(text.GetString());
            }
            return string.Join("\n", texts);
        }

        private static List<JsonElement> Results(HttpRequestSpec request)
        {
            var last = Body(request).GetProperty("messages").EnumerateArray().Last();
            return last.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "tool_result").ToList();
        }

        private static string ErrorOf(JsonElement result)
        {
            Assert.True(result.TryGetProperty("is_error", out var error) && error.GetBoolean(), "not an error: " + result.GetRawText());
            using (var doc = JsonDocument.Parse(result.GetProperty("content").GetString())) return doc.RootElement.GetProperty("error").GetString();
        }

        private static bool IsError(JsonElement result)
        {
            return result.TryGetProperty("is_error", out var error) && error.GetBoolean();
        }

        private string Folder(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId);
        }

        private List<JsonElement> Events(MissionControl mission, string kind)
        {
            var path = Path.Combine(Folder(mission), MissionStore.EventsFile);
            return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => { using (var doc = JsonDocument.Parse(l)) return doc.RootElement.Clone(); })
                .Where(e => e.GetProperty("event").GetString() == kind).ToList();
        }

        // ---- The Mission request ----

        [Fact]
        public void TheMissionRequestFromThePadCarriesTheStageTableAndThePadWording()
        {
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport);
            Plan(mission);

            var text = Text(transport.Requests[0]);
            // The figures stage_performance gives for this craft now, in vacuum and at Kerbin's sea level.
            Assert.Contains("these are the figures stage_performance and plan_ascent would give for this craft now", text);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: " + PlanningTools.Default().Run("stage_performance", Vacuum, Snapshot("on-pad.json"), null).Json, text);
            Assert.Contains("\"pressure_atm\":1", text);
            // The one stage's TWR at each body the Mission Goal names, on vacuum thrust: 60 kN on 4.36 t at Mun g 1.628 and Kerbin g 9.81.
            Assert.Contains("{\"stage\":0,\"Mun\":8.45,\"Kerbin\":1.4}", text);
            Assert.DoesNotContain("\"Minmus\"", text.Substring(text.IndexOf("TWR in vacuum", StringComparison.Ordinal)));
            // The pad wording.
            Assert.Contains("On the launch pad, predict_burn, solve_burn and find_landing_site have nothing to work on", text);
            Assert.Contains("The Mission Plan needs no check_script", text);
            Assert.Contains("needs no call", text);
        }

        [Fact]
        public void TheMissionRequestCarriesTheCraftChecksPlanAscentResult()
        {
            var pad = Snapshot("mun-shot-pad.json");
            const string ascent = "{\"target_ap\":80000}";
            // No router: the Craft Check calls plan_ascent, then passes; the Mission Plan request comes third.
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("cc", ("plan_ascent", ascent)),
                ScriptedResponse.Fixture("craft-check-pass-free.sse"), Plan());
            var mission = Control(transport, pad, route: false);
            Plan(mission);

            var expected = PlanningTools.Default().Run("plan_ascent", ascent, pad, null);
            Assert.False(expected.IsError, expected.Json);
            Assert.Contains("The Craft Check's plan_ascent " + ascent + ": " + expected.Json, Text(transport.Requests[2]));
        }

        [Fact]
        public void TheMissionRequestFromOrbitHasTheStageTableButNoPadWording()
        {
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport, InOrbit());
            Plan(mission);

            var text = Text(transport.Requests[0]);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: ", text);
            Assert.DoesNotContain("On the launch pad", text);
        }

        [Fact]
        public void TheToolsListOnTheMissionRequestIsByteIdenticalToBefore()
        {
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport);
            Plan(mission);

            var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools-list-mission.json"));
            Assert.Equal(golden, Body(transport.Requests[0]).GetProperty("tools").GetRawText());
        }

        // ---- Refusals ----

        [Fact]
        public void OnThePadTheThreeOrbitToolsAreRefusedWithWhyAndWhatToUse()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("pad", ("predict_burn", "{}"), ("solve_burn", "{}"), ("find_landing_site", "{\"body\":\"Mun\"}"), ("stage_performance", Vacuum)),
                Plan());
            var mission = Control(transport);
            Plan(mission);

            var results = Results(transport.Requests[1]);
            Assert.Equal("the craft is on the launch pad, with no orbit to burn from: plan the launch with plan_ascent (the stage table is in the Mission request), and this burn once in orbit", ErrorOf(results[0]));
            Assert.Equal("the craft is on the launch pad, with no orbit to solve a burn from: plan the launch with plan_ascent (the stage table is in the Mission request), and this burn once in orbit", ErrorOf(results[1]));
            Assert.Equal("the craft is on the launch pad, with no orbit and so no ground track to search: plan the landing site once in orbit round the body", ErrorOf(results[2]));
            Assert.False(IsError(results[3]));
        }

        [Fact]
        public void InOrbitTheOrbitToolsRunAsBefore()
        {
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("orb", ("predict_burn", "{}")), Plan());
            var mission = Control(transport, InOrbit());
            Plan(mission);

            var result = Assert.Single(Results(transport.Requests[1]));
            if (IsError(result)) Assert.DoesNotContain("launch pad", ErrorOf(result));
        }

        [Fact]
        public void CheckScriptOnAMissionPlanRequestIsRefused()
        {
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("cs", ("check_script", "{\"script\":\"addons:claudia:report(lexicon()).\"}")), Plan());
            var mission = Control(transport);
            Plan(mission);

            Assert.Equal("the Mission Plan needs no script check; the Sequence Plan's script is checked when you send it", ErrorOf(Assert.Single(Results(transport.Requests[1]))));
        }

        [Fact]
        public void AnIdenticalRepeatInTheSameTurnIsRefusedAndADifferentInputStillRuns()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("a", ("stage_performance", Vacuum)),
                // The same call with its keys in another order and spaced out, a different input, and a second copy in one reply.
                ToolUseStreams.ToolUse("b", ("stage_performance", "{ \"situation\": \"vacuum\" }"), ("stage_performance", "{\"situation\":\"vacuum\",\"body\":\"Mun\"}"),
                    ("stage_performance", "{\"body\":\"Mun\",\"situation\":\"vacuum\"}")),
                Plan());
            var mission = Control(transport);
            Plan(mission);

            Assert.False(IsError(Assert.Single(Results(transport.Requests[1]))));
            var results = Results(transport.Requests[2]);
            Assert.Equal("you already called this in step 1; its result is above", ErrorOf(results[0]));
            Assert.False(IsError(results[1]));
            Assert.Equal("you already called this in this reply; its result is with the first call's", ErrorOf(results[2]));
        }

        [Fact]
        public void ACallThatGotNoResultIsNoRepeat()
        {
            var transport = new StreamingFakeTransport(
                // The seventh call is over the per-step limit and gets no result, so sending it again in the next step runs it.
                ToolUseStreams.ToolUse("a", ("stage_performance", "{\"situation\":\"surface\"}"), ("stage_performance", "{\"situation\":\"vacuum\",\"body\":\"Mun\"}"),
                    ("stage_performance", "{\"situation\":\"vacuum\",\"body\":\"Minmus\"}"), ("stage_performance", "{\"situation\":\"surface\",\"body\":\"Mun\"}"),
                    ("stage_performance", "{\"situation\":\"surface\",\"body\":\"Minmus\"}"), ("stage_performance", "{\"situation\":\"vacuum\",\"body\":\"Sun\"}"),
                    ("stage_performance", Vacuum)),
                ToolUseStreams.ToolUse("b", ("stage_performance", Vacuum)),
                Plan());
            var mission = Control(transport);
            Plan(mission);

            Assert.StartsWith("at most 6 calls", ErrorOf(Results(transport.Requests[1])[6]));
            Assert.False(IsError(Assert.Single(Results(transport.Requests[2]))));
        }

        [Fact]
        public void RefusalsAreLoggedAsToolRefusedEventsWithTheTurnsCounts()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("a", ("stage_performance", Vacuum), ("predict_burn", "{}")),
                ToolUseStreams.ToolUse("b", ("stage_performance", Vacuum), ("check_script", "{\"script\":\"print 1.\"}")),
                Plan());
            var mission = Control(transport);
            Plan(mission);

            var refused = Events(mission, "tool_refused").Select(e => e.GetProperty("text").GetString()).ToList();
            Assert.Equal(3, refused.Count);
            Assert.StartsWith("predict_burn {} -> the craft is on the launch pad", refused[0]);
            Assert.EndsWith("(this turn: 1 refused, 0 repeats)", refused[0]);
            Assert.StartsWith("stage_performance {\"situation\":\"vacuum\"} -> you already called this in step 1", refused[1]);
            Assert.EndsWith("(this turn: 1 refused, 1 repeat)", refused[1]);
            Assert.StartsWith("check_script ", refused[2]);
            Assert.EndsWith("(this turn: 2 refused, 1 repeat)", refused[2]);
        }
    }
}
