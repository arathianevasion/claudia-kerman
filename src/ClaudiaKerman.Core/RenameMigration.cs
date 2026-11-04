// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;

namespace ClaudiaKerman.Core
{
    /// <summary>
    /// The rename from Claudia Kermin to Claudia Kerman (ADR 0015, #665): the old names, and the moves that carry an
    /// install over. The old spelling lives here and in the roster's search for her, nowhere else.
    /// </summary>
    public static class RenameMigration
    {
        /// <summary>The old folder name: the config folder, each save's Mission folder and the GameData folder.</summary>
        public const string OldFolderName = "ClaudiaKermin";

        /// <summary>Her old Kerbal name, as saves made before the rename hold it.</summary>
        public const string OldName = "Claudia Kermin";

        /// <summary>The Claudia Flag's old game URL, as parts and the agency flag in old saves hold it.</summary>
        public const string OldFlagUrl = "ClaudiaKermin/Flags/ClaudiaFlag";

        /// <summary>
        /// The folder to use. When only <paramref name="oldFolder"/> exists it is moved to <paramref name="newFolder"/> (a
        /// rename, so its files and their permissions carry over). When both exist, or neither, the new one is used and the
        /// old one is left alone. A move that fails leaves the old folder where it is and returns it, so it is still read,
        /// with one line to <paramref name="log"/>; the next load tries again.
        /// </summary>
        public static string MoveFolder(string oldFolder, string newFolder, Action<string> log, Action<string, string> move = null)
        {
            if (Directory.Exists(newFolder) || !Directory.Exists(oldFolder)) return newFolder;
            try
            {
                (move ?? Directory.Move)(oldFolder, newFolder);
                if (log != null) log("moved " + oldFolder + " to " + newFolder + " (the mod is now Claudia Kerman)");
                return newFolder;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                if (log != null) log("could not move " + oldFolder + " to " + newFolder + ", so the old folder is read: " + e.Message);
                return oldFolder;
            }
        }

        /// <summary>The save's Mission folder after carrying an old <c>ClaudiaKermin</c> one over (<see cref="MoveFolder"/>).</summary>
        public static string MissionFolder(string saveDirectory, Action<string> log, Action<string, string> move = null)
        {
            return MoveFolder(Path.Combine(saveDirectory, OldFolderName), Path.Combine(saveDirectory, Missions.MissionFolder.FolderName), log, move);
        }

        /// <summary>A flag URL from an old save mapped to the new one; any other flag as it is.</summary>
        public static string FlagUrl(string url)
        {
            return url == OldFlagUrl ? Missions.ClaudiaFlagKeeper.Url : url;
        }

        /// <summary>
        /// The message when the old GameData folder is installed beside the new one, or "" when it isn't. Both would load
        /// (two copies of the mod, fighting over her name), so the new one stands down until the old folder is deleted.
        /// </summary>
        public static string DuplicateInstall(string gameDataDirectory)
        {
            if (string.IsNullOrEmpty(gameDataDirectory) || !Directory.Exists(Path.Combine(gameDataDirectory, OldFolderName))) return "";
            return "Claudia Kerman is installed twice: GameData/" + OldFolderName + " is the old copy from before the rename. "
                + "Quit KSP, delete GameData/" + OldFolderName + " (keep GameData/ClaudiaKerman), then start KSP again. "
                + "Until then Claudia Kerman doesn't run.";
        }
    }
}
