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
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The craft's parts only when they change (#256), through <see cref="MissionControl"/> on the streaming fake: what each
    /// request of a leg carries of the Game Snapshot, and that the Mission log rebuilt from the transcript is what it was.
    /// </summary>
    public sealed class MissionSnapshotDietTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void ALegSendsOneSnapshotAndTheDebriefOfAnUnchangedCraftLeavesThePartsOut()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval);

            var turns = transport.Requests.Select(LastUserText).ToList();
            // The Mission Plan: the first turn, full.
            Assert.Contains(MissionPrompts.SnapshotHeader, turns[0]);
            Assert.Contains("\"parts\":[", turns[0]);
            // The Sequence Plan right after its Approval: the same leg, nothing flown, so it points back.
            Assert.EndsWith("\n" + SnapshotDiet.AsAbove, turns[1]);
            // The Debrief after the run: the craft has the same parts, so they are left out.
            Assert.DoesNotContain("\"parts\"", turns[2]);
            Assert.Contains(SnapshotDiet.DynamicHeader, turns[2]);
            Assert.Contains("\nparts: unchanged since the Game Snapshot at UT ", turns[2]);
            // The Continue turn right after the Debrief: one snapshot per leg.
            Assert.StartsWith("Continue the Mission.\n" + MissionPrompts.UtMarker, turns[3]);
            Assert.EndsWith("\n" + SnapshotDiet.AsAbove, turns[3]);
        }

        [Fact]
        public void TheDebriefOfACraftThatStagedSendsTheFullSnapshot()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            mission.Send(Snapshot(s => s.Craft.Stage = 0));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count == 4);

            var debrief = LastUserText(transport.Requests[2]);
            Assert.Contains(MissionPrompts.SnapshotHeader, debrief);
            Assert.Contains("\"parts\":[", debrief);
        }

        [Fact]
        public void TheMissionLogRebuiltFromTheTranscriptShowsTheSameLeg()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval);

            var transcript = MissionStore.ReadTranscript(Path.Combine(_save.Path, "ClaudiaKerman", "missions", mission.View.MissionId));
            var rebuilt = MissionLogRebuild.From(transcript, Goal, "", new PlayerConfig());
            var live = mission.View.Log.Where(e => e.Kind != LogKind.Notice).Select(e => e.Kind + ":" + e.Title + ":" + e.Text).ToList();
            Assert.Equal(live, rebuilt.Where(e => e.Kind != LogKind.Notice).Select(e => e.Kind + ":" + e.Title + ":" + e.Text).ToList());
            // The Sequence Plan cards still read the turn's UT, which the same-leg line keeps.
            Assert.Equal(2, rebuilt.Count(e => e.Kind == LogKind.SequencePlan));
        }

        // ---- Setup ----

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                return string.Concat(user.GetProperty("content").EnumerateArray()
                    .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }

        private MissionControl Control(IHttpTransport transport)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private StartScript ToRunning(MissionControl mission)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static GameSnapshot Snapshot(Action<GameSnapshot> change)
        {
            var snapshot = OnPad();
            change(snapshot);
            return snapshot;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
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
    }
}
