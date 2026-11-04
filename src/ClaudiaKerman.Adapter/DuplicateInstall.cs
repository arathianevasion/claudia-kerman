// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKerman.Core;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// The old GameData folder from before the rename (ADR 0015) installed beside this one: both copies would load and fight
    /// over her name, so this one stands down (the Loader, the roster) and says so once, in the log and in a dialog at the
    /// main menu, until the player deletes the old folder.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class DuplicateInstall : MonoBehaviour
    {
        private static string _message;

        /// <summary>The message to show, or "" when only this copy is installed. Checked once per session.</summary>
        public static string Message
        {
            get
            {
                if (_message == null)
                {
                    _message = RenameMigration.DuplicateInstall(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"));
                    if (_message.Length > 0) Debug.LogError(ModInfo.LogTag + " " + _message);
                }
                return _message;
            }
        }

        public static bool Found
        {
            get { return Message.Length > 0; }
        }

        private void Start()
        {
            if (Found)
                PopupDialog.SpawnPopupDialog(new MultiOptionDialog("ClaudiaKermanDuplicate", Message, ModInfo.Name, HighLogic.UISkin,
                    new DialogGUIButton("OK", () => { }, true)), true, HighLogic.UISkin);
            Destroy(this);
        }
    }
}
