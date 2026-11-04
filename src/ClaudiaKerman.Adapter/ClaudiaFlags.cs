// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Sets the Claudia Flag (#642) where <see cref="ClaudiaFlagKeeper"/> says: on every part of a Mission vessel, decals
    /// included, and as the game's agency flag. It only touches the game; the keeper decides.
    /// </summary>
    internal static class ClaudiaFlags
    {
        /// <summary>
        /// Every part of the vessel flies the Claudia Flag: a loaded vessel's parts and their decals, or an unloaded one's
        /// part snapshots. False when the vessel isn't in the game (yet), so the keeper asks again.
        /// </summary>
        public static bool FlyOn(string vesselId)
        {
            if (FlightGlobals.Vessels == null) return false;
            var vessel = FlightGlobals.Vessels.Find(v => v != null && v.id.ToString("N") == vesselId);
            if (vessel == null) return false;
            if (vessel.loaded)
            {
                foreach (var part in vessel.parts) Fly(part);
                return true;
            }
            if (vessel.protoVessel == null) return false;
            foreach (var snapshot in vessel.protoVessel.protoPartSnapshots) snapshot.flagURL = ClaudiaFlagKeeper.Url;
            return true;
        }

        /// <summary>The part flies the Claudia Flag, and its flag decals redraw with it.</summary>
        public static void Fly(Part part)
        {
            if (part == null) return;
            part.flagURL = ClaudiaFlagKeeper.Url;
            foreach (var decal in part.FindModulesImplementing<FlagDecal>()) decal.UpdateFlagTexture();
            foreach (var decal in part.FindModulesImplementing<FlagDecalBackground>()) decal.UpdateFlagTexture();
        }

        /// <summary>The game's agency flag now; "" with no game loaded.</summary>
        public static string Agency()
        {
            return HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flagURL ?? "" : "";
        }

        public static void SetAgency(string flag)
        {
            if (HighLogic.CurrentGame != null) HighLogic.CurrentGame.flagURL = flag;
        }
    }
}
