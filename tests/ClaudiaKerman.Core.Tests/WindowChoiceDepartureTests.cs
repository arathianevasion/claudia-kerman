// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #782: a same-parent departure flown straight on from the approved Mission Plan asks for the Transfer Window at the first Sequence
    /// Plan of its chain: a landed craft's ascent, or the departure itself in orbit. A goal that names no way home keeps the Start form and
    /// the Mission Plan from asking, as a return flown on from an approved plan does.
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        private const string Unnamed = "Explore and come home.";
        private const string DepartScript = "local res is transfer_to(lexicon(\\\"target\\\", \\\"Kerbin\\\", \\\"target_pe\\\", 35000)). addons:claudia:report(res).\\n";
        private const string ReturnScript = "local res is return(lexicon(\\\"target_pe\\\", 35000)). addons:claudia:report(res).\\n";

        private static ScriptedResponse DepartPlan(string name = "Return to Kerbin", string script = DepartScript, int step = 1, double due = -1)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: leave.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"" + name + "\",\"plan_step\":" + step + ",\"intent\":\"Leave.\",\"why\":\"Next.\",\"target\":\"Kerbin\","
                + "\"tolerances\":\"\",\"expected_delta_v\":500,\"expected_duration_s\":100000000,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"" + script + "\","
                + "\"due_time_ut\":" + due.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        /// <summary>The craft in a low circular orbit of <paramref name="body"/>.</summary>
        private static GameSnapshot InOrbit(string body, double ut, double deltaV = 20000)
        {
            var snapshot = OnPad(ut, deltaV);
            var at = snapshot.Bodies.Single(b => b.Name == body);
            var low = DeltaVMap.LowOrbit(at);
            snapshot.Situation = "ORBITING";
            snapshot.Body = body;
            snapshot.Orbit = new SnapshotOrbit
            {
                Body = body, SemiMajorAxis = low, Eccentricity = 0, Periapsis = low - at.Radius, Apoapsis = low - at.Radius, Epoch = ut,
                Period = 2 * Math.PI * Math.Sqrt(low * low * low / at.Gm),
            };
            return snapshot;
        }

        /// <summary>Started from <paramref name="snapshot"/> with no pick, to its first Sequence Plan awaiting Approval.</summary>
        private void ToDeparture(MissionControl mission, GameSnapshot snapshot, string goal = Unnamed)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(snapshot);
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.False(mission.View.WindowPickNeeded);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private static SurfaceDeparture SolvedFor(string body, double ut, WindowKind kind)
        {
            var snapshot = Landed(body, ut, 20000);
            var departure = new SurfaceDeparture { Planet = snapshot.Bodies.Single(b => b.Name == "Kerbin") };
            WindowWaits.Solve(snapshot, departure, new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent, ut, new WindowPick("Kerbin", kind));
            return departure;
        }

        private static void AsksAndHoldsApproval(MissionControl mission)
        {
            var view = mission.View;
            Assert.True(view.WindowPickNeeded);
            Assert.Equal("Kerbin", view.Windows.Body);
            Assert.Equal("Transfer Window to Kerbin: pick one", view.Windows.Caption);
            Assert.Equal(new[] { WindowKind.Soonest, WindowKind.Optimal }, view.Windows.Rows.Select(r => r.Kind));
            Assert.All(view.Windows.Rows, r => Assert.Contains(", capture 0 m/s, total ", r.Text));
            Assert.False(view.WindowPick.IsFor("Kerbin"));
            Assert.False(view.DueTime.HasDueTime);
            Assert.DoesNotContain(QuickReply.Approve, view.QuickReplies);
            Assert.False(MissionWindowRules.ApproveEnabled(view));
            Assert.Equal("Pick a Transfer Window, then Approve", MissionWindowRules.NeedsYou(view));

            mission.Send(new Approve());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text == WindowChoice.PickFirst);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
        }

        // ---- Where it asks ----

        [Theory]
        [InlineData("Duna", WindowKind.Soonest)]
        [InlineData("Duna", WindowKind.Optimal)]
        [InlineData("Ike", WindowKind.Soonest)]
        [InlineData("Ike", WindowKind.Optimal)]
        public void ALandedCraftsAscentAsksAndItsDueTimeFollowsThePick(string body, WindowKind kind)
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from " + body, "Return to Kerbin"), AscentPlan()));
            ToDeparture(mission, Landed(body, NewGame, 20000));
            AsksAndHoldsApproval(mission);

            mission.Send(new ChooseWindow(kind));
            clock.Take(mission);

            var solved = SolvedFor(body, NewGame, kind);
            var view = mission.View;
            Assert.False(view.WindowPickNeeded);
            Assert.Equal("Kerbin:" + kind, view.WindowPick.Key);
            Assert.Equal(solved.DueUt.Value, view.DueTime.Ut, 0);
            Assert.Equal("the Kerbin window", view.DueTime.WaitsFor);
            Assert.Equal(view.DueTime.Ut, clock.Alarms[view.MissionId], 0);
            Assert.Equal(solved.Window.DepartureUt, view.Windows.RowFor(view.WindowPick).DepartureUt, 0);
            Assert.Contains(view.Log, e => e.Kind == LogKind.SequencePlan && e.Text.Contains(KerbinTime.Date(view.DueTime.Ut)));
        }

        [Fact]
        public void ThePickDecidesTheWindowFromBothKindsOfSurface()
        {
            foreach (var body in new[] { "Duna", "Ike" })
                Assert.NotEqual(SolvedFor(body, NewGame, WindowKind.Soonest).DueUt, SolvedFor(body, NewGame, WindowKind.Optimal).DueUt);
        }

        [Theory]
        [InlineData(WindowKind.Soonest)]
        [InlineData(WindowKind.Optimal)]
        public void ACraftInOrbitAsksOnItsDepartureAndItsDueTimeFollowsThePick(WindowKind kind)
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Return to Kerbin", "Reentry"), DepartPlan()));
            var snapshot = InOrbit("Duna", NewGame);
            ToDeparture(mission, snapshot);
            AsksAndHoldsApproval(mission);

            mission.Send(new ChooseWindow(kind));

            var stamped = InOrbit("Duna", NewGame);
            stamped.StampMission(new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent, new WindowPick("Kerbin", kind));
            var wait = BurnPoints.First("local res is transfer_to(lexicon(\"target\", \"Kerbin\", \"target_pe\", 35000)). addons:claudia:report(res).\n", stamped, NewGame, 500);
            Assert.Equal(BurnPoints.DueTime(wait, NewGame).Value, mission.View.DueTime.Ut, 0);
        }

        [Fact]
        public void AScriptWrittenWithTheSettingsLiteralWindowFliesThePick()
        {
            var script = DepartScript.Replace("35000)", "35000, \\\"window\\\", 5000000)");
            var mission = Control(new StreamingFakeTransport(HomePlan("Return to Kerbin", "Reentry"), DepartPlan(script: script)));
            ToDeparture(mission, InOrbit("Duna", NewGame));

            mission.Send(new ChooseWindow(WindowKind.Optimal));

            var row = mission.View.Windows.RowFor(mission.View.WindowPick);
            var card = mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan);
            Assert.Contains("\"window\", " + Math.Round(row.WindowUt).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", card.Script);
            Assert.DoesNotContain("5000000", card.Script);
        }

        [Fact]
        public void FromAMoonsOrbitTheReturnAsksWhenThePlanLeadsOnToAnotherPlanet()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Return to Duna", "Transfer to Kerbin"), DepartPlan("Return to Duna", ReturnScript)));
            ToDeparture(mission, InOrbit("Ike", NewGame));
            AsksAndHoldsApproval(mission);
        }

        [Theory]
        [InlineData("Mun")]
        [InlineData("Minmus")]
        public void AReturnFromTheMunOrMinmusNeverAsks(string moon)
        {
            var landed = Control(new StreamingFakeTransport(HomePlan("Ascend from " + moon, "Return to Kerbin"), AscentPlan()));
            ToDeparture(landed, Landed(moon, NewGame, 20000));
            Assert.False(landed.View.WindowPickNeeded);
            Assert.False(landed.View.Windows.Needed);
            Assert.Contains(QuickReply.Approve, landed.View.QuickReplies);

            var orbiting = Control(new StreamingFakeTransport(HomePlan("Return to Kerbin", "Reentry"), DepartPlan("Return to Kerbin", ReturnScript)));
            ToDeparture(orbiting, InOrbit(moon, NewGame));
            Assert.False(orbiting.View.WindowPickNeeded);
            Assert.False(orbiting.View.Windows.Needed);
            Assert.Contains(QuickReply.Approve, orbiting.View.QuickReplies);
        }

        [Fact]
        public void APickForTheWayOutIsNoneForTheWayHome()
        {
            var departure = new SequencePlan { Name = "Return to Kerbin", Script = "local res is transfer_to(lexicon(\"target\", \"Kerbin\", \"target_pe\", 35000)).\n" };
            var found = WindowChoice.Departure(InOrbit("Duna", NewGame), departure, null, 1);
            Assert.Equal("Kerbin", found.Planet.Name);
            Assert.False(new WindowPick("Duna", WindowKind.Optimal).IsFor(found.Planet.Name));
        }

        [Fact]
        public void APickFromARevisedPlanOrTheStartFormCoversTheDeparture()
        {
            // The Start form asks for a goal that names Kerbin, from Duna; its pick covers the chain, and nothing is asked again.
            var transport = new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan());
            var mission = Control(transport);
            ToAscent(mission, "Duna", NewGame);
            Assert.False(mission.View.WindowPickNeeded);
            Assert.False(mission.View.Windows.Needed);
            Assert.True(mission.View.DueTime.HasDueTime);
        }

        [Fact]
        public void TheAscentsPickStandsForTheDepartureItLeadsTo()
        {
            var transport = new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan(), DepartPlan(step: 2));
            var mission = Control(transport);
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            mission.Send(new ChooseWindow(WindowKind.Optimal));

            // In orbit, the next Sequence Plan is the departure itself.
            mission.Send(InOrbit("Duna", NewGame));
            mission.Send(new ChangeSomething("Plan the departure."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && v.Log.Count(e => e.Kind == LogKind.SequencePlan) == 2);

            Assert.False(mission.View.WindowPickNeeded);
            Assert.Equal("Kerbin:Optimal", mission.View.WindowPick.Key);
        }

        [Fact]
        public void AManualStretchNeverAsks()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("manual:Surface operations", "Ascend from Duna", "Return to Kerbin"), SurfaceStay()));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            Assert.False(mission.View.WindowPickNeeded);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
        }

        // ---- A change of mind ----

        [Fact]
        public void AChangedPickMovesTheDueTimeBothWaysOnThePlansOwnDueTime()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            var soonest = SolvedFor("Duna", NewGame, WindowKind.Soonest).DueUt.Value;
            var optimal = SolvedFor("Duna", NewGame, WindowKind.Optimal).DueUt.Value;
            Assert.True(soonest < optimal);

            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Equal(optimal, mission.View.DueTime.Ut, 0);
            Assert.True(mission.View.Windows.Needed);
            mission.Send(new ChooseWindow(WindowKind.Soonest));
            clock.Take(mission);
            Assert.Equal(soonest, mission.View.DueTime.Ut, 0);
            Assert.Equal(soonest, clock.Alarms[mission.View.MissionId], 0);
        }

        [Fact]
        public void ALaterDueTimeOfClaudiasOwnStillStandsAndSurvivesAChangedPick()
        {
            var mine = SolvedFor("Duna", NewGame, WindowKind.Optimal).DueUt.Value + 5 * Day;
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan(due: mine)));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            mission.Send(new ChooseWindow(WindowKind.Soonest));
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Equal(mine, mission.View.DueTime.Ut, 0);
        }

        // ---- Claudia hears it ----

        [Fact]
        public void ClaudiaHearsThePickOnceInTheFirstRequestAfterTheRun()
        {
            var start = SolvedFor("Duna", NewGame, WindowKind.Soonest).Window.DepartureUt - 3600;
            var transport = new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan(), Stream("mission-debrief-next.sse"), DepartPlan(step: 2));
            var mission = Control(transport);
            ToDeparture(mission, Landed("Duna", start, 20000));
            mission.Send(new ChooseWindow(WindowKind.Soonest));
            Assert.False(mission.View.DueTime.HasDueTime);
            var sent = transport.Requests.Count;
            mission.TakeCommands();

            mission.Send(new Approve());
            Assert.Equal(sent, transport.Requests.Count);
            var run = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), run.RunId));
            DrainUntil(mission, v => transport.Requests.Count > sent);

            const string line = "The player picked the Soonest Transfer Window to Kerbin.";
            Assert.Contains(line, Messages(transport.Requests[sent]));
            // One line, one time: the transcript carries it from then on.
            Assert.Equal(1, Messages(transport.Requests[sent]).Split(new[] { line }, StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void AMissionThatNeverAsksHasNoLineInItsRequests()
        {
            var transport = new StreamingFakeTransport(HomePlan("Ascend from the Mun", "Return to Kerbin"), AscentPlan());
            var mission = Control(transport);
            ToDeparture(mission, Landed("Mun", NewGame, 20000));
            Assert.All(transport.Requests, r => Assert.DoesNotContain("The player picked", Messages(r)));
        }

        // ---- Auto Mode ----

        [Fact]
        public void AutoModeStopsForThePickAndNeverMakesItThenFliesOnceThePlayerHas()
        {
            var start = SolvedFor("Duna", NewGame, WindowKind.Soonest).Window.DepartureUt - 3600;
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToDeparture(mission, Landed("Duna", start, 20000));
            mission.Send(new SetAutoMode(true));
            mission.Send(new UtAdvanced(start + 60));
            _background.Drain();

            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.True(mission.View.WindowPickNeeded);
            Assert.False(mission.View.WindowPick.IsFor("Kerbin"));
            Assert.Equal("Auto Mode is waiting for you: a Transfer Window to pick.", mission.View.AutoModeStop);

            mission.Send(new ChooseWindow(WindowKind.Soonest));
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void AutoModeStopsAtTheDueTimeThePickSets()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            mission.Send(new SetAutoMode(true));
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            mission.Send(new UtAdvanced(mission.View.DueTime.Ut + 1));
            _background.Drain();

            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal("Auto Mode is waiting for you: a Due Time.", mission.View.AutoModeStop);
        }

        [Fact]
        public void InABlackoutTheDepartureWaitsForThePickAndThePickWaitsForTheLink()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Return to Kerbin", "Reentry"), DepartPlan()));
            ToDeparture(mission, InOrbit("Duna", NewGame));
            var dark = InOrbit("Duna", NewGame);
            dark.HasLink = false;
            mission.Send(dark);

            Assert.True(mission.View.Blackout);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.True(mission.View.WindowPickNeeded);
            Assert.Contains(mission.View.Log, e => e.Text.Contains("waits for you to pick its Transfer Window when the link returns"));
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.True(mission.View.WindowPickNeeded);
        }

        // ---- Save and load ----

        private MissionControl Reloaded(MissionSaveState state, GameSnapshot snapshot, params ScriptedResponse[] replies)
        {
            var reload = Control(new StreamingFakeTransport(replies));
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { state }));
            reload.Send(new UtAdvanced(snapshot.Ut));
            reload.Send(snapshot);
            return reload;
        }

        [Fact]
        public void APlanWaitingForThePickStillAsksAfterASaveAndLoad()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));

            var state = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            var reload = Reloaded(state, Landed("Duna", NewGame, 20000));

            Assert.Equal(MissionStage.AwaitingSequenceApproval, reload.View.Stage);
            Assert.True(reload.View.WindowPickNeeded);
            Assert.True(reload.View.Windows.Needed);
            reload.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Equal(SolvedFor("Duna", NewGame, WindowKind.Optimal).DueUt.Value, reload.View.DueTime.Ut, 0);
        }

        [Fact]
        public void ThePickAndTheDueTimeItSetSurviveASaveAndLoadAndCanStillChange()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            var due = mission.View.DueTime.Ut;

            var state = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal("Kerbin:Optimal", state.WindowPick.Key);
            Assert.True(state.WindowPickUnsaid);
            var reload = Reloaded(state, Landed("Duna", NewGame, 20000));

            Assert.False(reload.View.WindowPickNeeded);
            Assert.Equal("Kerbin:Optimal", reload.View.WindowPick.Key);
            Assert.Equal(due, reload.View.DueTime.Ut, 0);
            Assert.True(reload.View.Windows.Needed);

            reload.Send(new ChooseWindow(WindowKind.Soonest));
            Assert.Equal(SolvedFor("Duna", NewGame, WindowKind.Soonest).DueUt.Value, reload.View.DueTime.Ut, 0);
        }

        [Fact]
        public void AClaudiaDueTimeKeptUnderAPickSurvivesASaveAndLoad()
        {
            var mine = SolvedFor("Duna", NewGame, WindowKind.Soonest).DueUt.Value - 5 * Day;
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan(due: mine)));
            ToDeparture(mission, Landed("Duna", NewGame, 20000));
            mission.Send(new ChooseWindow(WindowKind.Optimal));

            var state = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal(mine, state.WindowOwnDue);
        }
    }
}
