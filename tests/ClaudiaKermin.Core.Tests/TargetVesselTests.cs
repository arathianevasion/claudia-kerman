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
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Target Vessel (ADR 0013): its compact entry in the Game Snapshot, where a Mission's target comes from, that only a
    /// Mission with one sends the entry, and that it survives a save and load.
    /// </summary>
    public sealed class TargetVesselTests : IDisposable
    {
        private const string Goal = "Rendezvous with Kerbin Station Alpha and dock.";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static SnapshotTarget Station(uint id = 4242)
        {
            var target = new SnapshotTarget
            {
                Id = id,
                Name = "Kerbin Station Alpha",
                Body = "Kerbin",
                Situation = "ORBITING",
                Orbit = new SnapshotOrbit
                {
                    Apoapsis = 120000, Periapsis = 118000, Inclination = 0.1, Eccentricity = 0.0003, SemiMajorAxis = 719000,
                    Lan = 12.5, ArgumentOfPeriapsis = 80, MeanAnomalyAtEpoch = 1.25, Epoch = 5000, Body = "Kerbin",
                },
            };
            target.FreePorts["size1"] = 2;
            target.FreePorts["size0"] = 1;
            target.PortTags["fore"] = "size1";
            return target;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        // ---- The entry ----

        [Fact]
        public void ASnapshotWithNoTargetHasNoEntry()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 4242, Name = "Kerbin Station Alpha" });

            Assert.DoesNotContain("\"target\"", snapshot.ToJson(false));
            Assert.DoesNotContain("Kerbin Station Alpha", snapshot.ToJson(true));
        }

        [Fact]
        public void TheEntryCarriesNameBodyOrbitAndFreePortsByCountWithTags()
        {
            var snapshot = OnPad();
            snapshot.Target = Station();

            var json = snapshot.ToJson(false);
            using (var doc = JsonDocument.Parse(json))
            {
                var target = doc.RootElement.GetProperty("target");
                Assert.Equal("Kerbin Station Alpha", target.GetProperty("name").GetString());
                Assert.Equal("Kerbin", target.GetProperty("body").GetString());
                Assert.Equal("ORBITING", target.GetProperty("situation").GetString());
                Assert.Equal(719000, target.GetProperty("orbit").GetProperty("semi_major_axis").GetDouble());
                Assert.Equal(5000, target.GetProperty("orbit").GetProperty("epoch").GetDouble());
                Assert.Equal(2, target.GetProperty("free_ports").GetProperty("size1").GetInt32());
                Assert.Equal(1, target.GetProperty("free_ports").GetProperty("size0").GetInt32());
                Assert.Equal("size1", target.GetProperty("port_tags").GetProperty("fore").GetString());
                // Claudia's turns leave out the id: the Mission keeps it.
                Assert.False(target.TryGetProperty("id", out _));
            }
            // The dynamic snapshot, which leaves out the craft's parts, still carries it.
            Assert.Contains("\"target\":{", snapshot.ToJson(false, false));
        }

        [Fact]
        public void TheEntryStaysCompact()
        {
            var snapshot = OnPad();
            var without = snapshot.ToJson(false).Length;
            var target = Station();
            // Orbit elements as KSP reads them, at full precision.
            target.Orbit = new SnapshotOrbit
            {
                Apoapsis = 120384.61538461538, Periapsis = 118922.30769230769, Inclination = 0.10471975511965977, Eccentricity = 0.0010175644531249998,
                SemiMajorAxis = 719653.4615384615, Lan = 12.566370614359172, ArgumentOfPeriapsis = 80.11061266653972,
                MeanAnomalyAtEpoch = 1.2566370614359172, Epoch = 51234.567890123456, Body = "Kerbin",
            };
            snapshot.Target = target;

            // About 150 tokens by the mod's estimate (characters / 3): the cost of a turn's snapshot for a Mission with a target.
            Assert.InRange(snapshot.ToJson(false).Length - without, 300, 480);
        }

        [Fact]
        public void ATargetWithNoTaggedPortsLeavesPortTagsOut()
        {
            var snapshot = OnPad();
            var target = Station();
            target.PortTags.Clear();
            snapshot.Target = target;

            Assert.DoesNotContain("port_tags", snapshot.ToJson(false));
        }

        [Fact]
        public void TheRecordingRoundTripsTheEntryWithItsId()
        {
            var snapshot = OnPad();
            snapshot.Target = Station();

            var read = GameSnapshot.Parse(snapshot.ToJson()).Target;

            Assert.NotNull(read);
            Assert.Equal(4242u, read.Id);
            Assert.Equal("Kerbin Station Alpha", read.Name);
            Assert.Equal(1.25, read.Orbit.MeanAnomalyAtEpoch);
            Assert.Equal("Kerbin", read.Orbit.Body);
            Assert.Equal(2, read.FreePorts["size1"]);
            Assert.Equal("size1", read.PortTags["fore"]);
        }

        // ---- Where the target comes from ----

        [Fact]
        public void KspsTargetIsTheTarget()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(new SnapshotVessel { Id = 9, Name = "Tug" });

            string how;
            var target = TargetVessel.Choose("Dock with the Tug.", snapshot, out how);

            Assert.Equal(7u, target.Id);
            Assert.Equal("KSP's target", how);
        }

        [Fact]
        public void WithNoKspTargetTheVesselTheGoalNamesIsTheTarget()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 1, Name = "Kerbin Station" });
            snapshot.Vessels.Add(new SnapshotVessel { Id = 2, Name = "Kerbin Station Alpha" });
            snapshot.Vessels.Add(new SnapshotVessel { Id = 3, Name = "Mun Lander" });

            string how;
            var target = TargetVessel.Choose("Rendezvous with kerbin station alpha, then dock.", snapshot, out how);

            // The longest name the goal holds: "Kerbin Station" is inside it too.
            Assert.Equal(2u, target.Id);
            Assert.Equal("Kerbin Station Alpha", target.Name);
            Assert.Equal("named in the Mission Goal", how);
        }

        [Fact]
        public void ANameMatchesOnlyWholeWordsAndShortNamesNever()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 1, Name = "Tug" });
            snapshot.Vessels.Add(new SnapshotVessel { Id = 2, Name = "A" });

            string how;
            Assert.Null(TargetVessel.Choose("Tugboat practice: reach a 100 km orbit.", snapshot, out how));
            Assert.Equal("", how);
        }

        [Fact]
        public void AGoalNamingSeveralVesselsOfOneNameHasNoTargetAndSaysWhy()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 1, Name = "Relay" });
            snapshot.Vessels.Add(new SnapshotVessel { Id = 2, Name = "Relay" });

            string how;
            Assert.Null(TargetVessel.Choose("Dock with the Relay.", snapshot, out how));
            Assert.Contains("several vessels are named \"Relay\"", how);
        }

        [Fact]
        public void AVesselNamedAsTheCraftIsNeverTheTarget()
        {
            var snapshot = OnPad();
            snapshot.Craft.Name = "Mun Lander";
            snapshot.Vessels.Add(new SnapshotVessel { Id = 1, Name = "Mun Lander" });

            string how;
            Assert.Null(TargetVessel.Choose("Fly Mun Lander to the Mun and back.", snapshot, out how));
        }

        [Fact]
        public void AGoalNamingNoVesselHasNoTarget()
        {
            string how;
            Assert.Null(TargetVessel.Choose(Goal, OnPad(), out how));
            Assert.Equal("", how);
        }

        // ---- Through the Mission ----

        [Fact]
        public void AMissionStartedWithKspsTargetSendsTheEntryAndShowsTheTarget()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(4242u, mission.View.TargetVesselId);
            Assert.Equal("Kerbin Station Alpha", mission.View.TargetVesselName);
            Assert.Contains("\"target\":{\"name\":\"Kerbin Station Alpha\"", LastUserText(transport.Requests.Last()));
        }

        [Fact]
        public void AMissionWithNoTargetSendsNoEntryWhateverTheAdapterRead()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
            // A target the player set after the start, or another Mission's, is not this Mission's.
            var later = OnPad();
            later.Target = Station();
            later.KspTarget = Station();
            mission.Send(later);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Null(mission.View.TargetVesselId);
            Assert.Equal("", mission.View.TargetVesselName);
            Assert.DoesNotContain("\"target\"", LastUserText(transport.Requests.Last()));
        }

        [Fact]
        public void AnEntryForAnotherVesselThanTheTargetIsDropped()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var other = OnPad();
            other.Target = Station(99);
            mission.Send(other);
            Assert.DoesNotContain("\"target\"", Captured(mission));

            var own = OnPad();
            own.Target = Station();
            mission.Send(own);
            Assert.Contains("\"target\":{\"id\":4242,", Captured(mission));
        }

        /// <summary>The snapshot the Mission holds now, through the debug capture button.</summary>
        private static string Captured(MissionControl mission)
        {
            mission.Send(new CaptureSnapshot());
            const string Saved = "Game Snapshot saved to ";
            var line = mission.View.Log.Last(e => e.Text.StartsWith(Saved, StringComparison.Ordinal)).Text;
            return File.ReadAllText(line.Substring(Saved.Length));
        }

        [Fact]
        public void TheMissionPlanWaitsForTheTargetTheGoalNamedToBeRead()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 4242, Name = "Kerbin Station Alpha" });
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            _background.Drain();

            // Named in the goal but not yet read: no request until a snapshot carries it.
            Assert.Equal("Kerbin Station Alpha", mission.View.TargetVesselName);
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Empty(transport.Requests);

            var read = OnPad();
            read.Vessels.Add(new SnapshotVessel { Id = 4242, Name = "Kerbin Station Alpha" });
            read.Target = Station();
            mission.Send(read);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains("\"target\":{\"name\":\"Kerbin Station Alpha\"", LastUserText(transport.Requests.Last()));
        }

        [Fact]
        public void TheTargetSurvivesASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            // Through the flat values a ConfigNode holds, as the ScenarioModule writes and reads them.
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();
            var loaded = Control(transport);
            loaded.Send(new MissionsRestored(_save.Path, saved));

            Assert.Equal(4242u, loaded.View.TargetVesselId);
            Assert.Equal("Kerbin Station Alpha", loaded.View.TargetVesselName);
        }

        [Fact]
        public void TheSaveStateRoundTripsTheTargetAndAnOlderSaveHasNone()
        {
            var state = new MissionSaveState { Id = "m1", TargetVesselId = 4242, TargetVesselName = "Station {A}/1" };
            var read = MissionSaveState.FromValues(state.ToValues());
            Assert.Equal(4242u, read.TargetVesselId);
            Assert.Equal("Station {A}/1", read.TargetVesselName);

            var older = MissionSaveState.FromValues(new MissionSaveState { Id = "m2" }.ToValues());
            Assert.Null(older.TargetVesselId);
            Assert.Equal("", older.TargetVesselName);
            Assert.False(new MissionSaveState { Id = "m3" }.ToValues().ContainsKey("targetVesselId"));
        }

        // ---- Rig ----

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            return mission;
        }

        private static string LastUserText(HttpRequestSpec request)
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
