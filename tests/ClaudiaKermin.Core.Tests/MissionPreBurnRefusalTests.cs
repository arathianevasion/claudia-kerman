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
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// A run refused before any burn (#579), through the Mission seam: no margin recheck and no Debrief request, the mod's own
    /// Off-Nominal card, its <c>plans.jsonl</c> line with <c>by: "mod"</c>, a user-side fact in the transcript that the log
    /// rebuild makes the card from, and the next request a Sequence Plan carrying the report.
    /// </summary>
    public sealed class MissionPreBurnRefusalTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Reason = "terrain on the approach: no Descent Shape clears it on any pass in the next 48 h";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void A_terrain_refusal_costs_one_request_the_next_Sequence_Plan()
        {
            var rig = new Rig(this);
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Refused(0), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            // The Mission Plan, the first Sequence Plan, then the next one: no Debrief request between, and no margin recheck.
            Assert.Equal(3, rig.Transport.Requests.Count);
            Assert.Single(rig.Router.CheckRequests);
            var text = LastUserText(rig.Transport.Requests[2]);
            Assert.StartsWith(PreBurnRefusal.NextTurnLead, text);
            Assert.Contains("Report (JSON data): ", text);
            Assert.Contains(Reason, text);
            Assert.StartsWith(MissionPrompts.SequenceNotice, Notice(rig.Transport.Requests[2]));
            Assert.DoesNotContain(MissionPrompts.DebriefNotice, Notice(rig.Transport.Requests[2]));
            // The snapshot diet (#256): the turn after a run sends the dynamic snapshot, not the full one again.
            Assert.Contains(SnapshotDiet.DynamicHeader, text);
            Assert.DoesNotContain(MissionPrompts.SnapshotHeader, text);
        }

        [Fact]
        public void The_fact_is_a_user_message_after_the_last_reply_so_the_prefix_and_order_stay_as_they_were()
        {
            var rig = new Rig(this);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Refused(0), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            var before = Messages(rig.Transport.Requests[1]);
            var after = Messages(rig.Transport.Requests[2]);
            // Everything the Sequence Plan request sent is sent again unchanged, then its reply, then the fact and the new turn.
            var sent = before.Count(m => m.Role != "system" || m.Text.Length == 0);
            Assert.Equal(before.Take(sent).Select(m => m.Role + m.Text), after.Take(sent).Select(m => m.Role + m.Text));
            var tail = after.Skip(sent).ToList();
            var assistant = tail.FindIndex(m => m.Role == "assistant");
            Assert.True(assistant >= 0);
            Assert.Equal(new[] { "user", "user", "system" }, tail.Skip(assistant + 1).Select(m => m.Role));
            Assert.StartsWith(PreBurnRefusal.FactPrefix, tail[assistant + 1].Text);
            Assert.Equal(1, after.Count(m => m.Role == "assistant") - before.Count(m => m.Role == "assistant"));
        }

        [Fact]
        public void The_mod_writes_an_Off_Nominal_card_with_the_reason_the_margin_and_the_next_Sequence_Plan()
        {
            var rig = new Rig(this);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Refused(0), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            var card = rig.Mission.View.Log.Single(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Debrief: Off-Nominal", card.Title);
            Assert.Equal("Off-Nominal", card.Outcome);
            Assert.Equal(PreBurnRefusal.CardWhy, card.Why);
            Assert.StartsWith("Refused before any burn: " + Reason, card.Text);
            var detail = card.Debrief;
            Assert.Equal("Off-Nominal", detail.Rating);
            Assert.Equal("Corrective sequence next: " + PreBurnRefusal.FollowUpText, detail.FollowUp);
            // The margin bar and the Margin check line are the mod's (#574), from the snapshot.
            Assert.NotNull(detail.Margin);
            Assert.NotEqual("", detail.RecheckSummary);
            // The Mission Goal's steps as the Mission Plan stored them (#581): the refusal ticks none.
            Assert.Equal(new[] { "Prove the link", "Go on" }, detail.Checklist.Select(i => i.Item));
            Assert.Equal(0, detail.ChecklistDone);
        }

        [Fact]
        public void Its_plans_jsonl_line_is_a_Debrief_by_the_mod_with_no_request()
        {
            var rig = new Rig(this);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Refused(0), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            var lines = File.ReadAllLines(Path.Combine(rig.Folder, PlanLog.FileName)).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement).Where(l => l.GetProperty("event").GetString() == "plan").ToList();
            var debrief = lines[0].GetProperty("debrief");
            Assert.Equal("Off-Nominal", debrief.GetProperty("outcome").GetString());
            Assert.Equal("mod", debrief.GetProperty("by").GetString());
            Assert.Equal(0, debrief.GetProperty("requests").GetInt32());
            Assert.Equal(0m, debrief.GetProperty("cost").GetDecimal());
            // The next Sequence Plan's own line follows, not yet debriefed.
            Assert.False(lines[1].TryGetProperty("debrief", out _));
        }

        [Fact]
        public void MissionLogRebuild_makes_the_mod_written_card_again_from_the_transcript()
        {
            var rig = new Rig(this);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Refused(0), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            var live = rig.Mission.View.Log.Single(e => e.Kind == LogKind.Debrief);

            var rebuilt = MissionLogRebuild.From(MissionStore.ReadTranscript(rig.Folder), Goal, "", new PlayerConfig());

            var card = rebuilt.Single(e => e.Kind == LogKind.Debrief);
            Assert.Equal(live.Title, card.Title);
            Assert.Equal(live.Text, card.Text);
            Assert.Equal(live.Why, card.Why);
            Assert.Equal(live.Outcome, card.Outcome);
            Assert.Equal(live.Debrief.FollowUp, card.Debrief.FollowUp);
            Assert.Equal(live.Debrief.OutcomeVsTarget, card.Debrief.OutcomeVsTarget);
            // Between the two Sequence Plan cards, as it was live.
            var kinds = rebuilt.Where(e => e.Kind == LogKind.SequencePlan || e.Kind == LogKind.Debrief).Select(e => e.Kind).ToList();
            Assert.Equal(new[] { LogKind.SequencePlan, LogKind.Debrief, LogKind.SequencePlan }, kinds);
        }

        [Fact]
        public void The_card_has_the_steps_a_Debrief_before_it_ticked_and_the_rebuild_has_them_too()
        {
            var rig = new Rig(this, debriefFirst: true, debriefTicks: "[1]");
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Refused(817), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            rig.Mission.Send(new Approve());
            var second = rig.Mission.TakeCommands().OfType<StartScript>().Single();
            rig.Mission.Send(new KosRunEnded(Refused(0), second.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy && v.Log.Count(e => e.Kind == LogKind.Debrief) == 2);

            var live = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal(new[] { true, false }, live.Checklist.Select(i => i.Done));
            var rebuilt = MissionLogRebuild.From(MissionStore.ReadTranscript(rig.Folder), Goal, "", new PlayerConfig()).Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal(new[] { true, false }, rebuilt.Checklist.Select(i => i.Done));
            Assert.Equal(new[] { true, false }, rig.Mission.View.GoalSteps.Select(i => i.Done));
        }

        [Fact]
        public void A_run_that_burned_gets_Claudias_Debrief_as_before()
        {
            var rig = new Rig(this, debriefFirst: true);
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Refused(817), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            Assert.StartsWith(MissionPrompts.DebriefNotice, Notice(rig.Transport.Requests[2]));
            Assert.DoesNotContain(PreBurnRefusal.FactPrefix, rig.Transport.Requests[3].Body);
        }

        [Fact]
        public void Mod_debriefs_refusals_false_sends_a_refusal_to_Claudias_Debrief()
        {
            var rig = new Rig(this, debriefFirst: true, modDebriefs: false);
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Refused(0), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            Assert.StartsWith(MissionPrompts.DebriefNotice, Notice(rig.Transport.Requests[2]));
            Assert.DoesNotContain(PreBurnRefusal.FactPrefix, rig.Transport.Requests[3].Body);
        }

        [Fact]
        public void The_config_line_reads_and_defaults_on()
        {
            Assert.True(new PlayerConfig().ModDebriefsRefusals);
            var dir = Path.Combine(_save.Path, "config");
            Assert.True(PlayerConfig.LoadOrCreate(dir).ModDebriefsRefusals);
            Assert.Contains("mod_debriefs_refusals = true", File.ReadAllText(Path.Combine(dir, PlayerConfig.FileName)));
            File.WriteAllText(Path.Combine(dir, PlayerConfig.FileName), "mod_debriefs_refusals = false\n");
            Assert.False(PlayerConfig.LoadOrCreate(dir).ModDebriefsRefusals);
        }

        // ---- Helpers ----

        /// <summary>A land block's refusal as 3dc5's were reported, with <paramref name="dvUsed"/> burned.</summary>
        private static RunOutcome Refused(double dvUsed)
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object>
            {
                { "success", false }, { "reason", Reason }, { "dv_used", dvUsed }, { "duration", 5.9 },
            }, "", "", false);
        }

        private const string DebriefReply =
            "{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
            + "\"debrief\":{\"outcome\":\"Off-Nominal\",\"why\":\"It burned.\",\"what_happened\":\"It burned.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\"}}";

        private sealed class Message
        {
            public string Role;
            public string Text;
        }

        private static List<Message> Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m =>
                {
                    var content = m.GetProperty("content");
                    var text = content.ValueKind == JsonValueKind.String ? content.GetString()
                        : string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
                    return new Message { Role = m.GetProperty("role").GetString(), Text = text };
                }).ToList();
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            return Messages(request).Last(m => m.Role == "user").Text;
        }

        /// <summary>The request's mod notice: the system message after its turn's user text.</summary>
        private static string Notice(HttpRequestSpec request)
        {
            return Messages(request).Last().Text;
        }

        /// <summary>A Mission with a two-sequence plan and one Sequence Plan, then (for a Debrief by Claudia) her Debrief, then the next Sequence Plan.</summary>
        private sealed class Rig
        {
            private readonly MissionPreBurnRefusalTests _tests;
            public readonly StreamingFakeTransport Transport;
            public readonly CraftCheckRouter Router;
            public readonly MissionControl Mission;

            /// <param name="debriefTicks">Claudia's Debrief's goal_steps_done, as JSON; none when null.</param>
            public Rig(MissionPreBurnRefusalTests tests, bool debriefFirst = false, bool modDebriefs = true, string debriefTicks = null)
            {
                _tests = tests;
                Transport = new StreamingFakeTransport(PlanReply(), SequenceReply());
                if (debriefFirst) Transport.Add(Reply(debriefTicks == null ? DebriefReply : DebriefReply.Replace("\"eva_science\":\"\"", "\"eva_science\":\"\",\"goal_steps_done\":" + debriefTicks)));
                Transport.Add(SequenceReply());
                // A second Sequence Plan, for a test that flies again.
                Transport.Add(SequenceReply());
                Router = new CraftCheckRouter(Transport);
                var client = new ClaudeClient(Router, () => "sk-ant-test", l => { });
                client.Timing.Wait = (d, c) => { };
                var config = new PlayerConfig { CheckDurationPercent = 1000, MarginRecheck = MarginRecheckMode.Always, ModDebriefsRefusals = modDebriefs };
                Mission = new MissionControl(client, tests._background, config, () => true, new PassingScriptCheck());
                Mission.Send(new SaveLoaded(tests._save.Path));
                Mission.Send(new SceneChanged(GameScene.Flight));
                Mission.Send(OnPad());
            }

            private static ScriptedResponse PlanReply()
            {
                return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                    + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                    + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                    + "{\"name\":\"Second leg\",\"intent\":\"Go on.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                    + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1,\"goal_steps\":[\"Prove the link\",\"Go on\"]}}");
            }

            private static ScriptedResponse SequenceReply()
            {
                return ScriptedResponse.Fixture("mission-sequence-plan.sse");
            }

            /// <summary>A reply as the stream carries it: the JSON as one text block, priced by its usage.</summary>
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

            private static GameSnapshot OnPad()
            {
                return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            }

            public StartScript RunToRunning()
            {
                var snapshot = OnPad();
                Mission.Send(snapshot);
                CraftCheckRouter.StartChecked(Mission, _tests._background, snapshot, new StartMission(Goal, 10m, Sonnet));
                DrainUntil(v => v.Stage == MissionStage.AwaitingPlanApproval);
                Mission.Send(new Approve());
                DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval);
                Mission.Send(new Approve());
                Assert.Equal(MissionStage.Running, Mission.View.Stage);
                return Mission.TakeCommands().OfType<StartScript>().Single();
            }

            public void DrainUntil(Func<MissionView, bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!condition(Mission.View) && DateTime.UtcNow < deadline)
                {
                    _tests._background.Drain();
                    Thread.Sleep(2);
                }
                _tests._background.Drain();
                Assert.True(condition(Mission.View), "never reached; stage is " + Mission.View.Stage + " " + Mission.View.PauseReason);
            }

            public string Folder
            {
                get { return Path.Combine(_tests._save.Path, "ClaudiaKermin", "missions", Mission.View.MissionId); }
            }
        }
    }
}
