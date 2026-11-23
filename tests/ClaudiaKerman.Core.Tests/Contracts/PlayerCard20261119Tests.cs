// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1260: the Artisan's Contract check card of 2026-11-19 (Career Test save, build e01560f), from the Game Snapshot of that flight:
    /// a craft of about 4,058 m/s, the words "Complete the contracts", and four accepted Contracts saved the way the game saves them.
    /// The recording predates the Contract title in the snapshot, so the test gives each Contract the title the game's window showed.
    /// </summary>
    public sealed class PlayerCard20261119Tests : IDisposable
    {
        private const string PartTest = "20ebfabe-13bf-4fd7-820b-d42504627516";
        private const string ReachSpace = "e087d8d1-f7d4-487f-bb26-e4596333dd05";
        private const string Survey = "9f444a7c-46f5-4693-bb4c-76439ca33876";
        private const string Orbit = "3d6a6efb-37c9-41ca-977d-16dae6b5b279";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static GameSnapshot Snapshot(bool titled)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "contract-card", "2026-11-19-career-test.json");
            var snapshot = GameSnapshot.Parse(File.ReadAllText(path));
            var scale = 4058 / snapshot.Stages.Sum(s => s.DeltaVVac);
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= scale;
            if (titled)
            {
                Contract(snapshot, PartTest).Title = "Test RT-10 \"Hammer\" Solid Fuel Booster";
                Contract(snapshot, ReachSpace).Title = "Reach space";
                Contract(snapshot, Survey).Title = "Gravity survey of the Mun";
                Contract(snapshot, Orbit).Title = "Orbit Kerbin";
            }
            return snapshot;
        }

        private static SnapshotContract Contract(GameSnapshot snapshot, string guid)
        {
            return snapshot.Contracts.Accepted.Single(c => c.Guid == guid);
        }

        private ContractCheckView Card(GameSnapshot snapshot)
        {
            var transport = new StreamingFakeTransport(new[] { ScriptedResponse.Fixture("mission-plan.sse") });
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(snapshot);
            mission.Send(new StartMission("Complete the contracts", 25m, "claude-haiku-5-5"));
            Settle();
            Assert.NotNull(mission.View.ContractCheck);
            return mission.View.ContractCheck;
        }

        private void Settle()
        {
            for (var i = 0; i < 40; i++)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
        }

        [Fact]
        public void TheCardReadsTheFourContractsAsTheGameSavedThem()
        {
            var card = Card(Snapshot(true));

            var orbit = card.Rows.Single(r => r.Guid == Orbit);
            Assert.Equal("Orbit Kerbin", orbit.Title);
            var space = card.Rows.Single(r => r.Guid == ReachSpace);
            Assert.Equal("Reach space", space.Title);
            // Planned with its band, not met at +0: this craft passes 51–56 km far faster than the test's 100–260 m/s, up or down.
            var test = card.NotThisFlight.Single(l => l.Guid == PartTest);
            Assert.Equal("Test RT-10 \"Hammer\" Solid Fuel Booster", test.Title);
            Assert.Equal(LeftOutKind.CraftCant, test.Kind);
            Assert.Contains("through 51–56 km", test.Reason);
            var survey = card.NotThisFlight.Single(l => l.Guid == Survey);
            Assert.Equal("Gravity survey of the Mun", survey.Title);
            Assert.Equal(LeftOutKind.CraftCant, survey.Kind);
            Assert.Contains("gravityScan", survey.Reason);
            Assert.Equal(2, card.Rows.Count);
            Assert.InRange(card.RouteDeltaV, 3000, 4058);
        }

        [Fact]
        public void TheHammerTestIsPlannedWithItsBand()
        {
            var snapshot = Snapshot(true);

            var read = Assert.Single(ContractKinds.Read(Contract(snapshot, PartTest), "Kerbin"));

            Assert.True(read.Plannable, read.NotYet);
            Assert.Equal(51000, read.Band.MinAltitude);
            Assert.Equal(56000, read.Band.MaxAltitude);
            Assert.Contains("51,000–56,000 m", read.Words);
        }

        [Fact]
        public void WithoutTheGamesTitleALineFallsBackToTheComposedOne()
        {
            var card = Card(Snapshot(false));

            Assert.Equal("Explore Kerbin", card.Rows.Single(r => r.Guid == Orbit).Title);
            Assert.Equal("PartTest", card.NotThisFlight.Single(l => l.Guid == PartTest).Title);
        }
    }
}
