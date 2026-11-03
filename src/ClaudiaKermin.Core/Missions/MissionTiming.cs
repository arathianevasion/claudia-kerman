// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Config;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>How Claudia trades time against delta-v when she picks a window.</summary>
    public enum TimeVersusDeltaV
    {
        /// <summary>Never waits for a better window.</summary>
        Fastest,
        /// <summary>Takes Soonest unless Optimal saves at least <see cref="Tools.TransferWindows.BalancedSaving"/> of its delta-v.</summary>
        Balanced,
        /// <summary>Waits for the cheapest window.</summary>
        MostEfficient,
    }

    /// <summary>
    /// The Mission Settings: the player's choices for how Claudia meets the Mission Goal. Chosen at start,
    /// persisted with the Mission (as the name/value pairs of <see cref="WriteTo"/>), editable later.
    /// </summary>
    public sealed class MissionSettingsValues
    {
        public const string DaylightKey = "daylight_landing";
        public const string TimeKey = "time_vs_dv";
        public const string PhysicsWarpKey = "physics_warp";

        /// <summary>
        /// Every Mission Setting, described once, in the order Claudia reads them. Persistence, the revision check, the prompt
        /// lines and the board summary all run from this list.
        /// </summary>
        public static readonly IList<MissionSettingDescriptor> All = new[]
        {
            MissionSettingDescriptor.YesNo(DaylightKey, "Daylight landing", c => c.DefaultDaylightLanding,
                v => v.DaylightLanding
                    ? "Daylight landing: on. Set the land block's daylight option to true and prefer a daylight landing."
                    : "Daylight landing: off. Do not wait for daylight.",
                v => "Daylight landing: " + (v.DaylightLanding ? "on" : "off")),
            MissionSettingDescriptor.Choice(TimeKey, "Time versus delta-v", c => c.DefaultTimeVersusDeltaV,
                // Each line states TransferWindows.Pick's rule (#669), the Balanced saving formatted from its constant.
                v => "Time versus delta-v, " + (v.TimeVersusDeltaV == TimeVersusDeltaV.Fastest
                    ? "Fastest: take soonest; never wait for a cheaper window."
                    : v.TimeVersusDeltaV == TimeVersusDeltaV.MostEfficient
                        ? "Most efficient: take optimal, the cheapest window, however long the wait."
                        : "Balanced: take soonest unless optimal costs at least " + BalancedSavingText + " less; take the plan_transfer pick."),
                v => "time versus delta-v: " + Label(v.TimeVersusDeltaV)),
            // The player's own choice starts on; the config alone gates it (PhysicsWarpOn), so turning the config back on turns it back on.
            MissionSettingDescriptor.YesNo(PhysicsWarpKey, "Physics warp on Coasts", c => true,
                v => v.PhysicsWarpOn
                    ? "Physics warp on Coasts: on. Blocks warp through Coasts with physics warp where rails can't; use the warp helpers for your own Coasts."
                    : "Physics warp on Coasts: off. The mod refuses physics warp. "
                        + (v.ConfigAllowsPhysicsWarp ? "" : "Only the player's config can turn it back on. ")
                        + "Where rails can't warp, Coasts run at 1×, so plan for the extra real time.",
                v => "physics warp on Coasts: " + (v.PhysicsWarpOn ? "on" : "off"), ownLine: true),
        };

        // The saved text of each setting by key; a typed accessor below reads and writes each one that code needs.
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public MissionSettingsValues()
        {
            var config = new PlayerConfig();
            foreach (var setting in All) _values[setting.Key] = setting.Default(config);
        }

        /// <summary>Prefer to land in daylight; drives the <c>land</c> block's <c>daylight</c> option.</summary>
        public bool DaylightLanding
        {
            get { return _values[DaylightKey] == "true"; }
            set { _values[DaylightKey] = MissionSettingDescriptor.Text(value); }
        }

        public TimeVersusDeltaV TimeVersusDeltaV
        {
            get { return (TimeVersusDeltaV)Enum.Parse(typeof(TimeVersusDeltaV), _values[TimeKey]); }
            set { _values[TimeKey] = value.ToString(); }
        }

        /// <summary>The player's choice of Physics warp on Coasts (ADR 0008); starts on, and the config alone gates it (<see cref="PhysicsWarpOn"/>).</summary>
        public bool PhysicsWarp
        {
            get { return _values[PhysicsWarpKey] == "true"; }
            set { _values[PhysicsWarpKey] = MissionSettingDescriptor.Text(value); }
        }

        /// <summary>
        /// <c>physics_warp</c> in the config is on. Not saved: read from the config each time, so the config alone can turn
        /// physics warp back on, and the setting shows greyed out and off while it is false.
        /// </summary>
        public bool ConfigAllowsPhysicsWarp = true;

        /// <summary>Physics warp is allowed now, the gate's single input: the config and the Mission Setting are both on.</summary>
        public bool PhysicsWarpOn
        {
            get { return ConfigAllowsPhysicsWarp && PhysicsWarp; }
        }

        /// <summary>The saving Balanced takes Optimal for, as the prompt line and the Settings window say it: "10%".</summary>
        public static string BalancedSavingText
        {
            get { return (Tools.TransferWindows.BalancedSaving * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; }
        }

        /// <summary>
        /// How far a Planning Tool looks ahead under Balanced when Claudia gives no window, hours: a search horizon, not a limit on
        /// a wait for a Transfer Window (Balanced picks by <see cref="Tools.TransferWindows.BalancedSaving"/>, #669).
        /// </summary>
        public const double BalancedSearchHours = 6;

        /// <summary>How long a Planning Tool looks ahead when Claudia gives no window: a short horizon under Fastest, a long one under Most efficient.</summary>
        public double TimeCapHours
        {
            get { return TimeVersusDeltaV == TimeVersusDeltaV.Fastest ? 1 : TimeVersusDeltaV == TimeVersusDeltaV.MostEfficient ? 24 : BalancedSearchHours; }
        }

        public static MissionSettingsValues Defaults(PlayerConfig config)
        {
            var values = new MissionSettingsValues { ConfigAllowsPhysicsWarp = config.PhysicsWarp };
            foreach (var setting in All) values._values[setting.Key] = setting.Default(config);
            return values;
        }

        /// <summary>The settings in a Mission's name/value pairs; a missing or unreadable one takes the default.</summary>
        public static MissionSettingsValues FromDictionary(IDictionary<string, string> pairs, MissionSettingsValues defaults)
        {
            var values = defaults.Copy();
            if (pairs == null) return values;
            foreach (var setting in All)
            {
                string text;
                if (pairs.TryGetValue(setting.Key, out text) && setting.IsValid(text)) values._values[setting.Key] = text;
            }
            return values;
        }

        public void WriteTo(IDictionary<string, string> pairs)
        {
            foreach (var setting in All) pairs[setting.Key] = _values[setting.Key];
        }

        public MissionSettingsValues Copy()
        {
            var copy = new MissionSettingsValues { ConfigAllowsPhysicsWarp = ConfigAllowsPhysicsWarp };
            foreach (var setting in All) copy._values[setting.Key] = _values[setting.Key];
            return copy;
        }

        /// <summary>No setting that needs a revised plan differs; the check that decides whether an edit asks Claudia to revise.</summary>
        public bool SameAs(MissionSettingsValues other)
        {
            if (other == null) return false;
            foreach (var setting in All)
                if (setting.NeedsRevision && _values[setting.Key] != other._values[setting.Key]) return false;
            return true;
        }

        public static string Label(TimeVersusDeltaV time)
        {
            switch (time)
            {
                case TimeVersusDeltaV.Fastest: return "Fastest";
                case TimeVersusDeltaV.MostEfficient: return "Most efficient";
                default: return "Balanced";
            }
        }

        /// <summary>The settings in the player's words, for the Mission board.</summary>
        public string Summary()
        {
            var parts = new List<string>();
            foreach (var setting in All) parts.Add(setting.Summary(this));
            return string.Join("; ", parts);
        }
    }

    // ---- Player actions ----

    /// <summary>
    /// Edit the Mission Settings of the Mission in play. Claudia then revises the Mission Plan, which needs
    /// Approval again. Taken only while a plan awaits Approval and no request is out.
    /// </summary>
    public sealed class EditSettings : MissionInput
    {
        public readonly MissionSettingsValues Settings;
        public EditSettings(MissionSettingsValues settings) { Settings = settings; }
    }

    /// <summary>The Warp to Due Time button: asks the adapter to warp to the Due Time on rails. The mod warps no other time between sequences.</summary>
    public sealed class WarpToDueTime : MissionInput
    {
    }

    /// <summary>The Go to <i>vessel</i> button, shown at the Due Time: switches to the Mission's vessel.</summary>
    public sealed class GoToVessel : MissionInput
    {
    }

    // ---- Adapter commands ----

    /// <summary>Set a stock Alarm Clock alarm at <see cref="Ut"/> for the Mission, replacing the Mission's earlier one.</summary>
    public sealed class SetDueTimeAlarm : AdapterCommand
    {
        public readonly string MissionId;
        public readonly double Ut;
        public readonly string Title;

        public SetDueTimeAlarm(string missionId, double ut, string title)
        {
            MissionId = missionId;
            Ut = ut;
            Title = title;
        }
    }

    /// <summary>Remove the Mission's Due Time alarm (the sequence started, was replaced, or the Mission ended).</summary>
    public sealed class ClearDueTimeAlarm : AdapterCommand
    {
        public readonly string MissionId;
        public ClearDueTimeAlarm(string missionId) { MissionId = missionId; }
    }

    /// <summary>Warp on rails to <see cref="Ut"/> (the Warp to Due Time button). The adapter stops at the target.</summary>
    public sealed class WarpToUt : AdapterCommand
    {
        public readonly double Ut;
        public WarpToUt(double ut) { Ut = ut; }
    }

    /// <summary>Drop time warp to 1× (the Due Time was reached).</summary>
    public sealed class DropWarp : AdapterCommand
    {
    }

    /// <summary>Switch to the vessel with <see cref="VesselId"/> (the Go to <i>vessel</i> button).</summary>
    public sealed class SwitchToVessel : AdapterCommand
    {
        public readonly string VesselId;
        public SwitchToVessel(string vesselId) { VesselId = vesselId; }
    }
}
