// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using Contracts;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Reads the player's accepted Contracts for the Game Snapshot (#1037, ADR 0021). Settings come from each Contract's and each
    /// Contract Requirement's own saved form (<c>Save(ConfigNode)</c>, the keys the save file writes), never from private fields;
    /// state, optional flags, the deadline, the rewards after Strategies, the limit and the active count from the game's public
    /// members. The game rebuilds these objects on every load and scene change, so nothing here is held: a Requirement is keyed by
    /// its Contract's GUID and its place in the tree. It only reads: it never accepts, declines or cancels a Contract. Main thread only.
    /// </summary>
    internal static class ContractReader
    {
        /// <summary>The figure at or above which the game's limit means none (it gives <c>int.MaxValue</c> at the top Mission Control level).</summary>
        private const int NoLimit = 1000000;

        /// <summary>How many of the newest finished Contracts the snapshot carries: enough for every Contract of one Mission.</summary>
        private const int Finished = 30;

        /// <summary>The accepted Contracts; null outside Career or before the game's Contract system has loaded.</summary>
        public static SnapshotContracts Read()
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.Mode != Game.Modes.CAREER) return null;
            var system = ContractSystem.Instance;
            if (system == null || !ContractSystem.loaded) return null;
            var read = new SnapshotContracts { Active = system.GetActiveContractCount() };
            if (GameVariables.Instance != null)
            {
                var limit = GameVariables.Instance.GetActiveContractsLimit(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl));
                read.Limit = limit >= NoLimit ? (int?)null : limit;
            }
            foreach (var contract in system.GetCurrentActiveContracts<Contract>())
            {
                if (contract == null) continue;
                var one = Of(contract);
                ReadRewards(one, contract);
                read.Accepted.Add(one);
            }
            // #1025: the newest finished ones, GUID and state only (no saved form), so Core tells a completion from a failure.
            var finished = system.ContractsFinished;
            if (finished != null)
                foreach (var contract in finished.Skip(System.Math.Max(0, finished.Count - Finished)))
                    if (contract != null) read.Finished.Add(new FinishedContract { Guid = contract.ContractGuid.ToString(), State = contract.ContractState.ToString() });
            return read;
        }

        /// <summary>
        /// One Contract, offered or accepted, as plain data: its kind, state, deadline, saved settings and Requirement tree. The rewards
        /// are read separately (<see cref="ReadRewards"/>), as they run the game's Strategy query, which Mission Control's mark doesn't need.
        /// </summary>
        internal static SnapshotContract Of(Contract contract)
        {
            var saved = new ConfigNode("CONTRACT");
            contract.Save(saved);
            var guid = contract.ContractGuid.ToString();
            var read = new SnapshotContract
            {
                Guid = guid,
                Kind = contract.GetType().Name,
                State = contract.ContractState.ToString(),
                // #1260: the title the game's Contracts window shows, for the card's rows.
                Title = contract.Title ?? "",
                // 0: no deadline. Accepted, it is the absolute game time (#1013).
                DeadlineUt = contract.DateDeadline > 0 ? contract.DateDeadline : (double?)null,
                Settings = SettingsOf(saved),
            };
            for (var i = 0; i < contract.ParameterCount; i++)
            {
                var requirement = contract.GetParameter(i);
                if (requirement != null) read.Requirements.Add(RequirementOf(requirement, guid, new List<int> { i }));
            }
            return read;
        }

        /// <summary>The completion rewards as the player will get them: the game's own query runs the active Strategies over the base figures.</summary>
        private static void ReadRewards(SnapshotContract read, Contract contract)
        {
            var query = CurrencyModifierQuery.RunQuery(TransactionReasons.ContractReward, (float)contract.FundsCompletion, contract.ScienceCompletion, contract.ReputationCompletion);
            read.Funds = contract.FundsCompletion + query.GetEffectDelta(Currency.Funds);
            read.Science = contract.ScienceCompletion + query.GetEffectDelta(Currency.Science);
            read.Reputation = contract.ReputationCompletion + query.GetEffectDelta(Currency.Reputation);
        }

        private static ContractRequirement RequirementOf(ContractParameter requirement, string guid, List<int> path)
        {
            var saved = new ConfigNode("PARAM");
            requirement.Save(saved);
            var read = new ContractRequirement
            {
                Key = ContractRequirement.KeyOf(guid, path),
                Kind = requirement.GetType().Name,
                State = requirement.State.ToString(),
                Optional = requirement.Optional,
                Settings = SettingsOf(saved),
            };
            for (var i = 0; i < requirement.ParameterCount; i++)
            {
                var child = requirement.GetParameter(i);
                if (child != null) read.Requirements.Add(RequirementOf(child, guid, new List<int>(path) { i }));
            }
            return read;
        }

        /// <summary>The saved node's own values, in order; the Requirements under it are read from the tree, not from its child nodes.</summary>
        private static List<ContractSetting> SettingsOf(ConfigNode saved)
        {
            return saved.values.Cast<ConfigNode.Value>().Select(v => new ContractSetting(v.name, v.value)).ToList();
        }
    }
}
