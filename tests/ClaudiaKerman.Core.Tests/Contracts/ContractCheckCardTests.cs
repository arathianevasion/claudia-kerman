// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1022, seam 1: Start Mission with accepted exploration Contracts posts Mission Control's Contract check card, with no request to
    /// Claude; "Plan these" starts the Mission and its first request carries the Contract Route section with the player's words.
    /// </summary>
    public sealed class ContractCheckCardTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private StreamingFakeTransport _transport;
        private CraftCheckRouter _router;

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(GameSnapshot snapshot, bool route = true, params ScriptedResponse[] responses)
        {
            _transport = new StreamingFakeTransport(responses.Length > 0 ? responses : new[] { ScriptedResponse.Fixture("mission-plan.sse") });
            _router = route ? new CraftCheckRouter(_transport) : null;
            var client = new ClaudeClient(route ? (IHttpTransport)_router : _transport, () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            // A crewed Mission started in Flight: the Mun Shot's crew is Claudia, aboard already.
            mission.Send(snapshot);
            return mission;
        }

        /// <summary>Every request sent to Claude, the Craft Check's included.</summary>
        private int Requests
        {
            get { return _transport.Requests.Count + (_router != null ? _router.CheckRequests.Count : 0); }
        }

        private static StartMission Start(string words)
        {
            return new StartMission(words, 25m, Sonnet);
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason + " " + mission.View.StartForm.Refusal + " check " + mission.View.CraftCheck.State + " " + mission.View.CraftCheck.Verdict + " " + mission.View.CraftCheck.Message + " " + string.Join(",", mission.View.CraftCheck.Findings.Select(f => f.Code)));
        }

        /// <summary>Some time for anything that was sent to come back.</summary>
        private void Settle()
        {
            for (var i = 0; i < 20; i++)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
        }

        private static string Text(HttpRequestSpec request)
        {
            var texts = new List<string>();
            using (var doc = JsonDocument.Parse(request.Body))
                foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
                {
                    var content = message.GetProperty("content");
                    if (content.ValueKind == JsonValueKind.String) texts.Add(content.GetString());
                    else foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("text", out var text)) texts.Add(text.GetString());
                }
            return string.Join("\n", texts);
        }

        private static GameSnapshot TwoAtTheMunAndAHaul()
        {
            return ContractCheckTests.With(
                ContractCheckTests.Fixture("ExplorationContract.json"),
                ContractCheckTests.Exploration("c-land", 30000, "Mun:LANDING"),
                ContractCheckTests.Fixture("SurveyContract.json"));
        }

        [Fact]
        public void StartMissionWithAcceptedContractsPostsTheCardAndAsksClaudeNothing()
        {
            var mission = Control(TwoAtTheMunAndAHaul());

            mission.Send(Start(""));
            Settle();

            var card = mission.View.ContractCheck;
            Assert.NotNull(card);
            Assert.False(card.Folded);
            Assert.Equal(3, card.Accepted);
            Assert.Equal(2, card.Rows.Count);
            Assert.All(card.Rows, r => Assert.True(r.Ticked));
            Assert.All(card.Rows, r => Assert.Equal("Mun", r.Phase));
            Assert.All(card.Rows, r => Assert.Matches(@"^Mun · \+[0-9,]+ m/s · no deadline$", r.Detail(mission.View.Ut)));
            Assert.True(card.RouteDeltaV > 0);
            Assert.True(card.CraftDeltaV > card.RouteDeltaV + card.Reserve);
            Assert.Equal(15, card.ReservePercent);
            var left = Assert.Single(card.NotThisFlight);
            Assert.StartsWith("not yet: ", left.Reason);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.StartsWith("2 of 3 Contracts · route ", card.Headline);
            Assert.Equal("Plan these 2", card.PlanLabel);
            Assert.True(card.CanPlan);
            Assert.Equal(0, Requests);
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        /// <summary>#1028: the card says when the planners' re-pricing moved the suggestion off the Delta-v Map's best set.</summary>
        [Fact]
        public void TheCardCarriesThePlannersRepricingNote()
        {
            var mission = Control(ContractSearchTests.DunaOnTheEdge(10283, ContractCheckTests.Exploration("duna", 30000, "Duna:FLYBY"), ContractCheckTests.Exploration("mun", 5000, "Mun:FLYBY")));

            mission.Send(Start(""));
            Settle();

            var card = mission.View.ContractCheck;
            Assert.Equal("mun", card.Rows.Single(r => r.Ticked).Guid);
            Assert.StartsWith("Priced with the planners, the best set by the Delta-v Map came out ", card.SearchNote);
            Assert.Equal(0, Requests);
        }

        /// <summary>#1033: the pad craft with an empty seat and an orbital rescue whose pod the game spawned in Kerbin orbit.</summary>
        private static GameSnapshot ARescue()
        {
            var rescue = ContractCheckTests.Fixture("RecoverAsset.json");
            rescue.Guid = "c-rescue";
            var snapshot = ContractCheckTests.With(rescue);
            snapshot.Craft.Seats = 2;
            snapshot.ContractVessels.Add(new SnapshotVessel
            {
                Id = 4401, Name = "Gusdun's Capsule", Body = "Kerbin", Situation = "ORBITING", PeriapsisM = 120000, ApoapsisM = 120000, Inclination = 0, ContractGuid = "c-rescue",
            });
            return snapshot;
        }

        [Fact]
        public void WithARescueTickedTheStartFormsTargetLineNamesItsVesselFromTheContractAndClearUnticksIt()
        {
            var mission = Control(ARescue());
            mission.Send(Start(""));
            Settle();
            Assert.True(mission.View.ContractCheck.Rows.Single().Ticked);

            var line = TargetVessel.Choose("", TargetPick.Auto, mission.View.StartForm.Targets).Line;
            mission.Send(new StartFormChanged("", TargetPick.Cleared));

            Assert.Equal("Target Vessel: Gusdun's Capsule · Kerbin · orbiting · from the Contract", line);
            Assert.False(mission.View.ContractCheck.Rows.Single().Ticked);
            Assert.Null(mission.View.StartForm.Targets.Contract);
        }

        [Fact]
        public void KspsTargetNeverPushesARescueOutOnlyTheGoalOrThePickDoes()
        {
            var snapshot = ARescue();
            var station = new SnapshotVessel { Id = 5000, Name = "Kerbin Station", Body = "Kerbin", Situation = "ORBITING" };
            snapshot.Vessels.Add(station);
            snapshot.KspTarget = new SnapshotTarget { Id = 5000, Name = "Kerbin Station", Body = "Kerbin", Situation = "ORBITING" };
            var mission = Control(snapshot);

            mission.Send(Start("Rendezvous and bring the kerbal home."));
            Settle();

            Assert.True(mission.View.ContractCheck.Rows.Single().Ticked);
        }

        [Fact]
        public void PickingAnotherVesselWithTheCardUpUnticksTheRescue()
        {
            var snapshot = ARescue();
            var station = new SnapshotVessel { Id = 5000, Name = "Kerbin Station", Body = "Kerbin", Situation = "ORBITING" };
            snapshot.Vessels.Add(station);
            var mission = Control(snapshot);
            mission.Send(Start(""));
            Settle();

            mission.Send(new StartFormChanged("", TargetPick.Of(station)));

            Assert.False(mission.View.ContractCheck.Rows.Single().Ticked);
        }

        [Fact]
        public void ARescueTickedAgainAfterClearIsUntickedAtPlanTheseNotFlownWithoutItsVessel()
        {
            var mission = Control(ARescue());
            mission.Send(Start(""));
            Settle();
            mission.Send(new StartFormChanged("", TargetPick.Cleared));
            mission.Send(new TickContract("c-rescue", true));

            mission.Send(new PlanContracts());
            Settle();

            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.False(mission.View.ContractCheck.Rows.Single().Ticked);
        }

        [Fact]
        public void PlanTheseTakesTheRescuesVesselAsTheMissionsTargetVessel()
        {
            var mission = Control(ARescue());
            mission.Send(Start(""));
            Settle();

            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Contains(mission.View.Log, e => e.Text == "Target Vessel: Gusdun's Capsule (from the Contract).");
            Assert.Contains("Target Vessel: \"Gusdun's Capsule\"", Text(Assert.Single(_transport.Requests)));
        }

        [Fact]
        public void AfterStartAMissionWhoseTargetVesselCameFromAContractShowsChooseAndClear()
        {
            var mission = Control(ARescue());
            mission.Send(Start(""));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.True(mission.View.TargetNeeded);
            Assert.True(mission.View.CanChangeTarget);
            Assert.Equal("Gusdun's Capsule", mission.View.TargetVesselName);
        }

        [Fact]
        public void ClearAfterStartDropsTheTargetVesselContractAndSaysSo()
        {
            var mission = Control(ARescue());
            mission.Send(Start(""));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            mission.Send(new ChangeTarget(TargetPick.Cleared));

            Assert.Null(mission.View.TargetVesselId);
            Assert.Contains(mission.View.Log, e => e.Text == "Contract dropped from the Mission: RecoverAsset (its Target Vessel is changed).");
            Assert.False(mission.View.TargetNeeded);
        }

        [Fact]
        public void PickingAnotherVesselAfterStartDropsTheTargetVesselContractAndSaysSo()
        {
            var snapshot = ARescue();
            var station = new SnapshotVessel { Id = 5000, Name = "Kerbin Station", Body = "Kerbin", Situation = "ORBITING" };
            snapshot.Vessels.Add(station);
            var mission = Control(snapshot);
            mission.Send(Start(""));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            mission.Send(new ChangeTarget(TargetPick.Of(station)));

            Assert.Equal(5000u, mission.View.TargetVesselId);
            Assert.Contains(mission.View.Log, e => e.Text == "Contract dropped from the Mission: RecoverAsset (its Target Vessel is changed).");
        }

        [Fact]
        public void PlanTheseSendsOneStartMissionRequestWithTheContractRouteAndThePlayersWords()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start("Also run a crew report in Mun orbit."));
            Settle();
            Assert.Equal(0, Requests);

            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var first = Assert.Single(_transport.Requests);
            var text = Text(first);
            Assert.Contains("Contract Route", text);
            Assert.Contains("Also run a crew report in Mun orbit.", text);
            Assert.Contains("land on the Mun", text);
            Assert.Contains("(phase: Mun)", text);
            Assert.Contains("not yet: low-flying surveys need an aircraft over the waypoint", text);
            // The card folds to its one line, which stays in the Mission's transcript.
            Assert.Null(mission.View.ContractCheck);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Mission Control · Contract check: 2 of 3 Contracts chosen", StringComparison.Ordinal));
        }

        [Fact]
        public void TheFoldedSummaryMarksTheRouteAsAnEstimateAfterATickButNotOtherwise()
        {
            var ticked = Control(TwoAtTheMunAndAHaul());
            ticked.Send(Start(""));
            ticked.Send(new TickContract("c-land", false));
            ticked.Send(new TickContract("c-land", true));
            Assert.Contains("(estimate)", ticked.View.ContractCheck.Headline);
            ticked.Send(new PlanContracts());
            DrainUntil(ticked, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains(ticked.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Mission Control · Contract check: 2 of 3 Contracts chosen · route ≈ ", StringComparison.Ordinal) && e.Text.Contains(" m/s (estimate) · "));

            var untouched = Control(TwoAtTheMunAndAHaul());
            untouched.Send(Start(""));
            Assert.DoesNotContain("(estimate)", untouched.View.ContractCheck.Headline);
            untouched.Send(new PlanContracts());
            DrainUntil(untouched, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains(untouched.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Mission Control · Contract check: 2 of 3 Contracts chosen", StringComparison.Ordinal) && !e.Text.Contains("(estimate)"));
        }

        [Fact]
        public void WithNothingTypedTheMissionPlansFromTheContractsAlone()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var text = Text(Assert.Single(_transport.Requests));
            Assert.Contains("Contract Route", text);
            Assert.Contains("No words typed", text);
            Assert.Contains("land on the Mun", mission.View.Goal);
        }

        [Fact]
        public void StartMissionAgainReplacesTheCardWithTheNewWords()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));
            Assert.False(mission.View.ContractCheck.CanPlanWithout);

            mission.Send(Start("Fly to Minmus and capture into orbit."));

            Assert.Equal("Fly to Minmus and capture into orbit.", mission.View.ContractCheck.Words);
            Assert.True(mission.View.ContractCheck.CanPlanWithout);
            Assert.Equal(0, Requests);
        }

        [Fact]
        public void WithTheCraftsDeltaVUnknownTheContractsArentDroppedSilently()
        {
            var snapshot = TwoAtTheMunAndAHaul();
            snapshot.DeltaVSimulator = false;
            var mission = Control(snapshot);

            mission.Send(Start("Fly to Minmus and capture into orbit."));
            Settle();

            Assert.Null(mission.View.ContractCheck);
            Assert.Contains("can't be priced", mission.View.StartForm.Refusal);
            Assert.Equal(0, Requests);
        }

        [Fact]
        public void PlanWithoutContractsPlansThePlayersWordsAlone()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start("Fly to Minmus and capture into orbit."));
            Assert.True(mission.View.ContractCheck.CanPlanWithout);
            mission.Send(new PlanWithoutContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var text = Text(Assert.Single(_transport.Requests));
            Assert.Contains("Fly to Minmus and capture into orbit.", text);
            Assert.DoesNotContain("Contract Route", text);
            Assert.Equal("Fly to Minmus and capture into orbit.", mission.View.Goal);
        }

        [Fact]
        public void PlanWithoutContractsNeedsWords()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));

            Assert.False(mission.View.ContractCheck.CanPlanWithout);
            mission.Send(new PlanWithoutContracts());
            Settle();
            Assert.Equal(0, Requests);
            Assert.NotNull(mission.View.ContractCheck);
        }

        [Fact]
        public void WithNoAcceptedContractsStartMissionIsUnchanged()
        {
            var snapshot = ContractCheckTests.Pad();
            var mission = Control(snapshot);

            mission.Send(Start("Fly to Minmus and capture into orbit."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Null(mission.View.ContractCheck);
            Assert.Single(_router.CheckRequests);
            var text = Text(Assert.Single(_transport.Requests));
            Assert.DoesNotContain("Contract Route", text);
        }

        [Fact]
        public void WhenTheTypedWordsAloneDontFitTheCraftCheckGivesItsBlockerAndNoContractIsSuggested()
        {
            var snapshot = TwoAtTheMunAndAHaul();
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= 0.25;
            var mission = Control(snapshot, false, ScriptedResponse.Fixture("craft-check-fail.sse"));

            mission.Send(Start("Fly to Minmus, capture into orbit and land, then return to Kerbin."));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            Assert.Null(mission.View.ContractCheck);
            Assert.StartsWith("Contract check card skipped: the typed words don't fit", Assert.Single(KspLog(mission), l => l.StartsWith("Contract check card")));
            Assert.Equal(CraftVerdict.Fail, mission.View.CraftCheck.Verdict);
            Assert.Contains(mission.View.CraftCheck.Findings, f => f.Code == "delta_v_short");
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        // ---- #1090: typed words that don't fit a craft that can't reach orbit ----

        private const string WordsNeedingOrbit = "Fly to Minmus and capture into orbit.";

        private static GameSnapshot ReachesTheBandButNotOrbit(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            ContractCheckTests.BoosterOnly(snapshot);
            return snapshot;
        }

        [Fact]
        public void TypedWordsThatDontFitGoToTheCraftCheckBlockerWhichNamesTheHopContractByTitle()
        {
            var mission = Control(ReachesTheBandButNotOrbit(ContractCheckTests.BoosterTest("c-test")), false, ScriptedResponse.Fixture("craft-check-fail.sse"));

            mission.Send(Start(WordsNeedingOrbit));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            Assert.Equal("", mission.View.StartForm.Refusal);
            Assert.Null(mission.View.ContractCheck);
            Assert.Equal(CraftVerdict.Fail, mission.View.CraftCheck.Verdict);
            var blocker = Assert.Single(mission.View.CraftCheck.Findings, f => f.Code == "delta_v_short");
            Assert.Contains("\nWithout your words this craft could fly: PartTest.", blocker.Explanation);
            Assert.DoesNotContain("liquidEngine", blocker.Explanation);
            Assert.Equal(1, blocker.Explanation.Split(new[] { "Without your words" }, StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void TypedWordsThatDontFitWithNoHopContractGiveTheCraftCheckBlockerAsBefore()
        {
            var mission = Control(ReachesTheBandButNotOrbit(ContractCheckTests.Exploration("c-mun", 10000, "Mun:ORBIT")), false, ScriptedResponse.Fixture("craft-check-fail.sse"));

            mission.Send(Start(WordsNeedingOrbit));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            Assert.Equal("", mission.View.StartForm.Refusal);
            Assert.Equal(CraftVerdict.Fail, mission.View.CraftCheck.Verdict);
            Assert.DoesNotContain(mission.View.CraftCheck.Findings, f => f.Explanation.Contains("Without your words"));
        }

        [Fact]
        public void WithNoWordsTheSameCraftGetsTheHopContractInThisFlight()
        {
            var mission = Control(ReachesTheBandButNotOrbit(ContractCheckTests.BoosterTest("c-test")));

            mission.Send(Start(""));
            Settle();

            Assert.Equal("c-test", Assert.Single(mission.View.ContractCheck.Rows).Guid);
            Assert.Equal("", mission.View.StartForm.Refusal);
        }

        // ---- #1023: ticking, unticking and Swap in on the card ----

        private static GameSnapshot MunAndMinmusTheCraftFliesOneOf()
        {
            return ContractCheckTests.EitherMoon(ContractCheckTests.Exploration("mun", 10000, "Mun:ORBIT"), ContractCheckTests.Exploration("minmus", 20000, "Minmus:ORBIT"));
        }

        [Fact]
        public void TickingAndUntickingReSurfacesTheCardInPlaceWithNoRequest()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));
            var before = mission.View.ContractCheck;
            var both = before.RouteDeltaV;

            mission.Send(new TickContract("c-land", false));

            var card = mission.View.ContractCheck;
            Assert.True(card.RouteIsEstimate);
            Assert.Contains("(estimate)", card.Headline);
            Assert.False(card.Rows.Single(r => r.Guid == "c-land").Ticked);
            Assert.True(card.RouteDeltaV <= both);
            Assert.StartsWith("1 of 3 Contracts · route ", card.Headline);
            Assert.Equal("Plan these 1", card.PlanLabel);
            Assert.True(card.CanPlan);

            mission.Send(new TickContract("c-land", true));

            Assert.Equal(both, mission.View.ContractCheck.RouteDeltaV, 6);
            Assert.Equal("Plan these 2", mission.View.ContractCheck.PlanLabel);
            Settle();
            Assert.Equal(0, Requests);
        }

        [Fact]
        public void UntickingEverythingLeavesNothingToPlan()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));
            foreach (var row in mission.View.ContractCheck.Rows.ToList()) mission.Send(new TickContract(row.Guid, false));

            Assert.False(mission.View.ContractCheck.CanPlan);
            Assert.Equal("Pick a Contract", mission.View.ContractCheck.PlanLabel);
            Assert.Equal("Tick a Contract to plan it.", mission.View.ContractCheck.TickNote);
        }

        /// <summary>#1261: the card says its ticks are Mission Control's suggestion and a click leaves one out.</summary>
        [Fact]
        public void TheCardSaysTickedRowsAreASuggestionAClickLeavesOut()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));

            var note = mission.View.ContractCheck.TickNote;

            Assert.Contains("suggestion for this flight", note);
            Assert.Contains("A click leaves one out", note);
        }

        [Fact]
        public void PlanTheseIsGreyedWithTheShortfallWhenTheTickedSetDoesntFitAndSendsNothing()
        {
            var mission = Control(MunAndMinmusTheCraftFliesOneOf());
            mission.Send(Start(""));
            Assert.True(mission.View.ContractCheck.CanPlan);

            mission.Send(new TickContract("mun", true));

            var card = mission.View.ContractCheck;
            Assert.False(card.CanPlan);
            Assert.Matches(@"^[0-9,]+ m/s short", card.Shortfall);
            Assert.StartsWith("Plan these 2", card.PlanLabel);
            mission.Send(new PlanContracts());
            Settle();
            Assert.Equal(0, Requests);
            Assert.NotNull(mission.View.ContractCheck);
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        [Fact]
        public void SwapInTradesOneSetForAnotherInOneClickAndPlanTheseThenPlansIt()
        {
            var mission = Control(MunAndMinmusTheCraftFliesOneOf());
            mission.Send(Start(""));
            var offer = mission.View.ContractCheck.NotThisFlight.Single().Swap;
            Assert.NotNull(offer);

            mission.Send(new SwapInContract("mun"));

            var card = mission.View.ContractCheck;
            Assert.True(card.CanPlan);
            Assert.Equal("Plan these 1", card.PlanLabel);
            Assert.True(card.NotThisFlight.Single().Ticked);
            Assert.False(card.Rows.Single().Ticked);
            Assert.Equal(0, Requests);

            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var text = Text(Assert.Single(_transport.Requests));
            Assert.Contains("orbit the Mun", text);
            Assert.DoesNotContain("Minmus", text.Substring(0, text.IndexOf("Not this flight", StringComparison.Ordinal) < 0 ? text.Length : text.IndexOf("Not this flight", StringComparison.Ordinal)));
        }

        [Fact]
        public void TheCraftCheckJudgesTheChosenSetNotAFreshSearch()
        {
            var mission = Control(TwoAtTheMunAndAHaul());
            mission.Send(Start(""));
            mission.Send(new TickContract("c-land", false));
            var chosen = mission.View.ContractCheck.Rows.Single(r => r.Ticked);
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            // The Mission's goal is the chosen Contract's own route: the unticked one is not there though it fit.
            Assert.DoesNotContain("land on the Mun", mission.View.Goal);
            Assert.Contains("1 of 3 Contracts chosen", mission.View.Log.First(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Mission Control · Contract check", StringComparison.Ordinal)).Text);
            var text = Text(Assert.Single(_transport.Requests));
            Assert.Contains("The player chose 1 of 3", text);
            Assert.Contains("the player left it out", text);
            Assert.NotNull(chosen);
        }

        // ---- #1262: the card's life in KSP.log, and the Contracts read saved at Start Mission ----

        private static List<string> KspLog(MissionControl mission)
        {
            return mission.TakeCommands().OfType<LogToGame>().Select(c => c.Text).ToList();
        }

        [Fact]
        public void PostingTheCardLogsOneBlockWithTheFiguresAndEachContractsGuidTitleVerdictReasonAndTickState()
        {
            var mission = Control(TwoAtTheMunAndAHaul());

            mission.Send(Start(""));

            var block = Assert.Single(KspLog(mission), l => l.StartsWith("Contract check card posted: 2 of 3 Contracts"));
            var card = mission.View.ContractCheck;
            Assert.Contains("craft delta-v " + Math.Round(card.CraftDeltaV) + " m/s", block);
            Assert.Contains("route delta-v " + Math.Round(card.RouteDeltaV) + " m/s", block);
            Assert.Contains("reserve " + Math.Round(card.Reserve) + " m/s (15%)", block);
            foreach (var row in card.Rows) Assert.Contains(row.Guid + " | " + row.Title + " | row | ticked", block);
            var left = card.NotThisFlight.Single();
            Assert.Contains(left.Guid + " | " + left.Title + " | Not this flight (NotYet) | ", block);
            Assert.Contains(left.Reason, block);
        }

        [Fact]
        public void EachTickAndSwapLogsTheGuidOnOrOffTheResultingSetAndWhetherPlanTheseCanBePressed()
        {
            var mission = Control(MunAndMinmusTheCraftFliesOneOf());
            mission.Send(Start(""));
            mission.TakeCommands();

            mission.Send(new TickContract("mun", true));
            var tick = Assert.Single(KspLog(mission));
            Assert.StartsWith("TickContract mun: on; ticked now [", tick);
            Assert.EndsWith("Plan these can't be pressed (" + mission.View.ContractCheck.Shortfall + ")", tick);

            mission.Send(new TickContract("mun", false));
            Assert.EndsWith("Plan these can be pressed", Assert.Single(KspLog(mission)));

            mission.Send(new SwapInContract("mun"));
            var swap = Assert.Single(KspLog(mission));
            Assert.StartsWith("SwapInContract mun: on; ticked now [mun]; ", swap);
            Assert.EndsWith("Plan these can be pressed", swap);
        }

        [Fact]
        public void PlanTheseAndPlanWithoutContractsEachLogOneLine()
        {
            var these = Control(TwoAtTheMunAndAHaul());
            these.Send(Start(""));
            var guids = these.View.ContractCheck.Rows.Select(r => r.Guid).ToArray();
            these.TakeCommands();
            these.Send(new PlanContracts());
            Assert.Equal("Plan these: " + string.Join(", ", guids) + ".", Assert.Single(KspLog(these), l => l.StartsWith("Plan these")));
            DrainUntil(these, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var without = Control(TwoAtTheMunAndAHaul());
            without.Send(Start("Fly to Minmus and capture into orbit."));
            without.TakeCommands();
            without.Send(new PlanWithoutContracts());
            Assert.Equal("Plan without Contracts: the player's words alone.", Assert.Single(KspLog(without), l => l.StartsWith("Plan without")));
            DrainUntil(without, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        [Fact]
        public void ASkippedOrRefusedCardLogsOneLineSaying()
        {
            var none = Control(ContractCheckTests.Pad());
            none.Send(Start("Fly to Minmus and capture into orbit."));
            Assert.Equal("Contract check card skipped: no accepted Contracts.", Assert.Single(KspLog(none), l => l.StartsWith("Contract check card")));

            var unpriced = TwoAtTheMunAndAHaul();
            unpriced.DeltaVSimulator = false;
            var refused = Control(unpriced);
            refused.Send(Start("Fly to Minmus and capture into orbit."));
            Assert.Contains("can't be priced", Assert.Single(KspLog(refused), l => l.StartsWith("Contract check card refused: ")));
        }

        [Fact]
        public void StartMissionWithAcceptedContractsWritesEachContractsReadToTheSavesContractsFolderBeforeAnyAnswer()
        {
            var mission = Control(TwoAtTheMunAndAHaul());

            mission.Send(Start(""));

            var folder = System.IO.Path.Combine(_save.Sub, "contracts");
            Assert.Equal(3, System.IO.Directory.GetFiles(folder, "*.json").Length);
            Assert.Contains(KspLog(mission), l => l.StartsWith("Contracts read at Start Mission saved (3) to " + folder));
        }

        [Fact]
        public void StartMissionWithNoAcceptedContractsWritesNoContractsFolder()
        {
            var mission = Control(ContractCheckTests.Pad());

            mission.Send(Start("Fly to Minmus and capture into orbit."));

            Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(_save.Sub, "contracts")));
        }
    }
}
