// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Debrief of a run is built from a Game Snapshot read after the run ended, and after a landing once the craft is still (#671).
    /// The wait runs on the Mission's real-time clock, which these tests set by hand.
    /// </summary>
    public sealed class MissionRunSettleTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const double EndedAt = 1210;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private DateTime _now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void TheDebriefFactsCarryTheSnapshotReadAfterTheRunNotTheMidBurnOne()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(Snapshot(1203.48, "ORBITING", altitude: 61234.5, throttle: 0.02));

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            _background.Drain();
            // The latest snapshot is older than the run's end: no Debrief request yet, and the Mission stays Running.
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            mission.Send(Snapshot(EndedAt + 0.5, "ORBITING", altitude: 70123.5));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var facts = LastUserText(transport.Requests[2]);
            Assert.Contains("70123.5", facts);
            Assert.DoesNotContain("61234.5", facts);
            Assert.DoesNotContain("0.02", facts);
        }

        [Fact]
        public void ASnapshotReadAheadOfTheEndAtOrAfterTheRunsUtIsTrusted()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            // The adapter reads a fresh snapshot the moment the run ends, ahead of KosRunEnded: no wait at all.
            mission.Send(Snapshot(EndedAt, "ORBITING", altitude: 70123.5));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("70123.5", LastUserText(transport.Requests[2]));
        }

        [Fact]
        public void AFlyingCraftComingDownWaitsForALandedStillSnapshot()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(Snapshot(1203.48, "FLYING", altitude: 1108, vertical: -5, surface: 30));

            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(Snapshot(EndedAt + 1, "FLYING", altitude: 640, vertical: -4, surface: 20));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            mission.Send(Snapshot(EndedAt + 2, "LANDED", altitude: 70, vertical: -0.2, surface: 0.4));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var facts = LastUserText(transport.Requests[2]);
            Assert.Contains("LANDED", facts);
            Assert.DoesNotContain("1108", facts);
        }

        [Fact]
        public void ALandedCraftThatKeepsSlidingGetsItsDebriefAfterTheTimeoutFromTheLatestSnapshot()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(Snapshot(1203.48, "LANDED", altitude: 70, surface: 3));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));

            mission.Send(Snapshot(EndedAt + 1, "LANDED", altitude: 71, surface: 3));
            _now = _now.AddSeconds(9);
            mission.Send(Snapshot(EndedAt + 10, "LANDED", altitude: 72, surface: 2.9));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);

            _now = _now.AddSeconds(2);
            mission.Send(Snapshot(EndedAt + 12, "LANDED", altitude: 73.5, surface: 2.8));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("73.5", LastUserText(transport.Requests[2]));
        }

        [Fact]
        public void TheTimeoutCountsWithNoSnapshotAtAllWhenTheClockAdvances()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(Snapshot(1203.48, "SPLASHED", altitude: 0, surface: 4));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            _now = _now.AddSeconds(11);
            mission.Send(new UtAdvanced(EndedAt + 11));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
        }

        [Fact]
        public void ManualControlTakenDuringTheWaitEndsItAndNoDebriefGoes()
        {
            var transport = Transport();
            var mission = Running(transport, out var start);
            mission.Send(Snapshot(1203.48, "LANDED", altitude: 70, surface: 3));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(new TakeManualControl());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);

            _now = _now.AddSeconds(30);
            mission.Send(Snapshot(EndedAt + 30, "LANDED", altitude: 70, surface: 0));
            _background.Drain();
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void TheSurfaceSpeedRoundTripsThroughTheSnapshotJsonAndARecordedSnapshotWithoutItStillParses()
        {
            var snapshot = Snapshot(1, "LANDED", altitude: 70, surface: 0.37);
            Assert.Equal(0.37, GameSnapshot.Parse(snapshot.ToJson()).SurfaceSpeed, 6);
            Assert.Equal(0, OnPad().SurfaceSpeed);
            // Claudia's turns don't carry it.
            Assert.DoesNotContain("surface_speed", snapshot.ToJson(false));
        }

        // ---- Helpers ----

        private MissionControl Running(StreamingFakeTransport transport, out StartScript start)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => _now;
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            start = mission.TakeCommands().OfType<StartScript>().Single();
            return mission;
        }

        private static StreamingFakeTransport Transport()
        {
            return new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
        }

        private static GameSnapshot Snapshot(double ut, string situation, double altitude, double vertical = 0, double surface = 0, double throttle = 0)
        {
            var snapshot = OnPad();
            snapshot.Ut = ut;
            snapshot.Situation = situation;
            snapshot.Altitude = altitude;
            snapshot.VerticalSpeed = vertical;
            snapshot.SurfaceSpeed = surface;
            snapshot.Throttle = throttle;
            return snapshot;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new System.Collections.Generic.Dictionary<string, object> { { "success", true } }, "", "", false);
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

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }
    }
}
