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
    /// #818: a manual stretch needs grounds (the player asked, the Mission Goal's words, EVA science), a refused one gets one retry
    /// and then stops the Mission, and a Failed Debrief that is only a landing miss is sent back once. The word list and the checks
    /// are tested directly; the rules are driven through the Mission seam with hand-written streams.
    /// </summary>
    public sealed class ManualGroundsTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string CraftId = "c0ffee00c0ffee00c0ffee00c0ffee00";
        private const string Landing = "local r is deorbit().";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The word list ----

        [Theory]
        [InlineData("I'll fly the landing by hand.")]
        [InlineData("Land it MANUALLY")]
        [InlineData("Let me fly the descent")]
        [InlineData("I will fly it")]
        [InlineData("I’ll fly it")]
        public void TheGoalOrAPlayersWordsAskForAManualStretchWithoutRegardToCase(string words)
        {
            Assert.True(ManualGrounds.Asked(words));
            Assert.True(ManualGrounds.Fold(false, words));
        }

        [Theory]
        [InlineData("Why are you asking me to fly manually?")]
        [InlineData("Is Manual Control available?")]
        [InlineData("Please avoid manual stretches")]
        [InlineData("nothing manual")]
        [InlineData("Land without manual input")]
        [InlineData("no need for anything manual here")]
        [InlineData("Why that orbit?")]
        [InlineData("never take manual control")]
        [InlineData("don't fly it manually")]
        [InlineData("don't hand over")]
        public void AQuestionOrANegationOrNoWordsIsNotAYes(string words)
        {
            Assert.False(ManualGrounds.Fold(false, words));
        }

        [Fact]
        public void AQuestionLeavesAnEarlierYesStandingAndALaterNoCancelsItAndALaterYesStandsAgain()
        {
            Assert.True(ManualGrounds.Fold(true, "Why are you asking me to fly manually?"));
            Assert.False(ManualGrounds.Fold(true, "No manual stretch, just land as close as you can"));
            Assert.False(ManualGrounds.Fold(true, "Please avoid manual stretches"));
            Assert.True(ManualGrounds.Fold(false, "ok, let me fly it"));
            Assert.True(ManualGrounds.Fold(true, "Is that safe?"));
            Assert.False(ManualGrounds.Fold(ManualGrounds.Asked("Land by hand"), "don't hand over"));
        }

        [Fact]
        public void TheLogLinesThatAreNotTheirTypedWordsAreListed()
        {
            Assert.Contains("Mission Goal: ", ManualGrounds.NotTyped);
            Assert.Contains("EVA science done by hand", ManualGrounds.NotTyped);
        }

        [Fact]
        public void OnlyNamedEvaScienceServesAManualStretch()
        {
            Assert.True(ManualGrounds.ServesEva(PlanText("Take EVA science at the station."), ""));
            Assert.True(ManualGrounds.ServesEva(PlanText("Dock."), "Surface sample on the Mun"));
            Assert.False(ManualGrounds.ServesEva(PlanText("No EVA possible with this craft."), ""));
            Assert.False(ManualGrounds.ServesEva(PlanText("No EVA science on this flight."), ""));
            Assert.False(ManualGrounds.ServesEva(PlanText("Dock by hand."), ""));
        }

        private static SequencePlan PlanText(string intent)
        {
            return new SequencePlan { Name = "Hand docking", Intent = intent, Why = "", Target = "", Tolerances = "" };
        }

        [Theory]
        [InlineData("land within 1 km of KSC", true)]
        [InlineData("A precision landing test at KSC", true)]
        [InlineData("Land exactly on the pad", true)]
        [InlineData("land on target", true)]
        [InlineData("Pinpoint the crater", true)]
        [InlineData("Return to KSC and land", false)]
        [InlineData("Orbit the Mun within 10 days", false)]
        [InlineData("Land within 5 m/s of vertical speed", false)]
        [InlineData("Land within 100 m of the pad", true)]
        [InlineData("Rendezvous within 1 km of the station, then land at KSC", false)]
        [InlineData("Land at KSC to within 50 m", true)]
        public void APrecisionGoalMakesTheSpotThePoint(string goal, bool precision)
        {
            Assert.Equal(precision, ManualGrounds.PrecisionGoal(goal));
        }

        [Theory]
        [InlineData("local r is deorbit().", true)]
        [InlineData("local r is land(lexicon(\"target\", \"auto\")).", true)]
        [InlineData("local r is return().", true)]
        [InlineData("local r is reentry().", true)]
        [InlineData("local r is circularize().", false)]
        [InlineData("", false)]
        public void ALandingStepCallsALandingBlock(string script, bool landing)
        {
            Assert.Equal(landing, ManualGrounds.LandingStep(script));
        }

        [Fact]
        public void DownAliveNeedsALandedOrSplashedCraftAndNoRecordedDeathOfTheRunsCrew()
        {
            var crew = new[] { "Claudia Kerman", "Bob Kerman" };
            var none = new Dictionary<string, double>();
            Assert.True(ManualGrounds.DownAlive(Down("LANDED", "Claudia Kerman"), crew, none, 100));
            // Bob on EVA or moved to another vessel is not in the snapshot's crew, and is not dead.
            Assert.True(ManualGrounds.DownAlive(Down("SPLASHED", "Claudia Kerman"), crew, none, 100));
            Assert.False(ManualGrounds.DownAlive(Down("LANDED", "Claudia Kerman"), crew, new Dictionary<string, double> { { "Bob Kerman", 150 } }, 100));
            // A death before the run started, or of a Kerbal not in the run, doesn't count.
            Assert.True(ManualGrounds.DownAlive(Down("LANDED", "Claudia Kerman"), crew, new Dictionary<string, double> { { "Bob Kerman", 50 } }, 100));
            Assert.True(ManualGrounds.DownAlive(Down("LANDED", "Claudia Kerman"), crew, new Dictionary<string, double> { { "Val Kerman", 150 } }, 100));
            Assert.False(ManualGrounds.DownAlive(Down("FLYING", "Claudia Kerman"), crew, none, 100));
            Assert.False(ManualGrounds.DownAlive(null, crew, none, 100));
            Assert.False(ManualGrounds.DownAlive(Down("LANDED"), null, none, 100));
        }

        // ---- The refusal ----

        [Fact]
        public void AManualStretchWithNoRequestAndNoGoalWordsIsRefusedAndAutoModeStaysOn()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Bare(), Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, AutoModeOn());
            ToSequenceTurn(mission, Goal);

            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("Refused: the Sequence Plan", LastUserText(transport.Requests[2]));
            Assert.True(mission.View.AutoMode);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.SequencePlan && e.Title == "Hand docking");
        }

        [Fact]
        public void AManualStretchAfterThePlayerSaysTheyWillFlyItIsAccepted()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"), Revise(Bare()));
            var mission = Control(transport);
            ToSequenceTurn(mission, Goal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            mission.Send(new Reply("I'll fly this one by hand."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy && v.Log.Any(e => e.Title == "Hand docking"));
            Assert.Equal(3, transport.Requests.Count);
        }

        [Fact]
        public void AManualStretchForNamedEvaScienceIsAccepted()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan-manual.sse"));
            var mission = Control(transport, AutoModeOn());
            ToSequenceTurn(mission, Goal);

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Title == "Hand docking");
        }

        [Fact]
        public void AGoalSayingIllFlyTheLandingByHandAcceptsAManualStretch()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Bare());
            var mission = Control(transport, AutoModeOn());
            ToSequenceTurn(mission, "Return to KSC. I'll fly the landing by hand.");

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void ThisFlightsTrimPlanUnderAutoModeIsRefusedAndALaterNoStaysARefusal()
        {
            var trim = Trim();
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), trim, Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, AutoModeOn());
            ToSequenceTurn(mission, "Fly to the Mun north pole and return to KSC.");
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            Assert.Contains("Refused", LastUserText(transport.Requests[2]));
            Assert.DoesNotContain("Auto Mode is waiting for you: a Manual stretch", string.Join("\n", mission.View.Log.Select(e => e.Text)));
        }

        [Fact]
        public void ASecondManualRetryWithNoGroundsStopsAutoModeWithARequest()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Bare(), Bare());
            var mission = Control(transport, AutoModeOn());
            ToSequenceTurn(mission, Goal);

            DrainUntil(mission, v => v.Stage == MissionStage.Paused && !v.Busy);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("manual stretch", mission.View.PauseReason);
            Assert.Contains("Retry with a note", mission.View.PauseReason);
            Assert.DoesNotContain("Reply", mission.View.PauseReason);
            Assert.DoesNotContain(mission.View.Log, e => e.Title == "Hand docking");
        }

        [Fact]
        public void AfterTheSecondRefusalRetryWithANoteSayingIllFlyItGetsTheStretchThrough()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Bare(), Bare(), Bare());
            var mission = Control(transport, AutoModeOn());
            ToSequenceTurn(mission, Goal);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused && !v.Busy);
            Assert.Contains(QuickReply.RetryWithNote, mission.View.QuickReplies);

            mission.Send(new RetryWithNote("I'll fly it by hand."));
            DrainUntil(mission, v => v.Stage != MissionStage.Paused && !v.Busy && v.Log.Any(e => e.Title == "Hand docking"));
            Assert.Equal(4, transport.Requests.Count);
            Assert.True(mission.ExportStates()[0].ManualAsk == "true");
        }

        [Fact]
        public void TheAnswerInARetryNoteSurvivesALoadAndALaterNoStillCancelsIt()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Bare(), Bare(), Bare());
            var mission = Control(transport);
            ToSequenceTurn(mission, Goal);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused && !v.Busy);
            mission.Send(new RetryWithNote("I'll fly it by hand."));
            DrainUntil(mission, v => v.Stage != MissionStage.Paused && !v.Busy && v.Log.Any(e => e.Title == "Hand docking"));

            var saved = new List<MissionSaveState> { MissionSaveState.FromValues(mission.ExportStates()[0].ToValues()) };
            Assert.Equal("true", saved[0].ManualAsk);
            var resumed = Control(new StreamingFakeTransport());
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal("true", resumed.ExportStates()[0].ManualAsk);

            resumed.Send(new Reply("On second thought, no manual stretch."));
            Assert.Equal("false", resumed.ExportStates()[0].ManualAsk);
        }

        [Fact]
        public void TakingManualControlWithTheButtonIsAYesAndALaterNoCancelsIt()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequenceTurn(mission, Goal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal("false", mission.ExportStates()[0].ManualAsk);

            mission.Send(new TakeManualControl());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Equal("true", mission.ExportStates()[0].ManualAsk);

            mission.Send(new HandBack("That's enough, no more manual stretches."));
            Assert.Equal("false", mission.ExportStates()[0].ManualAsk);
        }

        [Fact]
        public void AHeldInputTakeoverIsNotAYes()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequenceTurn(mission, Goal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            mission.Send(new ManualInputHeld(1.5));
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Equal("false", mission.ExportStates()[0].ManualAsk);
        }

        // ---- #873: the typed flag ----

        [Fact]
        public void AModWrittenPlayerLineWithManualInItIsNotTheirWordsOnceALineIsFlagged()
        {
            var log = new List<LogEntry>
            {
                new LogEntry { Kind = LogKind.Player, Text = "Mission Goal: " + Goal },
                new LogEntry { Kind = LogKind.Player, Text = "Something new the mod writes: manual stretch ahead" },
                new LogEntry { Kind = LogKind.Player, Text = "Why that orbit?", Typed = true },
            };
            Assert.Equal(new[] { "Why that orbit?" }, ManualGrounds.TypedWords(log));
            Assert.False(ManualGrounds.Asked(Goal, log));
        }

        [Fact]
        public void ALogWithNoFlagsFallsBackToThePrefixRule()
        {
            var log = new List<LogEntry>
            {
                new LogEntry { Kind = LogKind.Player, Text = "Mission Goal: " + Goal },
                new LogEntry { Kind = LogKind.Player, Text = "EVA science done by hand: surface samples" },
                new LogEntry { Kind = LogKind.Player, Text = "I'll fly the landing" },
                new LogEntry { Kind = LogKind.Claudia, Text = "manual" },
            };
            Assert.Equal(new[] { "I'll fly the landing" }, ManualGrounds.TypedWords(log));
            Assert.True(ManualGrounds.Asked(Goal, log));
        }

        [Fact]
        public void ATypedReplyIsFlaggedAndSurvivesASaveALoadAndTheLogRebuild()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"), Bare());
            var mission = Control(transport);
            ToSequenceTurn(mission, Goal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Reply("I'll fly the landing"));
            DrainUntil(mission, v => !v.Busy);

            Assert.True(mission.View.Log.Single(e => e.Text == "I'll fly the landing").Typed);
            Assert.False(mission.View.Log.Single(e => e.Text.StartsWith("Mission Goal: ", StringComparison.Ordinal)).Typed);
            Assert.Equal("true", mission.ExportStates()[0].ManualAsk);

            var saved = new List<MissionSaveState> { MissionSaveState.FromValues(mission.ExportStates()[0].ToValues()) };
            var resumed = Control(new StreamingFakeTransport());
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.True(resumed.View.Log.Single(e => e.Text == "I'll fly the landing").Typed);
            Assert.False(resumed.View.Log.Single(e => e.Text.StartsWith("Mission Goal: ", StringComparison.Ordinal)).Typed);
            Assert.Equal("true", resumed.ExportStates()[0].ManualAsk);
        }

        // ---- The Debrief ----

        [Fact]
        public void AFailedDebriefWithTheCraftDownTheCrewAliveAndALandingStepIsSentBackOnceAndASecondFailedIsAccepted()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), LandingPlan(), Fixture("mission-debrief-failed.sse"), Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var run = ToRunning(mission, Goal);
            mission.Send(Snapshot("LANDED", "Claudia Kerman"));
            mission.Send(new KosRunEnded(Finished(), run.RunId));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Contains(ManualGrounds.SendBack, string.Join("\n", transport.Requests[3].Body));
            Assert.DoesNotContain(ManualGrounds.SendBack, transport.Requests[2].Body);
        }

        [Fact]
        public void ASentBackDebriefRatedOffNominalIsKeptWithNoReducedGoal()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), LandingPlan(), Fixture("mission-debrief-failed.sse"), OffNominal());
            var mission = Control(transport);
            var run = ToRunning(mission, Goal);
            mission.Send(Snapshot("LANDED", "Claudia Kerman"));
            mission.Send(new KosRunEnded(Finished(), run.RunId));

            DrainUntil(mission, v => v.Log.Any(e => e.Kind == LogKind.Debrief && e.Outcome == "Off-Nominal") && !v.Busy);
            Assert.True(transport.Requests.Count >= 4);
            Assert.Equal("Off-Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Outcome);
            Assert.NotEqual(MissionStage.AwaitingReducedGoal, mission.View.Stage);
        }

        [Fact]
        public void APrecisionGoalKeepsFailedWithNoSendBack()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), LandingPlan(), Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var run = ToRunning(mission, "Land within 1 km of KSC.");
            mission.Send(Snapshot("LANDED", "Claudia Kerman"));
            mission.Send(new KosRunEnded(Finished(), run.RunId));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            Assert.Equal(3, transport.Requests.Count);
        }

        [Theory]
        [InlineData("LANDED", true)]
        [InlineData("FLYING", false)]
        public void ACrewLossOrACraftNotDownKeepsFailedWithNoSendBack(string situation, bool crewLost)
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), LandingPlan(), Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var run = ToRunning(mission, Goal, "Claudia Kerman", "Bob Kerman");
            if (crewLost) mission.Send(new KerbalKilled("Bob Kerman"));
            mission.Send(Snapshot(situation, "Claudia Kerman"));
            mission.Send(new KosRunEnded(Finished(), run.RunId));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            Assert.Equal(3, transport.Requests.Count);
        }

        [Fact]
        public void AKerbalOnEvaWithNoDeathRecordedDoesNotKeepFailed()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), LandingPlan(), Fixture("mission-debrief-failed.sse"), Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var run = ToRunning(mission, Goal, "Claudia Kerman", "Bob Kerman");
            mission.Send(Snapshot("LANDED", "Claudia Kerman"));
            mission.Send(new KosRunEnded(Finished(), run.RunId));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Contains(ManualGrounds.SendBack, string.Join("\n", transport.Requests[3].Body));
        }

        [Fact]
        public void ARecordedDeathSurvivesSaveAndLoad()
        {
            var mission = Control(new StreamingFakeTransport(Fixture("mission-plan.sse"), LandingPlan()));
            ToRunning(mission, Goal, "Claudia Kerman", "Bob Kerman");
            mission.Send(new KerbalKilled("Bob Kerman"));
            var states = mission.ExportStates();
            Assert.Contains("Bob Kerman", Assert.Single(states).CrewDeaths);

            var loaded = Control(new StreamingFakeTransport());
            loaded.Send(new MissionsRestored(_save.Path, states));
            Assert.Equal(states[0].CrewDeaths, Assert.Single(loaded.ExportStates()).CrewDeaths);
        }

        [Fact]
        public void AFailedDebriefOnAStepThatIsNotALandingIsNotSentBack()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"), Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var run = ToRunning(mission, Goal);
            mission.Send(Snapshot("LANDED", "Claudia Kerman"));
            mission.Send(new KosRunEnded(Finished(), run.RunId));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            Assert.Equal(3, transport.Requests.Count);
        }

        // ---- Streams and helpers ----

        private static ScriptedResponse Fixture(string name)
        {
            return ScriptedResponse.Fixture(name);
        }

        /// <summary>The manual-stretch fixture with no EVA science named.</summary>
        private static ScriptedResponse Bare()
        {
            return Edited(Fixture("mission-sequence-plan-manual.sse"), ", then take EVA science at the station", "");
        }

        /// <summary>A reply that revises the plan in hand.</summary>
        private static ScriptedResponse Revise(ScriptedResponse response)
        {
            return Edited(response, "\\\"reply_intent\\\":\\\"none\\\"", "\\\"reply_intent\\\":\\\"revise\\\"");
        }

        /// <summary>The shape of the flight's trim plan: a manual stretch to aim the landing, with no EVA in it.</summary>
        private static ScriptedResponse Trim()
        {
            return Edited(Edited(Bare(), "Hand docking", "Aim trim at Kerbin apoapsis"),
                "No Building Block docks, so you fly it.", "No planning tool sizes a burn to a landing site on Kerbin.");
        }

        /// <summary>The Sequence Plan fixture whose script calls a landing block.</summary>
        private static ScriptedResponse LandingPlan()
        {
            return Edited(Fixture("mission-sequence-plan.sse"), "print \\\\\\\"Claudia: link check.\\\\\\\".", Landing);
        }

        private static ScriptedResponse OffNominal()
        {
            return Edited(Fixture("mission-debrief-failed.sse"), "\\\"outcome\\\":\\\"Failed\\\"", "\\\"outcome\\\":\\\"Off-Nominal\\\"");
        }

        private static ScriptedResponse Edited(ScriptedResponse response, string from, string to)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace(from, to);
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user").GetRawText();
        }

        private static PlayerConfig AutoModeOn()
        {
            return new PlayerConfig { DefaultAutoMode = true };
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

        /// <summary>Starts the Mission, approves its Mission Plan, and leaves the Sequence Plan's turn to the test.</summary>
        private void ToSequenceTurn(MissionControl mission, string goal)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
        }

        private StartScript ToRunning(MissionControl mission, string goal, params string[] crewAtStart)
        {
            ToSequenceTurn(mission, goal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            if (crewAtStart.Length > 0) mission.Send(Snapshot("PRELAUNCH", crewAtStart));
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = CraftId;
            return snapshot;
        }

        /// <summary>The craft in a situation, with these Kerbals aboard.</summary>
        private static GameSnapshot Snapshot(string situation, params string[] crew)
        {
            var snapshot = OnPad();
            snapshot.Situation = situation;
            snapshot.Crew = crew.Select(n => new SnapshotCrewMember { Name = n, Trait = "Pilot" }).ToList();
            return snapshot;
        }

        private static GameSnapshot Down(string situation, params string[] crew)
        {
            return Snapshot(situation, crew);
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", false } }, "", "", false);
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
    }
}
