// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The player picks the Transfer Window for a transfer between bodies with the same parent (#686): before Start, and again for a
    /// revised Mission Plan whose transfer the pick no longer holds for (#966). Driven through the Mission seam with hand-written replies, on <see cref="WindowWaitTests"/>' harness.
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        private const double NewGame = 1203.48;

        private static WindowPick Duna(WindowKind kind)
        {
            return new WindowPick("Duna", kind);
        }

        private static WindowSearch DunaWindows(double ut)
        {
            var snapshot = OnPad(ut, 9000);
            var duna = snapshot.Bodies.Single(b => b.Name == "Duna");
            return TransferWindows.Find(snapshot, duna.Name, MissionPrompts.ArrivalPe(duna), true, new PlayerConfig().CraftCheckMarginPercent);
        }

        /// <summary>Started with <paramref name="pick"/> and on to the first Mission Plan awaiting Approval.</summary>
        private void ToMissionPlan(MissionControl mission, WindowPick pick, double ut = NewGame, string goal = Goal)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(ut));
            mission.Send(OnPad(ut, 9000));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(ut, 9000), new StartMission(goal, 10m, "claude-sonnet-5-5", window: pick));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        /// <summary>
        /// #966: after the picked window of <paramref name="kind"/> from the pad has gone, so a revised plan asks again (a missed window):
        /// half a synodic period after it, where the next window is Soonest. A day after its departure the window is still open.
        /// </summary>
        private static double PastThe(WindowKind kind)
        {
            var windows = DunaWindows(NewGame);
            return (kind == WindowKind.Optimal ? windows.Optimal : windows.Soonest).DepartureUt + windows.Synodic / 2 + Day;
        }

        /// <summary>The craft seen at <paramref name="ut"/>, after its picked window left, and a revised plan asked for.</summary>
        private void ToRevisedPlanAfterTheWindow(MissionControl mission, double ut, string vesselId = "")
        {
            mission.Send(new UtAdvanced(ut));
            mission.Send(OnPad(ut, 9000, vesselId));
            ToRevisedPlan(mission);
        }

        /// <summary>A setting edited asks Claudia for a revised Mission Plan, whose request has the stage table.</summary>
        private void ToRevisedPlan(MissionControl mission, TimeVersusDeltaV setting = TimeVersusDeltaV.MostEfficient)
        {
            var edited = mission.View.Settings.Copy();
            edited.TimeVersusDeltaV = setting;
            mission.Send(new EditSettings(edited));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        // ---- Which transfers ----

        [Theory]
        [InlineData("Orbit Duna.", "Duna")]
        [InlineData("Go to Ike.", "Duna")]
        [InlineData("Land on Laythe.", "Jool")]
        [InlineData("Orbit the Mun.", null)]
        [InlineData("Land on Minmus and return to Kerbin.", null)]
        public void FromThePadTheChoiceIsForTheBodyTheGoalReachesThroughItsOwnParent(string goal, string body)
        {
            var transfer = WindowChoice.Transfer(OnPad(NewGame, 9000), goal);
            Assert.Equal(body, transfer == null ? null : transfer.Name);
        }

        [Theory]
        [InlineData("Duna", "Return to Kerbin.", "Kerbin")]
        [InlineData("Mun", "Return to Kerbin.", null)]
        [InlineData("Mun", "Fly to Minmus.", "Minmus")]
        [InlineData("Ike", "Return to Duna.", null)]
        public void FromAnotherBodyTheRuleIsTheSameAndAMoonBackToItsPlanetKeepsTheSetting(string from, string goal, string body)
        {
            var transfer = WindowChoice.Transfer(Landed(from, NewGame, 9000), goal);
            Assert.Equal(body, transfer == null ? null : transfer.Name);
        }

        [Fact]
        public void ACraftAlreadyOnItsWayOutOfThePlanetHasNoChoiceToMake()
        {
            var leaving = OnPad(NewGame, 9000);
            leaving.Situation = "ESCAPING";
            leaving.Orbit = new SnapshotOrbit { Body = "Kerbin", Eccentricity = 1.2, SemiMajorAxis = -2000000 };
            Assert.Null(WindowChoice.Transfer(leaving, Goal));
        }

        // ---- The rows ----

        [Fact]
        public void TheOfferHasASoonestAndAnOptimalRowWithTheBurnDetails()
        {
            var windows = DunaWindows(NewGame);
            Assert.NotEqual(windows.Soonest.DepartureUt, windows.Optimal.DepartureUt);

            var offer = WindowChoice.Work(OnPad(NewGame, 9000), Goal, new PlayerConfig().CraftCheckMarginPercent, NewGame);
            Assert.True(offer.Needed);
            Assert.Equal("Duna", offer.Body);
            Assert.Equal(new[] { WindowKind.Soonest, WindowKind.Optimal }, offer.Rows.Select(r => r.Kind));
            var soonest = offer.Rows[0].Text;
            Assert.StartsWith("Soonest: leaves " + KerbinTime.Date(windows.Soonest.DepartureUt) + " (in " + KerbinTime.Span(windows.Soonest.DepartureUt - NewGame) + "), ejection ", soonest);
            Assert.Contains(", capture " + windows.Soonest.CaptureDv.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " m/s", soonest);
            Assert.Contains(", total " + windows.Soonest.TotalDv.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " m/s", soonest);
            Assert.EndsWith(", flight " + KerbinTime.Span(windows.Soonest.ArrivalUt - windows.Soonest.DepartureUt), soonest);
            Assert.StartsWith("Optimal: leaves " + KerbinTime.Date(windows.Optimal.DepartureUt), offer.Rows[1].Text);
        }

        [Fact]
        public void AGoalWithinOneSphereOfInfluenceOffersNoRows()
        {
            Assert.False(WindowChoice.Work(OnPad(NewGame, 9000), "Orbit the Mun.", 15, NewGame).Needed);
        }

        [Fact]
        public void ThePickOnlyEverAnswersItsOwnTransfer()
        {
            var windows = DunaWindows(NewGame);
            Assert.Same(windows.Optimal, TransferWindows.Pick(windows, TimeVersusDeltaV.Fastest, Duna(WindowKind.Optimal)));
            Assert.Same(windows.Soonest, TransferWindows.Pick(windows, TimeVersusDeltaV.MostEfficient, Duna(WindowKind.Soonest)));
            // Another transfer's pick leaves it to the setting.
            Assert.Same(windows.Optimal, TransferWindows.Pick(windows, TimeVersusDeltaV.MostEfficient, new WindowPick("Eve", WindowKind.Soonest)));
            Assert.Same(windows.Soonest, TransferWindows.Pick(windows, TimeVersusDeltaV.Fastest, WindowPick.None));
        }

        // ---- Before Start ----

        [Fact]
        public void StartIsRefusedWithNoRequestUntilAWindowIsPicked()
        {
            var transport = new StreamingFakeTransport(DunaPlan());
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad(NewGame, 9000));
            mission.Send(new StartFormChanged(Goal));

            var form = mission.View.StartForm;
            Assert.True(form.Windows.Needed);
            Assert.True(form.WindowPickMissing);
            Assert.Equal("Start Mission · pick a Transfer Window", MissionWindowRules.StartLabel(mission.View));

            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            Assert.Equal(WindowChoice.PickFirst, mission.View.StartForm.Refusal);
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Equal(CraftCheckState.None, mission.View.CraftCheck.State);
            Assert.Empty(transport.Requests);

            // A pick for another transfer is none for this one.
            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5", window: new WindowPick("Eve", WindowKind.Soonest)));
            Assert.Equal(MissionStage.None, mission.View.Stage);

            mission.Send(new StartFormChanged(Goal, null, Duna(WindowKind.Soonest)));
            Assert.False(mission.View.StartForm.WindowPickMissing);
        }

        [Fact]
        public void AMunGoalNeedsNoPickAndItsRequestHasNoPlayerRow()
        {
            var transport = new StreamingFakeTransport(DunaPlan());
            var mission = Control(transport);
            mission.Send(new StartFormChanged("Orbit the Mun."));
            mission.Send(OnPad(NewGame, 9000));
            mission.Send(new StartFormChanged("Orbit the Mun."));
            Assert.False(mission.View.StartForm.Windows.Needed);
            Assert.False(mission.View.StartForm.WindowPickMissing);

            ToMissionPlan(mission, null, NewGame, "Orbit the Mun.");
            Assert.DoesNotContain("Player", Messages(transport.Requests[0]).Replace("the player", "").Replace("The player", ""));
            Assert.False(mission.View.WindowPickNeeded);
        }

        [Fact]
        public void TheCraftCheckBudgetsThePickedWindowAndAChangedPickMakesItStale()
        {
            var snapshot = OnPad(NewGame, 9000);
            var offer = WindowChoice.Work(snapshot, Goal, 15, NewGame);
            var optimal = offer.RowFor(Duna(WindowKind.Optimal));
            var total = DunaWindows(NewGame).Optimal.TotalDv.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
            var facts = CraftCheckPrompts.Facts(Goal, snapshot, 15, null, false, optimal);
            Assert.Contains(": " + total + " m/s (the player's Optimal window)", facts);
            Assert.DoesNotContain("the player's Optimal", CraftCheckPrompts.Facts(Goal, snapshot, 15, null));

            var mission = Control(new StreamingFakeTransport());
            mission.Send(snapshot);
            mission.Send(new StartFormChanged(Goal, null, Duna(WindowKind.Optimal)));
            mission.Send(new RunCraftCheck(Goal, 10m, "claude-sonnet-5-5", false, null, Duna(WindowKind.Optimal)));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            Assert.False(mission.View.CraftCheck.Stale);

            mission.Send(new StartFormChanged(Goal, null, Duna(WindowKind.Soonest)));
            Assert.Equal("Stale: the Transfer Window changed since this check. Check again.", mission.View.CraftCheck.StaleReason);
        }

        // ---- Into the first Mission Plan ----

        [Theory]
        [InlineData(WindowKind.Soonest, "soonest")]
        [InlineData(WindowKind.Optimal, "optimal")]
        public void ThePickNotTheSettingIsWhatTheRequestNamesAndTheDueTimeUses(WindowKind kind, string named)
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan());
            var mission = Control(transport);
            // The setting would take the other window.
            var settings = new MissionSettingsValues { TimeVersusDeltaV = kind == WindowKind.Soonest ? TimeVersusDeltaV.MostEfficient : TimeVersusDeltaV.Fastest };
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(NewGame));
            mission.Send(OnPad(NewGame, 9000));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(NewGame, 9000), new StartMission(Goal, 10m, "claude-sonnet-5-5", settings, window: Duna(kind)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var request = Messages(transport.Requests[0]);
            Assert.Contains("\"setting\":\"Player\"", request.Replace("\\\"", "\""));
            Assert.Contains("\"pick\":\"" + named + "\"", request.Replace("\\\"", "\""));

            // The first plan carries the start form's pick: no second ask.
            Assert.False(mission.View.WindowPickNeeded);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            var windows = DunaWindows(NewGame);
            var window = kind == WindowKind.Optimal ? windows.Optimal : windows.Soonest;
            Assert.Equal(window.DepartureUt - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);
            // Heard in the request already, so the Approval says nothing more.
            Assert.DoesNotContain("The player picked", Messages(transport.Requests[1]));
        }

        [Fact]
        public void KerbinToIkeTakesThePickedDunaWindowForItsDueTime()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan());
            var mission = Control(transport);
            ToMissionPlan(mission, Duna(WindowKind.Optimal), NewGame, "Go to Ike.");
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(DunaWindows(NewGame).Optimal.DepartureUt - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);
            Assert.Equal("the Duna window", mission.View.DueTime.WaitsFor);
        }

        // ---- A revised Mission Plan ----

        [Fact]
        public void ARevisedPlanCarriesFreshRowsAndItsApprovalNeedsAPickWhichClaudiaThenHears()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), DunaPlan(), LaunchPlan());
            var mission = Control(transport);
            ToMissionPlan(mission, Duna(WindowKind.Soonest));

            // Time has passed, past the picked window (#966: a missed window asks again): the rows are the craft's as it is.
            var later = PastThe(WindowKind.Soonest);
            mission.Send(new UtAdvanced(later));
            mission.Send(OnPad(later, 9000));
            ToRevisedPlan(mission);

            const string input = "{\"target\":\"Duna\",\"target_pe\":60000}";
            var expected = OnPad(later, 9000);
            expected.StampMission(TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, Duna(WindowKind.Soonest));
            var row = PlanningTools.Default().Run("plan_transfer", input, expected, null);
            Assert.False(row.IsError, row.Json);
            Assert.Contains(row.Json, Messages(transport.Requests[1]).Replace("\\\"", "\""));

            var view = mission.View;
            Assert.True(view.WindowPickNeeded);
            Assert.DoesNotContain(QuickReply.Approve, view.QuickReplies);
            Assert.False(MissionWindowRules.ApproveEnabled(view));
            var windows = DunaWindows(later);
            Assert.StartsWith("Soonest: leaves " + KerbinTime.Date(windows.Soonest.DepartureUt), view.Windows.Rows[0].Text);
            Assert.StartsWith("Optimal: leaves " + KerbinTime.Date(windows.Optimal.DepartureUt), view.Windows.Rows[1].Text);

            // Approval without a pick does nothing but say so.
            mission.Send(new Approve());
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text == WindowChoice.PickFirst);
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.False(mission.View.WindowPickNeeded);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.Equal("Duna:Optimal", mission.View.WindowPick.Key);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("The player picked the Optimal Transfer Window to Duna.", Messages(transport.Requests[2]));
            Assert.Equal(windows.Optimal.DepartureUt - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);
        }

        [Fact]
        public void AWindowCanOnlyBePickedForAMissionPlanWaitingForApproval()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToLaunchPlan(mission, NewGame);
            var before = mission.View.WindowPick.Key;
            mission.Send(new ChooseWindow(before.EndsWith("Soonest") ? WindowKind.Optimal : WindowKind.Soonest));
            Assert.Equal(before, mission.View.WindowPick.Key);
        }

        [Fact]
        public void TheFirstPlanCarriesThePickAndOffersNoChoiceButARevisionMayChangeItUntilApproval()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            Assert.False(mission.View.Windows.Needed);
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);

            ToRevisedPlan(mission);
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            mission.Send(new ChooseWindow(WindowKind.Soonest));
            Assert.True(mission.View.Windows.Needed);
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
        }

        [Fact]
        public void ThePickIsOfTheMissionsOwnCraftAndWaitsForThePlayerToComeBackToIt()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), DunaPlan(), LaunchPlan()));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(NewGame));
            mission.Send(OnPad(NewGame, 9000, "ours"));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(NewGame, 9000, "ours"), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: Duna(WindowKind.Soonest)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var later = PastThe(WindowKind.Soonest);
            ToRevisedPlanAfterTheWindow(mission, later, "ours");
            Assert.True(mission.View.WindowPickNeeded);

            // Out of Flight: nothing to show, nothing picked, and Approve is still held.
            mission.Send(new SceneChanged(GameScene.Other));
            Assert.True(mission.View.WindowPickNeeded);
            Assert.False(mission.View.Windows.Needed);
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);

            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad(later, 9000, "ours"));
            Assert.True(mission.View.Windows.Needed);
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        [Fact]
        public void ARevisedPlanWaitingForAPickCallsThePlayerOnceAndNotAgainWhenTheyPick()
        {
            var attention = new AttentionWatch();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            attention.Observe(mission.View);
            ToRevisedPlanAfterTheWindow(mission, PastThe(WindowKind.Soonest));
            Assert.True(mission.View.WindowPickNeeded);
            Assert.NotNull(attention.Observe(mission.View));
            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Null(attention.Observe(mission.View));
        }

        [Fact]
        public void TheCraftCheckBudgetsAWindowHomeOnTheReturnAndAnotherPlanetsOnItsOwnTrip()
        {
            var home = new WindowRow { From = "Duna", Body = "Kerbin" };
            Assert.True(home.Budgets("return:Duna"));
            var onward = new WindowRow { From = "Duna", Body = "Jool" };
            Assert.False(onward.Budgets("return:Duna"));
            Assert.True(onward.Budgets("transfer:Jool"));
        }

        [Fact]
        public void TheOfferIsWorkedOutAgainWhenTheCraftOrItsEnginesChange()
        {
            var one = OnPad(NewGame, 9000, "a");
            var key = WindowChoice.Key(one, Goal, 15, NewGame);
            Assert.Equal(key, WindowChoice.Key(OnPad(NewGame + 1, 9000, "a"), Goal, 15, NewGame + 1));
            Assert.NotEqual(key, WindowChoice.Key(OnPad(NewGame, 9000, "b"), Goal, 15, NewGame));
            Assert.NotEqual(key, WindowChoice.Key(OnPad(NewGame, 9500, "a"), Goal, 15, NewGame));
            Assert.NotEqual(key, WindowChoice.Key(one, Goal, 15, NewGame + Day));
            var thrust = OnPad(NewGame, 9000, "a");
            thrust.Stages[0].ThrustVacKn += 100;
            Assert.NotEqual(key, WindowChoice.Key(thrust, Goal, 15, NewGame));
        }

        [Fact]
        public void InABlackoutARevisedPlanWaitsForThePickAndThePickWaitsForTheLink()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var later = PastThe(WindowKind.Soonest);
            ToRevisedPlanAfterTheWindow(mission, later);

            var dark = OnPad(later, 9000);
            dark.HasLink = false;
            mission.Send(dark);
            Assert.True(mission.View.Blackout);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.True(mission.View.WindowPickNeeded);

            mission.Send(new ChooseWindow(WindowKind.Optimal));
            Assert.Contains(mission.View.Log, e => e.Text == "No link to Kerbin: that waits for the link.");
            Assert.True(mission.View.WindowPickNeeded);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
        }

        // ---- Auto Mode ----

        [Fact]
        public void AutoModeNeverPicksARevisedPlansWindowButApprovesOnceThePlayerHas()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan(), LaunchPlan());
            var mission = Control(transport);
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            // Approved once, so a later plan is Auto Mode's to approve: only the pick holds this one.
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new SetAutoMode(true));
            var later = PastThe(WindowKind.Soonest);
            ToRevisedPlanAfterTheWindow(mission, later);
            mission.Send(new UtAdvanced(later + 60));
            _background.Drain();

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.True(mission.View.WindowPickNeeded);
            Assert.Equal("Auto Mode is waiting for you: a Transfer Window to pick.", mission.View.AutoModeStop);
            // Nothing was approved: no request after the revised plan's own (the plan, the launch's Sequence Plan, the revision).
            Assert.Equal(3, transport.Requests.Count);

            mission.Send(new ChooseWindow(WindowKind.Optimal));
            DrainUntil(mission, v => v.Stage != MissionStage.AwaitingPlanApproval);
            Assert.Equal("Duna:Optimal", mission.View.WindowPick.Key);
        }

        // ---- Save and load ----

        [Fact]
        public void ThePickAndAPlanWaitingForOneSurviveASaveAndLoad()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Optimal));
            var later = PastThe(WindowKind.Optimal);
            ToRevisedPlanAfterTheWindow(mission, later);
            Assert.True(mission.View.WindowPickNeeded);

            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal("Duna:Optimal", reread.WindowPick.Key);
            Assert.True(reread.WindowPickNeeded);

            var reload = Control(new StreamingFakeTransport(LaunchPlan()));
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { reread }));
            reload.Send(new UtAdvanced(later));
            reload.Send(OnPad(later, 9000));
            Assert.Equal(MissionStage.AwaitingPlanApproval, reload.View.Stage);
            Assert.True(reload.View.WindowPickNeeded);
            Assert.True(reload.View.Windows.Needed);
            Assert.Equal("Duna:Optimal", reload.View.WindowPick.Key);

            // A load is no new plan: a pick made before it isn't asked for again.
            reload.Send(new ChooseWindow(WindowKind.Soonest));
            var again = MissionSaveState.FromValues(Assert.Single(reload.ExportStates()).ToValues());
            Assert.Equal("Duna:Soonest", again.WindowPick.Key);
            Assert.False(again.WindowPickNeeded);
            var third = Control(new StreamingFakeTransport());
            third.Send(new SceneChanged(GameScene.Flight));
            third.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { again }));
            third.Send(new UtAdvanced(later));
            third.Send(OnPad(later, 9000));
            Assert.False(third.View.WindowPickNeeded);
            Assert.Contains(QuickReply.Approve, third.View.QuickReplies);
        }

        [Fact]
        public void AnOlderSaveHasNoPickAndWaitsForNone()
        {
            var state = MissionSaveState.FromValues(new Dictionary<string, string> { { "id", "m1" } });
            Assert.True(state.WindowPick.IsNone);
            Assert.False(state.WindowPickNeeded);
        }

        // ---- #784: a revised plan asks from the legs it still flies ----

        private static PlannedSequence Seq(string name, string intent, double deltaV, bool manual = false)
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = deltaV, Manual = manual };
        }

        private static ScriptedResponse DeorbitPlan()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: land.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Back at Kerbin.\",\"sequences\":["
                + "{\"name\":\"Land on Kerbin\",\"intent\":\"Deorbit and land.\",\"delta_v\":200,\"timing\":\"now\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":200,\"expected_duration\":\"an hour\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        [Fact]
        public void ADeorbitOnlyPlanBackAtKerbinNeedsNoPickAndAnUnmatchedOrTransferSequenceDoes()
        {
            var home = InOrbit("Kerbin", NewGame, 9000);
            Assert.False(WindowChoice.LegsAsk(home, new[] { Seq("Land on Kerbin", "Deorbit and land.", 200) }, ""));
            // A sequence PlanLegs can't read is asked about, whatever its name suggests.
            Assert.True(WindowChoice.LegsAsk(home, new[] { Seq("Land on Kerbin", "Deorbit and land.", 200), Seq("Ejection burn", "Prepare.", 100) }, ""));
            Assert.True(WindowChoice.LegsAsk(home, new[] { Seq("Duna transfer", "Burn for Duna and capture.", 1600) }, ""));
            // A manual leg with no words is still unmatched, and a flown one is not to fly.
            Assert.True(WindowChoice.LegsAsk(home, new[] { Seq("Fly it yourself", "By hand.", 100, manual: true) }, ""));
            Assert.False(WindowChoice.LegsAsk(home, new[] { Seq("Launch (flown)", "Done: orbit.", 3400), Seq("Land on Kerbin", "Deorbit and land.", 200) }, ""));
        }

        [Fact]
        public void ARevisedPlanThatOnlyDeorbitsBackAtKerbinAsksNoPickAndApprovalIsOpen()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), DeorbitPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));

            const double later = NewGame + 400 * Day;
            mission.Send(new UtAdvanced(later));
            mission.Send(InOrbit("Kerbin", later, 9000));
            ToRevisedPlan(mission);

            var view = mission.View;
            Assert.False(view.WindowPickNeeded);
            Assert.False(view.Windows.Needed);
            Assert.Contains(QuickReply.Approve, view.QuickReplies);
            Assert.True(MissionWindowRules.ApproveEnabled(view));
        }

        [Fact]
        public void ARevisedPlanStillHoldingTheDunaTransferOffersTheChoiceWithTheHeldPick()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            const double later = NewGame + 40 * Day;
            mission.Send(new UtAdvanced(later));
            mission.Send(InOrbit("Kerbin", later, 9000));
            ToRevisedPlan(mission);
            // #966: the pick holds for its transfer, so nothing waits for one.
            Assert.False(mission.View.WindowPickNeeded);
            Assert.True(mission.View.Windows.Needed);
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
        }
    }
}
