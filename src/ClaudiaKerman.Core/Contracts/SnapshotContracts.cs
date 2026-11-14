// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// The player's accepted Contracts as the Adapter read them on the main thread (#1037, ADR 0021): plain data, never the
    /// game's objects, which it rebuilds on every load and scene change. The mod never accepts, declines or cancels a Contract.
    /// </summary>
    public sealed class SnapshotContracts
    {
        /// <summary>The most Contracts the player may hold at once at the Mission Control level; null: no limit.</summary>
        public int? Limit;
        /// <summary>How many Contracts the game counts as active.</summary>
        public int Active;
        /// <summary>The accepted Contracts, in the game's order.</summary>
        public List<SnapshotContract> Accepted = new List<SnapshotContract>();

        internal void Write(JsonWriter json)
        {
            json.Name("contracts").BeginObject().Field("limit", Limit.HasValue ? (double?)Limit.Value : null).Field("active", (long)Active)
                .Name("accepted").BeginArray();
            foreach (var contract in Accepted)
            {
                json.BeginObject().Field("guid", contract.Guid).Field("kind", contract.Kind).Field("state", contract.State);
                if (contract.DeadlineUt.HasValue) json.Field("deadline_ut", contract.DeadlineUt.Value);
                json.Name("rewards").BeginObject().Field("funds", contract.Funds).Field("science", contract.Science)
                    .Field("reputation", contract.Reputation).EndObject();
                WriteSettings(json, contract.Settings);
                WriteRequirements(json, contract.Requirements);
                json.EndObject();
            }
            json.EndArray().EndObject();
        }

        private static void WriteSettings(JsonWriter json, List<ContractSetting> settings)
        {
            json.Name("settings").BeginObject();
            foreach (var setting in settings) json.Field(setting.Name, setting.Value);
            json.EndObject();
        }

        private static void WriteRequirements(JsonWriter json, List<ContractRequirement> requirements)
        {
            json.Name("requirements").BeginArray();
            foreach (var requirement in requirements)
            {
                json.BeginObject().Field("key", requirement.Key).Field("kind", requirement.Kind).Field("state", requirement.State)
                    .Field("optional", requirement.Optional);
                WriteSettings(json, requirement.Settings);
                if (requirement.Requirements.Count > 0) WriteRequirements(json, requirement.Requirements);
                json.EndObject();
            }
            json.EndArray();
        }

        /// <summary>The snapshot's <c>contracts</c> entry; null when it has none.</summary>
        internal static SnapshotContracts Read(JsonNode node)
        {
            if (node == null || node.Kind != JsonKind.Object) return null;
            var limit = JsonNode.Long(node["limit"]);
            var read = new SnapshotContracts { Limit = limit.HasValue ? (int?)limit.Value : null, Active = (int)(JsonNode.Long(node["active"]) ?? 0) };
            var accepted = node["accepted"];
            if (accepted == null) return read;
            foreach (var item in accepted.Items)
            {
                var rewards = item["rewards"];
                read.Accepted.Add(new SnapshotContract
                {
                    Guid = JsonNode.Str(item["guid"]) ?? "",
                    Kind = JsonNode.Str(item["kind"]) ?? "",
                    State = JsonNode.Str(item["state"]) ?? "",
                    DeadlineUt = JsonNode.Double(item["deadline_ut"]),
                    Funds = rewards != null ? JsonNode.Double(rewards["funds"]) ?? 0 : 0,
                    Science = rewards != null ? JsonNode.Double(rewards["science"]) ?? 0 : 0,
                    Reputation = rewards != null ? JsonNode.Double(rewards["reputation"]) ?? 0 : 0,
                    Settings = ReadSettings(item["settings"]),
                    Requirements = ReadRequirements(item["requirements"]),
                });
            }
            return read;
        }

        private static List<ContractSetting> ReadSettings(JsonNode node)
        {
            if (node == null || node.Kind != JsonKind.Object) return new List<ContractSetting>();
            return node.Members.Select(m => new ContractSetting(m.Key, JsonNode.Str(m.Value) ?? "")).ToList();
        }

        private static List<ContractRequirement> ReadRequirements(JsonNode node)
        {
            if (node == null) return new List<ContractRequirement>();
            return node.Items.Select(item => new ContractRequirement
            {
                Key = JsonNode.Str(item["key"]) ?? "",
                Kind = JsonNode.Str(item["kind"]) ?? "",
                State = JsonNode.Str(item["state"]) ?? "",
                Optional = JsonNode.Bool(item["optional"]) ?? false,
                Settings = ReadSettings(item["settings"]),
                Requirements = ReadRequirements(item["requirements"]),
            }).ToList();
        }
    }

    /// <summary>One accepted Contract.</summary>
    public sealed class SnapshotContract
    {
        /// <summary>The game's Contract GUID, which survives a save and load; with a tree position it keys each Contract Requirement.</summary>
        public string Guid = "";
        /// <summary>The game's name for the Contract's type, e.g. PartTest or SurveyContract.</summary>
        public string Kind = "";
        /// <summary>The game's state name: Active for an accepted Contract.</summary>
        public string State = "";
        /// <summary>The game time (UT, seconds) the Contract fails at; null: no deadline.</summary>
        public double? DeadlineUt;
        /// <summary>What completing it pays, after the player's Strategies: funds, science and reputation.</summary>
        public double Funds;
        public double Science;
        public double Reputation;
        /// <summary>The Contract's own saved settings, as the save writes them (the Requirements' are on each).</summary>
        public List<ContractSetting> Settings = new List<ContractSetting>();
        /// <summary>The Contract Requirements at the top of its tree, in the game's order.</summary>
        public List<ContractRequirement> Requirements = new List<ContractRequirement>();
    }

    /// <summary>One Contract Requirement, with the ones under it. The tree's shape varies within one Contract type.</summary>
    public sealed class ContractRequirement
    {
        /// <summary>The Contract's GUID and the Requirement's position in the tree (<see cref="KeyOf"/>).</summary>
        public string Key = "";
        /// <summary>The game's name for the Requirement's kind, e.g. ReachAltitudeEnvelope.</summary>
        public string Kind = "";
        /// <summary>The game's state name: Incomplete, Complete (met) or Failed.</summary>
        public string State = "";
        /// <summary>The Contract can complete without it.</summary>
        public bool Optional;
        /// <summary>Its saved settings, as the save writes them.</summary>
        public List<ContractSetting> Settings = new List<ContractSetting>();
        public List<ContractRequirement> Requirements = new List<ContractRequirement>();

        /// <summary>
        /// The key of the Requirement at <paramref name="path"/> (its index at each level, from the top) in Contract <paramref name="guid"/>,
        /// e.g. "…/0.2": the third under the first. The game has no id for a Requirement and rebuilds them on every load, so the key is
        /// where it sits, which a load doesn't change.
        /// </summary>
        public static string KeyOf(string guid, IEnumerable<int> path)
        {
            return guid + "/" + string.Join(".", path.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>One saved setting: the save file's key and its value as written.</summary>
    public sealed class ContractSetting
    {
        public readonly string Name;
        public readonly string Value;

        public ContractSetting(string name, string value)
        {
            Name = name ?? "";
            Value = value ?? "";
        }
    }
}
