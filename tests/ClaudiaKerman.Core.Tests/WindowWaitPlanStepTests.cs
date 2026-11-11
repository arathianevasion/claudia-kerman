// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Missions;

using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #977: the ascent from Duna sent as plan_step 5 (the crew report's step) in mission m20261110-203402-45d1 flies step 6, so it still
    /// waits on the surface for the Kerbin window; the mod logs the step it took and tells Claudia in the next request.
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        /// <summary>The flight's Mission Plan of 8 sequences, its names and delta-v as Claudia wrote them.</summary>
        private static ScriptedResponse DunaRoundTripPlan()
        {
            var legs = PlanStepMatchTests.AfterAscent();
            var sequences = string.Join(",", legs.Select(s => "{\"name\":\"" + s.Name + "\",\"intent\":\"" + s.Intent.Replace("\"", "") + "\",\"delta_v\":"
                + s.DeltaV.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"timing\":\"soon\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}"));
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Duna and home.\",\"sequences\":[" + sequences + "],"
                + "\"total_delta_v\":9000,\"expected_duration\":\"two years\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse DunaAscentPlan(int step)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Ascend from Duna.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Ascend from Duna\",\"plan_step\":" + step + ",\"intent\":\"Reach a 60 km Duna orbit.\",\"why\":\"Next.\",\"target\":\"60 km orbit\","
                + "\"tolerances\":\"Ap within 5 km\",\"expected_delta_v\":803,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"" + LaunchScript + "\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse Answer()
        {
            return Reply("{\"kind\":\"answer\",\"say\":\"Noted.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"debrief\":null}");
        }

        [Fact]
        public void TheAscentSentAsTheCrewReportsStepStillWaitsOnDunaForTheKerbinWindow()
        {
            var transport = new StreamingFakeTransport(DunaRoundTripPlan(), DunaAscentPlan(5), Answer());
            var mission = Control(transport);
            const double now = 1203.48;
            ToAscent(mission, "Duna", now, "Fly to Duna, land, send a crew report and come home.");

            // AfterAscent reads the departure from step 6 on: the Kerbin transfer.
            var view = mission.View;
            Assert.True(view.DueTime.HasDueTime);
            Assert.Equal("the Kerbin window", view.DueTime.WaitsFor);
            Assert.Equal(6, view.CurrentSequence + 1);

            // The next request tells Claudia, in one line.
            mission.Send(new Missions.Reply("why the wait?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 3);
            var asked = Messages(transport.Requests.Last());
            Assert.Contains("plan_step 5 is \\\"Crew report at Duna\\\"; this plan flies step 6, \\\"Ascend from Duna\\\".", asked);
        }

        private static ScriptedResponse KerbinTransferPlan(int step)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Transfer to Kerbin, next window.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Transfer to Kerbin, next window\",\"plan_step\":" + step + ",\"intent\":\"Depart Duna for Kerbin.\",\"why\":\"Next.\",\"target\":\"Kerbin encounter\","
                + "\"tolerances\":\"Pe within 20 km\",\"expected_delta_v\":1718,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"" + LaunchScript + "\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        [Fact]
        public void TheTransferSentAsTheLandingsStepKeepsBackOnlyTheReturnForLaterLegs()
        {
            var transport = new StreamingFakeTransport(DunaRoundTripPlan(), KerbinTransferPlan(4));
            var mission = Control(transport);
            ToAscent(mission, "Duna", 1203.48, "Fly to Duna, land, send a crew report and come home.");

            Assert.Equal(7, mission.View.CurrentSequence + 1);
            // What an in-flight plan_transfer or margin check reserves: the return alone, not 2,879 from step 4 on.
            Assert.Equal(100, MissionView.Stamp(new GameSnapshot(), mission.View).LaterLegsDv.Value, 3);
        }

        [Fact]
        public void APlanStepNamingItsOwnSequenceIsNotRemarked()
        {
            var transport = new StreamingFakeTransport(DunaRoundTripPlan(), DunaAscentPlan(6), Answer());
            var mission = Control(transport);
            ToAscent(mission, "Duna", 1203.48, "Fly to Duna, land, send a crew report and come home.");
            Assert.True(mission.View.DueTime.HasDueTime);

            mission.Send(new Missions.Reply("why the wait?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 3);
            Assert.DoesNotContain("this plan flies step", Messages(transport.Requests.Last()));
        }
    }
}
