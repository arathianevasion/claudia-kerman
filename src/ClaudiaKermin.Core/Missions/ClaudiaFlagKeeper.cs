// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Claudia Flag (#642): the flag the mod ships, which a Mission's craft and Child Vessels fly, and which is the agency
    /// flag while a Mission is under way (in play and not Paused; not the glossary's Running, which is a script executing). This decides, from the Mission view; the adapter sets <c>Part.flagURL</c> and the
    /// game's flag, and reports back what it flew. Save-level state (the player's own agency flag, kept while Claudia's
    /// flies) is the save's, not a Mission's: the adapter keeps it in the scenario node beside the Missions.
    /// </summary>
    public sealed class ClaudiaFlagKeeper
    {
        /// <summary>The flag's game URL: <c>GameData/ClaudiaKermin/Flags/ClaudiaFlag.png</c>.</summary>
        public const string Url = "ClaudiaKermin/Flags/ClaudiaFlag";

        /// <summary>The key of the kept agency flag in the save's scenario node.</summary>
        public const string SavedAgencyFlagKey = "saved_agency_flag";

        private readonly HashSet<string> _flagged = new HashSet<string>();
        private string _flaggedFor = "";
        private bool _wasUnderWay;

        /// <summary>The player's agency flag, kept while Claudia's flies in its place; null when nothing is kept.</summary>
        public string SavedAgencyFlag { get; private set; }

        /// <summary>A Mission is under way: one is in play and not Paused (a finished one isn't in play).</summary>
        public static bool UnderWay(MissionView view)
        {
            return view.CanEndMission && view.Stage != MissionStage.Paused;
        }

        /// <summary>
        /// A game was loaded: <paramref name="savedAgencyFlag"/> is what its save kept (null for nothing). Every Mission vessel
        /// flies the flag again, once, and a Mission found under way counts as resumed.
        /// </summary>
        public void Load(string savedAgencyFlag)
        {
            SavedAgencyFlag = string.IsNullOrEmpty(savedAgencyFlag) ? null : savedAgencyFlag;
            _flagged.Clear();
            _wasUnderWay = false;
        }

        /// <summary>
        /// The agency flag to set now, or null to leave it. When a Mission gets under way (a start, or a resume), the player's
        /// flag is kept, once, and Claudia's flies; a second Mission never keeps Claudia's over it. When none is under way any
        /// more the kept flag is restored, unless the player has picked another since: their own choice wins. Nothing is
        /// enforced in between.
        /// </summary>
        public string AgencyFlagFor(MissionView view, string current)
        {
            var running = UnderWay(view);
            var started = running && !_wasUnderWay;
            _wasUnderWay = running;
            if (started)
            {
                if (current == Url) return null;
                if (SavedAgencyFlag == null) SavedAgencyFlag = current;
                return Url;
            }
            if (running || SavedAgencyFlag == null) return null;
            var restore = current == Url ? SavedAgencyFlag : null;
            SavedAgencyFlag = null;
            return restore;
        }

        /// <summary>
        /// The Mission vessels still to fly the flag: the craft and every Child Vessel that has separated, while the Mission
        /// is in play. Each is asked for until <see cref="Flagged"/>; a new Mission asks for all of them again.
        /// </summary>
        public IList<string> VesselsToFlag(MissionView view)
        {
            var ids = new List<string>();
            if (!view.CanEndMission) return ids;
            if (view.MissionId != _flaggedFor)
            {
                _flaggedFor = view.MissionId;
                _flagged.Clear();
            }
            foreach (var id in MissionVessels(view))
                if (!_flagged.Contains(id) && !ids.Contains(id)) ids.Add(id);
            return ids;
        }

        /// <summary>The adapter set the flag on every part of this vessel.</summary>
        public void Flagged(string vesselId)
        {
            _flagged.Add(vesselId);
        }

        /// <summary>A docking: when a Mission vessel is one side, the merged vessel flies the flag again, joined parts and all.</summary>
        public void Docked(MissionView view, string first, string second)
        {
            if (!IsMissionVessel(view, first) && !IsMissionVessel(view, second)) return;
            _flagged.Remove(first);
            _flagged.Remove(second);
        }

        /// <summary>The vessel is the craft or a Child Vessel of the Mission in play (a Kerbal leaving it plants Claudia's flag).</summary>
        public bool IsMissionVessel(MissionView view, string vesselId)
        {
            if (!view.CanEndMission || string.IsNullOrEmpty(vesselId)) return false;
            foreach (var id in MissionVessels(view))
                if (id == vesselId) return true;
            return false;
        }

        private static IEnumerable<string> MissionVessels(MissionView view)
        {
            if (!string.IsNullOrEmpty(view.CraftVesselId)) yield return view.CraftVesselId;
            foreach (var child in view.ChildVessels)
                if (!string.IsNullOrEmpty(child.VesselId)) yield return child.VesselId;
        }
    }
}
