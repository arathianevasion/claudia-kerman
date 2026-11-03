// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
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
        private const string Fastest = "Time versus delta-v, Fastest: take soonest; never wait for a cheaper window.";
        private const string Balanced = "Time versus delta-v, Balanced: take soonest unless optimal costs at least 10% less; take the plan_transfer pick.";
        private const string Efficient = "Time versus delta-v, Most efficient: take optimal, the cheapest window, however long the wait.";
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
            var layer = MissionPrompts.TimingLayer("m1", settings);
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

        private static string TimeLine(TimeVersusDeltaV time)
        {
            foreach (var setting in MissionSettingsValues.All)
                if (setting.Key == MissionSettingsValues.TimeKey) return setting.Prompt(Make(true, time, true));
            throw new KeyNotFoundException(MissionSettingsValues.TimeKey);
        }

        [Fact]
        public void The_balanced_line_states_the_saving_from_BalancedSaving_and_no_wait_cap()
        {
            var line = TimeLine(TimeVersusDeltaV.Balanced);
            Assert.Contains((TransferWindows.BalancedSaving * 100).ToString("0", CultureInfo.InvariantCulture) + "%", line);
            Assert.DoesNotContain("6 h", line);
        }

        /// <summary>The rule each prompt line states, read back from its words, takes the same window as TransferWindows.Pick (#669).</summary>
        [Theory]
        [InlineData(TimeVersusDeltaV.Fastest, 910)]
        [InlineData(TimeVersusDeltaV.Fastest, 890)]
        [InlineData(TimeVersusDeltaV.Balanced, 910)]
        [InlineData(TimeVersusDeltaV.Balanced, 900)]
        [InlineData(TimeVersusDeltaV.Balanced, 890)]
        [InlineData(TimeVersusDeltaV.MostEfficient, 910)]
        [InlineData(TimeVersusDeltaV.MostEfficient, 890)]
        public void Each_prompt_line_and_Pick_take_the_same_window(TimeVersusDeltaV time, double optimalDv)
        {
            // Soonest 1000 m/s; Optimal 910 saves 9%, 900 exactly 10% ("at least"), 890 saves 11%.
            var search = new WindowSearch { Soonest = new TransferWindow { TotalDv = 1000 }, Optimal = new TransferWindow { TotalDv = optimalDv } };
            var rule = Regex.Match(TimeLine(time), @"take (soonest|optimal)(?: unless optimal costs at least (\d+)% less)?");
            Assert.True(rule.Success, TimeLine(time));
            var saving = (search.Soonest.TotalDv - search.Optimal.TotalDv) / search.Soonest.TotalDv;
            var lineTakesOptimal = rule.Groups[2].Success
                ? saving >= int.Parse(rule.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0
                : rule.Groups[1].Value == "optimal";
            Assert.Equal(lineTakesOptimal ? search.Optimal : search.Soonest, TransferWindows.Pick(search, time));
        }

        [Fact]
        public void The_time_cap_keeps_its_meaning()
        {
            Assert.Equal(1, Make(true, TimeVersusDeltaV.Fastest, true).TimeCapHours);
            Assert.Equal(6, Make(true, TimeVersusDeltaV.Balanced, true).TimeCapHours);
            Assert.Equal(24, Make(true, TimeVersusDeltaV.MostEfficient, true).TimeCapHours);
            Assert.Equal(6, MissionSettingsValues.BalancedSearchHours);
        }

        [Fact]
        public void The_board_summary_is_pinned()
        {
            Assert.Equal("Daylight landing: on; time versus delta-v: Balanced; physics warp on Coasts: on", Make(true, TimeVersusDeltaV.Balanced, true).Summary());
            Assert.Equal("Daylight landing: off; time versus delta-v: Fastest; physics warp on Coasts: off", Make(false, TimeVersusDeltaV.Fastest, true, false).Summary());
            Assert.Equal("Daylight landing: on; time versus delta-v: Most efficient; physics warp on Coasts: off", Make(true, TimeVersusDeltaV.MostEfficient, false).Summary());
        }
    }
}
