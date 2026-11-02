// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// A window wait over 6 h is a Due Time (#616): the launch of a Mission to Duna, on the pad at the start of a new game, waits
    /// for the Transfer Window #458's solver gives, less the launch lead; the KSP alarm behind <see cref="IAlarmClock"/> (a fake
    /// here) is set, moved and removed; a missed window is moved within the Craft Check budget or raises an Alert; Auto Mode
    /// never approves at it; and it survives a save and load. Driven through the Mission seam with hand-written replies.
    /// </summary>
    public sealed class WindowWaitTests : IDisposable
    {
        private const string Goal = "Orbit Duna.";
        private const double Day = 21600;
        /// <summary>The Mission Plan's budget: a 3,400 m/s launch and a 1,600 m/s transfer with capture.</summary>
        private const double Budget = 5000;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The fake alarm clock ----

        /// <summary>The KSP Alarm Clock as a dictionary: one alarm per Mission, as the stock one keeps them.</summary>
        private sealed class FakeAlarmClock : IAlarmClock
        {
            public readonly Dictionary<string, double> Alarms = new Dictionary<string, double>();
            public readonly List<string> Calls = new List<string>();

            public void Set(string missionId, double ut, string title)
            {
                Alarms[missionId] = ut;
                Calls.Add("set");
            }

            public void Clear(string missionId)
            {
                Alarms.Remove(missionId);
                Calls.Add("clear");
            }

            /// <summary>Carries out the Mission's commands as the adapter does, and returns them.</summary>
            public IList<AdapterCommand> Take(MissionControl mission)
            {
                var commands = mission.TakeCommands();
                foreach (var command in commands) AlarmClockCommands.Apply(command, this);
                return commands;
            }
        }

        // ---- Hand-written replies ----

        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\n"
                + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\n"
                + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        private static ScriptedResponse DunaPlan()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: launch at the Duna window, then the transfer.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Duna's window is months away.\",\"sequences\":["
                + "{\"name\":\"Launch\",\"intent\":\"Reach a 100 km orbit.\",\"delta_v\":3400,\"timing\":\"at the window\",\"due_time_ut\":-1},"
                + "{\"name\":\"Duna transfer\",\"intent\":\"Burn for Duna and capture.\",\"delta_v\":1600,\"timing\":\"right after\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":5000,\"expected_duration\":\"about a year\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse LaunchPlan(double due = -1)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Launch.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Launch\",\"intent\":\"Reach a 100 km orbit.\",\"why\":\"First step.\",\"target\":\"100 km orbit\","
                + "\"tolerances\":\"Ap within 5 km\",\"expected_delta_v\":3400,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\","
                + "\"due_time_ut\":" + due.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        // ---- Harness ----

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", name));
        }

        /// <summary>The craft on the pad with the stock body table (Duna in it), at <paramref name="ut"/>, with <paramref name="deltaV"/> m/s in vacuum.</summary>
        private static GameSnapshot OnPad(double ut, double deltaV, string vesselId = "")
        {
            var snapshot = GameSnapshot.Parse(Fixture("on-pad.json"));
            snapshot.Bodies = GameSnapshot.Parse(Fixture("mun-shot-v2-kerbin-orbit.json")).Bodies;
            snapshot.Ut = ut;
            snapshot.Stages[0].DeltaVVac = deltaV;
            snapshot.Craft.VesselId = vesselId;
            return snapshot;
        }

        /// <summary>The Duna window the mod picks from <paramref name="ut"/>, as the Mission's default settings pick it.</summary>
        private static TransferWindow Window(double ut)
        {
            var snapshot = OnPad(ut, 9000);
            var duna = snapshot.Bodies.Single(b => b.Name == "Duna");
            var window = WindowWaits.Window(snapshot, duna, new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent);
            Assert.NotNull(window);
            return window;
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        /// <summary>Through the Mission Plan to the launch's Sequence Plan awaiting Approval, the craft on the pad at <paramref name="ut"/>.</summary>
        private void ToLaunchPlan(MissionControl mission, double ut, double deltaV = 9000, string vesselId = "")
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad(ut, deltaV, vesselId));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(ut, deltaV, vesselId), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private static string Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return string.Join("\n", doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()));
        }

        // ---- The rule ----

        [Fact]
        public void TheRuleIsSixHoursFixedAndTheDueTimeIsTheWindowLessTheLaunchLead()
        {
            const double now = 1000;
            var departure = now + WindowWaits.LaunchLeadS + WindowWaits.LongWaitS;
            Assert.Null(WindowWaits.DueTime(departure, now));
            Assert.Equal(departure + 1 - WindowWaits.LaunchLeadS, WindowWaits.DueTime(departure + 1, now));
            Assert.Equal(6 * 3600, WindowWaits.LongWaitS);
        }

        [Fact]
        public void TheNextWindowFitsWhenTheBudgetSumWithItsCostHasTheMargin()
        {
            // 5,000 m/s budgeted with 1,600 for the window; the craft has 5,980, which is 5,200 plus 15%.
            Assert.True(WindowWaits.Fits(5000, 1600, 1800, 5980, 15));
            Assert.False(WindowWaits.Fits(5000, 1600, 1900, 5980, 15));
            // No delta-v figure: only a window no dearer than the planned one fits.
            Assert.True(WindowWaits.Fits(5000, 1600, 1600, null, 15));
            Assert.False(WindowWaits.Fits(5000, 1600, 1601, null, 15));
        }

        [Fact]
        public void AWindowWaitOverSixHoursIsADueTimeOnTheLaunchWithItsAlarmAndStripLine()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            const double now = 1203.48;
            ToLaunchPlan(mission, now);
            clock.Take(mission);

            var window = Window(now);
            var due = window.DepartureUt - WindowWaits.LaunchLeadS;
            Assert.True(due - now > WindowWaits.LongWaitS, "the first Duna window of a new game is months off");
            var view = mission.View;
            Assert.True(view.DueTime.HasDueTime);
            Assert.Equal(due, view.DueTime.Ut, 0);
            Assert.Equal("the Duna window", view.DueTime.WaitsFor);
            Assert.Equal(due, clock.Alarms[view.MissionId], 0);
            Assert.Contains(view.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Waiting for the Duna window"));
            Assert.StartsWith("Due Time: " + KerbinTime.Date(due), view.Log.Last(e => e.Kind == LogKind.SequencePlan).Text);
            // The strip names what it waits for and the day; nothing is asked of the player until then.
            Assert.Equal("Waiting for the Duna window: " + KerbinTime.ShortDate(due), MissionWindowRules.StripStatus(view).Text);
            Assert.Equal(ChipTone.Info, MissionWindowRules.StripStatus(view).Tone);
            // The Missions list shows it as Due, with the day.
            var entry = view.Missions.Single(m => m.Id == view.MissionId);
            Assert.Equal("Due", MissionListRules.Status(entry).Text);
            Assert.Equal("Due " + KerbinTime.ShortDate(due) + ": waiting for the Duna window", MissionListRules.Due(entry));

            // Approve before it starts nothing.
            mission.Send(new Approve());
            Assert.Empty(clock.Take(mission).OfType<StartScript>());
        }

        [Fact]
        public void AShorterWindowWaitStaysAnInScriptWarpWithNoDueTime()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            // Inside the window's wide part, the window the mod picks leaves within 6 h.
            var start = Window(1203.48).DepartureUt + 2 * Day;
            Assert.Null(WindowWaits.DueTime(Window(start).DepartureUt, start));
            ToLaunchPlan(mission, start);
            clock.Take(mission);

            Assert.False(mission.View.DueTime.HasDueTime);
            Assert.Empty(clock.Alarms);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Waiting for the Duna window"));
            mission.Send(new Approve());
            Assert.Single(clock.Take(mission).OfType<StartScript>());
        }

        [Fact]
        public void AGoalWithNoPlanetGetsNoWindowDueTime()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad(1203.48, 9000));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(1203.48, 9000), new StartMission("Orbit the Mun.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.False(mission.View.DueTime.HasDueTime);
        }

        // #647: a moon's window is the same rule. Minmus, out of the parking plane, is cheapest near a node weeks off: Most
        // efficient waits for it on the pad; Balanced doesn't (the node saves under 10%).
        [Fact]
        public void MostEfficientWaitsOnThePadForTheMinmusWindowAndBalancedDoesNot()
        {
            foreach (var setting in new[] { TimeVersusDeltaV.MostEfficient, TimeVersusDeltaV.Balanced })
            {
                var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()), new PlayerConfig { DefaultTimeVersusDeltaV = setting });
                mission.Send(new SceneChanged(GameScene.Flight));
                mission.Send(OnPad(1203.48, 9000));
                CraftCheckRouter.StartChecked(mission, _background, OnPad(1203.48, 9000), new StartMission("Orbit Minmus.", 10m, "claude-sonnet-5-5"));
                DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
                mission.Send(new Approve());
                DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

                var minmus = OnPad(1203.48, 9000).Bodies.Single(b => b.Name == "Minmus");
                var window = WindowWaits.Window(OnPad(1203.48, 9000), minmus, setting, new PlayerConfig().CraftCheckMarginPercent);
                if (setting == TimeVersusDeltaV.MostEfficient)
                {
                    Assert.True(mission.View.DueTime.HasDueTime);
                    Assert.Equal(window.DepartureUt - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);
                    Assert.Equal("the Minmus window", mission.View.DueTime.WaitsFor);
                }
                else
                {
                    Assert.Equal(1203.48, window.DepartureUt, 0);
                    Assert.False(mission.View.DueTime.HasDueTime);
                }
            }
        }

        [Fact]
        public void AutoModeNeverApprovesAtTheWindowsDueTime()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToLaunchPlan(mission, 1203.48);
            mission.Send(new SetAutoMode(true));
            var due = mission.View.DueTime.Ut;
            mission.Send(new UtAdvanced(due + 1));
            _background.Drain();

            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal("Auto Mode is waiting for you: a Due Time.", mission.View.AutoModeStop);
        }

        [Fact]
        public void ApprovingAtTheDueTimeOrInsideTheLaunchLeadStartsTheScriptWithNoMiss()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            // A craft with no margin at all: a re-check of the budget would raise an Alert.
            ToLaunchPlan(mission, 1203.48, Budget);
            clock.Take(mission);

            mission.Send(new UtAdvanced(mission.View.DueTime.Ut + WindowWaits.LaunchLeadS / 2));
            mission.Send(new Approve());

            Assert.Single(clock.Take(mission).OfType<StartScript>());
            Assert.Empty(clock.Alarms);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was missed"));
        }

        [Fact]
        public void ALaterDueTimeOfClaudiasOwnStandsAndIsNotAWindowWait()
        {
            var later = Window(1203.48).DepartureUt + 10 * Day;
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(later)));
            ToLaunchPlan(mission, 1203.48);

            Assert.Equal(later, mission.View.DueTime.Ut);
            Assert.Equal("", mission.View.DueTime.WaitsFor);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Waiting for the Duna window"));
        }

        // ---- A missed window ----

        [Fact]
        public void AMissedWindowWithinTheBudgetMovesTheDueTimeAndTheAlarm()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToLaunchPlan(mission, 1203.48, 20000);
            clock.Take(mission);
            var first = mission.View.DueTime.Ut;

            // Back long after the window has closed: the next one is a synodic period on.
            var back = Window(1203.48).DepartureUt + 120 * Day;
            mission.Send(new UtAdvanced(back));
            clock.Take(mission);
            mission.Send(new Approve());
            var commands = clock.Take(mission);

            var next = Window(back);
            var moved = next.DepartureUt - WindowWaits.LaunchLeadS;
            Assert.True(moved - back > WindowWaits.LongWaitS);
            Assert.Empty(commands.OfType<StartScript>());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal(moved, mission.View.DueTime.Ut, 0);
            Assert.False(mission.View.DueTime.Reached);
            Assert.Equal(moved, clock.Alarms[mission.View.MissionId], 0);
            Assert.NotEqual(first, clock.Alarms[mission.View.MissionId]);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("was missed") && e.Text.Contains("the Due Time moves to " + KerbinTime.Date(moved)));
        }

        [Fact]
        public void AMissedWindowWhoseNextOneIsUnderSixHoursOffStartsTheScriptAndRemovesTheAlarm()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToLaunchPlan(mission, 1203.48, 20000);
            clock.Take(mission);

            // Two days late, the window is still open.
            var back = Window(1203.48).DepartureUt + 2 * Day;
            mission.Send(new UtAdvanced(back));
            clock.Take(mission);
            mission.Send(new Approve());

            Assert.Single(clock.Take(mission).OfType<StartScript>());
            Assert.Empty(clock.Alarms);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("under 6 h off"));
        }

        [Fact]
        public void AMissedWindowOutsideTheBudgetRaisesAnAlertForARevisedMissionPlan()
        {
            var clock = new FakeAlarmClock();
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan());
            var mission = Control(transport);
            // Exactly the budget, with no margin: no window fits it.
            ToLaunchPlan(mission, 1203.48, Budget);
            clock.Take(mission);

            mission.Send(new UtAdvanced(Window(1203.48).DepartureUt + 120 * Day));
            clock.Take(mission);
            mission.Send(new Approve());
            var commands = clock.Take(mission);

            Assert.Empty(commands.OfType<StartScript>());
            Assert.Empty(clock.Alarms);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("outside the Craft Check budget") && e.Text.Contains("Claudia is revising the Mission Plan"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var asked = Messages(transport.Requests.Last());
            Assert.Contains("came back after the Transfer Window", asked);
            Assert.Contains("hand in a plan with no sequences whose why proposes a reduced goal", asked);
        }

        // ---- The alarm and the Mission's end ----

        [Fact]
        public void EndingTheMissionRemovesTheAlarm()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToLaunchPlan(mission, 1203.48);
            clock.Take(mission);
            Assert.Single(clock.Alarms);

            mission.Send(new EndMission());
            clock.Take(mission);

            Assert.Empty(clock.Alarms);
            Assert.Equal("clear", clock.Calls.Last());
        }

        // ---- Save and load ----

        [Fact]
        public void TheWindowsDueTimeSurvivesASaveAndLoadAndAMissedWindowStillMovesIt()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToLaunchPlan(mission, 1203.48, 20000, "abc123");
            var due = mission.View.DueTime.Ut;

            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal(due, reread.SequencePlan.DueTimeUt);
            Assert.Equal("Duna", reread.WindowPlanet);
            Assert.Equal(due + WindowWaits.LaunchLeadS, reread.WindowUt.Value, 3);
            Assert.True(reread.WindowDeltaV > 0);

            var clock = new FakeAlarmClock();
            var reload = Control(new StreamingFakeTransport());
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { reread }));
            reload.Send(OnPad(1203.48, 20000, "abc123"));
            clock.Take(reload);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, reload.View.Stage);
            Assert.Equal(due, reload.View.DueTime.Ut);
            Assert.Equal("the Duna window", reload.View.DueTime.WaitsFor);
            Assert.Equal(due, clock.Alarms[reload.View.MissionId]);

            var back = due + WindowWaits.LaunchLeadS + 120 * Day;
            reload.Send(new UtAdvanced(back));
            reload.Send(new Approve());
            clock.Take(reload);
            Assert.Equal(Window(back).DepartureUt - WindowWaits.LaunchLeadS, reload.View.DueTime.Ut, 0);
        }

        [Fact]
        public void AParkedMissionWaitingForItsWindowShowsAsDueOnTheList()
        {
            var state = new MissionSaveState
            {
                Id = "m1", Stage = MissionStage.Paused, StageBeforePause = MissionStage.AwaitingSequenceApproval, WindowPlanet = "Duna", WindowUt = 5e6, WindowDeltaV = 1600,
                SequencePlan = new SequencePlan { Name = "Launch", DueTimeUt = 5e6 - 3600 },
            };
            var entry = MissionHistory.Paused(_save.Path, "m1", state);
            Assert.Equal("Due", MissionListRules.Status(entry).Text);
            Assert.Equal("Due " + KerbinTime.ShortDate(5e6 - 3600) + ": waiting for the Duna window", MissionListRules.Due(entry));
        }
    }
}
