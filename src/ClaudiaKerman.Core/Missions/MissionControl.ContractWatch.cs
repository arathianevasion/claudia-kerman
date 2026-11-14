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
            var meeting = unmet.Where(r => ContractWatch.ConditionsHold(snapshot, r, CraftCheckFlow.HomeBody)).ToList();
            foreach (var requirement in unmet.Except(meeting)) Missed(mission, requirement, ContractWatch.Gap(snapshot, requirement));
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
                Missed(mission, requirement, "the craft met it (" + snapshot.Situation + " at " + ContractKinds.Named(snapshot.Body) + ") for "
                    + ContractWatch.HoldSeconds + " s of game time, and the game still hasn't marked it met");
            return Released(mission);
        }

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
            var readings = mission.Contracts.Read(snapshot.Contracts, served, null);
            return served.Where(r => readings.Any(x => x.Key == r.Key && x.State == "not met")).ToList();
        }

        /// <summary>The Debrief's Contract data: what changed since the last Debrief, and the state of each Requirement the flown leg served.</summary>
        private string ContractDebriefText(Mission mission)
        {
            if (mission.ChosenRequirements.Count == 0) return "";
            var text = "";
            var served = ServedByFlownLeg(mission);
            if (served.Count > 0)
                text += ContractWatch.Line(mission.Contracts.Read(_snapshot == null ? null : _snapshot.Contracts, served, mission.ContractGaps));
            text += mission.Contracts.TakeNews();
            return text.Length == 0 ? "" : "\n" + text.TrimEnd('\n');
        }
    }
}
