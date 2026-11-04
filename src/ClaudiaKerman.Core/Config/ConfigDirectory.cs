// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;

namespace ClaudiaKerman.Core.Config
{
    public enum HostOs { MacOS, Windows, Linux }

    /// <summary>Resolves the player's config folder per OS. Always under the user profile, never GameData.</summary>
    public static class ConfigDirectory
    {
        public const string FolderName = "ClaudiaKerman";

        public static string Resolve(HostOs os, string home, Func<string, string> env)
        {
            return Resolve(os, home, env, FolderName);
        }

        private static string Resolve(HostOs os, string home, Func<string, string> env, string folderName)
        {
            switch (os)
            {
                case HostOs.MacOS:
                    return Path.Combine(home, "Library", "Application Support", folderName);
                case HostOs.Windows:
                    var appData = env("APPDATA");
                    return Path.Combine(string.IsNullOrEmpty(appData) ? Path.Combine(home, "AppData", "Roaming") : appData, folderName);
                default:
                    var xdg = env("XDG_CONFIG_HOME");
                    return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, folderName);
            }
        }

        public static HostOs CurrentOs()
        {
            if (Path.DirectorySeparatorChar == '\\') return HostOs.Windows;
            return Directory.Exists("/Library/Application Support") ? HostOs.MacOS : HostOs.Linux;
        }

        /// <summary>
        /// The folder to use, after carrying an old <c>ClaudiaKermin</c> folder over to the new name (ADR 0015): the API key
        /// and <c>config.cfg</c> move with it. A failed move leaves the old folder in use (<see cref="RenameMigration.MoveFolder"/>).
        /// </summary>
        public static string Migrate(HostOs os, string home, Func<string, string> env, Action<string> log, Action<string, string> move = null)
        {
            return RenameMigration.MoveFolder(Resolve(os, home, env, RenameMigration.OldFolderName), Resolve(os, home, env), log, move);
        }

        private static readonly object CurrentLock = new object();
        private static string _current;
        private static string _unlogged;

        /// <summary>
        /// This machine's folder, migrated on the first call and the same for the rest of the session. The migration's line
        /// goes to the first <paramref name="log"/> given, once.
        /// </summary>
        public static string ResolveCurrent(Action<string> log = null)
        {
            lock (CurrentLock)
            {
                if (_current == null)
                    _current = Migrate(CurrentOs(), Environment.GetFolderPath(Environment.SpecialFolder.Personal), Environment.GetEnvironmentVariable,
                        line => _unlogged = line);
                if (_unlogged != null && log != null)
                {
                    log(_unlogged);
                    _unlogged = null;
                }
                return _current;
            }
        }
    }
}
