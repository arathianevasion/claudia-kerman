// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Claudia taken out on EVA by the player in Manual Control (#262): the craft stays the vessel she left, nothing acts on her Kerbal, Hand back waits for a craft.</summary>
    public sealed class EvaManualControlTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const uint Processor = 3102;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl InManualControl()
        {
            var transport = new StreamingFakeTransport(new[] { "mission-plan.sse", "mission-sequence-plan.sse" }.Select(f => ScriptedResponse.Fixture(f, 64)).ToArray());
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, Craft(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            mission.Send(Craft());
            Drain(mission, MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            Drain(mission, MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
            mission.Send(new TakeManualControl());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            mission.TakeCommands();
            return mission;
        }

        private void Drain(MissionControl mission, MissionStage stage)
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

        private static GameSnapshot Craft(Action<GameSnapshot> change = null)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = "craft";
            if (change != null) change(snapshot);
            return snapshot;
        }

        /// <summary>Claudia's Kerbal on EVA: her own vessel, no link home.</summary>
        private static GameSnapshot Kerbal(double ut)
        {
            return Craft(s =>
            {
                s.Craft.VesselId = "eva";
                s.Craft.Name = "Claudia Kermin";
                s.Craft.Eva = true;
                s.HasLink = false;
                s.Ut = ut;
                s.Craft.Parts.Clear();
            });
        }

        private static SeenVessel Vessel(string id, string name, bool claudia = false, bool eva = false, params uint[] processors)
        {
            var vessel = new SeenVessel { Id = id, Name = name, ClaudiaAboard = claudia, Eva = eva, HasKosProcessor = processors.Length > 0 };
            vessel.ProcessorPartIds.AddRange(processors);
            return vessel;
        }

        private static void GoOut(MissionControl mission)
        {
            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Craft", processors: Processor), Vessel("eva", "Claudia Kermin", claudia: true, eva: true) }));
        }

        [Fact]
        public void EvaDuringManualControlKeepsTheCraftTheVesselSheLeftAndBoardingItKeepsIt()
        {
            var mission = InManualControl();
            var ut = Craft().Ut;

            GoOut(mission);
            mission.Send(Kerbal(ut + 1));
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Equal("craft", mission.View.CraftVesselId);

            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Craft", claudia: true, processors: Processor) }));
            mission.Send(Craft(s => s.Ut = ut + 2));
            Assert.Equal("craft", mission.View.CraftVesselId);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Claudia boarded"));
        }

        [Fact]
        public void BoardingAnotherVesselMakesItTheCraftWithANoteInTheLog()
        {
            var mission = InManualControl();
            var ut = Craft().Ut;
            GoOut(mission);
            mission.Send(Kerbal(ut + 1));

            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Craft", processors: Processor), Vessel("lander", "Lander", claudia: true, processors: 7) }));
            mission.Send(Craft(s => { s.Craft.VesselId = "lander"; s.Craft.Name = "Lander"; s.Ut = ut + 2; }));

            Assert.Equal("lander", mission.View.CraftVesselId);
            Assert.Single(mission.View.Log, e => e.Text == "Claudia boarded Lander: it is now the Mission's craft.");
        }

        [Fact]
        public void NoLinkOnHerEvaKerbalTakesNothingBackAndMakesNothingSafe()
        {
            var mission = InManualControl();
            var ut = Craft().Ut;
            GoOut(mission);

            for (var i = 1; i <= 6; i++) mission.Send(Kerbal(ut + i * 100));

            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<MakeCraftSafe>());
            Assert.Null(mission.View.LinkLossTakeBackIn);
            Assert.Equal(ControlLocks.None, mission.View.Locks);
        }

        [Fact]
        public void HandBackIsUnavailableWhileSheIsOutAndAvailableOnceAboardACraftWithAKosProcessor()
        {
            var mission = InManualControl();
            var ut = Craft().Ut;
            Assert.Equal("", mission.View.HandBackUnavailable);

            GoOut(mission);
            mission.Send(Kerbal(ut + 1));
            Assert.Equal("Claudia is out on EVA: board a craft with a kOS processor to hand back", mission.View.HandBackUnavailable);
            mission.Send(new HandBack("done"));
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);

            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Craft", claudia: true, processors: Processor) }));
            mission.Send(Craft(s => s.Ut = ut + 2));
            Assert.Equal("", mission.View.HandBackUnavailable);
        }
    }
}
