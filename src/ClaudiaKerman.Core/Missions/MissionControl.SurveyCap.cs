// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #1343: a whole orbital survey ends within the lesser of 24 game hours from its first Sequence Plan at the body and the Contract's
    /// deadline (<see cref="SurveyCap"/>). After a Debrief that didn't end the Mission, with orbital waypoints still open at the craft's body,
    /// the next Sequence Plan request asks for the next Survey Plane when another 12-hour window fits before the cap and the #1306 stops
    /// (Margin check, Spending Cap) allow; otherwise it is for a revised Mission Plan that drops the Contract, which Auto Mode leaves for the player.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>#1343: the UT of the first Sequence Plan offered at an orbital survey's body; null before it. Saved.</summary>
            public double? SurveyStartUt;
        }

        /// <summary>#1343: the chosen orbital waypoints at the craft's body whose Requirement the game shows not met, with the Contract each belongs to.</summary>
        private List<ChosenRequirement> OpenSurveyWaypoints(Mission mission)
        {
            var snapshot = _snapshot;
            if (snapshot == null || snapshot.Contracts == null) return new List<ChosenRequirement>();
            var orbital = mission.ChosenRequirements.Where(r => SurveyScheduleTool.IsOrbital(r.Waypoint)
                && string.Equals(r.Waypoint.Body, snapshot.Body, StringComparison.OrdinalIgnoreCase)).ToList();
            var readings = mission.Contracts.Read(snapshot, orbital, null);
            return orbital.Where(r => readings.Any(x => x.Key == r.Key && x.State == "not met")).ToList();
        }

        /// <summary>#1343: the survey's clock starts at the first Sequence Plan offered while an orbital survey's waypoints are open at the craft's body.</summary>
        private void SurveyBegins(Mission mission)
        {
            if (mission.SurveyStartUt.HasValue || OpenSurveyWaypoints(mission).Count == 0) return;
            mission.SurveyStartUt = _ut;
        }

        /// <summary>#1343: the next request after a Debrief when orbital survey waypoints are open: the next Survey Plane, or the revised plan that drops the Contract; null when none is open.</summary>
        private Turn SurveyTurn(Mission mission, bool offNominal)
        {
            var open = OpenSurveyWaypoints(mission);
            if (open.Count == 0 || !mission.SurveyStartUt.HasValue) return null;
            var body = _snapshot.Body;
            var names = open.Select(r => r.Waypoint.Where() + " (" + r.Title + ")").ToList();
            var guids = new HashSet<string>(open.Select(r => ContractWatch.GuidOf(r.Key)));
            var deadlines = _snapshot.Contracts.Accepted.Where(c => guids.Contains(c.Guid) && c.DeadlineUt.HasValue).Select(c => c.DeadlineUt.Value).ToList();
            var cap = SurveyCap.Cap(mission.SurveyStartUt.Value, deadlines);
            var stop = "";
            if (!SurveyCap.WindowFits(_ut, cap)) stop = SurveyCap.WhyNoRoom(mission.SurveyStartUt.Value, _ut, deadlines);
            else stop = SurveyRetryStop(mission, open);
            if (stop.Length == 0) return NextSequenceTurn(offNominal, SurveyCap.NextPlaneLine(names, body));
            Event(mission, "survey_stopped", stop);
            // The revised plan may drop what the survey can no longer finish: its Contracts become droppable (ContractWatch.Missed), as a missed Requirement's are.
            foreach (var requirement in open) Missed(mission, requirement, "the survey can't finish: " + stop);
            mission.Stage = MissionStage.Planning;
            return new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = SurveyCap.StopLine(names, body, stop) + MissionPrompts.Facts(_ut, _snapshot) + PlanFigures(mission, mission.Goal),
                Notice = DebriefPrompts.RevisedPlanDroppingSurvey,
                GoalStands = true,
            };
        }

        /// <summary>#1343: the #1306 stops (Margin check, Spending Cap, deadline) on another Survey Plane, where the Mission Plan names the step that serves the waypoints; "" when none stops it.</summary>
        private string SurveyRetryStop(Mission mission, List<ChosenRequirement> open)
        {
            if (mission.Plan == null) return "";
            var steps = new List<OpenRequirement>();
            foreach (var requirement in open)
            {
                var served = mission.ServedBy.FirstOrDefault(s => s.Key == requirement.Key);
                if (served == null || served.Leg < 0 || served.Leg >= mission.Plan.Sequences.Count) continue;
                int misses;
                mission.RequirementMisses.TryGetValue(requirement.Key, out misses);
                steps.Add(new OpenRequirement { Requirement = requirement, Step = served.Leg + 1, Misses = misses });
            }
            return steps.Count == 0 ? "" : RetryStop(mission, steps);
        }
    }
}
