// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Contracts;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #1306: a Contract Requirement missed and still open at the body the craft is at is retried before any leg leaves it (<see cref="ContractRetry"/>).
    /// After a Debrief that didn't end the Mission, the next Sequence Plan request asks for the retry, unless the Margin check (the rest of the plan
    /// plus another retry won't fit), the Spending Cap (no room for another retry) or the Contract's deadline (it would pass) stops it: then the
    /// request is for a revised Mission Plan that drops the Contract, which Auto Mode leaves for the player (MissionControl.AutoMode.cs).
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>#1306: how many Debriefs showed each Requirement not met, by key; saved with the Mission (#1331), and an older save counts afresh.</summary>
            public readonly Dictionary<string, int> RequirementMisses = new Dictionary<string, int>();
        }

        /// <summary>#1306: a Debrief's readings: each one not met is a miss, and from the second the chat names it. Nothing waits on it.</summary>
        private void CountMisses(Mission mission, IEnumerable<RequirementReading> readings)
        {
            foreach (var reading in readings.Where(r => r.State == "not met"))
            {
                int misses;
                mission.RequirementMisses.TryGetValue(reading.Key, out misses);
                mission.RequirementMisses[reading.Key] = ++misses;
                if (misses >= 2) Notice(ContractRetry.MissedAgain(reading, misses));
            }
        }

        /// <summary>#1306: the chosen Requirements missed, still not met, whose body is the one the craft is at, each with the plan step that serves it.</summary>
        private List<OpenRequirement> OpenAtBody(Mission mission)
        {
            var snapshot = _snapshot;
            var open = new List<OpenRequirement>();
            if (snapshot == null || snapshot.Contracts == null || mission.Plan == null || mission.RequirementMisses.Count == 0) return open;
            var missed = mission.ChosenRequirements.Where(r => mission.RequirementMisses.ContainsKey(r.Key)
                && string.Equals(r.Body, snapshot.Body, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var reading in mission.Contracts.Read(snapshot, missed, null).Where(r => r.State == "not met"))
            {
                var served = mission.ServedBy.FirstOrDefault(s => s.Key == reading.Key);
                if (served == null || served.Leg < 0 || served.Leg >= mission.Plan.Sequences.Count) continue;
                open.Add(new OpenRequirement { Requirement = missed.First(r => r.Key == reading.Key), Step = served.Leg + 1, Misses = mission.RequirementMisses[reading.Key] });
            }
            return open;
        }

        /// <summary>
        /// #1306: why another retry of <paramref name="open"/> may not go, or "": the Margin check finds the rest of the plan plus the retried
        /// legs short, the Spending Cap has no room for another retry's requests, or the deadline recheck finds a Contract too late.
        /// </summary>
        private string RetryStop(Mission mission, IList<OpenRequirement> open)
        {
            var retried = open.Select(o => o.Step - 1).Distinct().Select(leg => mission.Plan.Sequences[leg]);
            var margin = MarginCheck.Of(_snapshot, retried.Concat(RestOfPlan(mission)).ToList(), _config.CraftCheckMarginPercent, _config.CheckElectricChargePercent,
                mission.Remote, null, mission.Chutes, mission.Goal, StepsBegun(mission), mission.PlannedAscent);
            // The delta-v figure alone: a finding the plan already had (chutes, charge) is the Debrief's, not a reason to stop retrying.
            if (margin.Margin != null && margin.Margin.Level == MarginLevel.Short)
                return "the Margin check finds the rest of the plan plus another retry won't fit (" + margin.Margin.Describe().TrimEnd('.') + ")";
            var worst = RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, NextSequenceTurn(true, ContractRetry.RetryLine(open, _snapshot.Body)))) + AutoModeReserve(mission);
            if (mission.Committed + worst > mission.SpendingCap)
                return "the Spending Cap ($" + Dollars(mission.SpendingCap) + ") has no room for another retry's requests ($" + Dollars(worst) + " at worst, $" + Dollars(mission.Committed) + " committed)";
            var guids = new HashSet<string>(open.Select(o => ContractWatch.GuidOf(o.Requirement.Key)));
            var late = RecheckDeadlines(mission).Where(l => guids.Contains(l.Guid)).ToList();
            if (late.Count > 0)
                return "the Contract's deadline would pass: " + string.Join("; ", late.Select(l => "\"" + l.Title + "\" is " + l.Reason).ToArray());
            return "";
        }

        /// <summary>#1306: the next request after a Debrief when a Requirement is open at the craft's body: the retry, or the revised plan that drops it; null when none is open.</summary>
        private Turn RetryTurn(Mission mission, bool offNominal)
        {
            var open = OpenAtBody(mission);
            if (open.Count == 0) return null;
            var body = _snapshot.Body;
            var stop = RetryStop(mission, open);
            if (stop.Length == 0) return NextSequenceTurn(offNominal, ContractRetry.RetryLine(open, body));
            Event(mission, "contract_retry_stopped", stop);
            mission.Stage = MissionStage.Planning;
            return new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = ContractRetry.StopLine(open, body, stop) + MissionPrompts.Facts(_ut, _snapshot) + PlanFigures(mission, mission.Goal),
                Notice = DebriefPrompts.RevisedPlanDroppingContract,
                GoalStands = true,
            };
        }
    }
}
