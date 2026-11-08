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
using ClaudiaKerman.Core.Kos;
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

        /// <summary>A two-leg Mission Plan at Duna: a transfer to Ike, then a capture that waits.</summary>
        private static ScriptedResponse IkePlan()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: Ike.\",\"reply_intent\":\"none\",\"sequence_plan\":null,"
                + "\"mission_plan\":{\"why\":\"Ike is near.\",\"sequences\":["
                + "{\"name\":\"Transfer\",\"intent\":\"Transfer to Ike.\",\"delta_v\":200,\"timing\":\"now\",\"due_time_ut\":-1},"
                + "{\"name\":\"Capture\",\"intent\":\"Capture at Ike.\",\"delta_v\":100,\"timing\":\"at Ike\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":300,\"expected_duration\":\"a day\",\"mission_time_s\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse TransferToIke()
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Transfer.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Transfer\",\"intent\":\"Transfer to Ike.\",\"why\":\"Ike.\",\"target\":\"Ike encounter\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":200,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"local res is transfer_to(lexicon(\\\"target\\\", \\\"Ike\\\")).\\n\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse NominalDebrief()
        {
            return Reply("{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Nominal\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Capture.\",\"eva_science\":\"\"}}");
        }

        /// <summary>
        /// Fix round (1): after transfer_to Ike, coasting with no script running, the meeting with Ike is the transfer's: the between-runs watch is told
        /// it is planned, and the next plan's wait for its Due Time on that coast holds no Blocker.
        /// </summary>
        [Fact]
        public void AfterTransferToTheMoonItsMeetingIsPlannedForTheWatchAndTheNextPlansWait()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(IkePlan(), TransferToIke(), NominalDebrief(), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Ike.");
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            // The run ejected: the coast now meets Ike on its first loop.
            mission.Send(OnTheCoastToIke(Now));
            mission.Send(new KosRunEnded(Ended(true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && transport.Requests.Count == 4);

            Assert.Contains("Ike", mission.View.NextPatchPlanned.Split(','));
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Single(mission.TakeCommands().OfType<WarpToUt>());
        }

        /// <summary>A Mission Plan at Duna: the capture, then <paramref name="next"/>.</summary>
        private static ScriptedResponse CapturePlan(string next, string first = "Capture at Duna.")
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan.\",\"reply_intent\":\"none\",\"sequence_plan\":null,"
                + "\"mission_plan\":{\"why\":\"Ike.\",\"sequences\":["
                + "{\"name\":\"Capture\",\"intent\":\"" + first + "\",\"delta_v\":300,\"timing\":\"now\",\"due_time_ut\":-1},"
                + "{\"name\":\"Next\",\"intent\":\"" + next + "\",\"delta_v\":200,\"timing\":\"after\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":500,\"expected_duration\":\"a day\",\"mission_time_s\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse CaptureAtDuna()
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Capture.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Capture\",\"intent\":\"Capture at Duna.\",\"why\":\"Duna.\",\"target\":\"Duna orbit\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":300,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"local res is capture().\\n\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        /// <summary>A Duna ellipse from 60 km through Ike's band, its periapsis <paramref name="argPe"/> degrees round, at <see cref="Now"/>.</summary>
        private static GameSnapshot KeptOrbit(double apAlt, double argPe)
        {
            var kept = InDunaOrbit();
            kept.Orbit = TransferRig.Elements(MoonCrossingTests.DunaOrbit(peAlt: 60000, apAlt: apAlt, argPe: argPe, ut: Now).Orbit, kept.Bodies.First(b => b.Name == "Duna"));
            return kept;
        }

        /// <summary>The first such ellipse with an Ike window within three loops, the wait to it meeting nothing (<see cref="KeptRoute.KeptWindow"/>).</summary>
        private static GameSnapshot TimedForIke()
        {
            foreach (var apAlt in new[] { 3193000.0, 4000000, 5000000, 6000000 })
                for (var argPe = 0; argPe < 360; argPe += 15)
                {
                    var kept = KeptOrbit(apAlt, argPe);
                    var at = kept.At(Now);
                    at.LegsAhead = new System.Collections.Generic.List<PlannedSequence>
                    {
                        new PlannedSequence { Name = "Capture", Intent = "Capture at Duna." }, new PlannedSequence { Name = "Next", Intent = "Transfer to Ike." },
                    };
                    if (KeptRoute.KeptWindow(at).HasValue) return kept;
                }
            throw new Exception("no ellipse through Ike's band has a window within three loops");
        }

        /// <summary>
        /// #866: a capture that kept its ellipse for the next leg's moon, timed to it, left no stray crossing: the Debrief judges the wait to the window only.
        /// The same ellipse with Kerbin next is a crossing, as #867 has it; so is one through Ike's band with no window in three loops, Ike next or not.
        /// </summary>
        [Theory]
        [InlineData("Transfer to Ike.", true, false)]
        [InlineData("Return to Kerbin.", true, true)]
        [InlineData("Transfer to Ike.", false, true)]
        public void TheDebriefJudgesAKeptEllipsesWaitToItsWindowOnly(string next, bool timed, bool crosses)
        {
            var transport = new StreamingFakeTransport(CapturePlan(next), CaptureAtDuna(), NominalDebrief(), Waiting(-1));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Ike.");
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            var kept = timed ? TimedForIke() : KeptOrbit(3193000, 180);
            mission.Send(kept);
            mission.Send(new KosRunEnded(Ended(true), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count >= 3);

            var debrief = transport.Requests[2].Body;
            Assert.Equal(crosses, debrief.Contains("orbit crosses a moon"));
        }

        private static RunOutcome Ended(bool success)
        {
            return new RunOutcome(RunOutcomeKind.Finished, new System.Collections.Generic.Dictionary<string, object> { { "success", success } }, "", "", false);
        }

        /// <summary>The craft at <paramref name="ut"/> on an orbit of 908b's shape, its periapsis turned so its path's first loop meets Ike: a transfer flown.</summary>
        private static GameSnapshot OnTheCoastToIke(double ut)
        {
            for (var argPe = 0; argPe < 360; argPe += 5)
            {
                var rig = MoonCrossingTests.DunaOrbit(argPe: argPe, ut: ut);
                if (MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, ut, ut + rig.Orbit.Period) == null) continue;
                var snapshot = InDunaOrbit();
                snapshot.Orbit = TransferRig.Elements(rig.Orbit, rig.Body);
                return snapshot;
            }
            throw new InvalidOperationException("no orbit of 908b's shape meets Ike on its first loop");
        }

        /// <summary>A Sequence Plan with a hand-written script (no transfer_to) whose name and intent are <paramref name="words"/>.</summary>
        private static ScriptedResponse CoastPlan(string words, int step = 1)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":" + step + ",\"name\":\"" + words + "\",\"intent\":\"" + words + "\",\"why\":\"x.\",\"target\":\"orbit\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":0,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":true,\"custom_script_reason\":\"a coast\",\"script\":\"wait 10.\\n\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private string DebriefOnTheCoastToIke(ScriptedResponse plan, ScriptedResponse sequence)
        {
            var transport = new StreamingFakeTransport(plan, sequence, NominalDebrief(), Waiting(-1));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Ike.");
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(OnTheCoastToIke(Now));
            mission.Send(new KosRunEnded(Ended(true), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count >= 3);
            return transport.Requests[2].Body;
        }

        /// <summary>#871: a hand-written coast to a moon its Sequence Plan names is planned: no fact.</summary>
        [Fact]
        public void ACoastWhoseSequencePlanNamesTheMoonIsPlanned()
        {
            var debrief = DebriefOnTheCoastToIke(CapturePlan("Return to Kerbin."), CoastPlan("Coast to Ike"));
            Assert.DoesNotContain("orbit crosses a moon", debrief);
            Assert.DoesNotContain("On course to meet", debrief);
        }

        /// <summary>#871: a later Mission Plan leg that names the moon (a capture, no transfer_to) plans the encounter: no fact.</summary>
        [Fact]
        public void ALaterLegThatNamesTheMoonPlansItsEncounter()
        {
            var debrief = DebriefOnTheCoastToIke(CapturePlan("Capture at Ike."), CaptureAtDuna());
            Assert.DoesNotContain("orbit crosses a moon", debrief);
            Assert.DoesNotContain("On course to meet", debrief);
        }

        /// <summary>Fix round: a moon a leg or the plan departs or returns from is where the craft was, not where it goes: the on-course fact stays.</summary>
        [Theory]
        [InlineData("Return from Ike to Duna")]
        [InlineData("Depart Ike for Duna orbit")]
        [InlineData("Ascend from Ike")]
        public void AMoonNamedAsTheOriginIsNotPlanned(string words)
        {
            Assert.Contains("On course to meet Ike at UT ", DebriefOnTheCoastToIke(CapturePlan("Return to Kerbin."), CoastPlan(words)));
            Assert.Contains("On course to meet Ike at UT ", DebriefOnTheCoastToIke(CapturePlan(words), CaptureAtDuna()));
        }

        /// <summary>
        /// #890: the Debrief, the Next-patch Watch and the next plan's wait judge a moon planned by one rule. A hand-written coast to Ike named in the Sequence
        /// Plan triggers none of the three; a moon named only as the origin, or by nothing, triggers all three.
        /// </summary>
        [Theory]
        [InlineData("Coast to Ike", "Return to Kerbin.", true)]
        [InlineData("Return from Ike to Duna", "Return to Kerbin.", false)]
        [InlineData("Coast", "Return to Kerbin.", false)]
        public void TheDebriefTheWatchAndTheBlockerShareThePlannedMoonRule(string words, string next, bool planned)
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(CapturePlan(next), CoastPlan(words), NominalDebrief(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Ike.");
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(OnTheCoastToIke(Now));
            mission.Send(new KosRunEnded(Ended(true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && transport.Requests.Count >= 4);

            Assert.Equal(!planned, transport.Requests[2].Body.Contains("On course to meet Ike at UT "));
            Assert.Equal(planned, mission.View.NextPatchPlanned.Split(',').Contains("Ike"));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Equal(planned, mission.TakeCommands().OfType<WarpToUt>().Any());
            Assert.Equal(!planned, PlanCard(mission).Contains("Blocker"));
        }

        /// <summary>
        /// #894: a moon named only to be avoided is not planned: the Debrief keeps the on-course fact, the watch does not plan it and the Blocker fires,
        /// whether the words are the Sequence Plan's or a later Mission Plan leg's. A moon that is a destination in another word is still planned.
        /// </summary>
        [Theory]
        [InlineData("Raise Ap clear of Ike", false)]
        [InlineData("Raise Ap clear of Ike's SOI", false)]
        [InlineData("Avoid Ike", false)]
        [InlineData("Coast, avoiding Ike", false)]
        [InlineData("Burn away from Ike", false)]
        [InlineData("Stay below Ike's SOI", false)]
        [InlineData("Stay under Ike", false)]
        [InlineData("Stay outside Ike's orbit", false)]
        [InlineData("Coast to miss Ike", false)]
        [InlineData("Coast, missing Ike", false)]
        [InlineData("Transfer to Ike, avoiding Dres", true)]
        public void AMoonNamedOnlyToBeAvoidedIsNotPlanned(string words, bool planned)
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(CapturePlan("Return to Kerbin."), CoastPlan(words), NominalDebrief(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Ike.");
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(OnTheCoastToIke(Now));
            mission.Send(new KosRunEnded(Ended(true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && transport.Requests.Count >= 4);

            Assert.Equal(!planned, transport.Requests[2].Body.Contains("On course to meet Ike at UT "));
            Assert.Equal(planned, mission.View.NextPatchPlanned.Split(',').Contains("Ike"));
            Assert.DoesNotContain("Dres", mission.View.NextPatchPlanned.Split(','));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Equal(planned, mission.TakeCommands().OfType<WarpToUt>().Any());
            Assert.Equal(!planned, PlanCard(mission).Contains("Blocker"));
        }

        /// <summary>#894: a moon that is a destination in one leg and avoided in another is planned by the leg that goes to it.</summary>
        [Fact]
        public void AMoonAvoidedInOneLegAndGoneToInAnotherIsPlanned()
        {
            var debrief = DebriefOnTheCoastToIke(CapturePlan("Capture at Ike."), CoastPlan("Coast, avoiding Ike"));
            Assert.DoesNotContain("On course to meet", debrief);
        }

        /// <summary>The craft at <see cref="Now"/> on an orbit of 908b's shape whose path meets Ike on a later loop (4th or so) but not on the first.</summary>
        private static GameSnapshot OnALateEncounterWithIke(out double period)
        {
            for (var argPe = 0; argPe < 360; argPe += 1)
            {
                var rig = MoonCrossingTests.DunaOrbit(argPe: argPe, ut: Now);
                var p = rig.Orbit.Period;
                if (MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, Now, Now + p) != null) continue;
                if (MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, Now + 3 * p, Now + 4 * p) == null) continue;
                period = p;
                var snapshot = InDunaOrbit();
                snapshot.Orbit = TransferRig.Elements(rig.Orbit, rig.Body);
                return snapshot;
            }
            throw new InvalidOperationException("no orbit of 908b's shape meets Ike only on its 4th loop");
        }

        /// <summary>
        /// Fix round (B1): an accidental encounter several loops out is judged once, at the Debrief; as it nears (a first-loop encounter) it does not
        /// become planned: the Blocker and the watch's drop stay, as on main, until the encounter.
        /// </summary>
        [Fact]
        public void AnAccidentalEncounterLoopsOutDoesNotBecomePlannedAsTimePasses()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(CapturePlan("Capture at Ike."), CoastPlan("Coast"), NominalDebrief(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Duna.");
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            var snapshot = OnALateEncounterWithIke(out var period);
            mission.Send(snapshot);
            mission.Send(new KosRunEnded(Ended(true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && transport.Requests.Count >= 4);

            for (var loop = 0; loop <= 3; loop++)
            {
                var ut = Now + loop * period;
                mission.Send(new UtAdvanced(ut, false));
                var later = OnALateEncounterWithIke(out _);
                later.Ut = ut;
                mission.Send(later);
                Assert.DoesNotContain("Ike", mission.View.NextPatchPlanned.Split(','));
                mission.TakeCommands();
                mission.Send(new WarpToDueTime());
                Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
                Assert.Contains("Blocker", PlanCard(mission));
            }
        }

        /// <summary>Fix round: a leg already begun (plan_step 2) that named the moon no longer plans it.</summary>
        [Fact]
        public void APastLegThatNamedTheMoonDoesNotPlanItsEncounter()
        {
            var debrief = DebriefOnTheCoastToIke(CapturePlan("Return to Kerbin.", first: "Capture at Ike."), CoastPlan("Coast", step: 2));
            Assert.Contains("On course to meet Ike at UT ", debrief);
        }

        /// <summary>Fix round: the run's words are saved, so after a restart a moon the run named still reads as planned.</summary>
        [Fact]
        public void TheRunsWordsSurviveASaveAndLoad()
        {
            var state = new MissionSaveState { Id = "m1", RunWords = "Coast to Ike Coast to Ike" };
            Assert.Equal("Coast to Ike Coast to Ike", MissionSaveState.FromValues(state.ToValues()).RunWords);
            Assert.Equal("", MissionSaveState.FromValues(new MissionSaveState { Id = "m1" }.ToValues()).RunWords);
        }

        /// <summary>#871: an encounter path nothing names says so, and offers the fix only if it isn't intended.</summary>
        [Fact]
        public void AnUnnamedEncounterPathIsOnCourseAndTheFixOnlyIfNotIntended()
        {
            var debrief = DebriefOnTheCoastToIke(CapturePlan("Return to Kerbin."), CaptureAtDuna());
            Assert.Contains("On course to meet Ike at UT ", debrief);
            Assert.Contains("if this encounter isn't intended, fly change_orbit set_ap", debrief);
            Assert.DoesNotContain("before waiting", debrief);
        }

        /// <summary>
        /// Review of the fix round: a transfer_to Ike that never ejected (refused, failed) planned nothing, so the parking orbit still crossing Ike
        /// is a crossing between runs and for the next plan's wait.
        /// </summary>
        [Fact]
        public void ATransferToTheMoonThatNeverEjectedLeavesItsMeetingUnplanned()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(IkePlan(), TransferToIke(), NominalDebrief(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit(), "Orbit Ike.");
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(InDunaOrbit());
            mission.Send(new KosRunEnded(Ended(false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && transport.Requests.Count >= 4);

            Assert.DoesNotContain("Ike", mission.View.NextPatchPlanned.Split(','));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
        }

        /// <summary>Fix round (2): leaving Flight takes away the craft's snapshot, not the Blocker: the mod's warp from the Tracking Station is still refused, and Auto Mode still stops.</summary>
        [Fact]
        public void TheBlockerStandsWithNoSnapshotOfTheCraft()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(due), Waiting(due));
            var mission = Control(transport);
            ToSequencePlan(mission, InDunaOrbit());
            Assert.Contains("Blocker:", PlanCard(mission));

            mission.Send(new SceneChanged(GameScene.Other));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
            mission.Send(new SetAutoMode(true));
            Assert.Contains("Auto Mode is waiting for you: a Blocker: the orbit round Duna crosses Ike's", mission.View.AutoModeStop);

            // Back in Flight with the fix flown, it lifts.
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(InDunaOrbit(apAlt: 1520000));
            mission.Send(new SetAutoMode(false));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Single(mission.TakeCommands().OfType<WarpToUt>());
        }

        /// <summary>Fix round (2): a plan offered with no snapshot of the craft (outside Flight) is judged on the first one: its Blocker on the card and in the log, and the warp refused.</summary>
        [Fact]
        public void APlanOfferedOutsideFlightIsJudgedOnTheFirstSnapshot()
        {
            var due = Now + MoonCrossingTests.Wait908b;
            var transport = new StreamingFakeTransport(HomePlan(), Waiting(due));
            var mission = Control(transport);
            var snapshot = InDunaOrbit();
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(Now, false));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(snapshot, Goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new SceneChanged(GameScene.Other));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(2, transport.Requests.Count);
            Assert.DoesNotContain("Blocker", PlanCard(mission));

            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(InDunaOrbit());
            Assert.Contains("Blocker: the orbit round Duna crosses Ike's sphere of influence", PlanCard(mission));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("crosses Ike's sphere of influence"));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
        }

        /// <summary>#877: the plan offered outside Flight, the craft known only by its saved orbit (what the adapter reads from the save).</summary>
        private MissionControl OfferedOutsideFlight(GameSnapshot saved, bool readFirst = true, double due = double.NaN, bool autoMode = false)
        {
            if (double.IsNaN(due)) due = Now + MoonCrossingTests.Wait908b;
            // Read first, the plan's figures see the crossing: #867's one automatic correction asks again (the third reply).
            var mission = Control(new StreamingFakeTransport(HomePlan(), Waiting(due), Waiting(due)));
            var snapshot = InDunaOrbit();
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(Now, false));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(snapshot, Goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new SceneChanged(GameScene.Other));
            if (readFirst) mission.Send(new SavedOrbitRead(saved));
            if (autoMode) mission.Send(new SetAutoMode(true));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            if (!readFirst) mission.TakeCommands();
            return mission;
        }

        /// <summary>#877: with only the saved orbit (908b's 100 x 2,144 km Duna orbit) the plan gets #867's Blocker and Warp to Due Time is held.</summary>
        [Fact]
        public void APlanOfferedWithOnlyTheSavedOrbitGetsTheBlockerAndTheWarpIsHeld()
        {
            var mission = OfferedOutsideFlight(InDunaOrbit());
            Assert.Contains("Blocker: the orbit round Duna crosses Ike's sphere of influence", PlanCard(mission));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
        }

        /// <summary>#877: the same with Auto Mode on: it stops.</summary>
        [Fact]
        public void AutoModeStopsOnTheBlockerJudgedFromTheSavedOrbit()
        {
            var mission = OfferedOutsideFlight(InDunaOrbit());
            mission.TakeCommands();
            mission.Send(new SetAutoMode(true));
            Assert.Contains("Auto Mode is waiting for you: a Blocker: the orbit round Duna crosses Ike's", mission.View.AutoModeStop);
        }

        /// <summary>#877, the adapter's order: the plan is offered first, the first read of the saved orbit comes after. It judges the plan: Notice, card, warp held.</summary>
        [Fact]
        public void TheSavedOrbitReadAfterTheOfferJudgesThePlanAndRewritesItsCard()
        {
            var mission = OfferedOutsideFlight(null, readFirst: false);
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("won't warp"));

            mission.Send(new SavedOrbitRead(InDunaOrbit()));
            Assert.Contains("Blocker: the orbit round Duna crosses Ike's sphere of influence", PlanCard(mission));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("crosses Ike's sphere of influence"));
            mission.Send(new WarpToDueTime());
            Assert.Empty(mission.TakeCommands().OfType<WarpToUt>());
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The mod won't warp to the Due Time"));
        }

        /// <summary>#877: Auto Mode on when the plan is offered doesn't approve it before the first read; the read then stops it on the Blocker.</summary>
        [Fact]
        public void AutoModeWaitsForTheFirstSavedOrbitReadThenStopsOnTheBlocker()
        {
            var mission = OfferedOutsideFlight(null, readFirst: false, autoMode: true);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Empty(mission.View.AutoModeStop);
            mission.Send(new SavedOrbitRead(InDunaOrbit()));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains("Auto Mode is waiting for you: a Blocker: the orbit round Duna crosses Ike's", mission.View.AutoModeStop);
        }

        /// <summary>#877: the read finds the craft isn't in the save: nothing to cross, the warp goes ahead.</summary>
        [Fact]
        public void ACraftNotInTheSaveIsJudgedByTheReadAndGetsNoBlocker()
        {
            var mission = OfferedOutsideFlight(null, readFirst: false);
            mission.Send(new SavedOrbitRead(null));
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            mission.Send(new WarpToDueTime());
            Assert.Single(mission.TakeCommands().OfType<WarpToUt>());
        }

        /// <summary>#877: a saved read of another vessel judges nothing.</summary>
        [Fact]
        public void ASavedReadOfAnotherVesselIsNotUsed()
        {
            var other = InDunaOrbit();
            other.Craft.VesselId = "ffff";
            var mission = OfferedOutsideFlight(other);
            Assert.DoesNotContain("Blocker", PlanCard(mission));
        }

        /// <summary>#877: a part-less read (as the adapter sends) for a plan with no Due Time of Claudia's own judges through the first burn point without throwing.</summary>
        [Fact]
        public void APartlessSavedReadJudgesAPlanWithNoDueTime()
        {
            var saved = InDunaOrbit();
            saved.Craft.Parts.Clear();
            saved.Stages.Clear();
            var mission = OfferedOutsideFlight(null, readFirst: false, due: -1);
            mission.Send(new SavedOrbitRead(saved));
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
        }

        /// <summary>#877: a landed craft outside Flight has no orbit to cross from: no Blocker, the warp goes ahead.</summary>
        [Fact]
        public void ALandedCraftOutsideFlightGetsNoBlocker()
        {
            var landed = InDunaOrbit();
            landed.Situation = "LANDED";
            landed.Orbit = null;
            var mission = OfferedOutsideFlight(landed);
            Assert.DoesNotContain("Blocker", PlanCard(mission));
            mission.TakeCommands();
            mission.Send(new WarpToDueTime());
            Assert.Single(mission.TakeCommands().OfType<WarpToUt>());
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
