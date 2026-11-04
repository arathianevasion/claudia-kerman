// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Puts each Mission's save-state in the save file, and beside them the save's own Claudia Flag state (#642). It only turns the core's <see cref="MissionSaveState"/>
    /// into ConfigNode values on save; the driver reads them back when a game is loaded and hands them to the
    /// core (<see cref="MissionsRestored"/>), which decides what the loop does. Loading here would also run on
    /// every scene change, which is not a load.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR, GameScenes.FLIGHT)]
    public class MissionScenario : ScenarioModule
    {
        public const string MissionNode = "MISSION";

        private ConfigNode _loaded;

        // Written back only when no driver runs (the mod stood down, DuplicateInstall), so saving then doesn't drop the Missions.
        public override void OnLoad(ConfigNode node)
        {
            _loaded = node.CreateCopy();
        }

        public override void OnSave(ConfigNode node)
        {
            var driver = MissionDriver.Current;
            if (driver == null)
            {
                if (_loaded == null) return;
                var kept = ReadSavedAgencyFlag(_loaded);
                if (kept != null) node.AddValue(ClaudiaFlagKeeper.SavedAgencyFlagKey, kept);
                foreach (var mission in _loaded.GetNodes(MissionNode)) node.AddNode(mission);
                return;
            }
            // Save-level, not a Mission's: the player's agency flag while the Claudia Flag flies in its place (#642).
            if (driver.SavedAgencyFlag != null) node.AddValue(ClaudiaFlagKeeper.SavedAgencyFlagKey, driver.SavedAgencyFlag);
            foreach (var state in driver.ExportStates())
            {
                var mission = node.AddNode(MissionNode);
                foreach (var pair in state.ToValues()) mission.AddValue(pair.Key, pair.Value);
            }
        }

        /// <summary>The agency flag a scenario module's node kept while the Claudia Flag flew; null when none.</summary>
        public static string ReadSavedAgencyFlag(ConfigNode scenario)
        {
            if (scenario == null) return null;
            var flag = scenario.GetValue(ClaudiaFlagKeeper.SavedAgencyFlagKey);
            return string.IsNullOrEmpty(flag) ? null : flag;
        }

        /// <summary>The Mission snapshots in a scenario module's node, as saved.</summary>
        public static List<MissionSaveState> Read(ConfigNode scenario)
        {
            var states = new List<MissionSaveState>();
            if (scenario == null) return states;
            foreach (var mission in scenario.GetNodes(MissionNode))
            {
                var values = new Dictionary<string, string>();
                foreach (ConfigNode.Value value in mission.values) values[value.name] = value.value;
                var state = MissionSaveState.FromValues(values);
                if (state != null) states.Add(state);
            }
            return states;
        }
    }
}
