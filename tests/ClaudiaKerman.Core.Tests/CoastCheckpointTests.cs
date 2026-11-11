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
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Coast Checkpoints (#775): during a long coast the player can leave the craft, a KSP alarm calls them back before the next
    /// correction, and the mod checks the trajectory on their return. The flight is <see cref="TransferFlight"/> on
    /// <see cref="TransferRig"/>; the rest is driven through the Mission seam with hand-written replies.
    /// </summary>
    public sealed class CoastCheckpointTests : IDisposable
    {
        private const string Goal = "Orbit Duna.";
        private const double Day = 21600;
        private const string Script = "local res is transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 60000)).\naddons:claudia:report(res).\n";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The flight ----

        private static TransferFlightAsk Ask(string target, double pe, bool checkpoints = true)
        {
            return new TransferFlightAsk { Target = target, TargetPe = pe, StartUt = 0, TurnTime = 60, DvLeft = 5000, Auto = true, Checkpoints = checkpoints };
        }

        private static string TokenOf(TransferFlightStep step)
        {
            object token;
            return step.Report.TryGetValue(CoastCheckpoints.TokenKey, out token) ? (string)token : null;
        }

        /// <summary>Flies the rig's steps until one ends the block, and gives that step; the waits and burns on the way are kept.</summary>
        private static TransferFlightStep FlyToEnd(TransferRig rig, TransferFlightAsk ask, List<TransferFlightStep> flown = null)
        {
            for (var i = 0; i < 40; i++)
            {
                var step = TransferFlight.Next(rig.Snapshot(), ask);
                if (step.Error != "" || step.Do == TransferFlightStep.Done) return step;
                if (flown != null) flown.Add(step);
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else rig.MoveTo(step.Ut);
                TransferFlight.Carry(ask, step);
            }
            throw new InvalidOperationException("the flight did not end in 40 steps");
        }

        /// <summary>A Kerbin to Duna flight flown to its first checkpoint: the rig on the coast, and the token.</summary>
        private static TransferRig ToFirstCheckpoint(out CoastToken token)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            var step = FlyToEnd(rig, Ask("Duna", 60000));
            Assert.True(step.Error == "", step.Error);
            token = CoastToken.Parse(TokenOf(step));
            Assert.NotNull(token);
            return rig;
        }

        [Fact]
        public void A_Duna_coast_checkpoints_before_each_correction_point_more_than_six_hours_away()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            // Every burn left short, so the corrections after the first have something to fly.
            rig.Residual = 0.3;
            var ask = Ask("Duna", 60000);
            var points = new List<CoastToken>();
            TransferFlightStep end;
            while (true)
            {
                end = FlyToEnd(rig, ask);
                Assert.True(end.Error == "", end.Error);
                var text = TokenOf(end);
                if (text == null) break;
                var token = CoastToken.Parse(text);
                Assert.True(token.PointUt - rig.Ut > WindowWaits.LongWaitS, "a checkpoint only before a point over 6 h off");
                points.Add(token);
                Assert.True(points.Count < 6, "resuming must not checkpoint the same point again");
                // exec_node's own residual from here: the trim near the target is held to pe_tolerance.
                rig.Residual = 0.05;
                // The player comes back at the Due Time, and the same step flies on from the token.
                rig.MoveTo(token.DueUt);
                ask = Ask("Duna", 60000);
                ask.Resume = token;
                ask.StartUt = rig.Ut;
            }
            // The mid-course correction, then the one at the target's edge and the finer one inside it; a post-escape correction comes with an SOI change first, and soon.
            Assert.Equal("mid", points[0].Phase);
            Assert.Contains(points, p => p.Phase == "near");
            Assert.Single(rig.Burns, b => b.Key == "ejection");
            Assert.Equal("Duna", rig.Body.Name);
            Assert.InRange(Convert.ToDouble(end.Report["target_pe_actual"]), 59000, 61000);
        }

        [Fact]
        public void A_Mun_coast_under_six_hours_has_no_checkpoint()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.Residual = 0.05;
            var end = FlyToEnd(rig, Ask("Mun", 30000));
            Assert.True(end.Error == "", end.Error);
            Assert.Null(TokenOf(end));
        }

        [Fact]
        public void A_flight_outside_a_Missions_run_never_checkpoints()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            var flown = new List<TransferFlightStep>();
            var end = FlyToEnd(rig, Ask("Duna", 60000, false), flown);
            Assert.Null(TokenOf(end));
            Assert.Contains(flown, s => s.Do == TransferFlightStep.Wait && s.Phase == "near");
            Assert.All(flown.Where(s => s.Watch != null), s => Assert.Equal("", s.Watch.Checkpoint));
        }

        // ---- The safety test ----

        [Fact]
        public void A_periapsis_in_the_atmosphere_prevents_the_checkpoint_and_the_block_keeps_flying()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            var flown = new List<TransferFlightStep>();
            // Duna's atmosphere tops out at 50 km.
            var end = FlyToEnd(rig, Ask("Duna", 10000), flown);
            Assert.True(end.Error == "", end.Error);
            Assert.Null(TokenOf(end));
            Assert.Contains(flown, s => s.Do == TransferFlightStep.Wait && s.Phase == "near");
        }

        [Fact]
        public void No_encounter_prevents_the_checkpoint()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            rig.Fly("by_hand", rig.Ut, -300, 0, 0);
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(rig.Snapshot(), "Duna", out error);
            Assert.Null(TransferFlight.Encounter(setup));
            Assert.Contains("no encounter", CoastCheckpoints.Unsafe(setup, null, token.PointUt));
        }

        [Fact]
        public void An_SOI_change_before_the_next_point_prevents_the_checkpoint_but_the_targets_own_does_not()
        {
            // Just after the ejection: Kerbin's edge comes first.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            var ask = Ask("Duna", 60000);
            var eject = TransferFlight.Next(rig.Snapshot(), ask);
            rig.Fly(eject.Kind, eject.Ut, eject.Prograde, eject.Normal, eject.Radial);
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(rig.Snapshot(), "Duna", out error);
            Assert.Contains("sphere of influence", CoastCheckpoints.Unsafe(setup, new TransferFlight.Arrival { Pe = 60000 }, rig.Ut + 10 * Day));

            // On the Sun's patch, the point just inside Duna's sphere of influence is the finer correction's.
            CoastToken token;
            var coast = ToFirstCheckpoint(out token);
            setup = PredictBurn.Setup.Prepare(coast.Snapshot(), "Duna", out error);
            var enc = TransferFlight.Encounter(setup);
            Assert.Equal("", CoastCheckpoints.Unsafe(setup, enc, enc.SoiUt + 30));
        }

        [Fact]
        public void The_due_time_is_the_correction_point_less_30_minutes_never_less_than_the_turn_time_and_the_burn_points_lead()
        {
            CoastToken token;
            ToFirstCheckpoint(out token);
            Assert.Equal(1800, token.LeadS);
            Assert.Equal(token.PointUt - 1800, token.DueUt);
            // A craft slow to turn: its turn time plus the margin is the floor.
            Assert.Equal(1800, CoastCheckpoints.Lead(60));
            Assert.Equal(1800, CoastCheckpoints.Lead(1740));
            Assert.Equal(3000 + BurnPoints.TurnMarginSeconds, CoastCheckpoints.Lead(3000));
        }

        /// <summary>A craft ejected from Duna to a 35 km Kerbin periapsis: on the Sun's patch with the encounter ahead.</summary>
        private static TransferRig HomeFromDuna(out TransferFlight.Arrival enc, out PredictBurn.Setup setup)
        {
            var rig = TransferRig.Circular("Duna", 100000, 0);
            rig.BurnError = 0.005;
            var ask = Ask("Kerbin", 35000);
            ToolOutcome error;
            // The ejection, then the mid-course correction that puts the periapsis where it was asked.
            for (var i = 0; i < 8; i++)
            {
                var s = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.Equal("", s.Error);
                if (s.MaxDuration.HasValue) ask.MaxDuration = s.MaxDuration;
                if (s.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(s.Kind, s.Ut, s.Prograde, s.Normal, s.Radial);
                else rig.MoveTo(s.Ut);
                TransferFlight.Carry(ask, s);
                if (rig.Body.Name != "Sun" || i == 0 && s.Kind == "ejection") continue;
                setup = PredictBurn.Setup.Prepare(rig.Snapshot(), "Kerbin", out error);
                enc = TransferFlight.Encounter(setup);
                if (enc != null && enc.Pe >= 0 && enc.Pe <= 60000) return rig;
            }
            throw new InvalidOperationException("the flight never had a Kerbin periapsis in the air");
        }

        [Fact]
        public void A_return_from_Duna_with_a_Kerbin_periapsis_in_the_air_checkpoints_before_a_correction_ahead_of_Kerbins_SOI_and_not_after_it()
        {
            TransferFlight.Arrival enc;
            PredictBurn.Setup setup;
            var rig = HomeFromDuna(out enc, out setup);
            Assert.True(ArrivalRule.EndsInReentry(setup.Snapshot, "Kerbin"));
            Assert.True(enc.Pe <= setup.Target.AtmosphereHeight);
            // The correction before the entry: the safety test passes, as for any other encounter.
            Assert.Equal("", CoastCheckpoints.Unsafe(setup, enc, enc.SoiUt - 3 * Day));
            // The correction at or after the entry: it never does.
            Assert.Contains("atmosphere", CoastCheckpoints.Unsafe(setup, enc, enc.SoiUt + 30));
            Assert.Contains("atmosphere", CoastCheckpoints.Unsafe(setup, enc, enc.SoiUt));
            // The ground is never safe, reentry or not.
            Assert.Contains("surface", CoastCheckpoints.Unsafe(setup, new TransferFlight.Arrival { SoiUt = enc.SoiUt, Pe = -1000 }, enc.SoiUt - 3 * Day));
            // A body with air that is not home keeps the old rule.
            var away = TransferRig.Circular("Kerbin", 100000, 0);
            ToolOutcome error;
            var toDuna = PredictBurn.Setup.Prepare(away.Snapshot(), "Duna", out error);
            Assert.Contains("atmosphere", CoastCheckpoints.Unsafe(toDuna, new TransferFlight.Arrival { SoiUt = 1e9, Pe = 10000 }, 1e9 - 3 * Day));

            // return's own step: a checkpoint ahead of the entry, a correction after it keeps flying.
            var snapshot = rig.Snapshot();
            var mid = rig.Ut + 3 * Day;
            Assert.True(mid < enc.SoiUt - Day, "the coast is long enough");
            var stop = BlockCalls.TransferStep(ReturnAsk(mid, "on"), snapshot);
            Assert.Equal("done", stop["do"]);
            var token = CoastToken.Parse((string)((IDictionary<string, object>)stop["report"])[CoastCheckpoints.TokenKey]);
            Assert.Equal(mid - 1800, token.DueUt);
            Assert.Equal("go", BlockCalls.TransferStep(ReturnAsk(enc.SoiUt + 100, "on"), snapshot)["do"]);
            // Resumed, the wait to the correction doesn't depend on the lead: it still stops 360 s short of the burn.
            var resumed = BlockCalls.TransferStep(ReturnAsk(mid, token.ToString()), snapshot);
            Assert.Equal("wait", resumed["do"]);
            Assert.Equal(mid - 360, (double)resumed["wait_ut"]);
            Assert.True((double)resumed["wait_ut"] > token.DueUt);
            // The check on return keeps the air periapsis before the entry; a far switch can leave.
            Assert.Equal(CoastVerdictKind.Kept, CoastCheckpoints.Judge(snapshot, token, token.DueUt).Kind);
            Assert.Equal("", CoastCheckpoints.CannotLeave(snapshot, token, rig.Ut));
        }

        // ---- The Mission seam: helpers ----

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
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: the transfer.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Duna.\",\"sequences\":["
                + "{\"name\":\"Duna transfer\",\"intent\":\"Fly to Duna.\",\"delta_v\":1600,\"timing\":\"now\",\"due_time_ut\":-1},"
                + "{\"name\":\"Duna capture\",\"intent\":\"Capture at Duna.\",\"delta_v\":800,\"timing\":\"after\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":2400,\"expected_duration\":\"months\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse SequenceReply(string name, double due = -1, string script = Script, string checks = "")
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":" + JsonSerializer.Serialize(name) + ",\"plan_step\":1,\"intent\":\"Fly to Duna.\",\"why\":\"Next.\",\"target\":\"Duna\","
                + "\"tolerances\":\"Pe within 1 km\",\"expected_delta_v\":1600,\"expected_duration_s\":40000000,\"abort_conditions\":[],\"risks\":[]," + checks
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + JsonSerializer.Serialize(script) + ","
                + "\"due_time_ut\":" + due.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
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

        /// <summary>The Mission's craft, flying as the rig does: the fixture's parts and crew, with the rig's orbit, UT and bodies.</summary>
        private static GameSnapshot Craft(TransferRig rig)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            var flying = rig.Snapshot();
            snapshot.Craft.VesselId = "craft";
            snapshot.Ut = flying.Ut;
            snapshot.Situation = flying.Situation;
            snapshot.Body = flying.Body;
            snapshot.Orbit = flying.Orbit;
            snapshot.Bodies = flying.Bodies;
            return snapshot;
        }

        private static GameSnapshot Elsewhere(TransferRig rig)
        {
            var snapshot = Craft(rig);
            snapshot.Craft.VesselId = "relay";
            snapshot.Craft.Name = "Relay Station";
            snapshot.Crew.Clear();
            snapshot.Crew.Add(new SnapshotCrewMember { Name = "Jebediah Kerman", Trait = "Pilot" });
            snapshot.Craft.Parts.RemoveAll(p => p.HasKosProcessor);
            return snapshot;
        }

        /// <summary>
        /// Through to the transfer running: approved in Kerbin orbit an hour before its ejection, as it would be, then the craft on the rig's coast
        /// when the block has flown there. Gives its start command.
        /// </summary>
        private StartScript ToRunning(MissionControl mission, TransferRig rig)
        {
            var parked = TransferRig.Circular("Kerbin", 100000, 0);
            var eject = TransferFlight.Next(parked.Snapshot(), Ask("Duna", 60000));
            parked.MoveTo(eject.Ut - 3600);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(parked.Ut));
            mission.Send(Craft(parked));
            CraftCheckRouter.StartChecked(mission, _background, Craft(parked), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(Craft(parked), Goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
            mission.Send(new Approve());
            var started = mission.TakeCommands().OfType<StartScript>().ToList();
            Assert.True(started.Count == 1, "no start: " + mission.View.Stage + ": " + string.Join(" | ", mission.View.Log.Skip(Math.Max(0, mission.View.Log.Count - 4)).Select(e => e.Text)));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            return started[0];
        }

        private static KosRunEnded AtCheckpoint(CoastToken token, int runId)
        {
            var report = new Dictionary<string, object>
            {
                { "success", true }, { CoastCheckpoints.TokenKey, token.ToString() }, { CoastCheckpoints.PointKey, Math.Round(token.PointUt) }, { "ejection_dv_used", 1030.0 },
            };
            return new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, report, "", "", false), runId);
        }

        private MissionControl RunToCheckpoint(StreamingFakeTransport transport, TransferRig rig, CoastToken token, bool autoMode = false)
        {
            var mission = Control(transport);
            var start = ToRunning(mission, rig);
            if (autoMode) mission.Send(new SetAutoMode(true));
            mission.Send(AtCheckpoint(token, start.RunId));
            _background.Drain();
            return mission;
        }

        private static string Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").GetRawText();
        }

        /// <summary>The player leaves for a far vessel with the run at the checkpoint, and is seen there.</summary>
        private static void Leave(MissionControl mission, TransferRig rig)
        {
            mission.Send(Elsewhere(rig));
        }

        // ---- A long coast that can't checkpoint says why (#778) ----

        private const string Duna = "transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 60000)).\n";
        private const string Mun = "transfer_to(lexicon(\"target\", \"Mun\", \"target_pe\", 60000)).\n";

        [Fact]
        public void The_hasnode_guard_and_a_local_options_lexicon_checkpoint()
        {
            var guarded = "if hasnode { remove nextnode. }\n" + Script;
            Assert.Contains("\"checkpoint\", \"on\"", CoastCheckpoints.Armed(guarded));
            Assert.StartsWith("if hasnode { remove nextnode. }", CoastCheckpoints.Armed(guarded));
            var local = "local opts is lexicon(\"target\", \"Duna\", \"target_pe\", 60000).\nlocal res is transfer_to(opts).\n";
            var armed = CoastCheckpoints.Armed(local);
            Assert.Contains("local opts is lexicon(\"checkpoint\", \"on\", \"target\"", armed);
            Assert.Contains("transfer_to(opts)", armed);
            // Both together; and a remove or a wait that isn't the guard still stops it.
            Assert.Contains("\"checkpoint\", \"on\"", CoastCheckpoints.Armed("if hasnode { remove nextnode. }\n" + local));
            var other = "if hasnode { remove nextnode. }\nremove nextnode.\n" + Script;
            Assert.Equal(other, CoastCheckpoints.Armed(other));
        }

        [Theory]
        [InlineData("circularize(lexicon(\"at\", \"ap\")).\n", "runs circularize before transfer_to")]
        [InlineData("tune_pe(lexicon(\"value\", 70000)).\n", "runs tune_pe before transfer_to")]
        [InlineData("doaction(\"panels\").\n", "uses doaction before transfer_to")]
        [InlineData("local o is makeopts().\n", "passes transfer_to its options in a way the mod can't read")]
        public void A_long_coast_that_cant_checkpoint_names_what_blocks_it(string before, string clause)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var script = before + (clause.StartsWith("passes") ? "transfer_to(o).\n" : Duna);
            Assert.Equal(clause, CoastCheckpoints.LongCoastBlocker(script, rig.Snapshot()));
            Assert.Equal("This script " + clause + ", so its coast can't stop at a Coast Checkpoint.", CoastCheckpoints.BlockerLine(clause));
        }

        [Fact]
        public void A_short_coast_or_one_that_can_resume_gets_no_line()
        {
            var snapshot = TransferRig.Circular("Kerbin", 100000, 0).Snapshot();
            Assert.Equal("", CoastCheckpoints.LongCoastBlocker("circularize(lexicon(\"at\", \"ap\")).\n" + Mun, snapshot));
            Assert.Equal("", CoastCheckpoints.LongCoastBlocker(Script, snapshot));
            Assert.Equal("", CoastCheckpoints.LongCoastBlocker("if hasnode { remove nextnode. }\n" + Script, snapshot));
            Assert.Equal("", CoastCheckpoints.LongCoastBlocker("circularize(lexicon(\"at\", \"ap\")).\n", snapshot));
            Assert.Equal("", CoastCheckpoints.LongCoastBlocker("circularize(lexicon(\"at\", \"ap\")).\n" + Duna, null));
        }

        [Fact]
        public void The_plan_card_the_log_and_a_far_switch_say_why_a_long_coast_cant_checkpoint()
        {
            var script = "circularize(lexicon(\"at\", \"ap\")).\n" + Script;
            var line = "This script runs circularize before transfer_to, so its coast can't stop at a Coast Checkpoint.";
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer", script: script)));
            ToRunning(mission, rig);
            Assert.EndsWith(line, mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).Text);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == line);
            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Equal("this script runs circularize before transfer_to", mission.View.FarSwitchWarning);

            mission.Send(new FarSwitchConfirmed("Relay Station"));
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Not a Coast Checkpoint: this script runs circularize before transfer_to"));
        }

        [Fact]
        public void A_plan_that_can_resume_has_no_line_on_its_card_or_in_its_log()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("can't stop at a Coast Checkpoint"));
        }

        // ---- A clean checkpoint ----

        [Fact]
        public void A_Missions_run_may_checkpoint_and_a_block_test_may_not()
        {
            Assert.Contains("\"checkpoint\", \"on\"", CoastCheckpoints.Armed(Script));
            Assert.Contains("lexicon(\"checkpoint\", \"on\")", CoastCheckpoints.Armed("local res is return().\n"));
            // What the mod can't start over stays as it is: a launch first, options it can't read, or a burn by hand.
            var launchFirst = "launch(lexicon(\"apoapsis\", 100000)).\n" + Script;
            Assert.Equal(launchFirst, CoastCheckpoints.Armed(launchFirst));
            var variable = "local o is lexicon(\"target\", \"Duna\", \"target_pe\", 60000).\ntransfer_to(o).\n";
            Assert.Contains("lexicon(\"checkpoint\", \"on\", \"target\", \"Duna\"", CoastCheckpoints.Armed(variable));
            // Options it can't read, or a lexicon something else reads or changes, still stay as they are.
            var shared = "local o is lexicon(\"target\", \"Duna\", \"target_pe\", 60000).\nset o[\"target_pe\"] to 70000.\ntransfer_to(o).\n";
            Assert.Equal(shared, CoastCheckpoints.Armed(shared));
            var computed = "local o is makeopts().\ntransfer_to(o).\n";
            Assert.Equal(computed, CoastCheckpoints.Armed(computed));
            foreach (var once in new[] { "stage.\n", "toggle ag1.\n", "lights on.\n" })
                Assert.Equal(once + Script, CoastCheckpoints.Armed(once + Script));

            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var start = ToRunning(Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"))), rig);
            Assert.Contains("\"checkpoint\", \"on\"", start.Source);
        }

        [Fact]
        public void A_clean_checkpoint_gets_a_mod_written_Nominal_Debrief_the_same_step_and_a_due_time_with_an_alarm()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = RunToCheckpoint(transport, rig, token);
            var requests = transport.Requests.Count;

            var view = mission.View;
            Assert.Equal(MissionStage.AwaitingSequenceApproval, view.Stage);
            Assert.Equal(requests, transport.Requests.Count);
            var debrief = view.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Nominal", debrief.Outcome);
            Assert.Equal(CoastCheckpoints.CardWhy, debrief.Why);
            Assert.True(view.DueTime.HasDueTime);
            Assert.Equal(token.PointUt - 1800, view.DueTime.Ut, 3);
            Assert.Equal(CoastCheckpoints.WaitsFor, view.DueTime.WaitsFor);
            var commands = mission.TakeCommands();
            Assert.Equal(token.DueUt, commands.OfType<SetDueTimeAlarm>().Single().Ut, 3);
            Assert.Contains(view.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("Fly other Missions meanwhile"));
            // One plan step, as a retry: the Mission Plan never names checkpoints, and the count doesn't move.
            var state = mission.ExportStates().Single();
            Assert.Equal(1, state.SequencesProposed);
            Assert.Equal(1, state.SequencePlan.PlanStep);
            Assert.Equal("Duna transfer", state.SequencePlan.Name);
            Assert.Equal(2, state.MissionPlan.Sequences.Count);
            Assert.Contains(token.ToString(), state.SequencePlan.Script);
            Assert.DoesNotContain("\"on\"", state.SequencePlan.Script);
        }

        [Fact]
        public void The_next_request_hears_of_the_checkpoint_from_its_fact_without_the_token()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"), SequenceReply("Fixing it"));
            var mission = RunToCheckpoint(transport, rig, token);
            Assert.NotNull(CoastCheckpoints.ReadFact(CoastCheckpoints.Fact("x", "what", "report")));
            Assert.Null(CoastCheckpoints.ReadFact("anything else"));

            Leave(mission, rig);
            rig.Fly("by_hand", rig.Ut, -300, 0, 0);
            mission.Send(Craft(rig));
            DrainUntil(mission, v => transport.Requests.Count == 3);
            var messages = Messages(transport.Requests[2]);
            Assert.Contains(CoastCheckpoints.FactPrefix.Substring(0, 30), messages);
            Assert.Contains("ejection_dv_used", messages);
            Assert.DoesNotContain("resume:block", messages);
        }

        // ---- The player stays ----

        [Fact]
        public void Auto_Mode_on_with_the_player_in_the_craft_starts_the_resumed_sequence_at_once()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = RunToCheckpoint(transport, rig, token, autoMode: true);

            var start = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.Contains(token.ToString(), start.Source);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.AutoModeStop);
        }

        /// <summary>#830: the runaway stop's warp hold doesn't carry across a checkpoint; the resumed run is timed and held on its own.</summary>
        [Fact]
        public void A_run_resumed_from_a_checkpoint_stops_at_its_deadline_as_today_and_holds_only_for_its_own_warp()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = Control(transport);
            var first = ToRunning(mission, rig);
            // The first run was warping to a far target when it checkpointed.
            mission.Send(new KosWarpRequested(rig.Ut + 1e10, first.RunId));
            mission.Send(new SetAutoMode(true));
            mission.Send(AtCheckpoint(token, first.RunId));
            _background.Drain();
            var resumed = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.NotEqual(first.RunId, resumed.RunId);

            // Nothing from the first run holds the resumed one: it stops at 300% of its own figure.
            mission.Send(new UtAdvanced(rig.Ut + 1e9));
            Assert.Single(mission.TakeCommands().OfType<AbortScript>());
        }

        [Fact]
        public void Auto_Mode_on_in_a_Blackout_with_the_player_in_the_craft_starts_the_resumed_sequence_at_once()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            var start = ToRunning(mission, rig);
            mission.Send(new SetAutoMode(true));
            var dark = Craft(rig);
            dark.HasLink = false;
            mission.Send(dark);
            Assert.True(mission.View.Blackout);
            mission.TakeCommands();
            mission.Send(AtCheckpoint(token, start.RunId));
            _background.Drain();

            // #967: approved as outside a Blackout, before its Due Time, and listed for when the link is back.
            var resumed = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.Contains(token.ToString(), resumed.Source);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("waits for the link"));
        }

        [Fact]
        public void Auto_Mode_starts_a_due_time_of_Claudias_own_only_once_it_is_reached()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var due = rig.Ut + 10 * Day;
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer", due)));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            CraftCheckRouter.StartChecked(mission, _background, Craft(rig), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(Craft(rig), Goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new SetAutoMode(true));
            _background.Drain();
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            // #967: at the Due Time (the window wait's, past Claudia's) Auto Mode carries on.
            Assert.True(mission.View.DueTime.Ut >= due);
            mission.Send(new UtAdvanced(mission.View.DueTime.Ut + 1));
            _background.Drain();
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void Auto_Mode_starts_an_ejection_wait_and_a_window_wait_at_their_due_time()
        {
            // A transfer_to in Kerbin orbit whose ejection is months off (#749), under the window's Due Time (#616).
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            CraftCheckRouter.StartChecked(mission, _background, Craft(rig), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(Craft(rig), Goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new SetAutoMode(true));
            var due = mission.View.DueTime;
            Assert.True(due.HasDueTime);
            mission.Send(new UtAdvanced(due.Ut + 1));
            _background.Drain();
            // #967: Auto Mode carries on at the Due Time; the script waits out the ejection itself.
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void A_reply_of_Claudias_cannot_mark_its_plan_as_a_checkpoint()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var script = Script;
            var reply = "{\"kind\":\"sequence_plan\",\"say\":\"x\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":{\"name\":\"Marked\",\"plan_step\":1,\"intent\":\"i\",\"why\":\"w\",\"target\":\"t\","
                + "\"tolerances\":\"t\",\"expected_delta_v\":1,\"expected_duration_s\":40000000,\"abort_conditions\":[],\"risks\":[],\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + JsonSerializer.Serialize(script) + ","
                + "\"due_time_ut\":" + (rig.Ut + Day).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1,\"coast_checkpoint\":" + JsonSerializer.Serialize(token.ToString()) + "},\"debrief\":null}";
            var mission = Control(new StreamingFakeTransport(DunaPlan(), Reply(reply)));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            CraftCheckRouter.StartChecked(mission, _background, Craft(rig), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(Craft(rig), Goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new SetAutoMode(true));
            _background.Drain();
            // Not a soft Due Time: nothing starts before it (#967: at it, Auto Mode carries on as for any Due Time).
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.DoesNotContain(QuickReply.Approve, mission.View.QuickReplies);
        }

        [Fact]
        public void Auto_Mode_off_waits_for_one_Approve_which_may_come_before_the_due_time()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = RunToCheckpoint(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")), rig, token);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.WarpToDueTime }, mission.View.QuickReplies.Take(2).ToArray());

            mission.Send(new Approve());
            var commands = mission.TakeCommands();
            Assert.Contains(token.ToString(), commands.OfType<StartScript>().Single().Source);
            Assert.Single(commands.OfType<ClearDueTimeAlarm>());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        // ---- A far switch ----

        [Fact]
        public void A_far_switch_during_a_coast_warp_is_a_clean_checkpoint_when_the_safety_test_passes()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = Control(transport);
            ToRunning(mission, rig);
            var requests = transport.Requests.Count;

            mission.Send(new CoastArmed(token.ToString()));
            Assert.True(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Equal("", mission.View.FarSwitchWarning);

            mission.Send(new FarSwitchConfirmed("Relay Station"));
            _background.Drain();
            var commands = mission.TakeCommands();
            Assert.Single(commands.OfType<AbortScript>());
            Assert.Equal(token.DueUt, commands.OfType<SetDueTimeAlarm>().Single().Ut, 3);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal("Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.Equal(requests, transport.Requests.Count);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was interrupted"));
            Assert.Equal("Due " + KerbinTime.ShortDate(token.DueUt) + ": check pending", MissionListRules.Due(mission.View.Missions.Single(m => m.Current)));

            // Before the player is back, Approve starts nothing.
            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void A_far_switch_is_an_interruption_with_a_warning_when_the_safety_test_fails()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            mission.Send(new CoastArmed(token.ToString()));
            rig.Fly("by_hand", rig.Ut, -300, 0, 0);
            mission.Send(Craft(rig));
            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Contains("lost", mission.View.FarSwitchWarning);

            mission.Send(new FarSwitchConfirmed("Relay Station"));
            var view = mission.View;
            Assert.Equal(MissionStage.Debriefing, view.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SetDueTimeAlarm>());
            Assert.Contains(view.Log, e => e.Text.Contains("was interrupted"));
            Assert.Contains(view.Log, e => e.Text.StartsWith("Not a Coast Checkpoint:") && e.Text.Contains("lost"));
        }

        [Fact]
        public void A_far_switch_after_a_crossed_Telemetry_Check_is_an_interruption_for_Claudia_to_judge()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var checks = "\"telemetry_checks\":[{\"quantity\":\"altitude\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":1000,\"why\":\"Test.\"}],";
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer", checks: checks), ScriptedResponse.Fixture("mission-alert-abort.sse")));
            ToRunning(mission, rig);
            var high = Craft(rig);
            high.Altitude = 5000000;
            mission.Send(high);
            DrainUntil(mission, v => v.AbortRecommended.Length > 0);
            mission.Send(new CoastArmed(token.ToString()));

            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Contains("Telemetry Check", mission.View.FarSwitchWarning);
            mission.Send(new FarSwitchConfirmed("Relay Station"));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.Contains("was interrupted"));
        }

        [Fact]
        public void A_run_that_ends_at_a_checkpoint_with_the_player_nearby_elsewhere_is_checked_when_they_are_back()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = Control(transport);
            var start = ToRunning(mission, rig);
            mission.Send(new SetAutoMode(true));
            mission.Send(Elsewhere(rig));
            mission.Send(AtCheckpoint(token, start.RunId));
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());

            mission.Send(Craft(rig));
            mission.Send(Craft(rig));
            _background.Drain();
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Checked on return:"));
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void A_resumed_flight_reports_the_encounter_for_what_comes_after_the_block()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            rig.MoveTo(token.DueUt);
            var ask = Ask("Duna", 60000);
            ask.Resume = token;
            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal(Math.Round(token.SoiUt), step.Report["encounter_ut"]);
        }

        [Fact]
        public void A_far_switch_with_nothing_armed_past_the_wait_or_in_a_burn_is_an_interruption_as_before()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Equal("", mission.View.FarSwitchWarning);

            mission.Send(new CoastArmed(token.ToString()));
            mission.Send(new UtAdvanced(token.WaitUt + 1));
            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Equal("", mission.View.FarSwitchWarning);

            mission.Send(new FarSwitchConfirmed("Relay Station"));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void An_encounter_watch_drop_ends_the_coast_a_far_switch_could_leave()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            mission.Send(new CoastArmed(token.ToString()));
            mission.Send(new EncounterWatchDropped("Encounter Watch: lost"));
            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
        }

        // ---- Leaving Flight for the Space Center or Tracking Station (#777) ----

        [Theory]
        [InlineData(LeaveTo.SpaceCenter)]
        [InlineData(LeaveTo.TrackingStation)]
        public void Leaving_Flight_during_a_coast_that_passes_the_test_is_recorded_before_the_scene_loads_and_survives_it_and_the_save(LeaveTo destination)
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = Control(transport);
            ToRunning(mission, rig);
            var requests = transport.Requests.Count;
            mission.Send(new CoastArmed(token.ToString()));
            Assert.True(mission.View.FarSwitchLeavesAtCheckpoint);

            // The adapter's answer comes first: the checkpoint is whole before KSP changes scene.
            mission.Send(new LeaveFlightConfirmed(destination));
            var commands = mission.TakeCommands();
            Assert.Single(commands.OfType<AbortScript>());
            Assert.Equal(token.DueUt, commands.OfType<SetDueTimeAlarm>().Single().Ut, 3);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal("Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
            var before = mission.ExportStates().Single();

            // Then the scene change, and the save that goes with it.
            mission.Send(new SceneChanged(GameScene.Other));
            var after = mission.ExportStates().Single();
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal(before.Stage, after.Stage);
            Assert.Contains(token.ToString(), after.SequencePlan.Script);
            Assert.Equal(1, after.SequencePlan.PlanStep);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was interrupted"));
            Assert.Empty(mission.TakeCommands().OfType<ClearDueTimeAlarm>());
            Assert.True(mission.View.DueTime.HasDueTime);
            Assert.Equal("Due " + KerbinTime.ShortDate(token.DueUt) + ": check pending", MissionListRules.Due(mission.View.Missions.Single(m => m.Current)));

            // Back through the alarm, Go to vessel or Fly: Flight loads and the return check runs, with no request.
            mission.Send(new SetAutoMode(true));
            rig.MoveTo(token.DueUt);
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Craft(rig));
            mission.Send(Craft(rig));
            _background.Drain();
            Assert.Equal(requests, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Checked on return:") && e.Text.Contains("is kept"));
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void Leaving_Flight_when_the_test_fails_is_an_interruption_with_the_reason()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            mission.Send(new CoastArmed(token.ToString()));
            rig.Fly("by_hand", rig.Ut, -300, 0, 0);
            mission.Send(Craft(rig));
            Assert.False(mission.View.FarSwitchLeavesAtCheckpoint);
            Assert.Contains("lost", mission.View.FarSwitchWarning);

            mission.Send(new LeaveFlightConfirmed(LeaveTo.SpaceCenter));
            mission.Send(new SceneChanged(GameScene.Other));
            var view = mission.View;
            Assert.Equal(MissionStage.Debriefing, view.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SetDueTimeAlarm>());
            Assert.Single(view.Log, e => e.Text.Contains("was interrupted"));
            Assert.Contains(view.Log, e => e.Text.StartsWith("Not a Coast Checkpoint:") && e.Text.Contains("lost"));
        }

        [Fact]
        public void The_main_menu_quit_and_revert_stay_interruptions_even_when_the_test_passes()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            mission.Send(new CoastArmed(token.ToString()));
            Assert.True(mission.View.FarSwitchLeavesAtCheckpoint);

            // The adapter offers a checkpoint only to the Space Center and the Tracking Station; any other leave says so.
            mission.Send(new LeaveFlightConfirmed(LeaveTo.Other));
            mission.Send(new SceneChanged(GameScene.Editor));
            var view = mission.View;
            Assert.Equal(MissionStage.Debriefing, view.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SetDueTimeAlarm>());
            Assert.Single(view.Log, e => e.Text.Contains("was interrupted"));
            Assert.DoesNotContain(view.Log, e => e.Kind == LogKind.Debrief);
        }

        [Fact]
        public void A_revert_to_the_VAB_shows_no_dialog_and_interrupts_a_Running_sequence_never_a_checkpoint()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var mission = Control(new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer")));
            ToRunning(mission, rig);
            mission.Send(new CoastArmed(token.ToString()));
            Assert.True(mission.View.LeavingFlightNeedsConfirmation);

            // The guard's choice: no dialog for the editor, even while Running; a dialog for the others.
            Assert.False(LeaveFlightRules.AsksFirst(mission.View.LeavingFlightNeedsConfirmation, LeaveTo.Editor));
            Assert.True(LeaveFlightRules.AsksFirst(mission.View.LeavingFlightNeedsConfirmation, LeaveTo.Other));
            Assert.True(LeaveFlightRules.AsksFirst(mission.View.LeavingFlightNeedsConfirmation, LeaveTo.SpaceCenter));
            Assert.False(LeaveFlightRules.CanCheckpoint(LeaveTo.Editor));

            // With no dialog, no LeaveFlightConfirmed is sent: the scene change alone interrupts.
            mission.Send(new SceneChanged(GameScene.Editor));
            var view = mission.View;
            Assert.Equal(MissionStage.Debriefing, view.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SetDueTimeAlarm>());
            Assert.Single(view.Log, e => e.Text.Contains("was interrupted"));
            Assert.DoesNotContain(view.Log, e => e.Kind == LogKind.Debrief);
        }

        // ---- The check on return ----

        [Fact]
        public void The_check_keeps_a_clean_trajectory_with_no_request_and_Auto_Mode_starts_the_resume()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = RunToCheckpoint(transport, rig, token);
            mission.TakeCommands();
            var requests = transport.Requests.Count;

            Leave(mission, rig);
            var entry = mission.View.Missions.Single(m => m.Current);
            Assert.Equal("Due " + KerbinTime.ShortDate(token.DueUt) + ": check pending", MissionListRules.Due(entry));
            mission.Send(new SetAutoMode(true));
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());

            // Back at the alarm.
            rig.MoveTo(token.DueUt);
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            _background.Drain();

            Assert.Equal(requests, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Checked on return:") && e.Text.Contains("is kept"));
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void A_kept_late_return_takes_the_resume_with_its_lateness_in_the_log()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = RunToCheckpoint(transport, rig, token);
            var requests = transport.Requests.Count;

            Leave(mission, rig);
            rig.MoveTo(token.PointUt + 2 * Day);
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            _background.Drain();

            Assert.Equal(requests, transport.Requests.Count);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Checked on return:") && e.Text.Contains(KerbinTime.Span(2 * Day) + " late"));
        }

        [Fact]
        public void A_drifted_coast_goes_to_Claudia_with_the_figures()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            // A limit a small nudge passes, the encounter kept.
            token.PeLimit = 1000;
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"), SequenceReply("Fix the coast"));
            var mission = RunToCheckpoint(transport, rig, token);
            mission.TakeCommands();

            Leave(mission, rig);
            rig.Fly("by_hand", rig.Ut, 0.5, 0, 0);
            var seen = CoastCheckpoints.Judge(Craft(rig), token, rig.Ut);
            Assert.Equal(CoastVerdictKind.Drifted, seen.Kind);
            mission.Send(Craft(rig));
            DrainUntil(mission, v => v.Log.Any(e => e.Kind == LogKind.SequencePlan && e.Title == "Fix the coast"));

            var asked = Messages(transport.Requests.Last());
            Assert.Contains("Coast Checkpoint", asked);
            Assert.Contains("has drifted", asked);
            Assert.Contains("km off the", asked);
            Assert.Single(mission.TakeCommands().OfType<ClearDueTimeAlarm>());
        }

        [Fact]
        public void A_lost_late_coast_goes_to_Claudia_with_the_figures_and_its_lateness()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"), SequenceReply("Recover"));
            var mission = RunToCheckpoint(transport, rig, token);
            mission.TakeCommands();

            Leave(mission, rig);
            rig.Fly("by_hand", rig.Ut, -300, 0, 0);
            rig.MoveTo(token.PointUt + 3 * Day);
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            DrainUntil(mission, v => v.Log.Any(e => e.Kind == LogKind.SequencePlan && e.Title == "Recover"));

            var asked = Messages(transport.Requests.Last());
            Assert.Contains("encounter is lost", asked);
            Assert.Contains("The player is late", asked);
            Assert.Contains(Math.Round(3 * Day).ToString("0", System.Globalization.CultureInfo.InvariantCulture), asked);
            Assert.Single(mission.TakeCommands().OfType<ClearDueTimeAlarm>());
            // The same step again, not a new one.
            Assert.Equal(1, mission.ExportStates().Single().SequencesProposed);
        }

        [Fact]
        public void The_check_runs_again_after_a_load_of_a_save_at_a_checkpoint()
        {
            CoastToken token;
            var rig = ToFirstCheckpoint(out token);
            var transport = new StreamingFakeTransport(DunaPlan(), SequenceReply("Duna transfer"));
            var mission = RunToCheckpoint(transport, rig, token);
            var state = mission.ExportStates().Single();
            Assert.Equal(token.ToString(), state.SequencePlan.CoastCheckpoint);

            // A fresh Mission Control, as a new game session has: the card comes back from the transcript's fact.
            mission = Control(transport);
            mission.Send(new MissionsRestored(_save.Path, new[] { MissionSaveState.FromValues(state.ToValues()) }));
            _background.Drain();
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains("check pending", MissionListRules.Due(mission.View.Missions.Single(m => m.Current)));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Debrief && e.Outcome == "Nominal" && e.Why == CoastCheckpoints.CardWhy);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(rig.Ut));
            mission.Send(Craft(rig));
            _background.Drain();
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Checked on return:"));
            Assert.Equal(token.DueUt, mission.View.DueTime.Ut, 3);
        }

        // ---- The token ----

        [Fact]
        public void The_token_round_trips_and_refuses_what_a_save_could_smuggle()
        {
            CoastToken token;
            ToFirstCheckpoint(out token);
            var back = CoastToken.Parse(token.ToString());
            Assert.Equal(token.ToString(), back.ToString());
            Assert.Null(CoastToken.Parse("on"));
            Assert.Null(CoastToken.Parse("resume:block=transfer_to"));
            Assert.Null(CoastToken.Parse(token.ToString().Replace("target=Duna", "target=Du\"na")));
            Assert.Null(CoastToken.Parse(token.ToString().Replace("block=transfer_to", "block=launch")));
        }

        // ---- return's twin ----

        private static IDictionary<string, object> ReturnAsk(double mid, string mode)
        {
            return new Dictionary<string, object> { { "timing", "coast" }, { "target", "Kerbin" }, { "mid", mid }, { "turn_time", 60.0 }, { "arrive_ut", mid * 2 }, { "checkpoint", mode } };
        }

        [Fact]
        public void Return_stops_at_a_checkpoint_only_on_a_safe_coast_over_six_hours_and_resumes_with_a_watched_wait()
        {
            // A high orbit round Kerbin, its periapsis well above the air.
            var rig = TransferRig.Circular("Kerbin", 40000000, 1000);
            var snapshot = rig.Snapshot();
            var mid = 1000 + 3 * Day;

            // Off (a block test), or a short coast: on with the flight.
            Assert.Equal("go", BlockCalls.TransferStep(ReturnAsk(mid, "off"), snapshot)["do"]);
            Assert.Equal("go", BlockCalls.TransferStep(ReturnAsk(1000 + Day / 2, "on"), snapshot)["do"]);

            var stop = BlockCalls.TransferStep(ReturnAsk(mid, "on"), snapshot);
            Assert.Equal("done", stop["do"]);
            var report = (IDictionary<string, object>)stop["report"];
            var token = CoastToken.Parse((string)report[CoastCheckpoints.TokenKey]);
            Assert.Equal("return", token.Block);
            Assert.Equal(mid, token.PointUt);
            Assert.Equal(mid - 1800, token.DueUt);

            // Resumed: a watched wait to the correction, not a second stop.
            var resumed = BlockCalls.TransferStep(ReturnAsk(mid, token.ToString()), snapshot);
            Assert.Equal("wait", resumed["do"]);
            Assert.Equal(mid, resumed["ut"]);
            Assert.Equal(mid - 360, (double)resumed["wait_ut"]);
            Assert.Equal(token.ToString(), ((IDictionary<string, object>)resumed["watch"])[CoastCheckpoints.TokenKey]);

            // Late: the correction as soon as the craft can turn to it.
            rig.MoveTo(mid + 500);
            var late = BlockCalls.TransferStep(ReturnAsk(mid, token.ToString()), rig.Snapshot());
            Assert.Equal(rig.Ut + CoastCheckpoints.ReturnBurnLeadS, late["ut"]);
        }

        [Fact]
        public void Return_with_its_periapsis_in_the_air_keeps_flying()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 1000);
            var kerbin = rig.System.Find("Kerbin");
            // Apoapsis 40 Mm, periapsis 35 km: the Block's default aim, which a late return would turn into a loss.
            var pe = kerbin.Radius + 35000;
            var ap = kerbin.Radius + 40000000;
            rig.Orbit = Conic.FromElements(kerbin.Gm, (pe + ap) / 2, (ap - pe) / (ap + pe), 0, 0, 0, 3, 1000);
            Assert.Equal("go", BlockCalls.TransferStep(ReturnAsk(1000 + 3 * Day, "on"), rig.Snapshot())["do"]);
        }

        [Fact]
        public void A_return_script_with_no_options_can_be_armed_and_resumed()
        {
            var plan = new SequencePlan { Name = "Home", Script = "local res is return().\naddons:claudia:report(res).\n" };
            var token = new CoastToken { Block = "return", Target = "Kerbin", Pe = 100000, PeLimit = 8000000, PointUt = 50000, LeadS = 120, WaitUt = 49640, Phase = "correction", ArriveUt = 90000, Bodies = "Kerbin" };
            var resumed = CoastCheckpoints.Resumed(plan, token, 1000);
            Assert.Contains("return(lexicon(\"checkpoint\", \"resume:block=return", resumed.Script);
            // A second checkpoint in a resumed run replaces the token.
            var next = new CoastToken { Block = "return", Target = "Kerbin", Pe = 100000, PeLimit = 8000000, PointUt = 70000, LeadS = 120, WaitUt = 69640, Phase = "correction", ArriveUt = 90000, Bodies = "Kerbin" };
            var again = CoastCheckpoints.Resumed(resumed, next, 2000);
            Assert.Contains(next.ToString(), again.Script);
            Assert.DoesNotContain(token.ToString(), again.Script);
        }
    }
}
