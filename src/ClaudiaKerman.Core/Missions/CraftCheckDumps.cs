// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #776: a Craft Check's raw request and SSE reply, kept so a verdict can be checked afterwards. Each check gets
    /// <c>&lt;save&gt;/ClaudiaKerman/craft-checks/&lt;timestamp&gt;/</c> (the last <see cref="Keep"/> stay); Start copies the latest into the
    /// Mission's folder as <c>craft-check/</c>. Written with <see cref="FileStreamDump"/>, whether or not <c>debug_tools</c> is on.
    /// </summary>
    internal static class CraftCheckDumps
    {
        public const int Keep = 10;
        public const string FolderName = "craft-checks";
        public const string MissionFolderName = "craft-check";

        /// <summary>A new folder for a check, older ones beyond the last <see cref="Keep"/> removed; null when the folder can't be made.</summary>
        public static string Begin(string saveDirectory, DateTime now)
        {
            if (string.IsNullOrEmpty(saveDirectory)) return null;
            try
            {
                var root = Path.Combine(MissionFolder.Root(saveDirectory), FolderName);
                // Named by when it began; two in the same millisecond take the next one.
                while (Directory.Exists(Path.Combine(root, now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)))) now = now.AddMilliseconds(1);
                var path = Path.Combine(root, now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(path);
                Prune(root);
                return path;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Best effort: an old folder that is locked stays, and this check is still saved.</summary>
        private static void Prune(string root)
        {
            foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(Keep))
            {
                try
                {
                    Directory.Delete(old, true);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        public static IRawStreamDump Writer(string folder)
        {
            return folder == null ? null : new FileStreamDump(folder);
        }

        /// <summary>Copies the check's files into the Mission's folder as <c>craft-check/</c>.</summary>
        public static void CopyTo(string checkFolder, string missionFolder)
        {
            if (checkFolder == null || !Directory.Exists(checkFolder)) return;
            var target = Path.Combine(missionFolder, MissionFolderName);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(checkFolder))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
    }
}
