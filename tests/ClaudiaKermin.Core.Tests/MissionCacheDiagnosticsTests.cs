// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Cache diagnostics (#569) through <see cref="MissionControl"/> on the streaming fake: under <c>debug_tools</c> every Mission
    /// request carries the beta and <c>diagnostics.previous_message_id</c> (null on the first), a cache read below the previous
    /// request's read plus write writes a <c>cache_diagnosis</c> event, and the id survives a load; without it the request is as before.
    /// The beta itself is unverified until the in-game flight: these tests pin the ticket's field names, not the API's behaviour.
    /// </summary>
    public sealed class MissionCacheDiagnosticsTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Diagnostics = "{\"cache_miss_reason\":{\"type\":\"system_changed\"},\"cache_missed_input_tokens\":2600}";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void WithDebugToolsEveryMissionRequestCarriesTheBetaAndThePreviousMessageIdNullOnTheFirst()
        {
            var transport = Fly(true, ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(JsonValueKind.Null, Body(transport.Requests[0]).GetProperty("diagnostics").GetProperty("previous_message_id").ValueKind);
            Assert.Equal("msg_01MissionPlan", Body(transport.Requests[1]).GetProperty("diagnostics").GetProperty("previous_message_id").GetString());
            foreach (var request in transport.Requests)
                Assert.Equal(ClaudeClient.BetaHeaders + ",cache-diagnosis-2026-04-07", Beta(request));
        }

        [Fact]
        public void WithoutDebugToolsTheRequestJsonAndHeadersAreAsBefore()
        {
            var transport = Fly(false, ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));

            Assert.Equal(2, transport.Requests.Count);
            foreach (var request in transport.Requests)
            {
                Assert.DoesNotContain("diagnostics", request.Body);
                Assert.Equal(ClaudeClient.BetaHeaders, Beta(request));
            }
            Assert.DoesNotContain("last_message_id", File.ReadAllText(Path.Combine(MissionFolder(), "transcript.json")));
        }

        [Fact]
        public void ARequestWithoutDiagnosticsSerializesExactlyAsBefore()
        {
            var plain = new MessagesRequest { Model = "claude-sonnet-5-5", MaxTokens = 10 };
            plain.Messages.Add(Message.UserText("hi"));
            var withBeta = new MessagesRequest { Model = "claude-sonnet-5-5", MaxTokens = 10, Diagnostics = true };
            withBeta.Messages.Add(Message.UserText("hi"));

            Assert.DoesNotContain("diagnostics", plain.ToJson());
            Assert.Equal(plain.ToJson().Replace("\"messages\"", "\"diagnostics\":{\"previous_message_id\":null},\"messages\""), withBeta.ToJson());
        }

        [Fact]
        public void ACacheReadBelowThePreviousReadPlusWriteWritesTheResponsesDiagnosticsAsAnEvent()
        {
            Fly(true, ScriptedResponse.Fixture("mission-plan.sse"), Missed(ScriptedResponse.Fixture("mission-sequence-plan.sse")));

            var events = File.ReadAllLines(Path.Combine(MissionFolder(), "events.jsonl")).Where(l => l.Contains("cache_diagnosis")).ToList();
            var line = Assert.Single(events);
            using (var doc = JsonDocument.Parse(line))
                Assert.Equal(Diagnostics, doc.RootElement.GetProperty("text").GetString());
        }

        [Fact]
        public void ACacheReadThatHeldWritesNoEvent()
        {
            Fly(true, ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));

            Assert.DoesNotContain("cache_diagnosis", File.ReadAllText(Path.Combine(MissionFolder(), "events.jsonl")));
        }

        [Fact]
        public void ThePreviousMessageIdIsKeptInTheTranscriptAndReadBack()
        {
            Fly(true, ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));

            // The id of the last completed response, as of the transcript's last write (the Mission Plan's commit, then the Sequence Plan's).
            Assert.Equal("msg_01MissionSequence", MissionStore.ReadTranscript(MissionFolder()).LastMessageId);
        }

        private static ScriptedResponse Missed(ScriptedResponse response)
        {
            var text = Encoding.UTF8.GetString(response.Body)
                .Replace("\"cache_read_input_tokens\":2600", "\"cache_read_input_tokens\":100")
                .Replace("\"id\":\"msg_01MissionSequence\"", "\"id\":\"msg_01MissionSequence\",\"diagnostics\":" + Diagnostics);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(text) };
        }

        private StreamingFakeTransport Fly(bool debugTools, params ScriptedResponse[] responses)
        {
            var transport = new StreamingFakeTransport(responses);
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { DebugTools = debugTools }, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, MissionStage.AwaitingSequenceApproval);
            return transport;
        }

        private void DrainUntil(MissionControl mission, MissionStage stage)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.Stage != stage && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.Equal(stage, mission.View.Stage);
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body)) return doc.RootElement.Clone();
        }

        private static string Beta(HttpRequestSpec request)
        {
            return request.Headers.Single(h => h.Key == "anthropic-beta").Value;
        }

        private string MissionFolder()
        {
            return Directory.GetDirectories(Path.Combine(_save.Path, "ClaudiaKermin", "missions")).Single();
        }
    }
}
