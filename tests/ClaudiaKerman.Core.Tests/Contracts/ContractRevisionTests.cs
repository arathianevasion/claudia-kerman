// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1379 (ADR 0022), at the Mission Control seam: a revised Mission Plan is checked over the Requirements still open, from where the
    /// craft is. A leg that reaches a body and serves no open Requirement and none of the player's words is sent back; a revision that drops
    /// the met visit and keeps the rest in the Route's order is offered. The scenario is Mission m20261126-040610-a4b7: the Mun met, a second Mun leg kept.
    /// </summary>
    public sealed class ContractRevisionTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const double EndedAt = 448444;
        private static readonly string[] FirstPlan = { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" };
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private StreamingFakeTransport _transport;

        public void Dispose()
        {
            _background.Drain();
            _save.Dispose();
        }

        /// <summary>The Exploration Contract, its Mun orbit met and its Minmus orbit still open.</summary>
        private static GameSnapshot MunMet(double ut)
        {
            var contract = ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT", "Minmus:ORBIT");
            contract.State = "Active";
            contract.Requirements[0].State = "Complete";
            var snapshot = ContractCheckTests.With(contract);
            snapshot.Ut = ut;
            snapshot.Body = "Mun";
            snapshot.Situation = "ORBITING";
            return snapshot;
        }

        /// <summary>The first plan flown to the Mun, then a Debrief asking for a revised plan; Claudia then proposes each revision in turn.</summary>
        private MissionControl Revising(params string[][] revisions)
        {
            return Revising("", FirstPlan, revisions);
        }

        /// <summary>#1381: as above, from the player's <paramref name="words"/> and <paramref name="firstPlan"/>.</summary>
        private MissionControl Revising(string words, string[] firstPlan, params string[][] revisions)
        {
            var responses = new List<ScriptedResponse>
            {
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, ContractWatchTests.PlanJson(firstPlan))),
                ContractWatchTests.MissionPlanKind(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ContractWatchTests.Reply("{\"kind\":\"debrief\",\"say\":\"Off-Nominal.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"alert\":null,"
                    + "\"debrief\":{\"outcome\":\"Off-Nominal\",\"why\":\"Plan again.\",\"what_happened\":\"The Mun is met.\",\"goal_reached\":false,\"next\":\"Revise.\",\"follow_up\":\"revised_plan\"}}"),
            };
            responses.AddRange(revisions.Select((r, i) => ToolUseStreams.ToolUse("r" + i, (ProposedPlans.ToolName, ContractWatchTests.PlanJson(r)))));
            responses.Add(ContractWatchTests.MissionPlanKind());
            _transport = new StreamingFakeTransport(responses.ToArray());
            var client = new ClaudeClient(new CraftCheckRouter(_transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Clock = () => new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            var contract = ContractCheckTests.Exploration("c1", 10000, "Mun:ORBIT", "Minmus:ORBIT");
            contract.State = "Active";
            mission.Send(ContractCheckTests.With(contract));
            mission.Send(new StartMission(words, 25m, Sonnet));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(MunMet(EndedAt));
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), start.RunId, EndedAt));
            mission.Send(new UtAdvanced(EndedAt + 61));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            return mission;
        }

        private string Answers
        {
            get { return string.Join("\n", _transport.Requests.Select(r => r.Body)).Replace("\\\\\\\"", "\"").Replace("\\\"", "\""); }
        }

        [Fact]
        public void ARevisionKeepingASecondMunLegAfterTheMunIsMetIsSentBackAsServingNothing()
        {
            var mission = Revising(
                new[] { "Transfer to the Mun and capture into low orbit", "Capture into low Mun orbit again", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" },
                new[] { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" });

            Assert.Contains("leg 2 \"Capture into low Mun orbit again\" serves no Requirement still open and none of your words", Answers);
            Assert.DoesNotContain("Capture into low Mun orbit again", mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text);
        }

        [Fact]
        public void ARevisionThatDropsTheMetVisitAndKeepsTheRestInTheRebuiltOrderIsOffered()
        {
            var mission = Revising(new[] { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Return to Kerbin" });

            Assert.DoesNotContain("serves no Requirement", Answers);
            Assert.DoesNotContain("a second time", Answers);
            Assert.Contains("Transfer to Minmus and capture into low orbit", mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text);
        }

        private const string BackToTheMun = "Orbit the Mun, then orbit Minmus, then back to the Mun, and come home.";

        [Fact]
        public void ARevisionKeepsTheRepeatTheWordsAskFor_ButAnotherMunVisitIsSentBack()
        {
            var mission = Revising(BackToTheMun,
                new[] { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Transfer back to the Mun and capture into low orbit", "Return to Kerbin" },
                new[] { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Transfer back to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit again", "Transfer back to the Mun and capture into low orbit again", "Return to Kerbin" },
                new[] { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit", "Transfer back to the Mun and capture into low orbit", "Return to Kerbin" });

            Assert.Contains("leg 5 \"Transfer back to the Mun and capture into low orbit again\" visits the Mun a second time", Answers);
            Assert.DoesNotContain("leg 3 \"Transfer back to the Mun and capture into low orbit\" serves no", Answers);
            var offered = mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan).Text;
            Assert.Contains("Transfer back to the Mun and capture into low orbit", offered);
            Assert.DoesNotContain("capture into low orbit again", offered);
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
        }
    }
}
