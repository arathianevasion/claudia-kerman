// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #887: a <c>return</c> from an inclined orbit has a Due Time that leads the cheap alignment (#816). Approved after the alignment but before the window,
    /// the block used to restart past the alignment and search on to the next one, about 19 h later, unflagged. The Mission now keeps the planned window:
    /// the block flies it while it is reachable, and a window gone by is a missed one (<see cref="WindowWaits.Missed"/>), worded as a return (#905).
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        private const string CheapReturnScript = "local res is return(lexicon(\"target\", \"none\")).\n";
        private const double CheapReturnStart = 1000;

        private static ScriptedResponse ReturnMissionPlan()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: return.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Home.\",\"sequences\":[{\"name\":\"Return\",\"intent\":\"Fly home.\",\"delta_v\":320,\"timing\":\"at the cheap window\",\"due_time_ut\":-1}],"
                + "\"total_delta_v\":320,\"expected_duration\":\"a day\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse ReturnSequencePlan(string script = CheapReturnScript)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Return.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Return\",\"intent\":\"Fly home.\",\"why\":\"Only step.\",\"target\":\"Kerbin\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":320,\"expected_duration_s\":90000,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + JsonSerializer.Serialize(script) + ","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        /// <summary>The craft in a 30 km orbit of the Mun, <paramref name="lan"/>° node, 66.43° off the Mun's plane, at <see cref="CheapReturnStart"/>.</summary>
        private static GameSnapshot OverTheMun(double lan, double ut = CheapReturnStart)
        {
            var snapshot = OnPad(ut, 9000);
            var mun = snapshot.Bodies.Single(b => b.Name == "Mun");
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Mun";
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = mun.Radius + 30000, Eccentricity = 0, Inclination = 66.43, Lan = lan, Body = "Mun", Periapsis = 30000, Apoapsis = 30000, Epoch = ut,
            };
            return snapshot;
        }

        private static IDictionary<string, object> CheapReturn(GameSnapshot snapshot, double earliest)
        {
            var input = new Dictionary<string, object> { { "target_pe", 35000.0 }, { "earliest_ut", earliest }, { "cheap_window", true } };
            return BlockCalls.ReturnBurn(input, snapshot);
        }

        /// <summary>A node whose cheap window the Due Time leads, with the plan's window and what the Due Time leads.</summary>
        private static GameSnapshot WindowPastItsAlignment(out double window, out double dueBy, double gapS = 1)
        {
            foreach (var lan in new[] { 90.0, 30, 150, 210, 270, 330, 0, 60, 120, 180, 240, 300 })
            {
                var snapshot = OverTheMun(lan);
                double? by;
                var plan = ReturnAim.CheapDepartureUt(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, CheapWindow = true }, out by);
                if (!plan.HasValue || !by.HasValue || by.Value + gapS > plan.Value) continue;
                window = plan.Value;
                dueBy = by.Value;
                return snapshot;
            }
            throw new Exception("no node with a Due Time ahead of its window");
        }

        private MissionControl ToReturnPlan(GameSnapshot snapshot, string script = CheapReturnScript)
        {
            var mission = Control(new StreamingFakeTransport(ReturnMissionPlan(), ReturnSequencePlan(script)));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(snapshot);
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission("Return to Kerbin.", 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(snapshot, "Return to Kerbin.")));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            return mission;
        }

        [Fact]
        public void AReturnApprovedPastReachingItsWindowIsAMissedWindowNotAWaitForTheNextBand()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            var clock = new FakeAlarmClock();
            var mission = ToReturnPlan(snapshot);
            clock.Take(mission);
            Assert.True(mission.View.DueTime.HasDueTime);

            var back = window + 600;
            mission.Send(new UtAdvanced(back));
            mission.Send(OverTheMun(snapshot.Orbit.Lan, back));
            clock.Take(mission);
            mission.Send(new Approve());
            var commands = clock.Take(mission);

            Assert.Empty(commands.OfType<StartScript>());
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Missed the return window the Due Time waited for (UT "));
        }

        [Fact]
        public void ALateReturnOverAPlaneThatCostsNothingIsUnchangedFromBeforeTheKeptWindow()
        {
            // A fixed window over three Kerbin days off: a Due Time, but no costly plane, so no window is kept and a late Approval just flies.
            var ut = CheapReturnStart + 3 * 21600;
            var snapshot = OverTheMun(90);
            snapshot.Orbit.Inclination = 0;
            var clock = new FakeAlarmClock();
            var mission = ToReturnPlan(snapshot, "local res is return(lexicon(\"target\", \"none\", \"window\", " + ut.ToString(CultureInfo.InvariantCulture) + ")).\n");
            clock.Take(mission);
            Assert.True(mission.View.DueTime.HasDueTime);
            Assert.Equal("", mission.View.DueTime.WaitsFor);

            var back = ut + 600;
            mission.Send(new UtAdvanced(back));
            var late = OverTheMun(90, back);
            late.Orbit.Inclination = 0;
            mission.Send(late);
            clock.Take(mission);
            mission.Send(new Approve());
            Assert.Single(clock.Take(mission).OfType<StartScript>());
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was missed") || e.Text.Contains("no next window"));
        }

        [Fact]
        public void TheMissionListNamesNoTransferWindowForAReturnsDueTime()
        {
            var entry = new MissionListEntry();
            var plan = new SequencePlan { Script = CheapReturnScript, DueTimeUt = 5000 };
            MissionHistory.Due(entry, MissionStage.AwaitingSequenceApproval, plan, "Kerbin", false);
            Assert.Equal(5000, entry.DueUt);
            Assert.Equal("", entry.DueFor);
        }

        [Fact]
        public void AReturnRestartedOnTimeIsUnchanged()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            var clock = new FakeAlarmClock();
            var mission = ToReturnPlan(snapshot);
            clock.Take(mission);
            Assert.Equal(dueBy - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);

            var due = dueBy - WindowWaits.LaunchLeadS;
            var plain = Convert.ToDouble(CheapReturn(snapshot, due)["ut"], CultureInfo.InvariantCulture);
            snapshot.ReturnWindowUt = window;
            Assert.Equal(plain, Convert.ToDouble(CheapReturn(snapshot, due)["ut"], CultureInfo.InvariantCulture), 3);

            mission.Send(new UtAdvanced(due + 60));
            mission.Send(OverTheMun(snapshot.Orbit.Lan, due + 60));
            clock.Take(mission);
            mission.Send(new Approve());
            Assert.Single(clock.Take(mission).OfType<StartScript>());
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was missed"));
        }

        // ---- #906: the kept window survives a save and load ----

        private MissionControl Reloaded(MissionControl mission, GameSnapshot at)
        {
            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            var reload = Control(new StreamingFakeTransport());
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { reread }));
            reload.Send(at);
            return reload;
        }

        private MissionControl RunningFromTheDueTime(GameSnapshot snapshot, double dueBy)
        {
            int runId;
            return RunningFromTheDueTime(snapshot, dueBy, out runId);
        }

        private MissionControl RunningFromTheDueTime(GameSnapshot snapshot, double dueBy, out int runId)
        {
            var clock = new FakeAlarmClock();
            var mission = ToReturnPlan(snapshot);
            clock.Take(mission);
            var due = dueBy - WindowWaits.LaunchLeadS;
            mission.Send(new UtAdvanced(due + 60));
            mission.Send(OverTheMun(snapshot.Orbit.Lan, due + 60));
            clock.Take(mission);
            mission.Send(new Approve());
            runId = Assert.Single(clock.Take(mission).OfType<StartScript>()).RunId;
            return mission;
        }

        [Fact]
        public void TheKeptWindowRoundTripsInTheSaveAndALateRestartAfterTheLoadFliesIt()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            var mission = RunningFromTheDueTime(snapshot, dueBy);
            var state = Assert.Single(mission.ExportStates());
            Assert.Equal(window, state.ReturnWindowUt.Value, 3);
            Assert.Equal(window, MissionSaveState.FromValues(state.ToValues()).ReturnWindowUt.Value, 3);

            var reload = Reloaded(mission, OverTheMun(snapshot.Orbit.Lan, dueBy));
            Assert.Equal(window, reload.View.ReturnWindowUt.Value, 3);

            var late = dueBy + (window - dueBy) / 2;
            Assert.True(late < window && late > dueBy, "late is past the alignment, before the window");
            var inSession = OverTheMun(snapshot.Orbit.Lan, late);
            inSession.ReturnWindowUt = window;
            var reloaded = MissionView.Stamp(OverTheMun(snapshot.Orbit.Lan, late), reload.View);
            Assert.Equal(window, reloaded.ReturnWindowUt.Value, 3);
            Assert.Equal(Convert.ToDouble(CheapReturn(inSession, late)["ut"], CultureInfo.InvariantCulture), Convert.ToDouble(CheapReturn(reloaded, late)["ut"], CultureInfo.InvariantCulture), 3);
        }

        [Fact]
        public void ALateRestartAfterTheLoadPastTheWindowIsTheNormalRuleAsInSession()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            var mission = RunningFromTheDueTime(snapshot, dueBy);
            var reload = Reloaded(mission, OverTheMun(snapshot.Orbit.Lan, dueBy));
            var late = window + 600;
            var stamped = MissionView.Stamp(OverTheMun(snapshot.Orbit.Lan, late), reload.View);
            var plain = OverTheMun(snapshot.Orbit.Lan, late);
            Assert.Equal(Convert.ToDouble(CheapReturn(plain, late)["ut"], CultureInfo.InvariantCulture), Convert.ToDouble(CheapReturn(stamped, late)["ut"], CultureInfo.InvariantCulture), 3);
        }

        [Fact]
        public void AnOldSaveWithoutTheWindowLoadsWithNoneAndRestartsAsBefore()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            var mission = RunningFromTheDueTime(snapshot, dueBy);
            var values = Assert.Single(mission.ExportStates()).ToValues();
            Assert.True(values.Remove("returnWindowUt"));
            var old = MissionSaveState.FromValues(values);
            Assert.Null(old.ReturnWindowUt);
            var reload = Control(new StreamingFakeTransport());
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { old }));
            reload.Send(OverTheMun(snapshot.Orbit.Lan, dueBy));
            Assert.Null(reload.View.ReturnWindowUt);
            Assert.DoesNotContain("returnWindowUt", new MissionSaveState().ToValues().Keys);
        }

        [Fact]
        public void TheKeptWindowIsClearedWhenTheRunEnds()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            int runId;
            var mission = RunningFromTheDueTime(snapshot, dueBy, out runId);
            Assert.NotNull(mission.View.ReturnWindowUt);
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), runId));
            Assert.Null(mission.View.ReturnWindowUt);
        }

        [Fact]
        public void ANonInclinedReturnRecordsNoWindow()
        {
            var ut = CheapReturnStart + 3 * 21600;
            var snapshot = OverTheMun(90);
            snapshot.Orbit.Inclination = 0;
            var mission = ToReturnPlan(snapshot, "local res is return(lexicon(\"target\", \"none\", \"window\", " + ut.ToString(CultureInfo.InvariantCulture) + ")).\n");
            var state = Assert.Single(mission.ExportStates());
            Assert.Equal("", state.WindowPlanet);
            Assert.Null(state.WindowUt);
        }

        [Fact]
        public void ACostlyPlaneWindowBeforeItsAlignmentIsRecorded()
        {
            foreach (var lan in new[] { 90.0, 30, 150, 210, 270, 330, 0, 60, 120, 180, 240, 300 })
            {
                var snapshot = OverTheMun(lan);
                double? by;
                var plan = ReturnAim.CheapDepartureUt(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, CheapWindow = true }, out by);
                if (!plan.HasValue || !by.HasValue || Math.Abs(by.Value - plan.Value) >= 1) continue;
                var mission = ToReturnPlan(snapshot);
                if (!mission.View.DueTime.HasDueTime) continue;
                Assert.Equal(plan.Value, Assert.Single(mission.ExportStates()).WindowUt.Value, 3);
                return;
            }
            throw new Exception("no node with a Due Time and a window before its alignment");
        }
    }
}
