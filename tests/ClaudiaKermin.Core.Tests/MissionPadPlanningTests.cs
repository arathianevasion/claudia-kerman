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
    /// can't help (the three orbit tools on the pad, an identical repeat) are refused
    /// with a reason, logged and counted, while the tools list stays byte for byte what it was.
    /// </summary>
    public sealed class MissionPadPlanningTests : IDisposable
    {
        private const string Goal = "Land on the Mun, then return to Kerbin.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Vacuum = "{\"situation\":\"vacuum\"}";
        /// <summary>A stage_performance input that is no row of the stage table, so a repeat of it is a repeat and not a figure already sent.</summary>
        private const string MunVacuum = "{\"situation\":\"vacuum\",\"body\":\"Mun\"}";
        private const string MunTransfer = "{\"target\":\"Mun\",\"target_pe\":30000}";
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
            Assert.Contains("The Script Check runs on every Sequence Plan's script when you send it; a script that fails comes back once for a rewrite.", text);
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

        // #531: the Mission's folder keeps what the Craft Check's plan_ascent flew from, so the ascent can be replayed offline.
        [Fact]
        public void TheMissionFolderKeepsTheGameSnapshotAndInputThePlanAscentCallFlewFrom()
        {
            var pad = Snapshot("mun-shot-pad.json");
            const string ascent = "{\"target_ap\":80000}";
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("cc", ("plan_ascent", ascent)),
                ScriptedResponse.Fixture("craft-check-pass-free.sse"), Plan());
            var mission = Control(transport, pad, route: false);
            Plan(mission);

            var folder = Assert.Single(Directory.GetDirectories(Path.Combine(MissionFolder.Root(_save.Path), "missions")));
            Assert.Equal(ascent, File.ReadAllText(Path.Combine(folder, MissionStore.AscentPlanInputFile)));
            var kept = GameSnapshot.Parse(File.ReadAllText(Path.Combine(folder, MissionStore.AscentPlanSnapshotFile)));
            Assert.Equal(pad.Ut, kept.Ut);
            Assert.Equal(pad.Stages.Count, kept.Stages.Count);
            Assert.Equal(pad.Craft.NoseDrag.Count, kept.Craft.NoseDrag.Count);
            Assert.Equal(pad.Bodies.Count, kept.Bodies.Count);
        }

        [Fact]
        public void ACraftCheckThatPlannedNoAscentLeavesNoSnapshotInTheMissionFolder()
        {
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport);
            Plan(mission);

            var folder = Assert.Single(Directory.GetDirectories(Path.Combine(MissionFolder.Root(_save.Path), "missions")));
            Assert.False(File.Exists(Path.Combine(folder, MissionStore.AscentPlanSnapshotFile)));
            Assert.False(File.Exists(Path.Combine(folder, MissionStore.AscentPlanInputFile)));
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
        public void TheRevisedMissionPlanRequestAfterASettingsChangeCarriesTheStageTableAndThePadWordingOnlyOnThePad()
        {
            var padTransport = new StreamingFakeTransport(Plan(), Plan());
            var onPad = Control(padTransport);
            Plan(onPad);
            onPad.Send(new EditSettings(new MissionSettingsValues { PhysicsWarp = false }));
            DrainUntil(onPad, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            var padText = Text(padTransport.Requests[1]);
            Assert.Contains("changed the Mission Settings", padText);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: ", padText);
            Assert.Contains("On the launch pad", padText);

            var orbitTransport = new StreamingFakeTransport(Plan(), Plan());
            var inOrbit = Control(orbitTransport, InOrbit());
            Plan(inOrbit);
            inOrbit.Send(new EditSettings(new MissionSettingsValues { PhysicsWarp = false }));
            DrainUntil(inOrbit, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            var orbitText = Text(orbitTransport.Requests[1]);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: ", orbitText);
            Assert.DoesNotContain("On the launch pad", orbitText);
            Assert.Equal(Body(padTransport.Requests[1]).GetProperty("tools").GetRawText(), Body(orbitTransport.Requests[1]).GetProperty("tools").GetRawText());
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

        [Fact]
        public void TheMissionToolsListHasNoCheckScriptAndTheCraftCheckOfferIsUnchanged()
        {
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport);
            Plan(mission);

            var names = Body(transport.Requests[0]).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Equal(new[] { "predict_burn", "solve_burn", "stage_performance", "plan_ascent", "find_landing_site", "plan_transfer", "propose_mission_plan" }, names);
            Assert.Equal(new[] { "stage_performance", "plan_ascent" }, PlanningTools.Default(new PassingScriptCheck()).Definitions(ToolOffer.CraftCheck).Select(t => t.Name).ToArray());
        }

        // #575: plan_transfer's answer for each body the Mission Goal names rides after the stage table.
        [Fact]
        public void AGoalNamingTheMunGetsAPlanTransferRowForTheMunAndAGoalNamingNoBodyGetsNone()
        {
            var orbit = Snapshot("mun-shot-v2-kerbin-orbit.json");
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport, orbit);
            Plan(mission);

            var text = Text(transport.Requests[0]);
            var expected = PlanningTools.Default().Run("plan_transfer", MunTransfer, orbit, null);
            Assert.False(expected.IsError, expected.Json);
            Assert.Contains("plan_transfer " + MunTransfer + ": " + expected.Json, text);
            Assert.Contains("A tool whose figures are here needs no call", text.Substring(text.IndexOf("Transfers to each body", StringComparison.Ordinal)));
            Assert.True(text.IndexOf("plan_transfer " + MunTransfer, StringComparison.Ordinal) > text.IndexOf("Stage table", StringComparison.Ordinal));
            Assert.DoesNotContain("plan_transfer {\"target\":\"Minmus\"", text);

            var noBody = new StreamingFakeTransport(Plan());
            var other = Control(noBody, Snapshot("mun-shot-v2-kerbin-orbit.json"));
            other.Send(new StartMission("Prove the kOS link.", 25m, Sonnet));
            DrainUntil(other, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.DoesNotContain("plan_transfer {", Text(noBody.Requests[0]));
        }

        // #458: a goal naming a planet gets its row too, with the Soonest and Optimal windows and the setting's pick; Kerbin, the
        // planet the craft orbits, gets none.
        [Fact]
        public void AGoalNamingDunaGetsAPlanTransferRowWithItsSoonestAndOptimalWindows()
        {
            var orbit = Snapshot("mun-shot-v2-kerbin-orbit.json");
            var transport = new StreamingFakeTransport(Plan());
            var mission = Control(transport, orbit);
            mission.Send(new StartMission("Fly to Duna, then return to Kerbin.", 25m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var text = Text(transport.Requests[0]);
            const string input = "{\"target\":\"Duna\",\"target_pe\":30000}";
            var expected = PlanningTools.Default().Run("plan_transfer", input, Snapshot("mun-shot-v2-kerbin-orbit.json"), null);
            Assert.False(expected.IsError, expected.Json);
            Assert.Contains("\"soonest\":", expected.Json);
            Assert.Contains("\"optimal\":", expected.Json);
            Assert.Contains("\"setting\":\"Balanced\"", expected.Json);
            Assert.Contains("plan_transfer " + input + ": " + expected.Json, text);
            Assert.DoesNotContain("plan_transfer {\"target\":\"Kerbin\"", text);
        }

        // ---- Refusals ----

        [Fact]
        public void AStagePerformanceOrPlanTransferCallWhoseInputIsARowOfTheMissionRequestIsRefusedAndOneForAnotherBodyRuns()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("rows", ("stage_performance", "{ \"situation\": \"vacuum\" }"), ("plan_transfer", "{\"target_pe\":30000,\"target\":\"Mun\"}"),
                    ("stage_performance", MunVacuum), ("plan_transfer", "{\"target\":\"Minmus\",\"target_pe\":30000}")),
                Plan());
            var mission = Control(transport, Snapshot("mun-shot-v2-kerbin-orbit.json"));
            Plan(mission);

            var results = Results(transport.Requests[1]);
            Assert.Equal("its figures are in the Mission request above", ErrorOf(results[0]));
            Assert.Equal("its figures are in the Mission request above", ErrorOf(results[1]));
            Assert.False(IsError(results[2]));
            Assert.False(IsError(results[3]));
            var refused = Events(mission, "tool_refused").Select(e => e.GetProperty("text").GetString()).ToList();
            Assert.Equal(2, refused.Count);
            Assert.EndsWith("(this turn: 2 refused, 0 repeats)", refused[1]);
        }

        [Fact]
        public void AStepOfOnlyCallsWhoseFiguresAreInTheRequestDoesNotCountAsRepeatsAndEndsNothing()
        {
            // #571 with #575: a refusal for figures already in the request is not an identical repeat, so it never stops the loop
            // or turns the next step plan-only, even when the same call comes twice running.
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("a", ("stage_performance", "{ \"situation\": \"vacuum\" }")),
                ToolUseStreams.ToolUse("b", ("stage_performance", "{ \"situation\": \"vacuum\" }")),
                Plan());
            var mission = Control(transport, Snapshot("mun-shot-v2-kerbin-orbit.json"));
            Plan(mission);

            Assert.Equal(3, transport.Requests.Count);
            foreach (var step in new[] { 1, 2 })
            {
                Assert.Equal("its figures are in the Mission request above", ErrorOf(Assert.Single(Results(transport.Requests[step]))));
                Assert.Equal("{\"type\":\"auto\"}", Body(transport.Requests[step]).GetProperty("tool_choice").GetRawText());
            }
            Assert.Empty(Events(mission, "tool_loop_repeat_stop"));
            Assert.EndsWith("0 repeats)", Events(mission, "tool_refused").Last().GetProperty("text").GetString());
        }

        [Fact]
        public void OnThePadTheThreeOrbitToolsAreRefusedWithWhyAndWhatToUse()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("pad", ("predict_burn", "{}"), ("solve_burn", "{}"), ("find_landing_site", "{\"body\":\"Mun\"}"), ("stage_performance", MunVacuum)),
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
        public void AnIdenticalRepeatInTheSameTurnIsRefusedAndADifferentInputStillRuns()
        {
            var transport = new StreamingFakeTransport(
                ToolUseStreams.ToolUse("a", ("stage_performance", MunVacuum)),
                // The same call with its keys in another order and spaced out, a different input, and a second copy in one reply.
                ToolUseStreams.ToolUse("b", ("stage_performance", "{ \"body\": \"Mun\", \"situation\": \"vacuum\" }"), ("stage_performance", "{\"situation\":\"vacuum\",\"body\":\"Minmus\"}"),
                    ("stage_performance", "{\"body\":\"Minmus\",\"situation\":\"vacuum\"}")),
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
                    ("stage_performance", "{\"body\":\"Kerbin\",\"situation\":\"vacuum\"}")),
                ToolUseStreams.ToolUse("b", ("stage_performance", "{\"body\":\"Kerbin\",\"situation\":\"vacuum\"}")),
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
                ToolUseStreams.ToolUse("a", ("stage_performance", MunVacuum), ("predict_burn", "{}")),
                ToolUseStreams.ToolUse("b", ("stage_performance", MunVacuum), ("solve_burn", "{}")),
                Plan());
            var mission = Control(transport);
            Plan(mission);

            var refused = Events(mission, "tool_refused").Select(e => e.GetProperty("text").GetString()).ToList();
            Assert.Equal(3, refused.Count);
            Assert.StartsWith("predict_burn {} -> the craft is on the launch pad", refused[0]);
            Assert.EndsWith("(this turn: 1 refused, 0 repeats)", refused[0]);
            Assert.StartsWith("stage_performance " + MunVacuum + " -> you already called this in step 1", refused[1]);
            Assert.EndsWith("(this turn: 1 refused, 1 repeat)", refused[1]);
            Assert.StartsWith("solve_burn ", refused[2]);
            Assert.EndsWith("(this turn: 2 refused, 1 repeat)", refused[2]);
        }
    }
}
