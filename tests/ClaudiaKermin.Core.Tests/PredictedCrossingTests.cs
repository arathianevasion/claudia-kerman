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
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// #674: a delta-v left Telemetry Check the Sequence Plan's own <c>expected_delta_v</c> is certain to cross. The pure rule,
    /// then the one correction through <see cref="MissionControl"/> (the on-pad snapshot starts at 3,200 m/s).
    /// </summary>
    public sealed class PredictedCrossingTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The rule ----

        private static TelemetryCheck Left(double threshold, bool below = true, bool isDefault = false)
        {
            return new TelemetryCheck { Quantity = TelemetryQuantity.DeltaVLeft, Below = below, Threshold = threshold, Default = isDefault };
        }

        [Fact]
        public void AThresholdAboveWhatThePlanLeavesIsAPredictedCrossingAndOneBelowItIsNot()
        {
            var found = PredictedCrossings.Find(new[] { Left(2000) }, 686, 2290);
            var crossing = Assert.Single(found);
            Assert.Equal(1604, crossing.PredictedLeft, 6);
            Assert.Empty(PredictedCrossings.Find(new[] { Left(1500) }, 686, 2290));
            Assert.Single(PredictedCrossings.Find(new[] { Left(5000) }, 686, 2290));
        }

        [Fact]
        public void ADefaultAnAboveCheckAndAnotherQuantityAreNeverPredictedCrossings()
        {
            var checks = new[]
            {
                Left(2000, isDefault: true),
                Left(2000, below: false),
                new TelemetryCheck { Quantity = TelemetryQuantity.DeltaVUsed, Below = true, Threshold = 5000 },
                new TelemetryCheck { Quantity = TelemetryQuantity.Altitude, Below = true, Threshold = 5000 },
            };
            Assert.Empty(PredictedCrossings.Find(checks, 686, 2290));
        }

        [Fact]
        public void NothingIsPredictedWithoutAReadableStartOrAnExpectedDeltaV()
        {
            Assert.Empty(PredictedCrossings.Find(new[] { Left(2000) }, 686, null));
            Assert.Empty(PredictedCrossings.Find(new[] { Left(2000) }, 0, 2290));
            Assert.Empty(PredictedCrossings.Find(new[] { Left(2000) }, -5, 2290));
        }

        // ---- Setup ----

        private MissionControl Control(StreamingFakeTransport transport, IScriptCheck check = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, check ?? new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
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

        /// <summary>The Mission Plan approved, the Sequence Plan turn answered with <paramref name="transport"/>'s next replies.</summary>
        private void ToSequencePlan(MissionControl mission, bool auto = false, GameSnapshot beforeApproval = null)
        {
            mission.Send(new StartMission(Goal, 25m, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            if (auto) mission.Send(new SetAutoMode(true));
            if (beforeApproval != null) mission.Send(beforeApproval);
            mission.Send(new Approve());
        }

        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = new StringBuilder();
            sse.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3900,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n");
            sse.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            sse.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n");
            sse.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            sse.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":90,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3900,\"output_tokens\":90}]}}\n\n");
            sse.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        private static ScriptedResponse PlanReply()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Second leg\",\"intent\":\"Go on.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1}}");
        }

        /// <summary>A Sequence Plan expecting 1,200 m/s with one "delta_v_left below <paramref name="threshold"/>" check.</summary>
        private static ScriptedResponse Sequence(double threshold, string script = "print \"hop\".")
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\","
                + "\"mission_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"name\":\"Hop\",\"intent\":\"Hop.\",\"why\":\"Because.\",\"target\":\"\",\"tolerances\":\"\",\"expected_delta_v\":1200,\"expected_duration_s\":60,"
                + "\"abort_conditions\":[],\"risks\":[],\"telemetry_checks\":[{\"quantity\":\"delta_v_left\",\"direction\":\"below\",\"threshold\":"
                + threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"resource\":\"\",\"why\":\"Keep a reserve.\"}],"
                + "\"script\":" + JsonSerializer.Serialize(script) + ",\"custom_script_reason\":\"No block hops.\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1,\"manual\":false,\"vessel\":\"\"}}");
        }

        private List<JsonElement> PlanLines(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "plans.jsonl");
            return File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).Where(l => l.GetProperty("event").GetString() == "plan").ToList();
        }

        private sealed class QuicksaveCheck : IScriptCheck
        {
            public ScriptCheckResult Check(string script)
            {
                return script.ToUpperInvariant().Contains("QUICKSAVE")
                    ? new ScriptCheckResult(null, new List<string> { "line 1: :QUICKSAVE (quicksave)" })
                    : ScriptCheckResult.Pass();
            }
        }

        // ---- Through the Mission ----

        [Fact]
        public void APlanThatCrossesItsOwnCheckGoesBackOnceNamingTheFiguresAndTheCorrectedPlanIsOfferedUnlogged()
        {
            // 3,200 - 1,200 leaves 2,000; the first plan's threshold is 2,500.
            var transport = new StreamingFakeTransport(PlanReply(), Sequence(2500), Sequence(1500));
            var mission = Control(transport);
            ToSequencePlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(3, transport.Requests.Count);
            var body = transport.Requests[2].Body;
            Assert.Contains("delta-v left under 2,500 m/s", body);
            Assert.Contains("3,200 m/s", body);
            Assert.Contains("1,200 m/s", body);
            Assert.Contains("2,000 m/s", body);
            var line = Assert.Single(PlanLines(mission));
            Assert.True(line.GetProperty("script_rewrite").GetBoolean());
            Assert.DoesNotContain(line.GetProperty("lints").EnumerateArray(), l => l.GetString().Contains("predicted crossing"));
        }

        [Fact]
        public void AStillCrossingCorrectedPlanIsOfferedWithNoSecondCorrectionAndTheWarningIsLogged()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Sequence(2500), Sequence(2400));
            var mission = Control(transport);
            ToSequencePlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(3, transport.Requests.Count);
            var line = Assert.Single(PlanLines(mission));
            Assert.Contains(line.GetProperty("lints").EnumerateArray(), l => l.GetString().StartsWith("predicted crossing: delta-v left under 2,400 m/s"));
        }

        [Fact]
        public void AScriptThatFailsAndACrossingGetOneCorrectionNamingBoth()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Sequence(2500, "quicksave."), Sequence(1500));
            var mission = Control(transport, new QuicksaveCheck());
            ToSequencePlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(3, transport.Requests.Count);
            var body = transport.Requests[2].Body;
            Assert.Contains("QUICKSAVE", body);
            Assert.Contains("delta-v left under 2,500 m/s", body);
        }

        [Fact]
        public void UnderAutoModeACrossingPlanIsNotApprovedBeforeItsCorrection()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Sequence(2500), Sequence(1500));
            var mission = Control(transport);
            ToSequencePlan(mission, auto: true);
            DrainUntil(mission, v => v.Stage == MissionStage.Running);

            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("delta-v left under 2,500 m/s", transport.Requests[2].Body);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void AnUnreadableStartOffersThePlanAsToday()
        {
            var snapshot = OnPad();
            snapshot.DeltaVSimulator = false;
            var transport = new StreamingFakeTransport(PlanReply(), Sequence(2500));
            var mission = Control(transport);
            ToSequencePlan(mission, beforeApproval: snapshot);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.False(Assert.Single(PlanLines(mission)).GetProperty("script_rewrite").GetBoolean());
        }
    }
}
