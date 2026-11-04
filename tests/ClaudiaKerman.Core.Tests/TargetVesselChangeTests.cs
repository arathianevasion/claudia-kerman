// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Changing the Target Vessel after Start (#661): Choose and Clear between sequences, never while Running; a change runs the
    /// Craft Check's target rule on the new target and asks Claudia for a revised Mission Plan, a pending Sequence Plan becoming
    /// Re-plan; Auto Mode re-plans and approves it like any revision (ADR 0009); the new target is in the next Game Snapshot and
    /// survives a save and load.
    /// </summary>
    public sealed class TargetVesselChangeTests : IDisposable
    {
        private const string Goal = "Rendezvous with Kerbin Station Alpha and dock.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string CraftId = "c0ffee00c0ffee00c0ffee00c0ffee00";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- When the control is on ----

        [Fact]
        public void ChooseAndClearAreOnBetweenSequencesAndOffWhileRunning()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToSequencePlan(mission);
            Assert.True(mission.View.TargetNeeded);
            Assert.True(mission.View.CanChangeTarget);

            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.False(mission.View.CanChangeTarget);
            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));
            mission.Send(new ChangeTarget(TargetPick.Cleared));
            Assert.Equal(4242u, mission.View.TargetVesselId);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Target Vessel: Tug", StringComparison.Ordinal));
        }

        [Fact]
        public void AGoalThatNeedsNoTargetOffersNoChange()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse")));
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission("Prove the kOS link from the launch pad.", 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            Assert.False(mission.View.TargetNeeded);
            Assert.False(mission.View.CanChangeTarget);
            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));
            Assert.Null(mission.View.TargetVesselId);
        }

        // ---- The change path ----

        [Fact]
        public void APendingSequencePlanBecomesReplanWhichRunsTheCraftCheckAndAsksForARevisedMissionPlan()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);

            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));
            Assert.Equal(9u, mission.View.TargetVesselId);
            Assert.Equal("Tug", mission.View.TargetVesselName);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text == "Target Vessel: Tug (your pick).");
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal("now Tug", mission.View.PlanStale);
            Assert.Contains(QuickReply.Replan, mission.View.QuickReplies);
            Assert.DoesNotContain(QuickReply.Approve, mission.View.QuickReplies);
            Assert.Equal("The Target Vessel has changed (now Tug): Re-plan", MissionWindowRules.LockLine(mission.View));
            Assert.Equal("The Target Vessel has changed: Re-plan?", MissionWindowRules.NeedsYou(mission.View));

            // Re-plan waits for a snapshot that has read the new target.
            mission.Send(new Replan());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("Re-plan waits for the next Game Snapshot to read the new Target Vessel.", mission.View.Log.Last().Text);

            mission.Send(Snapshot(Tug()));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            mission.Send(new Replan());
            Assert.Equal(MissionStage.Planning, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text == "Re-plan: the Target Vessel changed (now Tug).");
            // The Craft Check judges the craft's size1 port against the Tug's size2 one.
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Craft Check: Warning: ", StringComparison.Ordinal) && e.Text.Contains("Tug's free ports (1 size2)"));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            Assert.Equal(3, transport.Requests.Count);
            var asked = UserText(transport.Requests.Last());
            Assert.Contains("The player changed the Target Vessel between sequences. It is now (quoted data): \"Tug\"", asked);
            Assert.Contains("Tug's free ports", asked);
            Assert.Contains(MissionPrompts.TargetNotice, AllText(transport.Requests.Last()));
            Assert.Contains("\"target\":{\"name\":\"Tug\"", asked);
            Assert.Equal("", mission.View.PlanStale);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
        }

        [Fact]
        public void APendingMissionPlanIsRevisedOnceTheNewTargetIsRead()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Single(transport.Requests);

            mission.Send(Snapshot(Tug()));
            Assert.Equal(MissionStage.Planning, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == "Re-plan: the Target Vessel changed (now Tug).");
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("It is now (quoted data): \"Tug\"", UserText(transport.Requests.Last()));
        }

        [Fact]
        public void ClearAsksForARevisedPlanWithNoTargetAndNoEntry()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);

            mission.Send(new ChangeTarget(TargetPick.Cleared));
            Assert.Null(mission.View.TargetVesselId);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text == "No Target Vessel: cleared.");
            Assert.Equal("cleared", mission.View.PlanStale);

            // The adapter still reads the old target for a moment: the Mission drops its entry.
            mission.Send(Snapshot(Station()));
            mission.Send(new Replan());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            var asked = UserText(transport.Requests.Last());
            Assert.Contains("The player cleared the Target Vessel between sequences", asked);
            Assert.DoesNotContain("\"target\"", asked);
        }

        [Fact]
        public void TheSameTargetAgainOrAPickNoLongerInTheListChangesNothing()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);

            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(4242, "Kerbin Station Alpha"))));
            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(77, "Gone"))));

            Assert.Equal(4242u, mission.View.TargetVesselId);
            Assert.Equal("", mission.View.PlanStale);
            Assert.Contains(mission.View.Log, e => e.Text == "Target Vessel unchanged: \"Gone\", your pick, is no longer in the list.");
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
        }

        // ---- The Game Snapshot, save and load ----

        [Fact]
        public void TheNewTargetIsInTheNextGameSnapshotAndTheOldOnesEntryIsDropped()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToSequencePlan(mission);
            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));

            mission.Send(Snapshot(Station()));
            Assert.DoesNotContain("\"target\"", Captured(mission));
            mission.Send(Snapshot(Tug()));
            Assert.Contains("\"target\":{\"id\":9,", Captured(mission));
        }

        [Fact]
        public void TheNewTargetAndItsReplanSurviveASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));

            // Through the flat values a ConfigNode holds, as the ScenarioModule writes and reads them.
            var values = mission.ExportStates().Single().ToValues();
            Assert.Equal("9", values["targetVesselId"]);
            Assert.Equal("Tug", values["targetVesselName"]);
            Assert.Equal("now Tug", values["targetChange"]);
            var loaded = Control(transport);
            loaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { MissionSaveState.FromValues(values) }));
            loaded.Send(Snapshot(Tug()));

            Assert.Equal(9u, loaded.View.TargetVesselId);
            Assert.Equal("Tug", loaded.View.TargetVesselName);
            Assert.Equal("now Tug", loaded.View.PlanStale);
            Assert.Contains(QuickReply.Replan, loaded.View.QuickReplies);
            Assert.False(new MissionSaveState { Id = "m1" }.ToValues().ContainsKey("targetChange"));
        }

        // ---- Auto Mode (ADR 0009) ----

        [Fact]
        public void UnderAutoModeTheReplanIsTakenAndTheRevisedMissionPlanApproved()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefGoalNotMet(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.Send(new Approve());
            var run = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Equal(4, transport.Requests.Count);

            mission.Send(new ChangeTarget(TargetPick.Of(Vessel(9, "Tug"))));
            mission.Send(Snapshot(Tug()));
            mission.Send(new SetAutoMode(true));

            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == "Re-plan: the Target Vessel changed (now Tug).");
            DrainUntil(mission, v => v.Stage == MissionStage.Running && transport.Requests.Count == 6);
            Assert.Contains("It is now (quoted data): \"Tug\"", UserText(transport.Requests[4]));
            var log = mission.View.Log.ToList();
            var revised = log.FindLastIndex(e => e.Kind == LogKind.MissionPlan);
            Assert.Equal("Approved by Auto Mode.", log[revised + 1].Text);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void UnderAutoModeTheRevisionAfterDroppingTheFirstSequencePlanIsStillApproved()
        {
            // The first Sequence Plan, never flown, is dropped with the old plan: the player approved a Mission Plan all the same.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-manual.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, new PlayerConfig { DefaultAutoMode = true });
            ToSequencePlan(mission);

            mission.Send(new ChangeTarget(TargetPick.Cleared));
            mission.Send(new Replan());
            DrainUntil(mission, v => v.Stage == MissionStage.Running && transport.Requests.Count == 4);
            var log = mission.View.Log.ToList();
            Assert.Equal("Approved by Auto Mode.", log[log.FindLastIndex(e => e.Kind == LogKind.MissionPlan) + 1].Text);
            Assert.True(MissionSaveState.FromValues(mission.ExportStates().Single().ToValues()).MissionPlanApproved);
        }

        [Fact]
        public void UnderAutoModeARevisedFirstMissionPlanStillWaitsForThePlayer()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport, new PlayerConfig { DefaultAutoMode = true });
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            mission.Send(new ChangeTarget(TargetPick.Cleared));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy && transport.Requests.Count == 2);
            // The first Mission Plan, revised, is still the player's go-ahead.
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.DoesNotContain(mission.View.Log, e => e.Text == "Approved by Auto Mode.");
        }

        // ---- Rig ----

        private static SnapshotTarget Station()
        {
            var target = new SnapshotTarget { Id = 4242, Name = "Kerbin Station Alpha", Body = "Kerbin", Situation = "ORBITING" };
            target.FreePorts["size1"] = 2;
            return target;
        }

        private static SnapshotTarget Tug()
        {
            var target = new SnapshotTarget { Id = 9, Name = "Tug", Body = "Kerbin", Situation = "ORBITING" };
            target.FreePorts["size2"] = 1;
            return target;
        }

        private static SnapshotVessel Vessel(uint id, string name)
        {
            return new SnapshotVessel { Id = id, Name = name, Body = "Kerbin", Situation = "ORBITING" };
        }

        /// <summary>The pad craft fitted to dock, with KSP's target the station, both vessels in the game and <paramref name="read"/> as the Target Vessel's entry.</summary>
        private static GameSnapshot Snapshot(SnapshotTarget read = null)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = CraftId;
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 9001, Name = "dockingPort2", Title = "Clamp-O-Tron Docking Port", PortSize = "size1", Tags = { "docking_port" } });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 9002, Name = "RCSBlock.v2", Title = "RV-105 RCS Thruster Block", Tags = { "rcs" } });
            snapshot.Resources.Add(new SnapshotResource { Name = "MonoPropellant", Amount = 30, Max = 30 });
            snapshot.KspTarget = Station();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(9, "Tug"));
            snapshot.Target = read;
            return snapshot;
        }

        private void ToSequencePlan(MissionControl mission)
        {
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            Assert.Equal(4242u, mission.View.TargetVesselId);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            return mission;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        /// <summary>The Debrief fixture, Nominal but with the Mission Goal not met yet, so the loop goes on.</summary>
        private static ScriptedResponse DebriefGoalNotMet()
        {
            var text = Encoding.UTF8.GetString(ScriptedResponse.Fixture("mission-debrief.sse").Body);
            var edited = text.Replace("\\\"goal_reached\\\":true", "\\\"goal_reached\\\":false");
            Assert.NotEqual(text, edited);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(edited), ChunkSize = 4096 };
        }

        /// <summary>The snapshot the Mission holds now, through the debug capture button.</summary>
        private static string Captured(MissionControl mission)
        {
            mission.Send(new CaptureSnapshot());
            const string Saved = "Game Snapshot saved to ";
            var line = mission.View.Log.Last(e => e.Text.StartsWith(Saved, StringComparison.Ordinal)).Text;
            return File.ReadAllText(line.Substring(Saved.Length));
        }

        /// <summary>The text of the last user message of a request, as Claude reads it.</summary>
        private static string UserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray()
                    .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }

        /// <summary>Every text block of a request's messages, joined.</summary>
        private static string AllText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var text = new StringBuilder();
                foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
                {
                    var content = message.GetProperty("content");
                    if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
                    else
                        foreach (var block in content.EnumerateArray())
                            if (block.TryGetProperty("text", out var t)) text.Append(t.GetString());
                }
                return text.ToString();
            }
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
