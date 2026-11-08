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
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Live supervision (spec stories 83 to 89 and 125), driven through the Mission seam: the waiting call at the
    /// Spending Cap, one request per Mission with Alerts ahead of questions, questions while Running, Telemetry
    /// Checks and their Alerts, and the Debrief that lists every crossing.
    /// </summary>
    public sealed class MissionLiveTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly List<ScriptedResponse> _gates = new List<ScriptedResponse>();

        public void Dispose()
        {
            foreach (var response in _gates) response.Gate.Set();
            _save.Dispose();
        }

        // ---- The waiting call ----

        [Fact]
        public void AQuestionThatWouldPassTheCapWhileRunningWaitsAndTheMissionStaysRunning()
        {
            // The priced plan and Sequence Plan cost $1.08, so a question's worst case no longer fits a $1.20 cap.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);

            mission.Send(new AskQuestion("How long will it take?"));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Contains(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Text.Contains("Spending Cap") && m.Always);
            // The run is not touched.
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.LatestLine == "About 5 seconds, and no engines fire.");
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.CapWait);
            Assert.DoesNotContain(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains("\"blocked\"", CostLog(mission));

            // The run ends as usual afterwards.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void ALateApprovalReplanThatWouldPassTheCapWaitsWithoutPausing()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), "\"window_closes_ut\":1300,\"late_window_dv\":-1,"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 1.20m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            mission.Send(new UtAdvanced(1400));
            mission.Send(new Approve());

            Assert.Equal(MissionStage.Sequencing, mission.View.Stage);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Equal(new[] { QuickReply.RaiseCap }, mission.View.QuickReplies);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void ARunThatEndsWithAQuestionWaitingAtTheCapStillPausesForItsDebrief()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);
            mission.Send(new AskQuestion("How long will it take?"));
            Assert.Contains("Spending Cap", mission.View.CapWait);

            // Story 124: the Debrief goes first, and at the cap it Pauses the Mission with Raise cap or End Mission.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal("", mission.View.CapWait);

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded && !v.Busy && transport.Requests.Count == 4);
            Assert.Contains("kOS run ended: Finished.", LastUserText(transport.Requests[2]));
            Assert.Contains("How long will it take?", LastUserText(transport.Requests[3]));
        }

        [Fact]
        public void AnAlertWaitingAtTheCapWhenTheRunEndsIsDroppedNotSent()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);
            mission.Send(Snapshot(s => s.Altitude = 500));
            Assert.Contains("Spending Cap", mission.View.CapWait);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("altitude 500 m over 100 m: not judged: the run ended first", LastUserText(transport.Requests[2]));
        }

        [Fact]
        public void AnAlertClaudiaCantJudgePausesTheMissionButTheRunCanStillBeAborted()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("refusal.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(QuickReply.Abort, mission.View.QuickReplies.First());
            Assert.Contains(QuickReply.Retry, mission.View.QuickReplies);
            mission.TakeCommands();

            mission.Send(new AbortSequence());
            Assert.Equal(start.RunId, mission.TakeCommands().OfType<AbortScript>().Single().RunId);
        }

        [Fact]
        public void ARunRefusalWhilePausedFromRunningIsNotLostAndResumeDoesNotLeaveTheMissionRunningWithNoScript()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("refusal.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            mission.Send(new KosRunRefused("the processor is busy", start.RunId));

            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.Contains("The script didn't start: the processor is busy"));
            mission.Send(new Retry());
            Assert.NotEqual(MissionStage.Running, mission.View.Stage);
        }

        // ---- Questions while Running ----

        [Fact]
        public void QuestionsWhileRunningGoOneAtATimeWithACompactCraftSnapshotAndNeverTouchTheRun()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            Assert.True(mission.View.CanType);
            Assert.Contains(QuickReply.AskQuestion, mission.View.QuickReplies);

            mission.Send(new AskQuestion("How long will it take?"));
            Assert.True(mission.View.Busy);
            // Asked while the first is out: it waits its turn.
            mission.Send(new AskQuestion("And the fuel?"));
            Assert.Equal(2, mission.View.Log.Count(e => e.Kind == LogKind.Player && e.Text.EndsWith("?")));

            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.DoesNotContain(mission.TakeCommands(), c => c is AbortScript || c is StartScript);

            var first = LastUserText(transport.Requests[2]);
            Assert.Contains("How long will it take?", first);
            Assert.Contains("Craft now (compact, JSON data):", first);
            Assert.Contains("\"situation\":\"PRELAUNCH\"", first);
            Assert.Contains("\"run_elapsed_s\"", first);
            // Compact: not the whole Game Snapshot.
            Assert.DoesNotContain("Game Snapshot (JSON data", first);
            Assert.DoesNotContain("\"parts\"", first);
            Assert.Contains("And the fuel?", LastUserText(transport.Requests[3]));
            Assert.Equal("low", Effort(transport.Requests[3]));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void AQuestionClaudiaCantAnswerWhileRunningLeavesTheMissionRunning()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("refusal.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new AskQuestion("Why?"));
            DrainUntil(mission, v => !v.Busy);

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Claudia && e.Text == ClaudiaKerman.Core.Chat.ChatSession.RefusalLine);

            // The run's Debrief is the next request, as usual.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(4, transport.Requests.Count);
        }

        // ---- Telemetry Checks at Approval ----

        [Fact]
        public void ApprovalShowsClaudiasTelemetryChecksWithTheConfigDefaultsFillingTheGaps()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(5000)));
            var mission = Control(transport, new PlayerConfig { CheckElectricChargePercent = 20 });
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            var card = mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).Text;
            // Claudia's own; no delta-v default (the plan expects 0 m/s); 150% of 5 s; the config's electric charge; the fuel margin.
            Assert.Contains("Telemetry Checks: altitude over 5,000 m; time elapsed over 8 s (default); ElectricCharge under 20% (default); "
                + "needed-fuel margin under -10% (default)", card);
        }

        [Fact]
        public void TheDeltaVDefaultIs120PercentOfThePlanAndAQuantityClaudiaSetKeepsHers()
        {
            var plan = new SequencePlan { ExpectedDeltaV = 1000, ExpectedDurationSeconds = 600 };
            var defaults = TelemetryDefaults.Fill(plan, new PlayerConfig());
            Assert.Equal(new[] { "delta-v used over 1,200 m/s", "time elapsed over 900 s", "ElectricCharge under 10%", "needed-fuel margin under -10%" },
                defaults.Select(c => c.Describe()));

            plan.TelemetryChecks.Add(new TelemetryCheck { Quantity = TelemetryQuantity.DeltaVUsed, Threshold = 1100 });
            var filled = TelemetryDefaults.Fill(plan, new PlayerConfig());
            Assert.Equal("delta-v used over 1,100 m/s", filled.Single(c => c.Quantity == TelemetryQuantity.DeltaVUsed).Describe());
            Assert.False(filled.First().Default);
        }

        [Fact]
        public void APlansTelemetryChecksSurviveASave()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(5000)));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            var saved = MissionSaveState.FromValues(mission.ExportStates().Single().ToValues());
            var check = saved.SequencePlan.TelemetryChecks.Single();
            Assert.Equal("altitude over 5,000 m", check.Describe());
            Assert.Equal("Too high for a pad test.", check.Why);
        }

        // ---- A normal run ----

        [Fact]
        public void ANormalRunMakesNoClaudeCallsAndTheStripShowsTheBlockProgressAndKeyNumbers()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new KosBlockStarted("exec_node", start.RunId));
            mission.Send(new UtAdvanced(1203.48 + 2));
            mission.Send(Snapshot(s => { s.Altitude = 80; s.Stages[0].DeltaVVac = 3190; }));

            var progress = mission.View.RunProgress;
            Assert.True(progress.Running);
            Assert.Equal("Link check", progress.Sequence);
            Assert.Equal("exec_node", progress.Block);
            Assert.Equal(2, progress.ElapsedSeconds, 1);
            Assert.Equal(5, progress.PlannedSeconds);
            Assert.Equal(0.4, progress.Fraction, 2);
            Assert.Equal(10, progress.DeltaVUsed);
            Assert.Equal(80, progress.Altitude);
            Assert.Equal(100, progress.ElectricChargePercent);

            // Each block result adds a log row; a block from another run is ignored.
            var result = new Dictionary<string, object> { { "success", true }, { "reason", "" }, { "dv_used", 9.6 }, { "duration", 2.2 } };
            mission.Send(new KosBlockEnded("exec_node", result, start.RunId));
            mission.Send(new KosBlockEnded("exec_node", result, start.RunId + 1));
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == "Building Block exec_node: done, 10 m/s used, 2 s.");
            Assert.Equal(1, mission.View.RunProgress.BlocksDone);
            Assert.Equal("", mission.View.RunProgress.Block);

            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.False(mission.View.Busy);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            mission.Send(Snapshot(s => s.Ut = 1203.48 + 3));
            Assert.False(mission.View.RunProgress.Running);
        }

        // ---- Alerts ----

        [Fact]
        public void ACrossedCheckSendsOneAlertAndAnAbortRecommendationDropsWarpAndThePlayerAbortsToAnOffNominalDebrief()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-alert-abort.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            // 100 m/s used against a 50 m/s check, on two snapshots in a row (a stock delta-v crossing is confirmed).
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Telemetry Check crossed: delta-v used 100 m/s over 50 m/s"));
            // It fires once: more snapshots past it send nothing more.
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3000));
            DrainUntil(mission, v => v.AbortRecommended.Length > 0);
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 2900));
            _background.Drain();
            Assert.Equal(3, transport.Requests.Count);

            var alert = LastUserText(transport.Requests[2]);
            Assert.Contains("Alert: a Telemetry Check was crossed", alert);
            Assert.Contains("Craft now (compact, JSON data):", alert);
            Assert.Equal("low", Effort(transport.Requests[2]));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains("The burn is overrunning.", mission.View.AbortRecommended);
            Assert.Equal(QuickReply.Abort, mission.View.QuickReplies.First());
            var commands = mission.TakeCommands();
            Assert.Contains(commands, c => c is DropWarp);
            Assert.Contains(commands.OfType<PostScreenMessage>(), m => m.Always && m.Text.StartsWith("Claudia recommends Abort"));
            // Claudia never aborts herself.
            Assert.DoesNotContain(commands, c => c is AbortScript);

            mission.Send(new AbortSequence());
            var abort = mission.TakeCommands().OfType<AbortScript>().Single();
            Assert.Equal(start.RunId, abort.RunId);
            Assert.Equal("the player pressed Abort", abort.Reason);
            Assert.Equal("", mission.View.AbortRecommended);
            Assert.DoesNotContain(QuickReply.Abort, mission.View.QuickReplies);

            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Aborted, null, "Program aborted.", "the player pressed Abort", true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var report = LastUserText(transport.Requests[3]);
            Assert.Contains("kOS run ended: Aborted (aborted by the player).", report);
            Assert.Contains("Telemetry Checks crossed during the run, with your mid-run verdicts: delta-v used 100 m/s over 50 m/s: Abort recommended (The burn is overrunning.)", report);

            // Claudia rated it Nominal; a crossing rules that out.
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", debrief.Outcome);
            Assert.Equal("Debrief: Off-Nominal (aborted by the player)", debrief.Title);
            Assert.Contains("Telemetry Checks crossed: delta-v used 100 m/s over 50 m/s: Abort recommended", debrief.Text);
        }

        private static KosBlockEnded ReturnEnded(string note, int runId)
        {
            return new KosBlockEnded("return", new Dictionary<string, object> { { "success", true }, { "aim_note", note } }, runId);
        }

        private static List<string> Notices(MissionControl mission)
        {
            return mission.View.Log.Where(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Rated Off-Nominal")).Select(e => e.Text).ToList();
        }

        [Fact]
        public void AReturnThatFlewNoCorrectionIsRatedOffNominalWithItsNotice()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", debrief.Outcome);
            Assert.Equal(new[] { "Rated Off-Nominal, not Nominal: return's Course Correction couldn't be solved, so none was flown." }, Notices(mission));
        }

        [Fact]
        public void AReturnThatFlewACorrectionKeepsItsRating()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(ReturnEnded("fix flown: 12 m/s, 40 km down to 2 km", start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Equal("Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.Empty(Notices(mission));
        }

        [Fact]
        public void AReturnThatFlewNoCorrectionWithACrossedCheckShowsBothNotices()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-alert-abort.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            DrainUntil(mission, v => v.AbortRecommended.Length > 0);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Equal("Off-Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.Equal(new[]
            {
                "Rated Off-Nominal, not Nominal: a Telemetry Check was crossed during the run.",
                "Rated Off-Nominal, not Nominal: return's Course Correction couldn't be solved, so none was flown.",
            }, Notices(mission));
        }

        /// <summary>The Mission log as a load or the history rebuilds it from the Mission folder, as #904 saved it.</summary>
        private List<LogEntry> RebuiltLog(MissionControl mission)
        {
            var folder = MissionFolderOf(mission);
            return MissionLogRebuild.From(MissionStore.ReadTranscript(folder), "", "Kerbal X", new PlayerConfig(), MissionStore.ReadNotices(folder), out var _, MissionStore.ReadRatings(folder));
        }

        private static List<string> RatedNotices(IEnumerable<LogEntry> log)
        {
            return log.Where(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Rated Off-Nominal")).Select(e => e.Text).ToList();
        }

        [Fact]
        public void AReturnThenReentryRunKeepsTheNoCorrectionRatingInTheRebuiltLog()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            // The script's last block is reentry, so return's aim_note is not in the Debrief turn's text.
            mission.Send(new KosBlockEnded("reentry", new Dictionary<string, object> { { "success", true } }, start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var live = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", live.Outcome);
            var turnText = string.Concat(MissionStore.ReadTranscript(MissionFolderOf(mission)).Messages.Where(m => m.Role == Role.User).SelectMany(m => m.Content.OfType<TextBlock>()).Select(t => t.Text));
            Assert.False(LandingLine.NoCorrection(turnText));
            var rebuilt = RebuiltLog(mission);
            var debrief = rebuilt.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal(live.Title, debrief.Title);
            Assert.Equal(live.Outcome, debrief.Outcome);
            Assert.Equal(Notices(mission), RatedNotices(rebuilt));
            Assert.Single(RatedNotices(rebuilt));
        }

        [Fact]
        public void BothRatingNoticesSurviveTheRebuildWhenBothApply()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-alert-abort.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            DrainUntil(mission, v => v.AbortRecommended.Length > 0);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            mission.Send(new KosBlockEnded("reentry", new Dictionary<string, object> { { "success", true } }, start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var rebuilt = RebuiltLog(mission);
            Assert.Equal("Off-Nominal", rebuilt.Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.Equal(2, RatedNotices(rebuilt).Count);
            Assert.Equal(Notices(mission), RatedNotices(rebuilt));
        }

        [Fact]
        public void ASaveWithNoStoredRatingRebuildsAsBefore()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true }, { "aim_note", "no correction flown: the solver gave up; 40 km from the target, 80 m/s left" } }, "", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // An older Mission's folder has no rating line: the rebuild reads only the Debrief turn's text, as it did before #904.
            var folder = MissionFolderOf(mission);
            var events = Path.Combine(folder, MissionStore.EventsFile);
            File.WriteAllLines(events, File.ReadAllLines(events).Where(l => !l.Contains("\"" + MissionStore.RatingEvent + "\"")));
            Assert.Empty(MissionStore.ReadRatings(folder));
            // The return was the script's last block, so its aim_note is in the Debrief turn's text and the old rebuild rates it Off-Nominal.
            Assert.Equal("Off-Nominal", RebuiltLog(mission).Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.Equal(new[] { LandingLine.NoCorrectionNotice }, RatedNotices(RebuiltLog(mission)));
        }

        [Fact]
        public void AReturnThenReentrySaveWithNoStoredRatingRebuildsNominalAsBefore()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            mission.Send(new KosBlockEnded("reentry", new Dictionary<string, object> { { "success", true } }, start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var folder = MissionFolderOf(mission);
            var events = Path.Combine(folder, MissionStore.EventsFile);
            File.WriteAllLines(events, File.ReadAllLines(events).Where(l => !l.Contains("\"" + MissionStore.RatingEvent + "\"")));
            // Today's behaviour: the Debrief turn's text holds no aim_note, so the rebuild can't tell.
            Assert.Equal("Nominal", RebuiltLog(mission).Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.Empty(RatedNotices(RebuiltLog(mission)));
        }

        [Fact]
        public void AQuickloadReApplyingAnEarlierDebriefDoesNotRateALaterOneOffNominal()
        {
            var debrief = ScriptedResponse.Fixture("mission-debrief.sse", 64);
            debrief.PauseAt = 100;
            _gates.Add(debrief);
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                debrief);
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.Send(ReturnEnded("no correction flown: the solver gave up; 40 km from the target, 80 m/s left", start.RunId));
            mission.Send(new KosBlockEnded("reentry", new Dictionary<string, object> { { "success", true } }, start.RunId));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count >= 3);
            // Saved while Debrief 1 was in flight.
            var saved = MissionSaveState.FromValues(mission.ExportStates().Single().ToValues());
            Assert.Equal(MissionStage.Debriefing, saved.Stage);
            Assert.True(saved.NoCorrection);

            debrief.Gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var folder = MissionFolderOf(mission);
            var first = Assert.Single(MissionStore.ReadRatings(folder));

            // Debrief 2 landed later (Nominal, no rating line): two more messages in the transcript.
            var path = Path.Combine(folder, MissionFolder.TranscriptFile);
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            var messages = root["messages"].AsArray();
            var reply = messages[messages.Count - 1].DeepClone();
            Assert.Equal(first.Messages, messages.Count);
            messages.Add(System.Text.Json.Nodes.JsonNode.Parse(UserLine));
            messages.Add(reply);
            File.WriteAllText(path, root.ToJsonString());

            // The quickload re-applies Debrief 1 through the real load path, from the save made while it was in flight.
            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            DrainUntil(reloaded, v => !v.Busy);

            // Every rating line sits at Debrief 1's own reply, so the later Debrief is rebuilt Nominal, as it was live.
            Assert.All(MissionStore.ReadRatings(folder), r => Assert.Equal(first.Messages, r.Messages));
            var rebuilt = RebuiltLog(mission).Where(e => e.Kind == LogKind.Debrief).ToList();
            Assert.Equal(2, rebuilt.Count);
            Assert.Equal("Off-Nominal", rebuilt[0].Outcome);
            Assert.Equal("Nominal", rebuilt[1].Outcome);
            Assert.Single(RatedNotices(RebuiltLog(mission)));
        }

        private const string UserLine = "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"The run is over.\"}]}";

        [Fact]
        public void ACarryOnLeavesALogLineAndItsFollowUpThresholdFiresOnceMore()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-alert-carry-on.sse"),
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.TakeCommands();

            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => !v.Busy && v.LatestLine.StartsWith("Alert, altitude: carry on."));
            Assert.Equal("", mission.View.AbortRecommended);
            var commands = mission.TakeCommands();
            Assert.DoesNotContain(commands, c => c is DropWarp);
            Assert.DoesNotContain(commands, c => c is PostScreenMessage);

            // Below the follow-up threshold Claudia gave (1000 m): nothing.
            mission.Send(Snapshot(s => s.Altitude = 800));
            _background.Drain();
            Assert.Equal(3, transport.Requests.Count);

            mission.Send(Snapshot(s => s.Altitude = 1200));
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "Alert, altitude: carry on. Still fine.");
            Assert.Contains("altitude 1,200 m over 1,000 m (follow-up threshold)", LastUserText(transport.Requests[3]));

            // Each check fires at most once per run, and once at its follow-up.
            mission.Send(Snapshot(s => s.Altitude = 5000));
            _background.Drain();
            Assert.Equal(4, transport.Requests.Count);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", debrief.Outcome);
            Assert.Contains("altitude 500 m over 100 m: carry on (Within margins.) [standing]; altitude 1,200 m over 1,000 m (follow-up threshold): carry on (Still within margins.) [standing]", debrief.Text);
        }

        [Fact]
        public void ACarryOnCrossingBackOnTheSafeSideAtTheRunsEndIsRecoveredAndAllowsNominal()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-alert-carry-on.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.TakeCommands();

            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => !v.Busy && v.LatestLine.StartsWith("Alert, altitude: carry on."));
            mission.Send(Snapshot(s => s.Altitude = 50));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Contains("altitude 500 m over 100 m: carry on (Within margins.) [recovered]", LastUserText(transport.Requests[3]));
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Nominal", debrief.Outcome);
            Assert.Contains("Telemetry Checks crossed: altitude 500 m over 100 m: carry on (Within margins.) [recovered]", debrief.Text);
        }

        [Fact]
        public void AnAlertGoesAheadOfAQuestionWaitingInTheQueue()
        {
            var held = ScriptedResponse.Fixture("mission-answer.sse");
            held.PauseAt = 10;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                held,
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            ToRunning(mission, 10m);

            mission.Send(new AskQuestion("First question?"));
            mission.Send(new AskQuestion("Second question?"));
            mission.Send(Snapshot(s => s.Altitude = 500));
            held.Gate.Set();

            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 5);
            Assert.Contains("First question?", LastUserText(transport.Requests[2]));
            Assert.Contains("Alert: a Telemetry Check was crossed", LastUserText(transport.Requests[3]));
            Assert.Contains("Second question?", LastUserText(transport.Requests[4]));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void AnAlertThatWouldPassTheCapWaitsAndTheRunCarriesOn()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"));
            var mission = Control(transport);
            ToRunning(mission, 1.20m);
            mission.TakeCommands();

            mission.Send(Snapshot(s => s.Altitude = 500));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains("The sequence carries on without her", mission.View.CapWait);
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Always && m.Text.Contains("Spending Cap"));
            Assert.Equal(new[] { QuickReply.Abort, QuickReply.AskQuestion, QuickReply.RaiseCap }, mission.View.QuickReplies.Where(r => r != QuickReply.ManualControl));

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.LatestLine == "Alert, altitude: carry on. Still fine.");
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void AnAlertStillQueuedWhenTheRunEndsIsDroppedAndTheDebriefSaysItWasNeverJudged()
        {
            var held = ScriptedResponse.Fixture("mission-answer.sse");
            held.PauseAt = 10;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                held,
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new AskQuestion("First question?"));
            mission.Send(Snapshot(s => s.Altitude = 500));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            held.Gate.Set();

            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Contains("altitude 500 m over 100 m: not judged: the run ended first", LastUserText(transport.Requests[3]));
        }

        // ---- A stock delta-v crossing is confirmed over two snapshots (#381) ----

        [Fact]
        public void AOneOffDeltaVReadingPastTheCheckRaisesNoAlertAndTheNextCrossingStartsAgain()
        {
            var mission = RunningWith(QuantityCheck("delta_v_used", "above", 50));

            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            Assert.Equal(1, CrossedLines(mission));
        }

        [Theory]
        [InlineData("delta_v_used", "above", 50, 3100, "delta-v used")]
        [InlineData("delta_v_left", "below", 2950, 2900, "delta-v left")]
        [InlineData("fuel_margin", "above", 4000, 3150, "needed-fuel margin")]
        public void TwoDeltaVReadingsInARowPastTheCheckRaiseTheAlertOnTheSecond(string quantity, string direction, double threshold, double deltaV, string name)
        {
            // The fixture plans no delta-v, which leaves the needed-fuel margin unmeasured; plan 100 m/s.
            var mission = RunningWith(QuantityCheck(quantity, direction, threshold), 100);

            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = deltaV));
            Assert.Equal(0, CrossedLines(mission, name));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = deltaV));
            Assert.Equal(1, CrossedLines(mission, name));
        }

        [Fact]
        public void ANullDeltaVReadingBetweenTwoCrossingReadingsStillConfirms()
        {
            var mission = RunningWith(QuantityCheck("delta_v_used", "above", 50));

            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => { s.DeltaVSimulator = false; s.Stages[0].DeltaVVac = 3100; }));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            Assert.Equal(1, CrossedLines(mission));
        }

        [Fact]
        public void AnInputThatIsNotANewSnapshotIsNotASecondDeltaVReading()
        {
            var mission = RunningWith(QuantityCheck("delta_v_used", "above", 50));

            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(new UtAdvanced(OnPad().Ut + 1));
            mission.Send(new UtAdvanced(OnPad().Ut + 2));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            Assert.Equal(1, CrossedLines(mission));
        }

        [Fact]
        public void AnAltitudeCheckStillFiresOnItsFirstCrossing()
        {
            var mission = RunningWith(QuantityCheck("altitude", "above", 100));

            mission.Send(Snapshot(s => s.Altitude = 500));
            Assert.Equal(1, CrossedLines(mission));
        }

        // ---- An orbit check arms once its reading is on the safe side (#419) ----

        [Fact]
        public void APeriapsisCheckDoesNotFireOnThePadAndCrossesOnceArmed()
        {
            var mission = RunningWith(QuantityCheck("periapsis", "below", -100000));

            mission.Send(Snapshot(s => s.Orbit.Periapsis = -598435));
            mission.Send(Snapshot(s => s.Orbit.Periapsis = -598435));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Ascending(s => s.Orbit.Periapsis = -50000));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Ascending(s => s.Orbit.Periapsis = -150000));
            Assert.Equal(1, CrossedLines(mission, "periapsis"));
        }

        [Fact]
        public void AnApoapsisCheckArmsOnceUnderItsThreshold()
        {
            // The pad's apoapsis (-599,073 m) is already over the threshold: no crossing until it has been under.
            var mission = RunningWith(QuantityCheck("apoapsis", "above", -700000));

            mission.Send(Snapshot(s => s.Orbit.Apoapsis = -599073));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Ascending(s => s.Orbit.Apoapsis = -800000));
            mission.Send(Ascending(s => s.Orbit.Apoapsis = -599073));
            Assert.Equal(1, CrossedLines(mission, "apoapsis"));
        }

        [Fact]
        public void APeriapsisCheckThatStartsOnTheSafeSideCrossesOnTheFirstBadReading()
        {
            var mission = RunningWith(QuantityCheck("periapsis", "below", -100000));

            mission.Send(Ascending(s => s.Orbit.Periapsis = 80000));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Ascending(s => s.Orbit.Periapsis = -150000));
            Assert.Equal(1, CrossedLines(mission, "periapsis"));
        }

        [Fact]
        public void ANonOrbitCheckAlreadyCrossedAtStartStillCrossesAtOnce()
        {
            var mission = RunningWith(QuantityCheck("altitude", "below", 1000));

            mission.Send(Snapshot(s => s.Altitude = 50));
            Assert.Equal(1, CrossedLines(mission, "altitude"));
        }

        [Fact]
        public void TheFollowUpThresholdIsConfirmedOverTwoSnapshotsToo()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-alert-carry-on.sse"),
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"));
            var mission = Control(transport);
            ToRunning(mission, 10m);
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            DrainUntil(mission, v => !v.Busy);
            Assert.Equal(1, CrossedLines(mission));

            // Carry on arms a follow-up at 1,000 m/s used (the fixture's): one reading past it is not enough.
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 1900));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3000));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 1900));
            Assert.Equal(1, CrossedLines(mission));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 1900));
            Assert.Equal(2, CrossedLines(mission));
        }

        [Fact]
        public void BothDeltaVReadingsAreLoggedWithTheStagesAndThrottle()
        {
            var mission = RunningWith(QuantityCheck("delta_v_used", "above", 50));

            mission.Send(Snapshot(s => { s.Stages[0].DeltaVVac = 3100; s.Throttle = 0.03; }));
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            var events = File.ReadAllText(Path.Combine(MissionFolderOf(mission), MissionStore.EventsFile));
            Assert.Contains("telemetry_check_pending", events);
            Assert.Contains("delta-v used 100 m/s over 50 m/s: first crossing", events);
            Assert.Contains("throttle 0.03", events);
            Assert.Contains("telemetry_check_cleared", events);
            Assert.Contains("delta-v used 100 m/s then 0 m/s over 50 m/s", events);
        }

        // ---- Apsis and inclination checks wait out a burn (#582) ----

        private string EventsOf(MissionControl mission)
        {
            return File.ReadAllText(Path.Combine(MissionFolderOf(mission), MissionStore.EventsFile));
        }

        private static GameSnapshot Reading(double throttle, double periapsis = 80000, double inclination = 0)
        {
            return Ascending(s => { s.Throttle = throttle; s.Orbit.Periapsis = periapsis; s.Orbit.Inclination = inclination; });
        }

        [Fact]
        public void APeriapsisCrossedOnlyMidBurnRaisesNoAlertAndLeavesOneEventLine()
        {
            var mission = RunningWith(QuantityCheck("periapsis", "below", -100000));

            mission.Send(Reading(0));
            mission.Send(Reading(1, -150000));
            mission.Send(Reading(1, -250000));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Reading(0, 70000));
            mission.Send(Reading(0, 70000));
            _background.Drain();

            Assert.Equal(0, CrossedLines(mission));
            var recovered = EventsOf(mission).Split('\n').Where(l => l.Contains("telemetry_check_recovered")).ToList();
            Assert.Single(recovered);
            Assert.Contains("periapsis -250,000 m under -100,000 m mid-burn, 70,000 m after it: no Alert.", recovered[0]);
        }

        [Fact]
        public void APeriapsisStillCrossedWhenTheBurnEndsRaisesExactlyOneAlertOnThatReading()
        {
            var mission = RunningWith(QuantityCheck("periapsis", "below", -100000));

            mission.Send(Reading(0));
            mission.Send(Reading(1, -150000));
            mission.Send(Reading(0, -150000));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Reading(0, -150000));
            Assert.Equal(1, CrossedLines(mission, "periapsis"));
            mission.Send(Reading(0, -160000));
            Assert.Equal(1, CrossedLines(mission));
        }

        [Fact]
        public void AnInclinationCheckWaitsOutTheBurnToo()
        {
            var recovers = RunningWith(QuantityCheck("inclination", "above", 5));
            recovers.Send(Reading(0));
            recovers.Send(Reading(1, inclination: 20));
            Assert.Equal(0, CrossedLines(recovers));
            recovers.Send(Reading(0, inclination: 1));
            recovers.Send(Reading(0, inclination: 1));
            Assert.Equal(0, CrossedLines(recovers));
            Assert.Contains("telemetry_check_recovered", EventsOf(recovers));
        }

        [Fact]
        public void AnInclinationStillCrossedWhenTheBurnEndsRaisesAnAlert()
        {
            var mission = RunningWith(QuantityCheck("inclination", "above", 5));
            mission.Send(Reading(0));
            mission.Send(Reading(1, inclination: 20));
            mission.Send(Reading(0, inclination: 20));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(Reading(0, inclination: 20));
            Assert.Equal(1, CrossedLines(mission, "inclination"));
        }

        [Fact]
        public void AnAltitudeCheckStillCrossesMidBurn()
        {
            var mission = RunningWith(AltitudeCheck(100));
            mission.Send(Snapshot(s => { s.Throttle = 1; s.Altitude = 500; }));
            Assert.Equal(1, CrossedLines(mission, "altitude"));
        }

        [Fact]
        public void ADeltaVUsedCheckStillCrossesMidBurn()
        {
            var mission = RunningWith(QuantityCheck("delta_v_used", "above", 50));
            mission.Send(Snapshot(s => { s.Throttle = 1; s.Stages[0].DeltaVVac = 3100; }));
            mission.Send(Snapshot(s => { s.Throttle = 1; s.Stages[0].DeltaVVac = 3100; }));
            Assert.Equal(1, CrossedLines(mission, "delta-v used"));
        }

        [Fact]
        public void ASingleZeroThrottleReadingMidBurnDoesNotReleaseTheHeldChecks()
        {
            var mission = RunningWith(QuantityCheck("periapsis", "below", -100000));

            mission.Send(Reading(0));
            mission.Send(Reading(1, -150000));
            mission.Send(Reading(0, -150000));
            mission.Send(Reading(1, -150000));
            Assert.Equal(0, CrossedLines(mission));
        }

        [Fact]
        public void ARunThatEndsMidBurnTestsHeldChecksAgainstTheFinalReading()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), QuantityCheck("periapsis", "below", -100000)),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.TakeCommands();

            mission.Send(Reading(0));
            mission.Send(Reading(1, -150000));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Contains("periapsis -150,000 m under -100,000 m: not judged: the run ended first [standing]", LastUserText(transport.Requests[2]));
            Assert.Equal(0, CrossedLines(mission));
        }

        // ---- Orbit checks disarm landed, splashed, on the pad or in atmosphere (#604) ----

        private static GameSnapshot InFlight(string situation, double periapsis, double throttle = 0, string body = "Mun", double inclination = 0)
        {
            return Snapshot(s => { s.Situation = situation; s.Body = body; s.Throttle = throttle; s.Orbit.Periapsis = periapsis; s.Orbit.Inclination = inclination; });
        }

        /// <summary>The run's telemetry_check_disarmed lines giving <paramref name="situation"/>; every run here starts on the pad, which disarms orbit checks once.</summary>
        private int DisarmedLines(MissionControl mission, string situation = "")
        {
            return EventsOf(mission).Split('\n').Count(l => l.Contains("telemetry_check_disarmed") && l.Contains("the craft is " + situation));
        }

        [Fact]
        public void APeriapsisCheckArmedInOrbitIsDisarmedByTouchdownWithOneEvent()
        {
            // 2d0c: "periapsis under 5,000 m" for the orbit before the descent burn; after touchdown pe read -199,975 m.
            var mission = RunningWith(QuantityCheck("periapsis", "below", 5000));

            mission.Send(InFlight("ORBITING", 20000));
            mission.Send(InFlight("LANDED", -199975));
            mission.Send(InFlight("LANDED", -199975));
            _background.Drain();

            Assert.Equal(0, CrossedLines(mission));
            var disarmed = EventsOf(mission).Split('\n').Where(l => l.Contains("telemetry_check_disarmed") && l.Contains("LANDED")).ToList();
            Assert.Single(disarmed);
            Assert.Contains("periapsis under 5,000 m: the craft is LANDED.", disarmed[0]);
        }

        [Fact]
        public void APeriapsisCheckArmedAtTheMunDoesNotFireOnKerbinReentry()
        {
            // 2d0c: "periapsis under 10,000 m" meant for the Mun orbit after ascent; Kerbin reentry read pe 5,282 m, FLYING.
            var mission = RunningWith(QuantityCheck("periapsis", "below", 10000));

            mission.Send(InFlight("ORBITING", 15000));
            mission.Send(InFlight("FLYING", 5282, body: "Kerbin"));
            mission.Send(InFlight("FLYING", 5282, body: "Kerbin"));

            Assert.Equal(0, CrossedLines(mission));
            Assert.Equal(1, DisarmedLines(mission, "FLYING"));
        }

        [Fact]
        public void ACheckDisarmedByALandingReArmsOnlyOnTheSafeSideAfterLiftoffAndThenFires()
        {
            var mission = RunningWith(QuantityCheck("periapsis", "below", 10000));

            mission.Send(InFlight("ORBITING", 20000));
            mission.Send(InFlight("LANDED", -199975));
            // Just after liftoff periapsis is far under the surface: the check is still disarmed.
            mission.Send(InFlight("SUB_ORBITAL", -180000, throttle: 1));
            mission.Send(InFlight("SUB_ORBITAL", -180000));
            mission.Send(InFlight("SUB_ORBITAL", -180000));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(InFlight("SUB_ORBITAL", 15000, throttle: 1));
            mission.Send(InFlight("ORBITING", 15000));
            mission.Send(InFlight("ORBITING", 15000));
            Assert.Equal(0, CrossedLines(mission));

            mission.Send(InFlight("ORBITING", 8000));
            Assert.Equal(1, CrossedLines(mission, "periapsis"));
            Assert.Equal(1, DisarmedLines(mission, "LANDED"));
        }

        [Fact]
        public void ASubOrbitalDescentOverAnAirlessBodyStillCrossesAPeriapsisCheck()
        {
            // Haiku flights 22cc and 44e9: a low pass over the Mun is SUB_ORBITAL, and still checked.
            var mission = RunningWith(QuantityCheck("periapsis", "below", 5000));

            mission.Send(InFlight("ORBITING", 20000));
            mission.Send(InFlight("SUB_ORBITAL", -50000));

            Assert.Equal(1, CrossedLines(mission, "periapsis"));
            Assert.Equal(1, DisarmedLines(mission, "PRELAUNCH"));
            Assert.Equal(1, DisarmedLines(mission));
        }

        [Fact]
        public void AnInclinationCheckIsDisarmedOnThePadAndInAtmosphereToo()
        {
            var mission = RunningWith(QuantityCheck("inclination", "above", 5));

            mission.Send(InFlight("PRELAUNCH", -598435, inclination: 20));
            mission.Send(InFlight("FLYING", -500000, inclination: 20));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(InFlight("SUB_ORBITAL", -400000, inclination: 20));
            Assert.Equal(0, CrossedLines(mission));
            mission.Send(InFlight("SUB_ORBITAL", -300000, inclination: 1));
            mission.Send(InFlight("ORBITING", 80000, inclination: 20));
            Assert.Equal(1, CrossedLines(mission, "inclination"));
            // Disarmed from the pad to the safe reading: one line, not one per reading.
            Assert.Equal(1, DisarmedLines(mission));
        }

        [Fact]
        public void ASplashdownDisarmsAFollowUpThresholdToo()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), QuantityCheck("periapsis", "below", 5000)),
                ScriptedResponse.Fixture("mission-alert-carry-on.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            ToRunning(mission, 10m);
            mission.TakeCommands();

            mission.Send(InFlight("ORBITING", 20000));
            mission.Send(InFlight("ORBITING", 3000));
            DrainUntil(mission, v => !v.Busy && v.LatestLine.StartsWith("Alert, periapsis: carry on."));
            // The carry on's follow-up is periapsis under 1,000 m: splashed down it doesn't fire.
            mission.Send(InFlight("SPLASHED", -590000, body: "Kerbin"));
            _background.Drain();

            Assert.Equal(1, CrossedLines(mission));
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(1, DisarmedLines(mission, "SPLASHED"));
        }

        // ---- Delta-v dropped with a stage isn't delta-v used; propulsion checks disarm once no engine is aboard (#605) ----

        /// <summary>The on-pad snapshot out of the atmosphere with <paramref name="deltaV"/> m/s of stock delta-v left.</summary>
        private static GameSnapshot Left(double deltaV, double throttle = 0, bool engine = true)
        {
            return Ascending(s =>
            {
                s.Stages[0].DeltaVVac = deltaV;
                s.Throttle = throttle;
                if (!engine) s.Craft.Parts.RemoveAll(p => p.Tags.Contains("engine"));
            });
        }

        private static string Plan(params string[] checks)
        {
            return "\"telemetry_checks\":[" + string.Join(",", checks) + "],";
        }

        private static string Check(string quantity, string direction, double threshold)
        {
            return "{\"quantity\":\"" + quantity + "\",\"resource\":\"\",\"direction\":\"" + direction + "\",\"threshold\":" + threshold + ",\"why\":\"Test.\"}";
        }

        private int EventLines(MissionControl mission, string kind, string text = "")
        {
            return EventsOf(mission).Split('\n').Count(l => l.Contains("\"" + kind + "\"") && l.Contains(text));
        }

        [Fact]
        public void TheSwivelStackDroppedAtZeroThrottleOnTheLandingLegIsNotDeltaVUsed()
        {
            // 2d0c's landing leg: plan 730 m/s, so the default check is 876 m/s. 692 m/s burned; the Swivel stack dropped at
            // zero throttle took 197 m/s with it, which read as "delta-v used 889 over 876 (default check)".
            var mission = RunningWith(Plan(), 730, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(2900, throttle: 1));
            mission.Send(Left(2900));
            mission.Send(Left(2900));
            mission.Send(new CraftSeparated());
            // Right after staging stock misreads the next stage as empty (#520): that reading isn't the fall.
            mission.Send(Left(878));
            mission.Send(Left(2703));
            mission.Send(Left(2703));
            mission.Send(Left(2600, throttle: 1));
            mission.Send(Left(2311, throttle: 0.9));
            mission.Send(Left(2311, throttle: 0.9));

            Assert.Equal(0, CrossedLines(mission));
            Assert.Equal(692, mission.View.RunProgress.DeltaVUsed);
            Assert.Equal(1, EventLines(mission, "delta_v_rebased", "197 m/s"));
            var saved = mission.ExportStates().Single();
            Assert.Equal(3003, saved.RunStartDeltaVLeft);
            Assert.Equal(197, saved.RunDroppedDeltaV);
        }

        [Fact]
        public void AfterTheLanderStackDropOnTheReturnNoPropulsionCheckCrossesAndEachIsDisarmedOnce()
        {
            // 2d0c's return: plan 950 m/s (default check 1,140 m/s), Claudia's "delta-v left under 250 m/s". After 909 m/s of
            // burns the lander stack, with the last engine and 700 m/s, dropped: stock read 0 left, so everything "used".
            var mission = RunningWith(Plan(Check("delta_v_left", "below", 250)), 950, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(2291, throttle: 1));
            mission.Send(Left(2291));
            mission.Send(Left(2291));
            mission.Send(new CraftSeparated());
            mission.Send(Left(0, engine: false));
            mission.Send(Left(0, engine: false));
            mission.Send(Left(0, engine: false));
            _background.Drain();

            Assert.Equal(0, CrossedLines(mission));
            Assert.Equal(909, mission.View.RunProgress.DeltaVUsed);
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "delta-v left under 250 m/s: no engine aboard."));
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "delta-v used over 1,140 m/s: no engine aboard."));
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "needed-fuel margin under -10%: no engine aboard."));
        }

        [Fact]
        public void AfterDockingTheDeltaVChecksDoNotCrossOnTheMergedVesselsFiguresAndEachDisarmIsLogged()
        {
            // #711's Mission: the craft docked with Kerbin Station Alpha, and the merged vessel read 0 delta-v left, so everything "used".
            var mission = RunningWith(Plan(Check("delta_v_left", "below", 250)), 950, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(3100));
            mission.Send(new VesselsDocked(new SeenVessel { Id = "craft", Name = "Craft", ClaudiaAboard = true },
                new SeenVessel { Id = "station", Name = "Kerbin Station Alpha" }));
            mission.Send(Left(0));
            mission.Send(Left(0));
            mission.Send(Left(0));
            // Back to the craft's own figure later doesn't re-arm them either.
            mission.Send(Left(3100));
            mission.Send(Left(0));
            mission.Send(Left(0));
            _background.Drain();

            Assert.Equal(0, CrossedLines(mission));
            const string why = ": the craft docked with Kerbin Station Alpha, and the merged vessel's delta-v is not the craft's.";
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "delta-v left under 250 m/s" + why));
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "delta-v used over 1,140 m/s" + why));
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "needed-fuel margin under -10%" + why));
        }

        [Fact]
        public void APropulsionCheckStaysDisarmedForTheRestOfTheRunOnceNoEngineIsAboard()
        {
            var mission = RunningWith(Plan(Check("delta_v_used", "above", 500)), 0, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(3100, engine: false));
            // A reading back on the safe side doesn't re-arm it, and an engine read again later doesn't either.
            mission.Send(Left(2000));
            mission.Send(Left(2000));

            Assert.Equal(0, CrossedLines(mission));
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "delta-v used over 500 m/s: no engine aboard."));
        }

        [Fact]
        public void AnRcsThrusterIsNotAnEngine()
        {
            var mission = RunningWith(Plan(Check("delta_v_left", "below", 250)), 0, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Ascending(s =>
            {
                s.Stages[0].DeltaVVac = 100;
                s.Craft.Parts.RemoveAll(p => p.Tags.Contains("engine"));
                s.Craft.Parts.Add(new SnapshotPart { Id = 9, Name = "RCSBlock.v2", Title = "RV-105 RCS Thruster Block", Tags = { "rcs" } });
            }));

            Assert.Equal(0, CrossedLines(mission));
            Assert.Equal(1, EventLines(mission, "telemetry_check_disarmed", "delta-v left under 250 m/s: no engine aboard."));
        }

        [Theory]
        // Haiku 22cc: "delta-v used 483 over 450" during the descent, all of it under throttle.
        [InlineData(375, 3200 - 483)]
        // Haiku 44e9: the landing burn's crossing, under throttle too.
        [InlineData(500, 3200 - 640)]
        public void OverspendUnderThrottleStillCrosses(double planned, double left)
        {
            var mission = RunningWith(Plan(), planned, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(left, throttle: 1));
            mission.Send(Left(left, throttle: 1));

            Assert.Equal(1, CrossedLines(mission, "delta-v used"));
        }

        [Fact]
        public void AStageDroppedUnderThrottleStillCountsAsUsed()
        {
            var mission = RunningWith(Plan(Check("delta_v_used", "above", 450)), 0, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(3000, throttle: 1));
            mission.Send(new CraftSeparated());
            mission.Send(Left(2700, throttle: 1));
            mission.Send(Left(2700, throttle: 1));

            Assert.Equal(1, CrossedLines(mission, "delta-v used"));
            Assert.Equal(0, EventLines(mission, "delta_v_rebased"));
        }

        [Fact]
        public void AStageDroppedAtZeroThrottleIsNotRebasedWhenTheThrottleComesOnBeforeTheReadingsSettle()
        {
            var mission = RunningWith(Plan(Check("delta_v_used", "above", 450)), 0, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(3000));
            mission.Send(new CraftSeparated());
            mission.Send(Left(2700));
            mission.Send(Left(2690, throttle: 1));
            mission.Send(Left(2690, throttle: 1));

            Assert.Equal(1, CrossedLines(mission, "delta-v used"));
            Assert.Equal(0, EventLines(mission, "delta_v_rebased"));
            Assert.Equal(1, EventLines(mission, "delta_v_not_rebased"));
        }

        [Fact]
        public void LaunchStagingOfASpentStageLeavesTheBaselineUnchanged()
        {
            var mission = RunningWith(Plan(), 3170, Snapshot(s => s.Stages[0].DeltaVVac = 3200));

            mission.Send(Left(2400, throttle: 1));
            mission.Send(Left(2400));
            mission.Send(new CraftSeparated());
            mission.Send(Left(2400));
            mission.Send(Left(2400.4));

            Assert.Equal(0, EventLines(mission, "delta_v_rebased"));
            Assert.Equal(3200, mission.ExportStates().Single().RunStartDeltaVLeft);
            Assert.Equal(800, Math.Round(mission.View.RunProgress.DeltaVUsed.Value));
        }

        [Fact]
        public void AQuickloadAfterARebaseKeepsTheRebasedBaseline()
        {
            var mission = RunningWith(Plan(), 730, Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            mission.Send(Left(2900));
            mission.Send(new CraftSeparated());
            mission.Send(Left(2703));
            mission.Send(Left(2703));
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();

            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new MissionsRestored(_save.Path, saved));

            var state = reloaded.ExportStates().Single();
            Assert.Equal(3003, state.RunStartDeltaVLeft);
            Assert.Equal(197, state.RunDroppedDeltaV);
        }

        [Fact]
        public void AQuickloadBetweenTheDropAndTheAgreeingReadingsStillRebasesTheDrop()
        {
            var mission = RunningWith(Plan(), 730, Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            mission.Send(Left(2900));
            mission.Send(new CraftSeparated());
            mission.Send(Left(878));
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();
            Assert.Equal(2900, saved.Single().SeparationBefore);
            Assert.Equal(878, saved.Single().SeparationAfter);

            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new MissionsRestored(_save.Path, saved));
            // The load interrupts the run, so its Debrief finishes the measurement from the craft as it is then.
            reloaded.Send(Left(2703));
            _background.Drain();

            var state = reloaded.ExportStates().Single();
            Assert.Equal(3003, state.RunStartDeltaVLeft);
            Assert.Equal(197, state.RunDroppedDeltaV);
            Assert.Null(state.SeparationBefore);
            Assert.Null(state.SeparationAfter);
        }

        [Fact]
        public void ASaveFromBeforeThePendingMeasurementWasSavedLoadsWithNoneAndNothingIsRebased()
        {
            var mission = RunningWith(Plan(), 730, Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            mission.Send(Left(2900));
            var values = mission.ExportStates().Single().ToValues();
            values.Remove("separationBefore");
            values.Remove("separationAfter");

            var state = MissionSaveState.FromValues(values);

            Assert.Null(state.SeparationBefore);
            Assert.Null(state.SeparationAfter);
        }

        [Fact]
        public void TheThrottleAtTheSeparationEventDecidesNotTheLastSnapshot()
        {
            var mission = RunningWith(Plan(), 730, Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            mission.Send(Left(2900, throttle: 1));
            mission.Send(new CraftSeparated(0));
            mission.Send(Left(2703));
            mission.Send(Left(2703));

            Assert.Equal(1, EventLines(mission, "delta_v_rebased", "197 m/s"));
            Assert.Equal(197, mission.ExportStates().Single().RunDroppedDeltaV);
        }

        [Fact]
        public void AnEventThatCarriesAThrottleAboveZeroIsADropUnderThrust()
        {
            var mission = RunningWith(Plan(), 730, Snapshot(s => s.Stages[0].DeltaVVac = 3200));
            mission.Send(Left(2900));
            mission.Send(new CraftSeparated(1));
            mission.Send(Left(2703));
            mission.Send(Left(2703));

            Assert.Equal(0, EventLines(mission, "delta_v_rebased"));
        }

        // ---- Helpers ----

        private static string QuantityCheck(string quantity, string direction, double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"" + quantity + "\",\"resource\":\"\",\"direction\":\"" + direction
                + "\",\"threshold\":" + threshold + ",\"why\":\"Test.\"}],";
        }

        private MissionControl RunningWith(string check, double expectedDeltaV = 0, GameSnapshot atStart = null)
        {
            var sequence = WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), check);
            var text = Encoding.UTF8.GetString(sequence.Body).Replace("expected_delta_v\\\":0", "expected_delta_v\\\":" + expectedDeltaV);
            sequence.Body = Encoding.UTF8.GetBytes(text);
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                sequence,
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"));
            var mission = Control(transport);
            ToRunning(mission, 10m, atStart);
            return mission;
        }

        private static int CrossedLines(MissionControl mission, string name = "")
        {
            return mission.View.Log.Count(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Telemetry Check crossed: " + name));
        }

        private string MissionFolderOf(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKerman", "missions", mission.View.MissionId);
        }

        private static string DeltaVCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"delta_v_used\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Nothing should burn.\"}],";
        }

        private static string AltitudeCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"altitude\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Too high for a pad test.\"}],";
        }

        /// <summary>The on-pad Game Snapshot, changed.</summary>
        private static GameSnapshot Snapshot(Action<GameSnapshot> change)
        {
            var snapshot = OnPad();
            change(snapshot);
            return snapshot;
        }

        /// <summary>The on-pad Game Snapshot off the pad and out of the atmosphere (SUB_ORBITAL), where orbit checks are armed (#604), changed.</summary>
        private static GameSnapshot Ascending(Action<GameSnapshot> change)
        {
            return Snapshot(s => { s.Situation = "SUB_ORBITAL"; change(s); });
        }

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var escaped = fields.Replace("\"", "\\\"");
            var replaced = text.Replace("\\\"script\\\":", escaped + "\\\"script\\\":");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>The effort in force for a request: the last effort-only system message in its conversation.</summary>
        private static string Effort(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                    .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).Last();
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        /// <param name="atStart">The snapshot the run starts from, its baseline; the on-pad one when null.</param>
        private StartScript ToRunning(MissionControl mission, decimal cap, GameSnapshot atStart = null)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            if (atStart != null) mission.Send(atStart);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
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

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKerman", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        /// <summary>The text of the last user message in a request: the facts the turn gave Claudia.</summary>
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
