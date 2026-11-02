// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Config;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>How a Mission Setting is chosen.</summary>
    public enum MissionSettingKind
    {
        /// <summary>On or off; saved as <c>true</c> or <c>false</c>.</summary>
        YesNo,
        /// <summary>One of <see cref="MissionSettingDescriptor.Choices"/>; saved as the choice's name.</summary>
        Choice,
    }

    /// <summary>
    /// One Mission Setting, described once: its saved key, label, kind, default, how it reads in Claudia's prompt and on the
    /// Mission board, and whether a change needs a revised Mission Plan. <see cref="MissionSettingsValues"/> drives persistence,
    /// the revision check, the prompt lines and the board summary from <see cref="MissionSettingsValues.All"/>.
    /// A new setting is a new entry there, plus a typed accessor if code reads it, plus the gate that reads it.
    /// </summary>
    public sealed class MissionSettingDescriptor
    {
        /// <summary>The name in a Mission's saved name/value pairs.</summary>
        public readonly string Key;

        /// <summary>The setting's name as the Settings window shows it.</summary>
        public readonly string Label;

        public readonly MissionSettingKind Kind;

        /// <summary>For a <see cref="MissionSettingKind.Choice"/>: the saved names of the choices; empty for a yes/no.</summary>
        public readonly string[] Choices;

        /// <summary>The value a new Mission starts with, saved-text form, from the config where that applies.</summary>
        public readonly Func<PlayerConfig, string> Default;

        /// <summary>The setting's words in Claudia's prompt, as she must apply them.</summary>
        public readonly Func<MissionSettingsValues, string> Prompt;

        /// <summary>The setting in the player's words, for the Mission board.</summary>
        public readonly Func<MissionSettingsValues, string> Summary;

        /// <summary>The prompt line starts on its own line rather than following the one before it.</summary>
        public readonly bool OwnLine;

        /// <summary>A change asks Claudia for a revised Mission Plan (and so needs Approval again).</summary>
        public readonly bool NeedsRevision;

        public MissionSettingDescriptor(string key, string label, MissionSettingKind kind, string[] choices, Func<PlayerConfig, string> defaultValue,
            Func<MissionSettingsValues, string> prompt, Func<MissionSettingsValues, string> summary, bool ownLine = false, bool needsRevision = true)
        {
            Key = key;
            Label = label;
            Kind = kind;
            Choices = choices ?? new string[0];
            Default = defaultValue;
            Prompt = prompt;
            Summary = summary;
            OwnLine = ownLine;
            NeedsRevision = needsRevision;
        }

        /// <summary>A yes/no setting.</summary>
        public static MissionSettingDescriptor YesNo(string key, string label, Func<PlayerConfig, bool> defaultValue,
            Func<MissionSettingsValues, string> prompt, Func<MissionSettingsValues, string> summary, bool ownLine = false, bool needsRevision = true)
        {
            return new MissionSettingDescriptor(key, label, MissionSettingKind.YesNo, null, c => Text(defaultValue(c)), prompt, summary, ownLine, needsRevision);
        }

        /// <summary>A choice among the names of an enum.</summary>
        public static MissionSettingDescriptor Choice<T>(string key, string label, Func<PlayerConfig, T> defaultValue,
            Func<MissionSettingsValues, string> prompt, Func<MissionSettingsValues, string> summary, bool ownLine = false, bool needsRevision = true)
        {
            return new MissionSettingDescriptor(key, label, MissionSettingKind.Choice, Enum.GetNames(typeof(T)), c => defaultValue(c).ToString(), prompt, summary, ownLine, needsRevision);
        }

        /// <summary>The saved text of a yes/no.</summary>
        public static string Text(bool on)
        {
            return on ? "true" : "false";
        }

        /// <summary>Whether <paramref name="text"/> is a value this setting can take.</summary>
        public bool IsValid(string text)
        {
            if (Kind == MissionSettingKind.YesNo) return text == "true" || text == "false";
            return Array.IndexOf(Choices, text) >= 0;
        }
    }
}
