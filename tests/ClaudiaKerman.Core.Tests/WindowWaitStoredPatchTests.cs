// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #852 through the Mission seam: what a plan unread before #852 gets now on each Due Time path. PlanFigures raises the duration; WindowWait and TransferWait set the Due Time
    /// after it, so PlanFigures sees only a Due Time of Claudia's own.
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        private const string StoredCoast = "local t is time:seconds + ship:orbit:nextpatcheta.\nlocal res is coast(lexicon(\"until\", t + 60)).\nset res to capture(lexicon(\"target_pe\", 80000)).\n";
        private const double PatchEndIn = 63000;

        private static ScriptedResponse CoastPlan(double due = -1)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Coast.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Coast\",\"intent\":\"Coast to the patch and capture.\",\"why\":\"First step.\",\"target\":\"80 km orbit\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":180,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + JsonSerializer.Serialize(StoredCoast) + ","
                + "\"due_time_ut\":" + due.ToString(CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        /// <summary>The craft in Kerbin orbit, its patch ending <see cref="PatchEndIn"/> s after <paramref name="ut"/>.</summary>
        private static GameSnapshot InOrbit(double ut)
        {
            var snapshot = OnPad(ut, 9000);
            var shape = BurnPointsTests.Orbiting(1000);
            snapshot.Situation = shape.Situation;
            snapshot.Orbit = shape.Orbit;
            snapshot.NextPatchEndUt = ut + PatchEndIn;
            return snapshot;
        }

        private void ToCoastPlan(MissionControl mission, string goal, double ut)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(InOrbit(ut));
            CraftCheckRouter.StartChecked(mission, _background, InOrbit(ut), new StartMission(goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(InOrbit(ut), goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private static string PlanCard(MissionControl mission)
        {
            return mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).Text;
        }

        private static string Duration(double seconds)
        {
            return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        [Fact]
        public void AStoredNextPatchCoastWithNoDueTimeHasItsDurationRaised()
        {
            const double now = 1203.48;
            var mission = Control(new StreamingFakeTransport(DunaPlan(), CoastPlan()));
            ToCoastPlan(mission, "Orbit the Mun.", now);

            Assert.False(mission.View.DueTime.HasDueTime);
            var raised = BurnPoints.First(StoredCoast, InOrbit(now), now, 180);
            Assert.Equal(PatchEndIn + 60, raised.WaitSeconds, 3);
            Assert.Contains(Duration(raised.MinimumDurationSeconds), PlanCard(mission));
        }

        /// <summary>A Due Time of Claudia's own is there when PlanFigures looks: the plan stays as it was before #852, unread, its duration Claudia's.</summary>
        [Fact]
        public void AStoredNextPatchCoastOnAPlanWithItsOwnLaterDueTimeKeepsItsDuration()
        {
            const double now = 1203.48;
            var due = Window(now).DepartureUt;
            var mission = Control(new StreamingFakeTransport(DunaPlan(), CoastPlan(due)));
            ToCoastPlan(mission, Goal, now);

            Assert.Equal(due, mission.View.DueTime.Ut, 0);
            Assert.Contains(Duration(600), PlanCard(mission));
        }

        /// <summary>
        /// The one Due Time PlanFigures can't see: a window's on a craft in orbit before its first run, set after the raise. This pins what happens: the raise stands, counted
        /// from now though the run starts at the Due Time, as #833's direct form does too. It only moves the hard stop later, and the Due Time itself is the window's, untouched.
        /// </summary>
        [Fact]
        public void AStoredNextPatchCoastGettingAWindowDueTimeAfterTheRaiseKeepsBoth()
        {
            const double now = 1203.48;
            var mission = Control(new StreamingFakeTransport(DunaPlan(), CoastPlan()));
            ToCoastPlan(mission, Goal, now);

            var snapshot = InOrbit(now);
            var window = WindowWaits.Window(snapshot, snapshot.Bodies.Single(b => b.Name == "Duna"), new Config.PlayerConfig().DefaultTimeVersusDeltaV, new Config.PlayerConfig().CraftCheckMarginPercent);
            Assert.Equal(window.DepartureUt - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);
            Assert.Contains(Duration(BurnPoints.First(StoredCoast, InOrbit(now), now, 180).MinimumDurationSeconds), PlanCard(mission));
        }
    }
}
