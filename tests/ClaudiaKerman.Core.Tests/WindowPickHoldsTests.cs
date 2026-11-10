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

        /// <summary>
        /// <paramref name="mission"/> as saved before #966 and loaded with <paramref name="craft"/> in view: its pick has no window on
        /// record, so holds none, and a missed window moves on to the next one as #616 and #774 do. A held pick asks instead
        /// (<see cref="ALateApprovalAfterAHeldWindowClosedAsksThePlayerRatherThanMoveToTheNext"/>).
        /// </summary>
        private MissionControl Unheld(MissionControl mission, GameSnapshot craft, StreamingFakeTransport transport = null)
        {
            var state = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            state.WindowPickDepartureUt = null;
            var reload = Control(transport ?? new StreamingFakeTransport());
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { state }));
            reload.Send(craft);
            return reload;
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
            var mission = RevisedInOrbit(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()), Duna(WindowKind.Soonest), PastThe(WindowKind.Soonest));
            Assert.True(mission.View.WindowPickNeeded);
            Assert.True(mission.View.Windows.Needed);
            Assert.DoesNotContain(QuickReply.Approve, mission.View.QuickReplies);
            // The fresh rows are the next window's.
            Assert.True(mission.View.Windows.Rows[0].DepartureUt > DunaWindows(NewGame).Optimal.DepartureUt + DunaWindows(NewGame).Synodic / 2);
        }

        /// <summary>Started in a low Kerbin orbit at <paramref name="ut"/> with <paramref name="pick"/>, and its first plan approved.</summary>
        private void PickedInOrbit(MissionControl mission, WindowPick pick, double ut)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(ut));
            mission.Send(InOrbit("Kerbin", ut, 9000));
            CraftCheckRouter.StartChecked(mission, _background, InOrbit("Kerbin", ut, 9000), new StartMission(Goal, 10m, "claude-sonnet-5-5", window: pick));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        /// <summary>The craft seen in orbit at <paramref name="ut"/>, and the setting edited to <paramref name="setting"/> (a change, so a revision).</summary>
        private void RevisedAt(MissionControl mission, double ut, TimeVersusDeltaV setting = TimeVersusDeltaV.MostEfficient)
        {
            mission.Send(new UtAdvanced(ut));
            mission.Send(InOrbit("Kerbin", ut, 9000));
            ToRevisedPlan(mission, setting);
        }

        [Fact]
        public void AnOpenWindowIsNotMissedAsItsSoonestDepartureMovesWithTheClockButIsOnceItCloses()
        {
            // The band is open from about day 157 to day 278 from a new game: Soonest leaves "now" all through it.
            var start = NewGame + 157 * Day;
            var transport = new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan(), LaunchPlan(), DunaPlan(), LaunchPlan(), DunaPlan());
            var mission = Control(transport);
            PickedInOrbit(mission, Duna(WindowKind.Soonest), start);

            // An hour on, past the departure picked: the same window, still open.
            RevisedAt(mission, start + 3600);
            Assert.False(mission.View.WindowPickNeeded);
            Assert.Equal("Duna:Soonest", mission.View.WindowPick.Key);
            Assert.Equal(start + 3600, mission.View.Windows.Rows[0].DepartureUt, 0);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            // A second revision after that refresh, days later and the band still open.
            RevisedAt(mission, start + 10 * Day, TimeVersusDeltaV.Fastest);
            Assert.False(mission.View.WindowPickNeeded);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            // Past the band's close Soonest is the next window, about a synodic period on: the window has gone, so it asks.
            RevisedAt(mission, NewGame + 290 * Day);
            Assert.True(mission.View.WindowPickNeeded);
            Assert.DoesNotContain(QuickReply.Approve, mission.View.QuickReplies);
        }

        [Fact]
        public void AnOptimalPickKeepsItsOpenWindowWhenTheNextOneIsALittleCheaper()
        {
            // The player's Duna flight: Optimal picked, then in orbit while that window is open the next synodic window costs a little less.
            var picked = NewGame + 200 * Day;
            var config = new PlayerConfig();
            var window = WindowChoice.Work(InOrbit("Kerbin", picked, 9000), Goal, config.CraftCheckMarginPercent, picked).RowFor(Duna(WindowKind.Optimal));
            var revised = NewGame + 236 * Day;
            var whole = WindowChoice.Work(InOrbit("Kerbin", revised, 9000), Goal, config.CraftCheckMarginPercent, revised);
            var synodic = DunaWindows(NewGame).Synodic;
            Assert.True(whole.RowFor(Duna(WindowKind.Optimal)).DepartureUt > window.DepartureUt + synodic / 2, "the next window is not the cheaper one here");
            Assert.True(whole.RowFor(Duna(WindowKind.Optimal)).TotalDv < whole.RowFor(Duna(WindowKind.Soonest)).TotalDv);

            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            PickedInOrbit(mission, Duna(WindowKind.Optimal), picked);
            RevisedAt(mission, revised);
            Assert.False(mission.View.WindowPickNeeded);
            Assert.Equal("Duna:Optimal", mission.View.WindowPick.Key);
            // The held departure stays in the open window: it leaves now, not two years on.
            var optimal = mission.View.Windows.Rows[1];
            Assert.True(optimal.DepartureUt < window.DepartureUt + synodic / 2, "Optimal jumped to the next window");
            // The window's anchor is the departure picked, not moved by the revision.
            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal(window.DepartureUt, reread.WindowPickDepartureUt.Value, 0);
        }

        [Fact]
        public void PlanTransferForAHeldOptimalPickLeavesInTheHeldWindow()
        {
            var revised = NewGame + 236 * Day;
            var held = DunaWindows(NewGame).Optimal.DepartureUt;
            var limit = held + DunaWindows(NewGame).Synodic / 2;
            const string input = "{\"target\":\"Duna\",\"target_pe\":60000}";

            var free = InOrbit("Kerbin", revised, 9000);
            free.StampMission(TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, Duna(WindowKind.Optimal));
            Assert.True(LeavesAt(PlanningTools.Default().Run("plan_transfer", input, free, null)) > limit, "without a held window Optimal is the next one here");

            var stamped = InOrbit("Kerbin", revised, 9000);
            stamped.StampMission(TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, new WindowPick("Duna", WindowKind.Optimal, held));
            var row = PlanningTools.Default().Run("plan_transfer", input, stamped, null);
            Assert.False(row.IsError, row.Json);
            // Past the held window's best departure, Optimal leaves now, and the row still names the player's pick.
            Assert.InRange(LeavesAt(row), revised - 1, revised + 1);
            Assert.Contains("\"pick\":\"optimal\"", row.Json);
        }

        [Fact]
        public void TheBlockTheCraftFliesLeavesInTheHeldWindow()
        {
            // The player's Duna flight: Optimal picked, then transfer_to(lexicon("target","Duna","target_pe",60000)) flown with its
            // default window while that window is open and the next synodic one costs a little less.
            var picked = NewGame + 200 * Day;
            var flown = NewGame + 236 * Day;
            var config = new PlayerConfig();
            var window = WindowChoice.Work(InOrbit("Kerbin", picked, 9000), Goal, config.CraftCheckMarginPercent, picked).RowFor(Duna(WindowKind.Optimal));
            var limit = window.DepartureUt + DunaWindows(NewGame).Synodic / 2;
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            PickedInOrbit(mission, Duna(WindowKind.Optimal), picked);
            mission.Send(new UtAdvanced(flown));
            mission.Send(InOrbit("Kerbin", flown, 9000));

            // The block's snapshot is stamped from the Mission's view, as the adapter stamps every in-flight one.
            var snapshot = MissionView.Stamp(InOrbit("Kerbin", flown, 9000), mission.View);
            var ask = new TransferFlightAsk { Target = "Duna", TargetPe = 60000, StartUt = flown, TurnTime = 60, DvLeft = 9000, Auto = true };
            var step = TransferFlight.Next(snapshot, ask);
            Assert.True(step.Error == "", step.Error);
            Assert.InRange(step.Ut, flown - 1, limit);
        }

        [Fact]
        public void AnOpenHeldWindowOnAnEccentricPairIsNotMissedWhenTheNextOneIsMuchCheaperButIsOnceItCloses()
        {
            // Kerbin to Moho: the window near day 835 from a new game costs over 15 % more than the next, near day 940.
            var margin = new PlayerConfig().CraftCheckMarginPercent;
            var picked = NewGame + 760 * Day;
            var held = MohoWindows(picked, null);
            var next = MohoWindows(NewGame + 900 * Day, null);
            Assert.InRange((held.Optimal.DepartureUt - NewGame) / Day, 820, 850);
            Assert.True(held.Optimal.TotalDv > next.Optimal.TotalDv * (1 + margin / 100.0), "the next window is not that much cheaper here");
            var limit = held.Optimal.DepartureUt + held.Synodic / 2;
            var pick = new WindowPick("Moho", WindowKind.Optimal, held.Optimal.DepartureUt);

            // In orbit a few days into the open window, the horizon reaching the cheaper next one: still held, and it leaves now-ish.
            var open = MohoWindows(NewGame + 838 * Day, pick);
            Assert.False(open.HeldMissed);
            Assert.True(open.Soonest.DepartureUt < limit && open.Optimal.DepartureUt < limit, "the rows jumped to the next window");

            // Past its close, still short of half a period on, it has gone.
            Assert.True(MohoWindows(NewGame + 870 * Day, pick).HeldMissed);
        }

        /// <summary>Kerbin to Moho: the departure of the held window B, near day 835 from a new game, and its search from day 760.</summary>
        private static WindowSearch MohoB(out double b)
        {
            var held = MohoWindows(NewGame + 760 * Day, null);
            b = held.Optimal.DepartureUt;
            return held;
        }

        [Fact]
        public void AHeldMohoWindowMovedLaterInItNeverMovesToTheNextWindow()
        {
            // The pick changed ten days into window B (its anchor moved there), then a revision ten days on: still B, or missed.
            double b;
            var halfWay = MohoB(out b).Synodic / 2;
            var later = MohoWindows(b + 20 * Day, new WindowPick("Moho", WindowKind.Optimal, b + 10 * Day));
            Assert.False(later.HeldMissed, "window B read as missed 20 days after its best departure");
            Assert.True(later.Soonest.DepartureUt < b + halfWay && later.Optimal.DepartureUt < b + halfWay,
                "the held rows moved to the next window: Soonest day " + (later.Soonest.DepartureUt - b) / Day + ", Optimal day " + (later.Optimal.DepartureUt - b) / Day + " after B");
        }

        [Fact]
        public void AMissedHeldWindowNeverReopens()
        {
            double b;
            MohoB(out b);
            var pick = new WindowPick("Moho", WindowKind.Optimal, b);
            Assert.True(MohoWindows(b + 30 * Day, pick).HeldMissed, "not missed 30 days after B");
            // Cost falls toward the next window, but B has closed.
            Assert.True(MohoWindows(b + 65 * Day, pick).HeldMissed, "reopened 65 days after B");
        }

        [Fact]
        public void AHeldOptimalPastItsBestDepartureLeavesNow()
        {
            double b;
            MohoB(out b);
            var now = b + 25 * Day;
            var open = MohoWindows(now, new WindowPick("Moho", WindowKind.Optimal, b));
            Assert.False(open.HeldMissed, "window B closed by 25 days after its best departure");
            Assert.InRange(open.Optimal.DepartureUt, now, now + 1);
            Assert.InRange(open.Soonest.DepartureUt, now, now + 1);
        }

        [Fact]
        public void AHeldWindowsCheapestIsFoundPastAFalseDipInItsRows()
        {
            // Soonest picked about 45 days before B: a row or two on from the anchor the coarse costs rise a little, then fall to B.
            double b;
            MohoB(out b);
            var open = MohoWindows(b - 55 * Day, new WindowPick("Moho", WindowKind.Soonest, b - 45.1 * Day));
            Assert.False(open.HeldMissed);
            Assert.True(System.Math.Abs(open.Optimal.DepartureUt - b) < Day, "Optimal leaves " + (open.Optimal.DepartureUt - b) / Day + " days from B");
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void AHeldSearchIsWellInsideTheToolCap()
        {
            double b;
            MohoB(out b);
            var snapshot = InOrbit("Kerbin", b - 55 * Day, 9000);
            snapshot.StampMission(TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, new WindowPick("Moho", WindowKind.Soonest, b - 45.1 * Day));
            var moho = snapshot.Bodies.Single(x => x.Name == "Moho");
            Timing.Within(TransferWindows.BudgetMs, () => Assert.Null(TransferWindows.Find(snapshot, "Moho", MissionPrompts.ArrivalPe(moho), true, 15).Error));
        }

        /// <summary>Duna's held Optimal window from a new game, and a low Kerbin orbit at day 290, after it has closed, stamped with that pick.</summary>
        private static GameSnapshot PastAHeldDunaWindow()
        {
            var snapshot = InOrbit("Kerbin", NewGame + 290 * Day, 9000);
            snapshot.StampMission(TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, new WindowPick("Duna", WindowKind.Optimal, DunaWindows(NewGame).Optimal.DepartureUt));
            return snapshot;
        }

        [Fact]
        public void TheBlockFailsForAMissedHeldWindowRatherThanFlyTheNext()
        {
            // transfer_to's default window, from a snapshot stamped with the held pick and no revision since its window closed.
            var snapshot = PastAHeldDunaWindow();
            var ask = new TransferFlightAsk { Target = "Duna", TargetPe = 60000, StartUt = snapshot.Ut, TurnTime = 60, DvLeft = 9000, Auto = true };
            var step = TransferFlight.Next(snapshot, ask);
            Assert.Contains("window the player picked", step.Error);
            Assert.Contains("missed", step.Error);
        }

        [Fact]
        public void AWaitForAMissedHeldWindowTakesNoLaterWindow()
        {
            var snapshot = PastAHeldDunaWindow();
            var duna = snapshot.Bodies.Single(x => x.Name == "Duna");
            Assert.Null(WindowWaits.Window(snapshot, duna, TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, snapshot.WindowPick));
        }

        [Fact]
        public void ALateApprovalAfterAHeldWindowClosedAsksThePlayerRatherThanMoveToTheNext()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            ToLaunchPlan(mission, 1203.48, 20000);
            clock.Take(mission);
            // Back long after the held window has closed.
            var back = Window(1203.48).DepartureUt + 120 * Day;
            mission.Send(new UtAdvanced(back));
            clock.Take(mission);
            mission.Send(new Approve());
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("the Due Time moves to") || e.Text.Contains("so the script warps there"));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("was missed") && e.Text.Contains("picks again"));
            // Claudia's revision waits for the player's fresh pick.
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.True(mission.View.WindowPickNeeded);
        }

        /// <summary>The Moho windows from a low Kerbin orbit at <paramref name="ut"/>, keeping to <paramref name="pick"/>'s window when it holds one.</summary>
        private static WindowSearch MohoWindows(double ut, WindowPick pick)
        {
            var snapshot = InOrbit("Kerbin", ut, 9000);
            snapshot.StampMission(TimeVersusDeltaV.MostEfficient, new PlayerConfig().CraftCheckMarginPercent, pick);
            var moho = snapshot.Bodies.Single(b => b.Name == "Moho");
            return TransferWindows.Find(snapshot, "Moho", MissionPrompts.ArrivalPe(moho), true, new PlayerConfig().CraftCheckMarginPercent);
        }

        [Fact]
        public void AnOlderSavesPickWithNoDepartureIsNotHeldSoARevisionAsks()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Optimal));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var state = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            // Saved before #966: the pick, but no departure on record.
            state.WindowPickDepartureUt = null;

            var reload = Control(new StreamingFakeTransport(DunaPlan()));
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { state }));
            reload.Send(new UtAdvanced(NewGame + Day));
            reload.Send(InOrbit("Kerbin", NewGame + Day, 9000));
            ToRevisedPlan(reload);
            Assert.True(reload.View.WindowPickNeeded);
            Assert.DoesNotContain(QuickReply.Approve, reload.View.QuickReplies);
        }

        /// <summary>The departure plan_transfer's pick leaves at, from its summary ("... leaving at UT 5098803").</summary>
        private static double LeavesAt(ToolOutcome outcome)
        {
            var at = outcome.Summary.LastIndexOf("UT ", System.StringComparison.Ordinal);
            return double.Parse(outcome.Summary.Substring(at + 3), System.Globalization.CultureInfo.InvariantCulture);
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
            // The ejection has flown: the pick no longer holds, and a save says it was spent.
            Assert.True(mission.View.WindowPick.IsNone);
            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.True(reread.WindowPick.IsNone);
            Assert.True(reread.WindowPickSpent);

            var atDuna = between + 200 * Day;
            mission.Send(new UtAdvanced(atDuna));
            mission.Send(InOrbit("Duna", atDuna, 9000));
            ToRevisedPlan(mission);
            Assert.True(mission.View.WindowPickNeeded);
            Assert.Equal("Kerbin", mission.View.Windows.Body);
        }

        [Fact]
        public void ARevisionOutOfFlightAfterTheEjectionStillWaitsToAskForTheReturn()
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Soonest), NewGame, RoundTrip);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var atDuna = NewGame + 300 * Day;
            mission.Send(new UtAdvanced(atDuna));
            mission.Send(InOrbit("Duna", atDuna, 9000));
            Assert.True(mission.View.WindowPick.IsNone);

            // Revised from the Tracking Station: nothing to work the rows from, so it waits for the craft.
            mission.Send(new SceneChanged(GameScene.Other));
            ToRevisedPlan(mission);
            Assert.True(mission.View.WindowPickNeeded);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(InOrbit("Duna", atDuna, 9000));
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
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan(), DunaPlan()));
            ToMissionPlan(mission, Duna(WindowKind.Optimal));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var picked = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues()).WindowPickDepartureUt;
            Assert.True(picked.HasValue);
            mission.Send(new UtAdvanced(NewGame + Day));
            mission.Send(InOrbit("Kerbin", NewGame + Day, 9000));
            ToRevisedPlan(mission);
            Assert.False(mission.View.WindowPickNeeded);

            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal("Duna:Optimal", reread.WindowPick.Key);
            Assert.False(reread.WindowPickNeeded);
            Assert.True(reread.WindowPickCarried);
            // The held window's anchor: the departure picked on the form, kept through the revision.
            Assert.Equal(picked.Value, reread.WindowPickDepartureUt.Value);

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
