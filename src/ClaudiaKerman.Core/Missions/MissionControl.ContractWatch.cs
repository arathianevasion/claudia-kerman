// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #1025: watching the chosen Contracts in flight, with no request (ADR 0021). Every Game Snapshot is read against the chosen
    /// Contracts: a completion posts the mod's own chat line, and failures, expiries and fall-backs wait for the next Debrief as data. When a
    /// run ends on a leg serving a Requirement the game hasn't marked met, the Debrief holds (the Mission stays Running, the script is
    /// over, nothing flies) until the game marks it met, or <see cref="ContractWatch.HoldSeconds"/> of game time after the craft is seen
    /// meeting it; a timeout gives the gap. The Debrief carries each served Requirement's state. No Alert and no Telemetry Check come of any
    /// of it, and a flight is never changed for a Contract.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed class ContractHold
        {
            /// <summary>Game time the craft was first seen meeting an unmet served Requirement; null until it is.</summary>
            public double? MeetingSince;
            public bool Noted;
        }

        private sealed partial class Mission
        {
            public readonly ContractWatch Contracts = new ContractWatch();
            /// <summary>The gap of each served Requirement the last run's leg left unmet, by key; cleared as a run starts.</summary>
            public readonly Dictionary<string, string> ContractGaps = new Dictionary<string, string>();
            public ContractHold Hold;
            /// <summary>#1029: Core's timing of the planned part tests, and the chosen Requirements it was last given.</summary>
            public PartTestWatch PartTests;
            public List<ChosenRequirement> PartTestsFrom;
            /// <summary>#1305: the Debrief request showed a Requirement the leg meant to meet as unmet, so a Nominal reply is held to Off-Nominal; cleared as the Debrief is rated.</summary>
            public bool RequirementMissed;
        }

        /// <summary>#1029: the Mission's part test watch, given the chosen Requirements again when a revised plan changed them.</summary>
        private static PartTestWatch PartTestsOf(Mission mission)
        {
            if (mission.PartTests == null) mission.PartTests = new PartTestWatch(mission.ChosenRequirements, CraftCheckFlow.HomeBody);
            else if (!ReferenceEquals(mission.PartTestsFrom, mission.ChosenRequirements)) mission.PartTests.Use(mission.ChosenRequirements);
            mission.PartTestsFrom = mission.ChosenRequirements;
            return mission.PartTests;
        }

        /// <summary>#1029: a part test run or its window missed: a log row, a line in the Mission's events and the run's events. Never an Alert.</summary>
        private void PartTestLine(PartTestNoted noted)
        {
            var mission = _mission;
            if (mission == null) return;
            Note(LogKind.Notice, "Part test: " + noted.Text);
            if (mission.Folder != null) Event(mission, "part_test", noted.Text);
            if (IsRunning(mission)) mission.RunEvents.Add(_ut, RunEventKind.PartTest, noted.Text);
        }

        /// <summary>A part test the mod times (#1029): its moment is Core's, so whether it was run, not where the craft is, says if it is met.</summary>
        private static bool Timed(ChosenRequirement requirement)
        {
            return requirement.Test != null && !requirement.Test.Haul && requirement.Test.Window != TestWindow.None;
        }

        /// <summary>A Game Snapshot's Contracts against the Mission's chosen ones: the completion lines go out now, the rest waits for a Debrief.</summary>
        private void WatchContracts(Mission mission, GameSnapshot snapshot)
        {
            if (mission.ChosenRequirements.Count == 0 || snapshot.Contracts == null) return;
            foreach (var line in mission.Contracts.See(snapshot.Contracts, mission.ChosenRequirements, Math.Max(_ut, snapshot.Ut))) Notice(line);
        }

        /// <summary>The chosen Requirements the leg just flown serves.</summary>
        private static List<ChosenRequirement> ServedByFlownLeg(Mission mission)
        {
            var step = mission.PendingSequence != null && mission.PendingSequence.PlanStep.HasValue ? mission.PendingSequence.PlanStep.Value : StepsBegun(mission);
            var keys = mission.ServedBy.Where(s => s.Leg == step - 1).Select(s => s.Key).ToList();
            return mission.ChosenRequirements.Where(r => keys.Contains(r.Key)).ToList();
        }

        /// <summary>
        /// The run has ended and the craft settled: true while the Debrief holds for the game to mark a served Requirement met. A hold
        /// starts only once the craft is seen meeting the Requirement; one it doesn't meet goes to the Debrief now, with the gap.
        /// </summary>
        private bool HeldForContracts(Mission mission)
        {
            var snapshot = _snapshot;
            var unmet = UnmetServed(mission, snapshot);
            if (unmet.Count == 0) return Released(mission);
            var tests = PartTestsOf(mission);
            foreach (var test in unmet.Where(r => Timed(r) && !tests.Ran(r.Key)))
                Missed(mission, test, tests.Gap(test.Key) ?? "the test's window never came: the flight didn't reach " + (test.Test.Band != null ? test.Test.Band.Through() : test.Test.Situation.ToLowerInvariant()));
            var meeting = unmet.Where(r => Timed(r) ? tests.Ran(r.Key) : ContractWatch.ConditionsHold(snapshot, r, CraftCheckFlow.HomeBody, ServingChildId(mission, r))).ToList();
            foreach (var requirement in unmet.Except(meeting).Where(r => !Timed(r))) Missed(mission, requirement, ContractWatch.Gap(snapshot, requirement, ServingChildId(mission, requirement)));
            if (meeting.Count == 0) return Released(mission);
            var now = Math.Max(_ut, snapshot.Ut);
            if (mission.Hold == null) mission.Hold = new ContractHold();
            if (!mission.Hold.MeetingSince.HasValue) mission.Hold.MeetingSince = now;
            if (!mission.Hold.Noted)
            {
                mission.Hold.Noted = true;
                Notice("Holding for the game to mark " + string.Join(" and ", meeting.Select(r => "\"" + r.Words + "\"").ToArray())
                    + " met: up to " + ContractWatch.HoldSeconds + " s of game time.");
            }
            if (now - mission.Hold.MeetingSince.Value < ContractWatch.HoldSeconds) return true;
            foreach (var requirement in meeting)
                Missed(mission, requirement, (ServingChildId(mission, requirement).Length > 0 ? "the Child Vessel met it for " : "the craft met it (" + snapshot.Situation + " at " + ContractKinds.Named(snapshot.Body) + ") for ")
                    + ContractWatch.HoldSeconds + " s of game time, and the game still hasn't marked it met");
            return Released(mission);
        }

        /// <summary>
        /// #1243: the vessel id of the Child Vessel that serves <paramref name="requirement"/>, when the plan serves it from a Child Vessel's
        /// leg (a satellite the craft releases); "" when the craft serves it. One not yet separated gets <see cref="NotSeparated"/>, which no vessel has, so its gap is "no reading of the Child Vessel".
        /// </summary>
        private static string ServingChildId(Mission mission, ChosenRequirement requirement)
        {
            var served = mission.ServedBy.FirstOrDefault(s => s.Key == requirement.Key);
            var plan = mission.Plan;
            if (served == null || plan == null || served.Leg < 0 || served.Leg >= plan.Sequences.Count) return "";
            var vessel = plan.Sequences[served.Leg].Vessel;
            if (FliesCraft(mission, vessel)) return "";
            var child = PlannedChild(mission, vessel);
            return child == null || child.VesselId.Length == 0 ? NotSeparated : child.VesselId;
        }

        /// <summary>A Child Vessel serves the Requirement but has no vessel id yet: no vessel in the snapshot has it, so it has no reading.</summary>
        private const string NotSeparated = "(not separated)";

        private static bool Released(Mission mission)
        {
            mission.Hold = null;
            return false;
        }

        private static void Missed(Mission mission, ChosenRequirement requirement, string gap)
        {
            mission.ContractGaps[requirement.Key] = gap;
            mission.Contracts.Missed(requirement, gap);
        }

        private static List<ChosenRequirement> UnmetServed(Mission mission, GameSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Contracts == null) return new List<ChosenRequirement>();
            var served = ServedByFlownLeg(mission);
            var readings = mission.Contracts.Read(snapshot, served, null);
            return served.Where(r => readings.Any(x => x.Key == r.Key && x.State == "not met")).ToList();
        }

        /// <summary>
        /// #1032: at Hand back of a planned Manual stretch, the state of each hands-on Requirement it served (the one it was tagged with),
        /// for the short Debrief. One not met is a miss, so a revised plan may drop its Contract, or keep it with a retry stretch.
        /// </summary>
        private string HandBackContractText(Mission mission)
        {
            if (!mission.ManualPlanned || mission.ChosenRequirements.Count == 0) return "";
            var served = ServedByFlownLeg(mission).Where(r => r.Manual.Length > 0).ToList();
            if (served.Count == 0) return "";
            var readings = mission.Contracts.Read(_snapshot, served, null);
            mission.RequirementMissed = ContractWatch.Unmet(readings).Count > 0;
            foreach (var reading in readings.Where(r => r.State == "not met"))
            {
                // #1244: a station never set up keeps its placement's gap.
                if (reading.Gap.Length == 0) reading.Gap = ContractWatch.NotMetAtHandBack;
                Missed(mission, served.First(r => r.Key == reading.Key), reading.Gap);
            }
            return "\n" + ContractWatch.HandBackLine(readings).TrimEnd('\n');
        }

        /// <summary>
        /// #1026: each chosen Contract's planned completion against its deadline and the spare rule, from priced figures (no planner search,
        /// no request): the Contracts it finds too late may be dropped by the next revised plan.
        /// </summary>
        private List<LateContract> RecheckDeadlines(Mission mission)
        {
            List<string> rechecked;
            var closed = new HashSet<string>(mission.Contracts.Completed.Concat(mission.Contracts.Ended));
            var late = ContractRecheck.Check(_snapshot, mission.ChosenRequirements, mission.ContractWords, CraftCheckFlow.HomeBody, closed, out rechecked);
            mission.Contracts.Rechecked(late, rechecked);
            return late;
        }

        /// <summary>#1026: at a Due Time the chosen Contracts' deadlines are rechecked; each one now too late is a line in the log.</summary>
        private void DueTimeDeadlines(Mission mission)
        {
            if (mission.ChosenRequirements.Count == 0) return;
            foreach (var late in RecheckDeadlines(mission))
                Note(LogKind.Notice, "Contract deadline at risk: \"" + late.Title + "\" is " + late.Reason + ". The next Debrief proposes dropping it.");
        }

        /// <summary>The Debrief's Contract data: what changed since the last Debrief, and the state of each Requirement the flown leg served.</summary>
        private string ContractDebriefText(Mission mission)
        {
            mission.RequirementMissed = false;
            if (mission.ChosenRequirements.Count == 0) return "";
            var text = "";
            var late = RecheckDeadlines(mission);
            if (late.Count > 0)
                text += "Contract deadlines at risk (the mod's reading of its priced figures, data): "
                    + string.Join("; ", late.Select(l => "\"" + l.Title + "\" is " + l.Reason).ToArray()) + ".\n"
                    + "For a Contract that will miss, propose dropping it, with the reason, in a revised Mission Plan for the player's Approval.\n";
            var served = ServedByFlownLeg(mission);
            if (served.Count > 0)
            {
                var readings = mission.Contracts.Read(_snapshot, served, mission.ContractGaps);
                mission.RequirementMissed = ContractWatch.Unmet(readings).Count > 0;
                text += ContractWatch.Line(readings);
            }
            text += mission.Contracts.TakeNews();
            return text.Length == 0 ? "" : "\n" + text.TrimEnd('\n');
        }
    }
}
