// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
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
    /// #887: a <c>return</c> from an inclined orbit has a Due Time that leads the cheap alignment (#816). Approved after the alignment but before the window,
    /// the block used to restart past the alignment and search on to the next one, about 19 h later, unflagged. The Mission now keeps the planned window:
    /// the block flies it while it is reachable, and a window gone by is a missed one (<see cref="WindowWaits.Missed"/>) with its usual wording.
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

        private static ScriptedResponse ReturnSequencePlan()
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Return.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Return\",\"intent\":\"Fly home.\",\"why\":\"Only step.\",\"target\":\"Kerbin\","
                + "\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":320,\"expected_duration_s\":90000,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + JsonSerializer.Serialize(CheapReturnScript) + ","
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
        private static GameSnapshot WindowPastItsAlignment(out double window, out double dueBy)
        {
            foreach (var lan in new[] { 90.0, 30, 150, 210, 270, 330, 0, 60, 120, 180, 240, 300 })
            {
                var snapshot = OverTheMun(lan);
                double? by;
                var plan = ReturnAim.CheapDepartureUt(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, CheapWindow = true }, out by);
                if (!plan.HasValue || !by.HasValue || by.Value >= plan.Value + 1) continue;
                window = plan.Value;
                dueBy = by.Value;
                return snapshot;
            }
            throw new Exception("no node with a Due Time ahead of its window");
        }

        private MissionControl ToReturnPlan(GameSnapshot snapshot)
        {
            var mission = Control(new StreamingFakeTransport(ReturnMissionPlan(), ReturnSequencePlan()));
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
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("window of " + KerbinTime.Date(window)) && e.Text.Contains("was missed"));
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
    }
}
