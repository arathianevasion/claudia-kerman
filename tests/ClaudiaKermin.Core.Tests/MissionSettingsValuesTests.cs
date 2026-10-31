// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Pins the Mission Settings (#359): the prompt text Claudia reads, the name/value pairs saved with a Mission, and the
    /// revision check. The prompt text is part of the cached prefix, so it must not change by one character.
    /// </summary>
    public sealed class MissionSettingsValuesTests
    {
        private const string DaylightOn = "Daylight landing: on. Set the land block's daylight option to true and prefer a daylight landing.";
        private const string DaylightOff = "Daylight landing: off. Do not wait for daylight.";
        private const string Fastest = "Time versus delta-v, Fastest: never wait for a better window; take the next one.";
        private const string Balanced = "Time versus delta-v, Balanced: wait up to 6 h to save delta-v; do not wait longer than that for a better window.";
        private const string Efficient = "Time versus delta-v, Most efficient: wait for the cheapest window, however long (the script warps there).";
        private const string WarpOn = "Physics warp on Coasts: on. Blocks warp through Coasts with physics warp where rails can't; use the warp helpers for your own Coasts.";
        private const string WarpOff = "Physics warp on Coasts: off. The mod refuses physics warp. Where rails can't warp, Coasts run at 1×, so plan for the extra real time.";
        private const string WarpOffConfig = "Physics warp on Coasts: off. The mod refuses physics warp. Only the player's config can turn it back on. Where rails can't warp, Coasts run at 1×, so plan for the extra real time.";

        private static MissionSettingsValues Make(bool daylight, TimeVersusDeltaV time, bool warp, bool config = true)
        {
            return new MissionSettingsValues { DaylightLanding = daylight, TimeVersusDeltaV = time, PhysicsWarp = warp, ConfigAllowsPhysicsWarp = config };
        }

        [Fact]
        public void The_prompt_text_is_pinned_for_every_combination()
        {
            Assert.Equal("Mission Settings. " + DaylightOn + " " + Balanced + "\n" + WarpOn, MissionPrompts.SettingsText(Make(true, TimeVersusDeltaV.Balanced, true)));
            Assert.Equal("Mission Settings. " + DaylightOff + " " + Fastest + "\n" + WarpOff, MissionPrompts.SettingsText(Make(false, TimeVersusDeltaV.Fastest, false)));
            Assert.Equal("Mission Settings. " + DaylightOn + " " + Efficient + "\n" + WarpOffConfig, MissionPrompts.SettingsText(Make(true, TimeVersusDeltaV.MostEfficient, true, false)));
            Assert.Equal("Mission Settings. " + DaylightOn + " " + Fastest + "\n" + WarpOffConfig, MissionPrompts.SettingsText(Make(true, TimeVersusDeltaV.Fastest, false, false)));
        }

        [Fact]
        public void The_changed_notice_wraps_the_same_text()
        {
            var settings = Make(false, TimeVersusDeltaV.MostEfficient, true);
            Assert.Equal("The player changed the Mission Settings. They now read (these replace the settings you were given before):\n"
                + "Mission Settings. " + DaylightOff + " " + Efficient + "\n" + WarpOn, MissionPrompts.SettingsChanged(settings));
        }

        [Fact]
        public void The_timing_layer_still_ends_with_the_settings_text_and_the_chat_rule()
        {
            var settings = Make(true, TimeVersusDeltaV.Balanced, true);
            var layer = MissionPrompts.TimingLayer(settings);
            Assert.Contains("\n\n" + MissionPrompts.SettingsText(settings) + "\nAsked in chat to stop or allow physics warp, point the player to that setting; change no plan until they switch it.", layer);
            Assert.EndsWith("change no plan until they switch it.", layer);
        }

        [Fact]
        public void The_saved_name_value_pairs_are_pinned()
        {
            var pairs = new Dictionary<string, string>();
            Make(false, TimeVersusDeltaV.MostEfficient, false).WriteTo(pairs);
            Assert.Equal(3, pairs.Count);
            Assert.Equal("false", pairs["daylight_landing"]);
            Assert.Equal("MostEfficient", pairs["time_vs_dv"]);
            Assert.Equal("false", pairs["physics_warp"]);

            pairs.Clear();
            Make(true, TimeVersusDeltaV.Balanced, true).WriteTo(pairs);
            Assert.Equal("true", pairs["daylight_landing"]);
            Assert.Equal("Balanced", pairs["time_vs_dv"]);
            Assert.Equal("true", pairs["physics_warp"]);
        }

        [Fact]
        public void Saved_pairs_read_back_and_a_missing_or_unreadable_one_takes_the_default()
        {
            var defaults = MissionSettingsValues.Defaults(new PlayerConfig { DefaultDaylightLanding = false, DefaultTimeVersusDeltaV = TimeVersusDeltaV.Fastest, PhysicsWarp = false });
            var read = MissionSettingsValues.FromDictionary(new Dictionary<string, string>
            {
                { "daylight_landing", "true" }, { "time_vs_dv", "MostEfficient" }, { "physics_warp", "false" },
            }, defaults);
            Assert.True(read.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.MostEfficient, read.TimeVersusDeltaV);
            Assert.False(read.PhysicsWarp);

            var empty = MissionSettingsValues.FromDictionary(new Dictionary<string, string>(), defaults);
            Assert.False(empty.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.Fastest, empty.TimeVersusDeltaV);
            Assert.True(empty.PhysicsWarp);
            Assert.False(empty.ConfigAllowsPhysicsWarp);

            var junk = MissionSettingsValues.FromDictionary(new Dictionary<string, string>
            {
                { "daylight_landing", "maybe" }, { "time_vs_dv", "Sometime" }, { "physics_warp", "" },
            }, defaults);
            Assert.False(junk.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.Fastest, junk.TimeVersusDeltaV);
            Assert.True(junk.PhysicsWarp);

            Assert.True(MissionSettingsValues.FromDictionary(null, defaults).SameAs(defaults));
        }

        [Fact]
        public void The_config_is_not_saved_and_gates_physics_warp_with_the_setting()
        {
            var pairs = new Dictionary<string, string>();
            Make(true, TimeVersusDeltaV.Balanced, true, false).WriteTo(pairs);
            Assert.False(pairs.ContainsKey("config_allows_physics_warp"));
            Assert.Equal(3, pairs.Count);

            Assert.True(Make(true, TimeVersusDeltaV.Balanced, true, true).PhysicsWarpOn);
            Assert.False(Make(true, TimeVersusDeltaV.Balanced, true, false).PhysicsWarpOn);
            Assert.False(Make(true, TimeVersusDeltaV.Balanced, false, true).PhysicsWarpOn);
            Assert.False(MissionSettingsValues.Defaults(new PlayerConfig { PhysicsWarp = false }).PhysicsWarpOn);
            Assert.True(MissionSettingsValues.Defaults(new PlayerConfig { PhysicsWarp = false }).PhysicsWarp);
        }

        [Fact]
        public void Defaults_come_from_the_config()
        {
            var values = MissionSettingsValues.Defaults(new PlayerConfig { DefaultDaylightLanding = false, DefaultTimeVersusDeltaV = TimeVersusDeltaV.MostEfficient });
            Assert.False(values.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.MostEfficient, values.TimeVersusDeltaV);
            Assert.True(values.PhysicsWarp);
            Assert.True(values.ConfigAllowsPhysicsWarp);
        }

        [Fact]
        public void The_revision_check_notices_each_saved_setting_and_nothing_else()
        {
            var baseline = Make(true, TimeVersusDeltaV.Balanced, true);
            Assert.True(baseline.SameAs(baseline.Copy()));
            Assert.False(baseline.SameAs(null));
            Assert.False(baseline.SameAs(Make(false, TimeVersusDeltaV.Balanced, true)));
            Assert.False(baseline.SameAs(Make(true, TimeVersusDeltaV.Fastest, true)));
            Assert.False(baseline.SameAs(Make(true, TimeVersusDeltaV.Balanced, false)));
            Assert.True(baseline.SameAs(Make(true, TimeVersusDeltaV.Balanced, true, false)));
        }

        [Fact]
        public void The_copy_keeps_everything_and_is_independent()
        {
            var original = Make(false, TimeVersusDeltaV.Fastest, false, false);
            var copy = original.Copy();
            Assert.False(copy.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.Fastest, copy.TimeVersusDeltaV);
            Assert.False(copy.PhysicsWarp);
            Assert.False(copy.ConfigAllowsPhysicsWarp);
            copy.DaylightLanding = true;
            Assert.False(original.DaylightLanding);
        }

        [Fact]
        public void Every_described_setting_is_saved_prompted_and_summarised_without_further_wiring()
        {
            var values = MissionSettingsValues.Defaults(new PlayerConfig());
            var pairs = new Dictionary<string, string>();
            values.WriteTo(pairs);
            var prompt = MissionPrompts.SettingsText(values);
            var summary = values.Summary();
            Assert.Equal(MissionSettingsValues.All.Count, pairs.Count);
            foreach (var setting in MissionSettingsValues.All)
            {
                Assert.True(setting.IsValid(pairs[setting.Key]), setting.Key);
                Assert.Contains(setting.Prompt(values), prompt);
                Assert.Contains(setting.Summary(values), summary);
                Assert.True(setting.NeedsRevision, setting.Key);
            }
        }

        [Fact]
        public void The_time_cap_keeps_its_meaning()
        {
            Assert.Equal(1, Make(true, TimeVersusDeltaV.Fastest, true).TimeCapHours);
            Assert.Equal(6, Make(true, TimeVersusDeltaV.Balanced, true).TimeCapHours);
            Assert.Equal(24, Make(true, TimeVersusDeltaV.MostEfficient, true).TimeCapHours);
            Assert.Equal(6, MissionSettingsValues.BalancedWaitHours);
        }

        [Fact]
        public void The_board_summary_is_pinned()
        {
            Assert.Equal("Daylight landing: on; time versus delta-v: Balanced (waits up to 6 h); physics warp on Coasts: on", Make(true, TimeVersusDeltaV.Balanced, true).Summary());
            Assert.Equal("Daylight landing: off; time versus delta-v: Fastest; physics warp on Coasts: off", Make(false, TimeVersusDeltaV.Fastest, true, false).Summary());
            Assert.Equal("Daylight landing: on; time versus delta-v: Most efficient; physics warp on Coasts: off", Make(true, TimeVersusDeltaV.MostEfficient, false).Summary());
        }
    }
}
