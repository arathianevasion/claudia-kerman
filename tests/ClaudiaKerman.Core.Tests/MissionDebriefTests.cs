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
    /// The full Debrief (#56) and its margin recheck (#101): the run's event log, the extended Debrief reply, the card
    /// the log drawer shows, the margin bar, and what follows a Failed or Off-Nominal Debrief. Driven through the Mission
    /// seam with hand-written streams.
    /// </summary>
    public sealed class MissionDebriefTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const double ApprovedAt = 1203.48;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The event log ----

        [Fact]
        public void TheEventLogKeepsEventsInOrderAndCountsTheSameEventTwiceInARowOnce()
        {
            var log = new RunEventLog();
            log.Add(110, RunEventKind.Staging, "stage 2 fired");
            log.Add(115, RunEventKind.Overheating, "Mk1 Command Pod at 93% of its limit");
            log.Add(116, RunEventKind.Overheating, "Mk1 Command Pod at 93% of its limit");
            log.Add(120, RunEventKind.Science, "Mystery Goo on Kerbin's surface: 3.0 science");
            log.Add(121, RunEventKind.PartLost, "  ");

            Assert.Equal(3, log.Count);
            Assert.Equal(new[] { "+10 s staging: stage 2 fired", "+15 s overheating: Mk1 Command Pod at 93% of its limit" }, log.EventLines(100));
            Assert.Equal(new[] { "+20 s Mystery Goo on Kerbin's surface: 3.0 science" }, log.ScienceLines(100));
        }

        [Fact]
        public void TheEventLogIsBoundedAndSaysHowManyEventsItLeftOut()
        {
            var log = new RunEventLog();
            for (var i = 0; i < RunEventLog.Max + 5; i++) log.Add(i, RunEventKind.PartLost, "part " + i);

            Assert.Equal(RunEventLog.Max, log.Count);
            Assert.Equal(5, log.Dropped);
            Assert.Equal("(5 more events not listed)", log.EventLines(0).Last());
            log.Clear();
            Assert.Equal(0, log.Count);
            Assert.Equal("", log.DebriefText(0));
        }

        // ---- The margin bar ----

        [Fact]
        public void TheMarginBarSaysEnoughTightOrShortAgainstTheRestOfThePlan()
        {
            Assert.Equal(MarginLevel.Enough, MarginBar.Compute(1200, 1000, 10).Level);
            Assert.Equal(MarginLevel.Tight, MarginBar.Compute(1050, 1000, 10).Level);
            Assert.Equal(MarginLevel.Short, MarginBar.Compute(900, 1000, 10).Level);

            var tight = MarginBar.Compute(1050, 1000, 10);
            Assert.Equal(1100, tight.Needed, 6);
            Assert.Equal(1050.0 / 1100, tight.Fraction, 6);
            Assert.Equal(5, tight.ActualPercent.Value, 6);
            Assert.Equal("1,050 m/s left against 1,000 m/s still to fly (+5%; 10% wanted): tight.", tight.Describe());
            Assert.Equal(1, MarginBar.Compute(5000, 1000, 10).Fraction);
        }

        [Fact]
        public void ThereIsNoMarginBarWhenNothingIsLeftToFlyOrTheSimulatorGaveNoFigure()
        {
            Assert.Null(MarginBar.Compute(1200, 0, 10));
            Assert.Null(MarginBar.Compute(null, 1000, 10));
        }

        // ---- The reply ----

        [Fact]
        public void TheSchemaRequiresEveryDebriefFieldTheMissionAsksFor()
        {
            // A Debrief request sends the one Mission schema (ADR 0012).
            using (var request = JsonDocument.Parse(MissionReply.MissionSchemaJson))
            {
                var schema = request.RootElement;
                var debrief = schema.GetProperty("properties").GetProperty("debrief").GetProperty("anyOf")[0];
                var required = debrief.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
                var properties = debrief.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
                foreach (var name in new[] { "outcome_vs_target", "plan_margin", "science", "goal_steps_done", "follow_up", "follow_up_text" })
                {
                    Assert.Contains(name, required);
                    Assert.Contains(name, properties);
                }
                Assert.Equal(required.OrderBy(n => n), properties.OrderBy(n => n));
            }
        }

        // ---- What the Debrief request carries ----

        [Fact]
        public void TheDebriefRequestCarriesTheRunsEventsTheRestOfThePlanAndTheMarginRecheck()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = Reply("{\"summary\":\"3,200 m/s left against 1,000 m/s to fly: plenty.\",\"findings\":[]}");
            var start = rig.RunToRunning();

            rig.Mission.Send(new RunEventSeen(RunEventKind.Staging, "stage 1 fired", ApprovedAt + 12));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Science, "Mystery Goo on the pad: 3.0 science", ApprovedAt + 20));
            rig.Mission.Send(new KosBlockEnded("launch", new Dictionary<string, object> { { "success", false }, { "reason", "abort min_ec_pct" } }, start.RunId));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("+12 s staging: stage 1 fired", request);
            Assert.Contains("+20 s Mystery Goo on the pad: 3.0 science", request);
            Assert.Contains("block aborted: Building Block launch: failed (abort min_ec_pct)", request);
            Assert.Contains("Maneuver Sequences still to fly in the Mission Plan (quoted)", request);
            Assert.Contains("Second leg", request);
            Assert.Contains("The mod's margin recheck", request);
            Assert.Contains("3,200 m/s left against 1,000 m/s to fly: plenty.", request);
        }

        /// <summary>#867: a run that leaves the craft in an orbit crossing a moon (908b's, round Duna): the Debrief's request and card carry the fact, the rating Claudia's.</summary>
        [Fact]
        public void TheDebriefAfterARunEndingInAnOrbitThatCrossesAMoonCarriesTheFact()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            var duna = MoonCrossingTests.DunaOrbit(ut: OnPad().Ut);
            rig.Mission.Send(Craft(s =>
            {
                s.Situation = "ORBITING";
                s.Body = "Duna";
                s.Orbit = TransferRig.Elements(duna.Orbit, duna.Body);
                s.Bodies = duna.Bodies;
            }));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("orbit crosses a moon: The orbit round Duna crosses Ike's sphere of influence (from 1,734 km up)", request);
            Assert.Contains("fly change_orbit set_ap 1,520 km (about 26 m/s; a circle costs about 260 m/s) before waiting; a plan that waits in it is blocked", request);
            var card = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Contains(card.Events, e => e.Contains("orbit crosses a moon: The orbit round Duna crosses Ike's"));
            Assert.Equal("Nominal", card.Rating);
        }

        [Fact]
        public void TheDebriefAfterARunEndingInASafeOrbitSaysNothingOfMoons()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            var duna = MoonCrossingTests.DunaOrbit(apAlt: 1520000, ut: OnPad().Ut);
            rig.Mission.Send(Craft(s =>
            {
                s.Situation = "ORBITING";
                s.Body = "Duna";
                s.Orbit = TransferRig.Elements(duna.Orbit, duna.Body);
                s.Bodies = duna.Bodies;
            }));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.DoesNotContain("crosses a moon", LastUserText(rig.Transport.Requests[2]));
        }

        // ---- The blocks trail (#609) ----

        [Fact]
        public void TheDebriefRequestListsEveryBlocksResultWhateverTheScriptReported()
        {
            // 20ac's Sequence Plan 3: transfer_to's encounter was lost when the script reported a lexicon of its own.
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3200);
            // The ejection burned: not a refusal before any burn, which the mod would Debrief itself.
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2340));

            rig.Mission.Send(new KosBlockEnded("transfer_to", new Dictionary<string, object>
            {
                { "success", true }, { "reason", "done" }, { "encounter_ut", 1203456.0 }, { "target_pe_actual", 41200.0 }, { "ejection_wait", 300.0 },
            }, start.RunId));
            var own = new Dictionary<string, object> { { "success", false }, { "reason", "transfer ended outside the Mun SOI" } };
            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, own, "", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("Report (JSON data): {\"success\":false,\"reason\":\"transfer ended outside the Mun SOI\"}\n" + BlockTrail.LinePrefix
                + "[{\"block\":\"transfer_to\",\"success\":true,\"reason\":\"done\",\"encounter_ut\":1203456,\"target_pe_actual\":41200}]", request);
        }

        [Fact]
        public void ThePlansDebriefRecordCarriesTheBlocksTrail()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            var launch = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "duration", 80.0 } };
            var circularize = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "duration", 41.0 }, { "ecc", 0.002 } };

            rig.Mission.Send(new KosBlockEnded("launch", launch, start.RunId));
            rig.Mission.Send(new KosBlockEnded("circularize", circularize, start.RunId));
            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, circularize, "", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            // The Debrief text leaves out the block the script reported; the log keeps every block.
            Assert.Contains(BlockTrail.LinePrefix + "[{\"block\":\"launch\",\"success\":true,\"reason\":\"done\"}]", LastUserText(rig.Transport.Requests[2]));
            var path = Path.Combine(_save.Path, "ClaudiaKerman", "missions", rig.Mission.View.MissionId, "plans.jsonl");
            var plan = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).Single(l => l.GetProperty("event").GetString() == "plan" && l.TryGetProperty("debrief", out _));
            var blocks = plan.GetProperty("debrief").GetProperty("blocks").EnumerateArray().ToList();
            Assert.Equal(new[] { "launch", "circularize" }, blocks.Select(b => b.GetProperty("block").GetString()));
            Assert.Equal(0.002, blocks[1].GetProperty("ecc").GetDouble());
        }

        // ---- The whole run's delta-v (#384) ----

        [Fact]
        public void TheDebriefRequestCarriesTheDeltaVTheWholeRunUsedFromStock()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3913.3);

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2875.3));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Delta-v used by the whole run (all blocks, the mod's own reading from stock; the report's dv_used may be one block's): 1,038 m/s (left at start 3,913 m/s, now 2,875 m/s).", LastUserText(rig.Transport.Requests[2]));
        }

        private const string RunTotalLabel = "Delta-v used by the whole run (all blocks, the mod's own reading from stock; the report's dv_used may be one block's): ";

        [Fact]
        public void TheRunTotalComesOnceRightAfterTheReportAndBlocksTrailAndBeforeTheGameSnapshot()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3913.3);
            var launch = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "duration", 80.0 } };
            var own = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "dv_used", 1038.0 } };

            rig.Mission.Send(new KosBlockEnded("launch", launch, start.RunId));
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2875.3));
            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, own, "", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            var trail = request.IndexOf(BlockTrail.LinePrefix, StringComparison.Ordinal);
            var line = request.IndexOf(RunTotalLabel, StringComparison.Ordinal);
            Assert.True(trail >= 0 && line > trail);
            Assert.Equal(line, request.IndexOf("\n", trail, StringComparison.Ordinal) + 1);
            Assert.Equal(line, request.LastIndexOf(RunTotalLabel, StringComparison.Ordinal));
            var snap = request.IndexOf("Game Snapshot", line, StringComparison.Ordinal);
            Assert.True(snap > line, request);
            Assert.DoesNotContain("differs", request);
        }

        [Fact]
        public void AReportsDvUsedFarFromTheRunTotalIsFlaggedAndThePlansDebriefRecordsTheTotal()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3000);
            var own = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "dv_used", 1.5 } };

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2841));
            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, own, "", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains(RunTotalLabel + "159 m/s (left at start 3,000 m/s, now 2,841 m/s). The report's dv_used 2 differs.", LastUserText(rig.Transport.Requests[2]));
            var path = Path.Combine(_save.Path, "ClaudiaKerman", "missions", rig.Mission.View.MissionId, "plans.jsonl");
            var plan = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).Single(l => l.GetProperty("event").GetString() == "plan" && l.TryGetProperty("debrief", out _));
            Assert.Equal(159, plan.GetProperty("debrief").GetProperty("run_dv_used").GetDouble());
        }

        [Fact]
        public void ALastEngineDroppedAtZeroThrottleAfterABurnStillGivesTheRunTotalWhateverTheReportSays()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 1000);
            var own = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "dv_used", 0.0 }, { "dv_remaining", 0.0 } };

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 730));
            rig.Mission.Send(new CraftSeparated());
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 0));
            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, own, "", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains(RunTotalLabel + "270 m/s (left at start 1,000 m/s, now 0 m/s; 730 m/s dropped with stages at zero throttle isn't counted). "
                + "The report's dv_used 0 differs.", request);
            Assert.DoesNotContain("The report's dv_remaining", request);
        }

        // ---- The Chute Groups (ADR 0019) ----

        private static Action<GameSnapshot> WithChute(ClaudiaKerman.Core.Tools.ReentryChuteState state)
        {
            return s => s.Craft.Parts.Add(new SnapshotPart
            {
                Id = 4242, ParentId = s.Craft.Parts[0].Id, Name = "parachuteSingle", Title = "Mk16 Parachute", MassT = 0.1, Stage = -1,
                Tags = new List<string> { "parachute" }, Chute = new ClaudiaKerman.Core.Tools.ReentryChute { State = state, FullArea = 337 },
            });
        }

        [Fact]
        public void TheChuteGroupsAreWorkedOutAtMissionPlanApprovalAndAgainAtEachDebriefFromTheCraftAsItIs()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(WithChute(ClaudiaKerman.Core.Tools.ReentryChuteState.Stowed));
            var approved = rig.Mission.View.ChuteGroups;
            Assert.NotNull(approved);
            Assert.Equal(new uint[] { 4242 }, approved.Chutes.Select(c => c.PartId));

            // The run opened the chute: the Debrief's groups have none left to give.
            rig.Mission.Send(Craft(WithChute(ClaudiaKerman.Core.Tools.ReentryChuteState.Deployed)));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var debriefed = rig.Mission.View.ChuteGroups;
            Assert.NotSame(approved, debriefed);
            Assert.Empty(debriefed.Chutes);
        }

        // ---- Data kept on board (#612) ----

        [Fact]
        public void TheDebriefNoticeSaysTheDataIsAboardOnlyWhenAScienceCallKeptSome()
        {
            var kept = new Rig(this, DebriefJson("Nominal", ""));
            var start = kept.RunToRunning();
            kept.Mission.Send(new KosBlockEnded("science", ScienceResult(("crewReport", "kept")), start.RunId));
            kept.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            kept.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("The data is aboard; sending it needs no new landing.", kept.Transport.Requests[2].Body);

            var sent = new Rig(this, DebriefJson("Nominal", ""));
            var sentStart = sent.RunToRunning();
            sent.Mission.Send(new KosBlockEnded("science", new Dictionary<string, object> { { "success", true } }, sentStart.RunId));
            sent.Mission.Send(new KosRunEnded(Finished(), sentStart.RunId));
            sent.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.DoesNotContain("no new landing", sent.Transport.Requests[2].Body);
        }

        private static Dictionary<string, object> ScienceResult(params (string Experiment, string Transmit)[] entries)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "results", entries.Select(e => (object)new Dictionary<string, object> { { "experiment", e.Experiment }, { "transmit", e.Transmit } }).ToList() },
            };
        }

        private static string DebriefAfterScience(MissionDebriefTests test, params Dictionary<string, object>[] calls)
        {
            var rig = new Rig(test, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            foreach (var call in calls) rig.Mission.Send(new KosBlockEnded("science", call, start.RunId));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);
            return rig.Transport.Requests[2].Body;
        }

        [Fact]
        public void ALaterScienceCallThatTransmitsTheKeptDataTakesTheAboardNoticeAway()
        {
            var body = DebriefAfterScience(this, ScienceResult(("crewReport", "kept")), ScienceResult(("crewReport", "transmitted")));
            Assert.DoesNotContain("no new landing", body);
        }

        [Fact]
        public void TheAboardNoticeStaysWhileAnotherExperimentIsStillKept()
        {
            var body = DebriefAfterScience(this, ScienceResult(("crewReport", "kept"), ("temperatureScan", "kept")), ScienceResult(("temperatureScan", "transmitted")));
            Assert.Contains("The data is aboard; sending it needs no new landing.", body);
        }

        [Fact]
        public void DeltaVDroppedWithAStageAtZeroThrottleIsLeftOutOfTheRunsDeltaVUsedAndTheLineSaysSo()
        {
            // 2d0c's landing leg (#605): 692 m/s burned, and the Swivel stack took 197 m/s with it.
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3200);

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2900));
            rig.Mission.Send(new CraftSeparated());
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2703));
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2703));
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2311));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Delta-v used by the whole run (all blocks, the mod's own reading from stock; the report's dv_used may be one block's): 692 m/s (left at start 3,200 m/s, now 2,311 m/s; 197 m/s dropped with stages at zero "
                + "throttle isn't counted).", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void AStageDroppedJustBeforeTheRunEndsIsStillLeftOutOfTheRunsDeltaVUsed()
        {
            // 2d0c's return dropped the lander stack after the last burn: the script can end before two readings agree.
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3200);

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2291));
            rig.Mission.Send(new CraftSeparated());
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 0));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Delta-v used by the whole run (all blocks, the mod's own reading from stock; the report's dv_used may be one block's): 909 m/s (left at start 3,200 m/s, now 0 m/s; 2,291 m/s dropped with stages at zero "
                + "throttle isn't counted).", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void ADeltaVUsedBelowZeroIsShownAsZero()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 2000);

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2100));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Delta-v used by the whole run (all blocks, the mod's own reading from stock; the report's dv_used may be one block's): 0 m/s (left at start 2,000 m/s, now 2,100 m/s).", LastUserText(rig.Transport.Requests[2]));
        }

        [Theory]
        [InlineData(true, false, true, "the stock delta-v simulator is off now")]
        [InlineData(false, true, true, "a stage's delta-v reading is unreliable now")]
        [InlineData(false, false, false, "no stock delta-v figure at the run's start")]
        public void WhenStocksDeltaVCantBeReadTheDebriefSaysWhyInsteadOfANumber(bool simulatorOffAtEnd, bool unreliableAtEnd, bool startReadable, string why)
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(startReadable ? (Action<GameSnapshot>)(s => s.Stages[0].DeltaVVac = 3913.3) : (s => s.Stages[0].DeltaVUnreliable = true));

            rig.Mission.Send(Craft(s =>
            {
                s.DeltaVSimulator = !simulatorOffAtEnd;
                s.Stages[0].DeltaVUnreliable = unreliableAtEnd;
            }));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("Delta-v used by the whole run: unknown (" + why + ").", request);
            Assert.DoesNotContain("Delta-v used this run, from stock", request);
        }

        [Fact]
        public void AHandBackDebriefLeavesTheRunsDeltaVOut()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3913.3);
            rig.Mission.Send(new TakeManualControl());
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2875.3));

            rig.Mission.Send(new HandBack("Done by hand."));
            rig.DrainUntil(v => v.Stage != MissionStage.Debriefing);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("took Manual Control", request);
            Assert.DoesNotContain("Delta-v used by the whole run", request);
        }

        // ---- The monopropellant the run used (#399) ----

        private static Action<GameSnapshot> Mono(double amount)
        {
            return s => s.Resources.Add(new SnapshotResource { Name = "MonoPropellant", Amount = amount, Max = 40 });
        }

        [Fact]
        public void TheDebriefRequestCarriesTheMonopropellantTheRunUsed()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(Mono(40));

            rig.Mission.Send(Craft(Mono(28.4)));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("Monopropellant used this run: 12 units (40 at start, 28 now).", request);
            Assert.True(request.IndexOf("Delta-v used by the whole run", StringComparison.Ordinal) < request.IndexOf("Monopropellant used this run", StringComparison.Ordinal));
        }

        [Fact]
        public void MonopropellantDroppedFromTheCraftCountsAsUsed()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(Mono(40));

            rig.Mission.Send(Craft(s => { }));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Monopropellant used this run: 40 units (40 at start, 0 now).", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void TheMonopropellantLineIsLeftOutWhenTheCraftCarriedNoneAtTheStart()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();

            rig.Mission.Send(Craft(Mono(5)));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.DoesNotContain("Monopropellant used this run", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void WhenTheMonopropellantCantBeReadTheDebriefSaysWhyInsteadOfANumber()
        {
            Assert.Equal("Monopropellant used this run: unknown (no monopropellant reading at the run's start).",
                MissionPrompts.RunMonopropellantLine(null, new GameSnapshot()));
            Assert.Equal("Monopropellant used this run: unknown (the craft's readings aren't available now).",
                MissionPrompts.RunMonopropellantLine(40, null));
        }

        [Fact]
        public void AHandBackDebriefLeavesTheMonopropellantOut()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.RunToRunning(Mono(40));
            rig.Mission.Send(new TakeManualControl());
            rig.Mission.Send(Craft(Mono(10)));

            rig.Mission.Send(new HandBack("Done by hand."));
            rig.DrainUntil(v => v.Stage != MissionStage.Debriefing);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("took Manual Control", request);
            Assert.DoesNotContain("Monopropellant used this run", request);
        }

        // ---- RCS Use (#398) ----

        private static Dictionary<string, object> Switch(bool on, string reason)
        {
            return new Dictionary<string, object> { { "on", on }, { "reason", reason } };
        }

        [Fact]
        public void TheRcsTallyCountsSwitchesOnAndSecondsOnPerBlockAndNamesTheBlockWithMost()
        {
            var tally = new RcsUseTally();
            tally.Switched(100, "circularize", true);
            tally.Switched(110, "circularize", false);
            tally.Switched(200, "land", true);
            tally.Switched(205, "land", true);   // already on: not counted again
            tally.Switched(210, "land", false);
            tally.Switched(210, "land", false);  // already off
            tally.Switched(300, "circularize", true);
            tally.Switched(302, "circularize", false);

            Assert.Equal(3, tally.TimesOn);
            Assert.Equal(22, tally.SecondsOn, 6);
            Assert.Equal("circularize", tally.MostBlock);
            Assert.Equal(2, tally.MostBlockTimes);
        }

        [Fact]
        public void TheRcsTallyCountsASwitchStillOnAtRunEndUpToThatMoment()
        {
            var tally = new RcsUseTally();
            tally.Switched(100, "land", true);
            tally.Close(130);
            tally.Close(500);

            Assert.Equal(1, tally.TimesOn);
            Assert.Equal(30, tally.SecondsOn, 6);
            tally.Clear();
            Assert.Equal(0, tally.TimesOn);
            Assert.Equal(0, tally.SecondsOn, 6);
        }

        [Fact]
        public void TheRcsUseLineSaysHowOftenHowLongAndWhereMostOrThatThereWasNone()
        {
            var tally = new RcsUseTally();
            Assert.Equal("RCS Use this run: none.", MissionPrompts.RcsUseLine(tally));
            for (var i = 0; i < 14; i++)
            {
                tally.Switched(i * 10, i < 11 ? "Circularize at 80 km" : "land", true);
                tally.Switched(i * 10 + (i < 12 ? 7 : 6), "x", false);
            }
            Assert.Equal("RCS Use this run: switched on 14 times, 96 s in all; most in \"Circularize at 80 km\" (11).", MissionPrompts.RcsUseLine(tally));

            var once = new RcsUseTally();
            once.Switched(0, "", true);
            once.Close(4);
            Assert.Equal("RCS Use this run: switched on 1 time, 4 s in all.", MissionPrompts.RcsUseLine(once));
        }

        [Fact]
        public void AnRcsSwitchLineCarriesTheReasonAndTheFiguresItWasGiven()
        {
            var values = Switch(true, "turning");
            values["error_deg"] = 34.2;
            values["rate_dps"] = 0.1;
            values["wheels_s"] = 85.0;
            values["left_s"] = 60;
            values["monoprop"] = 30.0;
            values["monoprop_max"] = 40.0;
            string problem;
            Assert.Equal("RCS on: turning (error 34.2°, rate 0.1°/s, wheels 85 s vs 60 s left, monoprop 30/40)", RcsSwitch.Parse(values, out problem).Line());

            var off = Switch(false, "aligned");
            off["error_deg"] = 1.4;
            off["rate_dps"] = 0.2;
            Assert.Equal("RCS off: aligned (error 1.4°, rate 0.2°/s)", RcsSwitch.Parse(off, out problem).Line());
            Assert.Equal("RCS off: block end", RcsSwitch.Parse(Switch(false, "block end"), out problem).Line());
        }

        [Fact]
        public void AnRcsSwitchLineRecordsABurstTurnsPlanAndItsOneLineAtTheEnd()
        {
            // #692: the switch on gives the planned coast rate; the turn's one line at its end gives what it flew and spent.
            var values = Switch(true, "big turn");
            values["error_deg"] = 128.0;
            values["coast_dps"] = 3.55;
            string problem;
            Assert.Equal("RCS on: big turn (error 128.0°, coast 3.55°/s planned)", RcsSwitch.Parse(values, out problem).Line());

            var turned = Switch(false, "turned");
            turned["coast_dps"] = 3.55;
            turned["flown_dps"] = 3.1;
            turned["spin_s"] = 1.3;
            turned["stop_s"] = 1.2;
            turned["monoprop_used"] = 0.31;
            turned["left_deg"] = 4.2;
            turned["corrections"] = 0.0;
            turned["dv_mps"] = 0.012;
            Assert.Equal("RCS off: turned (coast 3.55°/s planned, 3.10°/s flown, bursts 1.3 s up, 1.2 s down, monoprop 0.31 used, "
                + "4.2° left after the stop, 0 corrections, orbital velocity changed 0.012 m/s)", RcsSwitch.Parse(turned, out problem).Line());
        }

        [Fact]
        public void ABadRcsSwitchLexiconFailsWithAProblemAndNoException()
        {
            string problem;
            Assert.Null(RcsSwitch.Parse(null, out problem));
            Assert.Null(RcsSwitch.Parse(new Dictionary<string, object>(), out problem));
            Assert.Contains("'on'", problem);
            Assert.Null(RcsSwitch.Parse(Switch(true, "bored"), out problem));
            Assert.Contains("'reason'", problem);
            var text = Switch(true, "turning");
            text["error_deg"] = "big";
            Assert.Null(RcsSwitch.Parse(text, out problem));
            Assert.Contains("'error_deg'", problem);
        }

        [Fact]
        public void TheDebriefRequestCarriesTheRcsUseLineAndTheMissionLogHasEachSwitch()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosBlockStarted("circularize", start.RunId));
            rig.Mission.Send(new KosRcsSwitched(Switch(true, "turning"), ApprovedAt + 10, start.RunId));
            rig.Mission.Send(new KosRcsSwitched(Switch(false, "aligned"), ApprovedAt + 15, start.RunId));
            rig.Mission.Send(new KosRcsSwitched(Switch(true, "bored"), ApprovedAt + 16, start.RunId));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("RCS Use this run: switched on 1 time, 5 s in all; most in \"circularize\" (1).", LastUserText(rig.Transport.Requests[2]));
            var log = rig.Mission.View.Log.Select(e => e.Text).ToList();
            Assert.Contains("RCS on: turning", log);
            Assert.Contains("RCS off: aligned", log);
            Assert.Contains(log, t => t != null && t.StartsWith("RCS switch not logged: 'reason'"));
        }

        [Fact]
        public void ARunWithNoRcsSwitchesSaysNoneAndAHandBackDebriefLeavesTheLineOut()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("RCS Use this run: none.", LastUserText(rig.Transport.Requests[2]));

            var hand = new Rig(this, DebriefJson("Nominal", ""));
            hand.RunToRunning();
            hand.Mission.Send(new TakeManualControl());
            hand.Mission.Send(new HandBack("Done by hand."));
            hand.DrainUntil(v => v.Stage != MissionStage.Debriefing);
            Assert.DoesNotContain("RCS Use this run", LastUserText(hand.Transport.Requests[2]));
        }

        [Fact]
        public void EveryDebriefRunsTheMarginRecheckThroughTheCraftCheckAndChargesItToTheMission()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            // The router's default Pass carries no usage; this one is billed, so the charge can be seen.
            rig.Router.Recheck = Reply("{\"summary\":\"Plenty left.\",\"findings\":[]}");
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;
            var spentBefore = rig.Mission.View.Spent;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            // One more Craft Check request, asked of the craft as it is now against the plan that is left.
            Assert.Equal(checksBefore + 1, rig.Router.CheckRequests.Count);
            var body = rig.Router.CheckRequests.Last().Body;
            Assert.Contains("Margin recheck after a Maneuver Sequence", body);
            Assert.Contains("Second leg", body);

            // The Mission started from a cleared Craft Check, so its cost opens the log as Craft Check entries and stays what it was.
            var all = rig.CostLog();
            Assert.Equal(new[] { "craft_check_pending", "craft_check_paid" }, all.Take(2).Select(c => c.Event));
            Assert.DoesNotContain(all.Skip(2), c => c.Event.StartsWith("craft_check"));

            // The recheck is charged like any Mission request: a pending worst case, then its real usage, before the Debrief's own.
            var cost = all.Skip(2).ToList();
            // The Mission Plan, the Sequence Plan, the recheck, then the Debrief itself (the next Sequence Plan follows it).
            Assert.Equal(new[] { "pending", "paid", "pending", "paid", "pending", "paid", "pending", "paid" }, cost.Take(8).Select(c => c.Event));
            Assert.Equal("margin_recheck", cost[4].Outcome);
            Assert.Equal("completed", cost[5].Outcome);
            Assert.True(cost[5].Amount > 0);
            Assert.True(rig.Mission.View.Spent >= spentBefore + cost[5].Amount);
            Assert.Equal(rig.Mission.View.Spent, all.Where(c => c.Event == "paid" || c.Event == "craft_check_paid").Sum(c => c.Amount));
        }

        [Fact]
        public void UnderAutoAnEnoughMarginSendsNoRecheckAndTheDebriefAndCardCarryTheModsCheck()
        {
            // 3,200 m/s left against the 1,000 m/s Second leg: enough.
            var rig = new Rig(this, DebriefJson("Nominal", ""), recheck: MarginRecheckMode.Auto);
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore, rig.Router.CheckRequests.Count);
            Assert.DoesNotContain(rig.CostLog(), c => c.Outcome == "margin_recheck");
            var text = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("The mod's margin check (the craft as it is now against the Maneuver Sequences still to fly): 3,200 m/s left against 1,000 m/s still to fly (+220%; 15% wanted): enough.", text);
            Assert.DoesNotContain("The mod's margin recheck", text);
            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.StartsWith("3,200 m/s left against 1,000 m/s", detail.RecheckSummary);
            // The on-pad craft generates no electric charge: the start check's Warning, shown again but not making the margin tight.
            Assert.Contains(detail.RecheckFindings, f => f.StartsWith("Warning: No electric charge generation"));
            Assert.NotNull(detail.Margin);
        }

        [Fact]
        public void UnderAutoATightMarginSendsTheRecheckAndBothReachTheDebrief()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""), recheck: MarginRecheckMode.Auto);
            rig.Router.Recheck = Reply("{\"summary\":\"Within the legs, not the margin.\",\"findings\":[]}");
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            // 1,100 m/s left against 1,000 still to fly: the legs fit, the 15% margin doesn't.
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 1100));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore + 1, rig.Router.CheckRequests.Count);
            Assert.Contains(rig.CostLog(), c => c.Event == "pending" && c.Outcome == "margin_recheck");
            var text = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("1,100 m/s left against 1,000 m/s still to fly (+10%; 15% wanted): tight.", text);
            Assert.Contains("Blocker: Not enough delta-v", text);
            Assert.Contains("The mod's margin recheck (a Craft Check of the craft as it is now", text);
            Assert.Contains("Within the legs, not the margin.", text);
        }

        [Fact]
        public void NeverSendsNoRecheckEvenWhenTheMarginIsShort()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""), recheck: MarginRecheckMode.Never);
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 500));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore, rig.Router.CheckRequests.Count);
            Assert.Contains("500 m/s left against 1,000 m/s still to fly (-50%; 15% wanted): short.", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void TheMarginRecheckIsSkippedWhenTheSpendingCapHasNoRoomForItBesideTheDebrief()
        {
            // Room for the plan, the sequence and the Debrief, but not for a recheck on top of them.
            var rig = new Rig(this, DebriefJson("Nominal", ""), cap: CapWithRoomForTheDebriefButNotARecheck());
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore, rig.Router.CheckRequests.Count);
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The margin recheck was skipped: it could pass the Spending Cap"));
            Assert.DoesNotContain("The mod's margin recheck", LastUserText(rig.Transport.Requests[2]));
            Assert.Contains(rig.CostLog(), c => c.Event == "blocked" && c.Outcome == "over_cap");
            Assert.Contains("Spending Cap", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped);
            // The Mission is not Paused for a request it did not need.
            Assert.NotEqual(MissionStage.Paused, rig.Mission.View.Stage);
        }

        /// <summary>
        /// A Spending Cap measured from the rig's own requests, so it keeps its meaning as the tools list (counted in every
        /// request's worst case) grows: what the plan and the sequence really cost, plus the Debrief's worst case. A recheck's
        /// worst case and the Debrief's reserve then no longer fit beside it, while the Debrief itself does.
        /// </summary>
        private static decimal CapWithRoomForTheDebriefButNotARecheck()
        {
            // A dry run with a roomy cap: the same flow, in its own save, with the recheck allowed.
            using (var probe = new MissionDebriefTests())
            {
                var rig = new Rig(probe, DebriefJson("Nominal", ""));
                var start = rig.RunToRunning();
                rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
                rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

                var log = rig.CostLog();
                var recheck = log.Select((c, i) => new { c, i }).First(x => x.c.Event == "pending" && x.c.Outcome == "margin_recheck").i;
                // The plan and the sequence settled at their real cost; the Debrief is the request sent after the recheck.
                var settled = log.Take(recheck).Where(c => c.Event == "paid" || c.Event == "craft_check_paid").Sum(c => c.Amount);
                var after = log.Skip(recheck + 2).ToList();
                var debrief = after[0];
                var next = after.First(c => c.Event == "pending" && !ReferenceEquals(c, debrief));
                Assert.Equal("pending", debrief.Event);
                Assert.Equal("paid", after[1].Event);
                // The Debrief must fit, and then the next Sequence Plan's worst case beside the Debrief's real cost.
                var cap = settled + Math.Max(debrief.Amount, after[1].Amount + next.Amount);
                // The recheck's worst case alone must not fit in what the cap leaves over the plan and the sequence.
                Assert.True(cap < settled + log[recheck].Amount + debrief.Amount, "the recheck would fit under the derived cap " + cap);
                return cap;
            }
        }

        private static void QueueRecheckToolSteps(Rig rig, int count, int pricedStep = -1)
        {
            for (var i = 0; i < count; i++)
            {
                var call = ("stage_performance", "{\"situation\":\"surface\",\"n\":" + i + "}");
                rig.Router.Rechecks.Enqueue(i == pricedStep ? ToolUseStreams.ToolUsePriced("rc" + i, 100000, call) : ToolUseStreams.ToolUse("rc" + i, call));
            }
        }

        private static string Events(Rig rig)
        {
            var path = Path.Combine(rig.Folder, MissionStore.EventsFile);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        [Fact]
        public void AfterEightToolStepsTheMarginRecheckSendsOneLastRequestWithToolChoiceNoneAndTheSameToolsAndATextReplyCompletesIt()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            QueueRecheckToolSteps(rig, 8);
            rig.Router.Rechecks.Enqueue(Reply("{\"summary\":\"Plenty left.\",\"findings\":[]}"));
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore + 9, rig.Router.CheckRequests.Count);
            using (var first = JsonDocument.Parse(rig.Router.CheckRequests[checksBefore].Body))
            {
                for (var step = 0; step < 9; step++)
                {
                    using (var doc = JsonDocument.Parse(rig.Router.CheckRequests[checksBefore + step].Body))
                    {
                        Assert.Equal(step == 8 ? "{\"type\":\"none\"}" : "{\"type\":\"auto\"}", doc.RootElement.GetProperty("tool_choice").GetRawText());
                        Assert.Equal(first.RootElement.GetProperty("tools").GetRawText(), doc.RootElement.GetProperty("tools").GetRawText());
                        Assert.Equal(first.RootElement.GetProperty("system").GetRawText(), doc.RootElement.GetProperty("system").GetRawText());
                    }
                }
            }
            Assert.Contains("tool_limit_final", Events(rig));
            Assert.Contains(MissionPrompts.StepLimitVerdictLine, rig.Router.CheckRequests[checksBefore + 8].Body);
            Assert.DoesNotContain(MissionPrompts.StepLimitVerdictLine, rig.Router.CheckRequests[checksBefore + 7].Body);
            Assert.Contains("The mod's margin recheck", LastUserText(rig.Transport.Requests[2]));
            Assert.Equal("", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped ?? "");
        }

        [Fact]
        public void AMarginRecheckWhoseLastReplyStillCallsToolsEndsWithoutItAsBefore()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            QueueRecheckToolSteps(rig, 9);
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore + 9, rig.Router.CheckRequests.Count);
            Assert.Contains("Claudia kept calling Planning Tools after the limit of 8 steps", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped);
        }

        [Fact]
        public void TheMarginRechecksLastRequestIsBlockedByTheDebriefRoomGateAsAnyStepIs()
        {
            // The cap that fits the first eight recheck requests and the Debrief's room, not the ninth.
            decimal cap;
            using (var probe = new MissionDebriefTests())
            {
                var rig = new Rig(probe, DebriefJson("Nominal", ""));
                QueueRecheckToolSteps(rig, 8, pricedStep: 7);
                rig.Router.Rechecks.Enqueue(Reply("{\"summary\":\"Plenty left.\",\"findings\":[]}"));
                var start = rig.RunToRunning();
                rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
                rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

                var log = rig.CostLog();
                var first = log.Select((c, i) => new { c, i }).First(x => x.c.Event == "pending" && x.c.Outcome == "margin_recheck").i;
                var committed = log.Take(first).Where(c => c.Event == "paid" || c.Event == "craft_check_paid").Sum(c => c.Amount);
                var reserve = log[first + 18].Amount;
                Assert.Equal("pending", log[first + 18].Event);
                decimal low = 0;
                for (var k = 0; k < 8; k++)
                {
                    low = Math.Max(low, committed + log[first + 2 * k].Amount + reserve);
                    committed += log[first + 2 * k + 1].Amount;
                }
                var high = committed + log[first + 16].Amount + reserve;
                Assert.True(low < high, "no gap for a cap that fits the eight steps but not the last request: " + low + " to " + high);
                // Just under the last request's gate, so the Debrief itself still fits once the recheck is cut short.
                cap = high - 0.0001m;
            }

            var run = new Rig(this, DebriefJson("Nominal", ""), cap: cap);
            QueueRecheckToolSteps(run, 8, pricedStep: 7);
            run.Router.Rechecks.Enqueue(Reply("{\"summary\":\"Plenty left.\",\"findings\":[]}"));
            var started = run.RunToRunning();
            var before = run.Router.CheckRequests.Count;
            run.Mission.Send(new KosRunEnded(Finished(), started.RunId));
            run.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(before + 8, run.Router.CheckRequests.Count);
            Assert.Contains("Spending Cap", run.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped);
            Assert.DoesNotContain("tool_limit_final", Events(run));
        }

        [Fact]
        public void AMarginRecheckThatFailsIsPaidForReportedAndTheDebriefGoesOnWithoutIt()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = Reply("this is not a craft check reply");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The margin recheck failed"));
            Assert.Contains("did not come back", LastUserText(rig.Transport.Requests[2]));
            Assert.Contains("the recheck failed", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped);
            // After the held Craft Check entries: Mission Plan, Sequence Plan, then the recheck's pair.
            Assert.Equal("off_schema", rig.CostLog().Skip(2).ElementAt(5).Outcome);
        }

        [Fact]
        public void ARefusedMarginRecheckIsPaidForAndPausesTheDebriefForRetryPerRefusals()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = ScriptedResponse.Fixture("refusal.sse");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.EndMission }, rig.Mission.View.QuickReplies);
            Assert.Equal("refused", rig.CostLog().Skip(2).ElementAt(5).Outcome);
            // The Debrief itself has not been sent.
            Assert.Equal(2, rig.Transport.Requests.Count);
        }

        [Fact]
        public void AMarginRecheckThatFindsTheModelGonePausesForChooseModel()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = ScriptedResponse.Error(404, "not_found_error", "model: claude-sonnet-5-5");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.ChooseModel, QuickReply.EndMission }, rig.Mission.View.QuickReplies);
            Assert.Equal(2, rig.Transport.Requests.Count);
        }

        [Fact]
        public void AMarginRecheckFindingReachesTheCardAndTheRequest()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ""));
            rig.Router.Recheck = Reply("{\"summary\":\"Short by 300 m/s.\",\"findings\":[{\"code\":\"delta_v_short\",\"explanation\":\"900 m/s left against 1,200 needed.\",\"fix\":\"Add a tank.\"}]}");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            // The mod's own margin line comes first on the card, then Claudia's; her finding follows the mod's.
            Assert.Equal("3,200 m/s left against 1,000 m/s still to fly (+220%; 15% wanted): enough. Claudia's recheck: Short by 300 m/s.", detail.RecheckSummary);
            Assert.Contains("900 m/s left against 1,200 needed.", detail.RecheckFindings.Last());
            Assert.Contains("Short by 300 m/s.", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void EventsFromBeforeTheRunAreNotInItsDebrief()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Staging, "a stage before the run", ApprovedAt));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.DoesNotContain("a stage before the run", LastUserText(rig.Transport.Requests[2]));
            Assert.Empty(rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.Events);
        }

        [Fact]
        public void TheDebriefCardReportsTheRcsSwitchAndTheMonopropellantLeft()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            var snapshot = OnPad();
            snapshot.RcsOn = true;
            snapshot.Resources.Add(new SnapshotResource { Name = "MonoPropellant", Amount = 15, Max = 60 });
            rig.Mission.Send(snapshot);
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal("RCS on", detail.RcsState);
            Assert.Contains("MonoPropellant 25%", detail.ResourcesLeft);
        }

        [Fact]
        public void TheDebriefCardSaysNothingOfRcsWhenTheSnapshotDidNotReadIt()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal("", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RcsState);
        }

        [Fact]
        public void TheRcsSwitchSurvivesTheSnapshotJsonAndIsOmittedWhenUnread()
        {
            var on = new GameSnapshot { RcsOn = true };
            Assert.Contains("\"rcs_on\":true", on.ToJson());
            Assert.True(GameSnapshot.Parse(on.ToJson()).RcsOn);
            Assert.False(GameSnapshot.Parse(new GameSnapshot { RcsOn = false }.ToJson()).RcsOn);
            Assert.DoesNotContain("rcs_on", new GameSnapshot().ToJson());
            Assert.Null(GameSnapshot.Parse(new GameSnapshot().ToJson()).RcsOn);
        }

        // ---- The card ----

        [Fact]
        public void TheDebriefCardShowsOutcomeMarginScienceEventsChecklistAndTheFollowUp()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", FullExtras));
            var start = rig.RunToRunning();
            rig.Mission.Send(new RunEventSeen(RunEventKind.PartLost, "Spark Engine broke off", ApprovedAt + 30));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Science, "Mystery Goo on the pad: 3.0 science", ApprovedAt + 31));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            var card = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            var detail = card.Debrief;
            Assert.Equal("Off-Nominal", detail.Rating);
            Assert.Equal("Off-Nominal", card.Outcome);
            Assert.Equal("Apoapsis 74 km against 80 km planned.", detail.OutcomeVsTarget);
            Assert.Equal("3,200 m/s left against 1,000 m/s to fly.", detail.PlanMargin);
            // The bar is the mod's own sum: the simulator's 3,200 m/s against the 1,000 m/s Second leg still to fly.
            Assert.Equal(3200, detail.Margin.DeltaVLeft);
            Assert.Equal(1000, detail.Margin.RestOfPlan);
            Assert.Equal(MarginLevel.Enough, detail.Margin.Level);
            Assert.Equal("Two goo samples.", detail.Science);
            Assert.Equal(new[] { "+30 s part lost: Spark Engine broke off" }, detail.Events);
            Assert.Equal(new[] { "+31 s Mystery Goo on the pad: 3.0 science" }, detail.ScienceSeen);
            Assert.Equal(1, detail.ChecklistDone);
            Assert.Equal(2, detail.Checklist.Count);
            Assert.Equal("Corrective sequence next: Raise the apoapsis by 6 km.", detail.FollowUp);
            Assert.Equal("", detail.ScriptError);
        }

        private const string LeanExtras =
            ",\"outcome_vs_target\":\"\",\"plan_margin\":\"\",\"science\":\"\",\"goal_steps_done\":[1],\"follow_up\":\"none\",\"follow_up_text\":\"\"";

        [Fact]
        public void ALeanNominalDebriefIsAcceptedAndItsCardHasTheModsSections()
        {
            var rig = new Rig(this, DebriefJson("Nominal", LeanExtras).Replace("\"what_happened\":\"It went.\"", "\"what_happened\":\"\""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new RunEventSeen(RunEventKind.Staging, "Stage 1 separated", ApprovedAt + 20));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Science, "Mystery Goo on the pad: 3.0 science", ApprovedAt + 31));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            var card = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Debrief: Nominal", card.Title);
            Assert.Equal("Next: Fly on.", card.Text);
            var detail = card.Debrief;
            Assert.Equal("", detail.OutcomeVsTarget);
            Assert.Equal("", detail.PlanMargin);
            Assert.Equal("", detail.Science);
            // The mod's sections carry the card: the margin bar, the Margin check line, its events and the science it saw.
            Assert.NotNull(detail.Margin);
            Assert.NotEqual("", detail.RecheckSummary);
            Assert.Single(detail.Events);
            Assert.Equal(new[] { "+31 s Mystery Goo on the pad: 3.0 science" }, detail.ScienceSeen);
            // The plan gave the Mission Goal's steps; the Debrief ticked the first.
            Assert.Equal(1, detail.ChecklistDone);
            Assert.Equal(2, detail.Checklist.Count);
            Assert.Equal(new[] { true, false }, rig.Mission.View.GoalSteps.Select(s => s.Done));
        }

        [Fact]
        public void AnOffNominalDebriefWithThoseFieldsEmptyIsHandledAsBefore()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", LeanExtras.Replace("\"follow_up\":\"none\"", "\"follow_up\":\"corrective_sequence\"")));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            var card = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Debrief: Off-Nominal", card.Title);
            Assert.Equal("It went.\nNext: Fly on.", card.Text);
            Assert.Equal("", card.Debrief.OutcomeVsTarget);
            Assert.Equal("Corrective sequence next: Fly on.", card.Debrief.FollowUp);
        }

        [Fact]
        public void AFailedRunsDebriefCardIncludesTheScriptsErrorText()
        {
            var rig = new Rig(this, DebriefJson("Failed", ""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.NoReport, null, "Undefined Variable Name: APOAPSIS2.\nat line 4", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);

            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal("Undefined Variable Name: APOAPSIS2.\nat line 4", detail.ScriptError);
            Assert.Contains("Undefined Variable Name: APOAPSIS2.", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void ARunThatReportedHasNoScriptErrorOnItsCard()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal("", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.ScriptError);
        }

        // ---- Off-Nominal ----

        [Fact]
        public void AnOffNominalDebriefWithARevisedPlanAsksForTheMissionPlanNotTheNextSequence()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ",\"follow_up\":\"revised_plan\",\"follow_up_text\":\"Add a second burn.\""), PlanReply());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Contains("Revise the Mission Plan after the Off-Nominal Debrief", LastUserText(rig.Transport.Requests[3]));
            Assert.Contains("Revised Mission Plan next: Add a second burn.", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.FollowUp);
        }

        /// <summary>A propose_mission_plan input: the named sequences, or none (a plan that declines the goal).</summary>
        private static string ToolPlan(string why, params string[] names)
        {
            return "{\"why\":" + JsonSerializer.Serialize(why) + ",\"sequences\":["
                + string.Join(",", names.Select(n => "{\"name\":\"" + n + "\",\"intent\":\"Fly it.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}"))
                + "],\"total_delta_v\":" + (names.Length * 100) + ",\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\\\"\\\"\",\"child_vessels\":[],\"goal_steps\":[]}";
        }

        private static ScriptedResponse MissionPlanKind()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Revised.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null}");
        }

        /// <summary>
        /// #697, Mission m20261103-031325-bac8: the revision after an Off-Nominal Debrief got the full plan and an empty placeholder
        /// in one reply. Neither is accepted; the re-send of the full plan is, and it is the plan offered, not an empty one.
        /// </summary>
        [Fact]
        public void TheRevisionThatGotAFullAndAnEmptyPlanInOneReplyEndsWithTheFullPlanOffered()
        {
            var full = ToolPlan("Land and come home.", "Link check", "Land at Twin Craters", "Return to Kerbin");
            var rig = new Rig(this, DebriefJson("Off-Nominal", ",\"follow_up\":\"revised_plan\",\"follow_up_text\":\"Land at the craters.\""),
                ToolUseStreams.ToolUse("two", (ProposedPlans.ToolName, full), (ProposedPlans.ToolName, ToolPlan("placeholder"))));
            rig.Transport.Add(ToolUseStreams.ToolUse("again", (ProposedPlans.ToolName, full)));
            rig.Transport.Add(MissionPlanKind());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 6 && v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            Assert.Contains(ProposedPlans.OnePerReplyJson, rig.Transport.Requests[4].Body.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            Assert.Equal(new[] { "Link check", "Land at Twin Craters", "Return to Kerbin" }, rig.Mission.View.PlanSequences);
            var events = File.ReadAllText(Path.Combine(rig.Folder, MissionStore.EventsFile));
            Assert.DoesNotContain("tool_loop_repeat_stop", events);
        }

        [Fact]
        public void TheRevisionAfterAnOffNominalDebriefRefusesAPlanWithNoSequences()
        {
            var full = ToolPlan("Land and come home.", "Link check", "Land at Twin Craters");
            var rig = new Rig(this, DebriefJson("Off-Nominal", ",\"follow_up\":\"revised_plan\",\"follow_up_text\":\"Land at the craters.\""),
                ToolUseStreams.ToolUse("empty", (ProposedPlans.ToolName, ToolPlan("Can't do it."))));
            rig.Transport.Add(ToolUseStreams.ToolUse("full", (ProposedPlans.ToolName, full)));
            rig.Transport.Add(MissionPlanKind());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 6 && v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            Assert.Contains("this is a revision of a Mission still under way: give the sequences still to fly", rig.Transport.Requests[4].Body);
            Assert.Equal(new[] { "Link check", "Land at Twin Craters" }, rig.Mission.View.PlanSequences);
        }

        [Fact]
        public void TheRevisedMissionPlanRequestAfterADebriefCarriesTheStageTable()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ",\"follow_up\":\"revised_plan\",\"follow_up_text\":\"Add a second burn.\""), PlanReply());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingPlanApproval);

            var text = LastUserText(rig.Transport.Requests[3]);
            Assert.Contains("Stage table: these are the figures stage_performance and plan_ascent would give for this craft now", text);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: ", text);
        }

        /// <summary>#680 (m20261102-155440-e17d): a revised plan from orbit with the parts unchanged sends neither the parts nor the ascent figures.</summary>
        [Fact]
        public void TheRevisedMissionPlanRequestFromOrbitLeavesOutThePartsAndTheAscentFigures()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ",\"follow_up\":\"revised_plan\",\"follow_up_text\":\"Add a second burn.\""), PlanReply());
            var start = rig.RunToRunning();
            var orbit = Craft(s => { s.Situation = "ORBITING"; s.Ut += 600; });

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            // In orbit by the time the revised plan is asked for (a run's own situation change would raise an Alert).
            rig.Mission.Send(orbit);
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingPlanApproval);

            var text = LastUserText(rig.Transport.Requests[3]);
            Assert.StartsWith("Revise the Mission Plan after the Off-Nominal Debrief", text);
            Assert.DoesNotContain("\"parts\":[", text);
            Assert.True(text.Contains("\nparts: unchanged since the Game Snapshot at UT ") || text.Contains(SnapshotDiet.AsAbove), text);
            Assert.DoesNotContain("The Craft Check's plan_ascent", text);
            Assert.DoesNotContain("stage_performance {\"body\":\"Kerbin\",\"situation\":\"surface\"}", text);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: ", text);
            // Under #256's rule the request carried the full snapshot and the surface row.
            var before = "Revise the Mission Plan after the Off-Nominal Debrief.\n" + MissionPrompts.Facts(orbit.Ut, orbit)
                + MissionPrompts.StageTable(Craft(s => { }), Goal, "");
            Assert.True(text.Length < before.Length, text.Length + " against " + before.Length);
        }

        [Fact]
        public void AnOffNominalDebriefWithNoRevisedPlanAsksForACorrectiveSequence()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ""), Rig.SequenceReply());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Continue the Mission with a corrective sequence.", LastUserText(rig.Transport.Requests[3]));
        }

        // ---- Failed ----

        [Fact]
        public void AFailedDebriefWithAReducedGoalWaitsForApprovalAndEndMissionStaysOnOffer()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"reduced_goal\",\"follow_up_text\":\"Reach a stable orbit of Kerbin.\""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            var view = rig.Mission.View;
            Assert.Equal(new[] { QuickReply.AcceptReducedGoal, QuickReply.EndMission }, view.QuickReplies);
            Assert.False(view.CanType);
            Assert.True(view.CanEndMission);
            Assert.Equal(Goal, view.Goal);
            Assert.Contains("Reduced goal, for your Approval: Reach a stable orbit of Kerbin.", view.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.FollowUp);
            // It survives a save and a load.
            var saved = rig.Mission.ExportStates().Single();
            Assert.Equal("Reach a stable orbit of Kerbin.", MissionSaveState.FromValues(saved.ToValues()).ReducedGoal);

            rig.Mission.Send(new EndMission());
            Assert.Equal(MissionStage.None, rig.Mission.View.Stage);
        }

        [Fact]
        public void ApprovingTheReducedGoalChangesTheGoalAndAsksClaudiaToReviseTheMissionPlanForApproval()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"reduced_goal\",\"follow_up_text\":\"Reach a stable orbit of Kerbin.\""), PlanReply());
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            rig.Mission.Send(new AcceptReducedGoal());
            Assert.Equal("Reach a stable orbit of Kerbin.", rig.Mission.View.Goal);
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingPlanApproval);

            var request = LastUserText(rig.Transport.Requests[3]);
            Assert.Contains("approved a reduced Mission Goal", request);
            Assert.Contains("Reach a stable orbit of Kerbin.", request);
            Assert.Contains(Goal, request);
            // Nothing runs before the player approves the revised plan.
            Assert.Contains(QuickReply.Approve, rig.Mission.View.QuickReplies);
            Assert.Empty(rig.Mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void AFailedDebriefWithNoReducedGoalWaitsWithEndMissionAndRetryWithANote()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"end_mission\",\"follow_up_text\":\"\""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            var view = rig.Mission.View;
            Assert.Equal(new[] { QuickReply.RetryWithNote, QuickReply.EndMission }, view.QuickReplies);
            Assert.True(view.CanEndMission);
            Assert.Contains("End Mission, or Retry with a note", view.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.FollowUp);

            rig.Mission.Send(new EndMission());
            Assert.Equal(MissionStage.None, rig.Mission.View.Stage);
        }

        [Fact]
        public void EndingAFailedMissionWithNoReducedGoalRecordsItInTheMissionsListAsFailed()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"end_mission\",\"follow_up_text\":\"\""));
            var start = rig.RunToRunning();
            var id = rig.Mission.View.MissionId;
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            rig.Mission.Send(new EndMission());

            Assert.Equal(MissionStage.None, rig.Mission.View.Stage);
            var entry = Assert.Single(rig.Mission.View.Missions, m => m.Id == id);
            Assert.Equal(MissionStatus.Ended, entry.Status);
            Assert.Equal("Failed", entry.FinalDebrief.Outcome);
            Assert.True(entry.CanDelete);
            Assert.False(entry.CanResume);
            Assert.True(File.Exists(Path.Combine(_save.Path, "ClaudiaKerman", "missions", id, MissionStore.FinishedFile)));
        }

        [Fact]
        public void RetryWithANoteAfterAFailedDebriefWithNoReducedGoalAsksClaudiaForOne()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"end_mission\",\"follow_up_text\":\"\""),
                Reply(DebriefJson("Failed", ",\"follow_up\":\"reduced_goal\",\"follow_up_text\":\"Reach a stable orbit of Kerbin.\"")));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            rig.Mission.Send(new RetryWithNote("Any orbit will do."));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);

            Assert.Contains("Any orbit will do.", LastUserText(rig.Transport.Requests[3]));
            Assert.Equal(new[] { QuickReply.AcceptReducedGoal, QuickReply.EndMission }, rig.Mission.View.QuickReplies);
        }

        [Fact]
        public void ApproveReducedGoalDoesNothingOutsideItsStage()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.RunToRunning();
            var goal = rig.Mission.View.Goal;

            rig.Mission.Send(new AcceptReducedGoal());

            Assert.Equal(goal, rig.Mission.View.Goal);
            Assert.Equal(MissionStage.Running, rig.Mission.View.Stage);
        }

        // ---- A Debrief reply with no debrief (#748) ----

        private static string NoDebriefJson(string kind = "debrief")
        {
            return "{\"kind\":\"" + kind + "\",\"say\":\"placeholder\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,\"debrief\":null}";
        }

        [Fact]
        public void ADebriefReplyWithNoDebriefGetsOneCorrectionThenTheModsOwnOffNominalDebriefAndNoMoreRequests()
        {
            var rig = new Rig(this, NoDebriefJson(), Reply(NoDebriefJson("answer")), recheck: MarginRecheckMode.Never);
            // Were the loop to come back, these would answer it; the count below shows none is asked.
            for (var i = 0; i < 12; i++) rig.Transport.Add(Reply(NoDebriefJson()));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Paused);

            // Plan, Sequence Plan, the Debrief, its one correction: four.
            Assert.Equal(4, rig.Transport.Requests.Count);
            Assert.Contains("Your reply had no debrief", rig.Transport.Requests[3].Body);
            Assert.DoesNotContain("Your reply had no debrief", rig.Transport.Requests[2].Body);
            var card = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", card.Outcome);
            Assert.Contains("The mod's own Debrief", card.Why);
            Assert.Contains("Resume", rig.Mission.View.PauseReason);

            // Waiting for the player: nothing more goes until Resume, which asks for the next Sequence Plan.
            rig.DrainUntil(v => !v.Busy);
            Assert.Equal(4, rig.Transport.Requests.Count);
            Assert.Contains("debrief_retry", File.ReadAllText(Path.Combine(rig.Folder, "events.jsonl")));
            Assert.Contains("mod_debrief", File.ReadAllText(Path.Combine(rig.Folder, "events.jsonl")));
        }

        [Fact]
        public void AGoodReplyAfterTheCorrectionIsTakenAndTheMissionCarriesOn()
        {
            var rig = new Rig(this, NoDebriefJson(), Reply(DebriefJson("Nominal", "")), recheck: MarginRecheckMode.Never);
            rig.Transport.Add(Rig.SequenceReply());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(5, rig.Transport.Requests.Count);
            Assert.Contains("Your reply had no debrief", rig.Transport.Requests[3].Body);
            Assert.Equal("Nominal", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
        }

        [Fact]
        public void TheModsOwnDebriefIsRebuiltFromTheTranscriptAfterALoad()
        {
            var text = EmptyDebrief.Fact("It was not judged.") + "\nRun report.";
            Assert.Equal("It was not judged.", EmptyDebrief.ReadFact(text));
            Assert.Null(EmptyDebrief.ReadFact("Continue the Mission."));
        }

        // ---- Helpers ----

        private const string FullExtras =
            ",\"outcome_vs_target\":\"Apoapsis 74 km against 80 km planned.\",\"plan_margin\":\"3,200 m/s left against 1,000 m/s to fly.\","
            + "\"science\":\"Two goo samples.\",\"goal_checklist\":[{\"item\":\"Reach orbit\",\"done\":true},{\"item\":\"Land on the Mun\",\"done\":false}],"
            + "\"follow_up\":\"corrective_sequence\",\"follow_up_text\":\"Raise the apoapsis by 6 km.\"";

        private static string DebriefJson(string outcome, string extra)
        {
            return "{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"" + outcome + "\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\"" + extra + "}}";
        }

        private static ScriptedResponse PlanReply()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Second leg\",\"intent\":\"Go on.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1,\"goal_steps\":[\"Prove the link\",\"Go on\"]}}");
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

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static GameSnapshot Craft(Action<GameSnapshot> shape)
        {
            var snapshot = OnPad();
            shape(snapshot);
            return snapshot;
        }

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

        private sealed class CostLine
        {
            public string Event;
            public string Outcome;
            public decimal Amount;
        }

        /// <summary>A Mission with a two-sequence plan, one Sequence Plan, the Debrief under test and any replies after it.</summary>
        private sealed class Rig
        {
            private readonly MissionDebriefTests _tests;
            private readonly decimal _cap;
            public readonly StreamingFakeTransport Transport;
            public readonly CraftCheckRouter Router;
            public readonly MissionControl Mission;

            /// <param name="recheck">Always by default: most of these tests are of Claudia's Margin recheck itself, which auto sends only when the mod's margin check is not enough.</param>
            public Rig(MissionDebriefTests tests, string debrief, ScriptedResponse after = null, decimal cap = 10m, MarginRecheckMode recheck = MarginRecheckMode.Always)
            {
                _tests = tests;
                _cap = cap;
                Transport = new StreamingFakeTransport(PlanReply(), SequenceReply(), Reply(debrief));
                // What follows a Debrief that doesn't end the Mission: the next Sequence Plan unless the test says otherwise.
                Transport.Add(after ?? SequenceReply());
                Router = new CraftCheckRouter(Transport);
                var client = new ClaudeClient(Router, () => "sk-ant-test", l => { });
                client.Timing.Wait = (d, c) => { };
                var config = new PlayerConfig { CheckDurationPercent = 1000, MarginRecheck = recheck };
                Mission = new MissionControl(client, tests._background, config, () => true, new PassingScriptCheck());
                Mission.Send(new SaveLoaded(tests._save.Path));
                Mission.Send(new SceneChanged(GameScene.Flight));
                Mission.Send(OnPad());
            }

            public static ScriptedResponse SequenceReply()
            {
                return ScriptedResponse.Fixture("mission-sequence-plan.sse");
            }

            /// <param name="craft">Shapes the Game Snapshot the Mission starts from, so the run's start figure is what the test wants.</param>
            public StartScript RunToRunning(Action<GameSnapshot> craft = null)
            {
                var snapshot = Craft(craft ?? (s => { }));
                Mission.Send(snapshot);
                CraftCheckRouter.StartChecked(Mission, _tests._background, snapshot, new StartMission(Goal, _cap, Sonnet));
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
                get { return Path.Combine(_tests._save.Path, "ClaudiaKerman", "missions", Mission.View.MissionId); }
            }

            public IList<CostLine> CostLog()
            {
                var path = Path.Combine(_tests._save.Path, "ClaudiaKerman", "missions", Mission.View.MissionId, "cost-log.jsonl");
                return File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line =>
                {
                    var root = JsonDocument.Parse(line).RootElement;
                    JsonElement outcome, amount;
                    return new CostLine
                    {
                        Event = root.GetProperty("event").GetString(),
                        Outcome = root.TryGetProperty("outcome", out outcome) ? outcome.GetString() : "",
                        Amount = root.TryGetProperty("amount", out amount) ? amount.GetDecimal() : 0m,
                    };
                }).ToList();
            }
        }
    }
}
