// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Starting a Mission saves its setup as the player's defaults (#639): the core puts the setup into the player config
    /// and asks the adapter for the write with <see cref="SaveConfigDefaults"/>. A setup that never starts saves nothing.
    /// </summary>
    public sealed class MissionStartDefaultsTests : IDisposable
    {
        private const string Goal = "Land on the Mun and take an EVA report.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Haiku = "claude-haiku-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly TempDir _configDir = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
            _configDir.Dispose();
        }

        private static GameSnapshot Snapshot()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private MissionControl Control(PlayerConfig config)
        {
            var client = new ClaudeClient(new CraftCheckRouter(new StreamingFakeTransport()), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        private static MissionSettingsValues Settings(bool daylight, TimeVersusDeltaV priority)
        {
            return new MissionSettingsValues { DaylightLanding = daylight, TimeVersusDeltaV = priority };
        }

        [Fact]
        public void StartingAMissionWithSonnetMakesItsSetupTheSavedDefaults()
        {
            var config = PlayerConfig.LoadOrCreate(_configDir.Sub);
            Assert.Equal(Haiku, config.DefaultModel);
            var mission = Control(config);
            mission.Send(new SceneChanged(GameScene.Editor));

            CraftCheckRouter.StartChecked(mission, _background, Snapshot(),
                new StartMission(Goal, 4.5m, Sonnet, Settings(false, TimeVersusDeltaV.MostEfficient)));

            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Single(mission.TakeCommands().OfType<SaveConfigDefaults>());
            Assert.Equal(new[] { Sonnet, Haiku }, config.Models);
            Assert.Equal(4.5m, config.DefaultSpendingCap);
            Assert.False(config.DefaultDaylightLanding);
            Assert.Equal(TimeVersusDeltaV.MostEfficient, config.DefaultTimeVersusDeltaV);
            Assert.False(config.DefaultRemote);
            Assert.False(config.DefaultAutoMode);
            // The start form offers the new defaults to the next Mission.
            Assert.Equal(Sonnet, mission.View.StartForm.DefaultModel);

            // The adapter's write, and a restart: Sonnet is still the default.
            Assert.Equal("", config.TrySaveDefaults(_configDir.Sub));
            var loaded = PlayerConfig.LoadOrCreate(_configDir.Sub);
            Assert.Equal(new[] { Sonnet, Haiku }, loaded.Models);
            Assert.Equal(4.5m, loaded.DefaultSpendingCap);
            Assert.False(loaded.DefaultDaylightLanding);
            Assert.Equal(TimeVersusDeltaV.MostEfficient, loaded.DefaultTimeVersusDeltaV);
        }

        [Fact]
        public void ARemoteMissionStartSavesTheRemoteMissionType()
        {
            var config = new PlayerConfig();
            var mission = Control(config);
            mission.Send(new SceneChanged(GameScene.Editor));

            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Haiku, remote: true));

            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Single(mission.TakeCommands().OfType<SaveConfigDefaults>());
            Assert.True(config.DefaultRemote);
        }

        [Fact]
        public void ASetupThatNeverStartsSavesNothing()
        {
            var config = new PlayerConfig();
            var mission = Control(config);
            mission.Send(new SceneChanged(GameScene.Editor));

            // A Craft Check alone, with Sonnet picked: the setup is still pending.
            mission.Send(Snapshot());
            mission.Send(new RunCraftCheck(Goal, 3m, Sonnet));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.CraftCheck.State == CraftCheckState.Running && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SaveConfigDefaults>());
            Assert.Equal(Haiku, config.DefaultModel);
            Assert.Equal(10m, config.DefaultSpendingCap);
        }

        [Fact]
        public void ARefusedStartSavesNothing()
        {
            var config = new PlayerConfig();
            var mission = Control(config);
            // A crewed Mission started in Flight with Claudia not aboard is refused before any Craft Check.
            mission.Send(new SceneChanged(GameScene.Flight));
            var snapshot = Snapshot();
            snapshot.Crew.Clear();
            snapshot.ClaudiaStanding = "Available";
            mission.Send(snapshot);

            mission.Send(new StartMission(Goal, 3m, Sonnet));

            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SaveConfigDefaults>());
            Assert.Equal(Haiku, config.DefaultModel);
            Assert.Equal(10m, config.DefaultSpendingCap);
        }
    }
}
