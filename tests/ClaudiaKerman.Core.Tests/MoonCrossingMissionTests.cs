// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #867 through the Mission seam, from 908b's Duna orbit with Kerbin's window 12 million s away: a plan that waits for its Due Time in an orbit
    /// that crosses Ike goes back once, and still crossing holds a Blocker that refuses the mod's warp to the Due Time and stops Auto Mode; one in a safe
    /// orbit doesn't. Between runs the view names the craft for the adapter's next-patch watch, and its drop raises a request.
    /// </summary>
    public sealed class MoonCrossingMissionTests : IDisposable
    {
        private const string Goal = "Return to Kerbin.";
        private const double Now = 168850200;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

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

        private static ScriptedResponse HomePlan()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: wait for Kerbin's window, then go home.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Kerbin's window is months away.\",\"sequences\":["
                + "{\"name\":\"Home\",\"intent\":\"Burn for Kerbin at the window.\",\"delta_v\":500,\"timing\":\"at the window\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":500,\"expected_duration\":\"about a year\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        /// <summary>A Sequence Plan with a Due Time of Claudia's own (-1 for none), whose script does nothing the checks read.</summary>
        private static ScriptedResponse Waiting(double due)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Home.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Home\",\"intent\":\"Burn for Kerbin at the window.\",\"why\":\"The window.\",\"target\":\"Kerbin encounter\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":500,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\","
                + "\"due_time_ut\":" + due.ToString(CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        /// <summary>The craft in a Duna orbit of <paramref name="apAlt"/> m apoapsis (908b's 2,144 km by default), at <see cref="Now"/>.</summary>
        private static GameSnapshot InDunaOrbit(double apAlt = 2144000)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            var duna = MoonCrossingTests.DunaOrbit(apAlt: apAlt, ut: Now);
            snapshot.Ut = Now;
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Duna";
            snapshot.Orbit = TransferRig.Elements(duna.Orbit, duna.Body);
            snapshot.Bodies = duna.Bodies;
            snapshot.Craft.VesselId = "908b";
            return snapshot;
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

        private void ToSequencePlan(MissionControl mission, GameSnapshot snapshot, string goal = Goal)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(Now, false));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(snapshot, goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private static string PlanCard(MissionControl mission)
        {
            return mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).Text;
        }

        [Fact]
        public void APlanThatWaitsInACrossingOrbitGoesBackOnceThenHoldsABlockerNamingTheMoonTheTimeAndTheFix()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit());

            // The one correction went back with the line and the fix.
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("The craft would wait for the Due Time in an orbit that crosses a moon: the orbit round Duna crosses Ike's sphere of influence (from 1,734 km up)", transport.Requests[2].Body);
            Assert.Contains("change_orbit set_ap 1,520 km (about 26 m/s", transport.Requests[2].Body);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The craft would wait in an orbit that crosses Ike's sphere of influence; Claudia is revising it"));

            // Still crossing after it: the plan holds a Blocker, on its card.
            var card = PlanCard(mission);
            Assert.Contains("Blocker: the orbit round Duna crosses Ike's sphere of influence (from 1,734 km up) about UT ", card);
            Assert.Contains("fly change_orbit set_ap 1,520 km (about 26 m/s; a circle costs about 260 m/s) before waiting.", card);

            // The mod's warp to the Due Time is refused.
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The mod won't warp to the Due Time"));

            // Auto Mode stops at it, as at any Blocker.
            mission.Send(new SetAutoMode(true));
            Assert.Contains("Auto Mode is waiting for you: a Blocker: the orbit round Duna crosses Ike's", mission.View.AutoModeStop);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
        }

        [Fact]
        public void APlanThatWaitsInASafeOrbitIsOfferedAsItIsAndTheModWarps()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(apAlt: 1520000));

            Assert.Equal(2, transport.Requests.Count);
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Single(mission.TakeCommands().OfType<WarpToUt>());
            mission.Send(new SetAutoMode(true));
            Assert.DoesNotContain("Blocker", mission.View.AutoModeStop);
        }

        [Fact]
        public void TheBlockerLiftsOnceThePlayerHasFlownTheFix()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit());
            Assert.Contains("Blocker:", PlanCard(mission));

            mission.Send(InDunaOrbit(apAlt: 1520000));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Single(mission.TakeCommands().OfType<WarpToUt>());
        }

        [Fact]
        public void BetweenRunsTheViewNamesTheCraftToWatchAndADropRaisesARequest()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(due));
            var mission = Control(transport);
            var snapshot = InDunaOrbit(apAlt: 1520000);
            ToSequencePlan(mission, snapshot);

            Assert.Equal(snapshot.Craft.VesselId, mission.View.NextPatchVesselId);
            Assert.NotEqual("", mission.View.NextPatchVesselId);
            Assert.Equal("", mission.View.NextPatchPlanned);

            mission.TakeCommands();
            const string line = "Warp stopped: the craft would meet Ike's sphere of influence at UT 169,104,601, in 58m.";
            mission.Send(new NextPatchDropped(line));
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Text == line);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == line);
        }

        /// <summary>A plan of Claudia's with no Due Time, given one by the mod's window wait after its figures were checked: the Blocker, with no correction.</summary>
        [Fact]
        public void APlanGivenItsDueTimeByTheModsWindowWaitIsJudgedAsOffered()
        {
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(-1));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit());

            Assert.Equal(2, transport.Requests.Count);
            Assert.True(mission.View.DueTime.HasDueTime);
            Assert.Contains("Blocker: the orbit round Duna crosses Ike's sphere of influence", PlanCard(mission));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.DoesNotContain(mission.TakeCommands(), c => c is WarpToUt);
        }

        /// <summary>While a script runs, its blocks' own waits are watched (transfer_to's through its Encounter Watch call): the between-runs watch is off.</summary>
        [Fact]
        public void WhileAScriptRunsTheBetweenRunsWatchIsOffAndADropIsIgnored()
        {
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(-1));
            var mission = Control(transport);
            // A goal at Duna itself: no window, so no Due Time, and the plan runs at once.
            ToSequencePlan(mission, InDunaOrbit(apAlt: 1520000), "Orbit Duna.");
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.NextPatchVesselId);

            mission.TakeCommands();
            mission.Send(new NextPatchDropped("Warp stopped: the craft would meet Ike's sphere of influence at UT 1."));
            Assert.DoesNotContain(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Text.Contains("Ike"));
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("Warp stopped"));
        }

        [Fact]
        public void APlanFindingReadsInTheCorrectionAndItsWarning()
        {
            var rig = MoonCrossingTests.DunaOrbit();
            var crossing = MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + MoonCrossingTests.Wait908b);
            var found = new PlanFindings { MoonCrossing = crossing };
            Assert.True(found.FiguresWrong);
            Assert.Contains("crosses Ike's sphere of influence", MissionPrompts.PlanCheckFailed(found));
            Assert.Contains("the orbit fixed before any wait (change_orbit set_ap)", MissionPrompts.PlanFixNotice(found));
            Assert.StartsWith("Blocker: the orbit round Duna crosses Ike's", Assert.Single(found.Warnings()));
        }
    }
}
