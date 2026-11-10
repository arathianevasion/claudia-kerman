// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #966: a picked Transfer Window holds for its transfer until the ejection burn has flown. A revised Mission Plan for the same
    /// transfer carries it (the card shows it and it can change, but nothing waits for it), even when the ejection now costs more.
    /// A different transfer, a missed window, or a pick spent by its ejection asks again.
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        private const string RoundTrip = "Orbit Duna and return to Kerbin.";

        /// <summary>Picked on the form, approved, and the craft in a low Kerbin orbit at <paramref name="ut"/> when a revised plan arrives.</summary>
        private MissionControl RevisedInOrbit(StreamingFakeTransport transport, WindowPick pick, double ut, string goal = Goal)
        {
            var mission = Control(transport);
            ToMissionPlan(mission, pick, NewGame, goal);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new UtAdvanced(ut));
            mission.Send(InOrbit("Kerbin", ut, 9000));
            ToRevisedPlan(mission);
            return mission;
        }

        /// <summary>The craft between the planets, after its ejection from Kerbin.</summary>
        private static GameSnapshot Heliocentric(double ut)
        {
            var snapshot = OnPad(ut, 9000);
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Sun";
            snapshot.Orbit = new SnapshotOrbit { Body = "Sun", SemiMajorAxis = 17000000000, Eccentricity = 0.1, Periapsis = 15000000000, Apoapsis = 19000000000, Epoch = ut };
            return snapshot;
        }

        [Fact]
        public void ARevisedPlanInOrbitCarriesTheFormsPickAndNeedsNoFreshOne()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan(), LaunchPlan());
            var mission = RevisedInOrbit(transport, Duna(WindowKind.Optimal), NewGame + Day);

            var view = mission.View;
            Assert.False(view.WindowPickNeeded);
            Assert.Equal("Duna:Optimal", view.WindowPick.Key);
            // The card still shows the choice with the held pick on it, and Approve is open.
            Assert.True(view.Windows.Needed);
            Assert.Contains(QuickReply.Approve, view.QuickReplies);
            Assert.True(MissionWindowRules.ApproveEnabled(view));

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            // Claudia heard the held pick in the revision's own request: nothing more with the Approval.
            Assert.DoesNotContain("The player picked", Messages(transport.Requests[3]));
        }

        [Fact]
        public void TheHeldPickCanStillBeChangedOnTheRevisedPlan()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan(), LaunchPlan());
            var mission = RevisedInOrbit(transport, Duna(WindowKind.Optimal), NewGame + Day);
            mission.Send(new ChooseWindow(WindowKind.Soonest));
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("The player picked the Soonest Transfer Window to Duna.", Messages(transport.Requests[3]));
        }

        [Fact]
        public void AutoModeApprovesARevisedPlanThatCarriesTheHeldPick()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan(), LaunchPlan());
            var mission = Control(transport);
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new SetAutoMode(true));
            mission.Send(InOrbit("Kerbin", NewGame + 60, 9000));
            var edited = mission.View.Settings.Copy();
            edited.TimeVersusDeltaV = TimeVersusDeltaV.MostEfficient;
            mission.Send(new EditSettings(edited));
            mission.Send(new UtAdvanced(NewGame + 60));
            // The revision (the third request) is approved with no pick: the fourth asks for its first Sequence Plan.
            DrainUntil(mission, v => transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
            // Whatever Auto Mode waits for next (the Sequence Plan's Due Time), it is not a pick.
            Assert.NotEqual("Auto Mode is waiting for you: a Transfer Window to pick.", mission.View.AutoModeStop);
        }

        [Fact]
        public void ACostRiseAloneNeverAsksAgain()
        {
            // A later revision, before the picked window leaves, where that window's row now costs more than when it was picked.
            var config = new PlayerConfig();
            var start = NewGame + 5 * Day;
            var picked = WindowChoice.Work(InOrbit("Kerbin", start, 9000), Goal, config.CraftCheckMarginPercent, start).RowFor(Duna(WindowKind.Soonest));
            var later = Enumerable.Range(6, 140).Select(d => NewGame + d * Day)
                .Where(ut => ut < picked.DepartureUt - Day)
                .FirstOrDefault(ut =>
                {
                    var row = WindowChoice.Work(InOrbit("Kerbin", ut, 9000), Goal, config.CraftCheckMarginPercent, ut).RowFor(Duna(WindowKind.Soonest));
                    return row != null && System.Math.Abs(row.DepartureUt - picked.DepartureUt) < 3600 && row.TotalDv > picked.TotalDv;
                });
            Assert.True(later > 0, "no later day before the window with a dearer ejection");

            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(start));
            mission.Send(InOrbit("Kerbin", start, 9000));
            CraftCheckRouter.StartChecked(mission, _background, InOrbit("Kerbin", start, 9000), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: Duna(WindowKind.Soonest)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new UtAdvanced(later));
            mission.Send(InOrbit("Kerbin", later, 9000));
            ToRevisedPlan(mission);
            Assert.True(mission.View.Windows.Needed);
            Assert.False(mission.View.WindowPickNeeded);
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
        }

        [Fact]
        public void AMissedWindowAsksAgain()
        {
            var past = DunaWindows(NewGame).Soonest.DepartureUt + Day;
            var mission = RevisedInOrbit(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()), Duna(WindowKind.Soonest), past);
            Assert.True(mission.View.WindowPickNeeded);
            Assert.True(mission.View.Windows.Needed);
            Assert.DoesNotContain(QuickReply.Approve, mission.View.QuickReplies);
        }

        [Fact]
        public void AfterTheEjectionTheReturnTransferAsks()
        {
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan());
            var mission = Control(transport);
            ToMissionPlan(mission, Duna(WindowKind.Soonest), NewGame, RoundTrip);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var between = NewGame + 100 * Day;
            mission.Send(new UtAdvanced(between));
            mission.Send(Heliocentric(between));
            // The ejection has flown: the pick no longer holds.
            Assert.True(mission.View.WindowPick.IsNone);

            var atDuna = between + 200 * Day;
            mission.Send(new UtAdvanced(atDuna));
            mission.Send(InOrbit("Duna", atDuna, 9000));
            ToRevisedPlan(mission);
            Assert.True(mission.View.WindowPickNeeded);
            Assert.Equal("Kerbin", mission.View.Windows.Body);
        }

        [Fact]
        public void APickSpentByItsEjectionIsNotHeldForTheSameTransferAgain()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(Heliocentric(NewGame + Day));
            // Back round Kerbin (the transfer turned back), and a revision for Duna again: a new transfer, so it asks.
            mission.Send(InOrbit("Kerbin", NewGame + 2 * Day, 9000));
            ToRevisedPlan(mission);
            Assert.True(mission.View.WindowPickNeeded);
        }

        [Fact]
        public void BeforeTheEjectionThePickHoldsThroughAscentAndOrbit()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(InOrbit("Kerbin", NewGame + Day, 9000));
            mission.Send(InOrbit("Mun", NewGame + 2 * Day, 9000));
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
        }

        [Fact]
        public void SaveAndLoadKeepTheHeldPick()
        {
            var mission = RevisedInOrbit(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()), Duna(WindowKind.Optimal), NewGame + Day);
            Assert.False(mission.View.WindowPickNeeded);

            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal("Duna:Optimal", reread.WindowPick.Key);
            Assert.False(reread.WindowPickNeeded);
            Assert.True(reread.WindowPickHeld);
            // The held window's departure as worked out from orbit at the revision.
            var fromOrbit = WindowChoice.Work(InOrbit("Kerbin", NewGame + Day, 9000), Goal, new PlayerConfig().CraftCheckMarginPercent, NewGame + Day).RowFor(Duna(WindowKind.Optimal));
            Assert.Equal(fromOrbit.DepartureUt, reread.WindowPickUt.Value, 0);

            var reload = Control(new StreamingFakeTransport());
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { reread }));
            reload.Send(new UtAdvanced(NewGame + Day));
            reload.Send(InOrbit("Kerbin", NewGame + Day, 9000));
            Assert.Equal(MissionStage.AwaitingPlanApproval, reload.View.Stage);
            Assert.False(reload.View.WindowPickNeeded);
            Assert.True(reload.View.Windows.Needed);
            Assert.Equal("Duna:Optimal", reload.View.WindowPick.Key);
            Assert.Contains(QuickReply.Approve, reload.View.QuickReplies);
        }
    }
}
