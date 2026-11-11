// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public class DefaultSizesTests
    {
        // At 100% UI and Apps scale (UiScale.Base, 0.96) every size is 20% under the 1.2 look before #993.
        private const float Now = 0.96f;

        [Fact]
        public void At_the_base_scale_the_defaults_are_20_percent_under_the_sizes_before()
        {
            var mission = DefaultSizes.Mission(Now, 260, 80);
            Assert.Equal(400, mission.Width);
            Assert.Equal(640, mission.Height);
            var settings = DefaultSizes.Settings(Now, 200, 90);
            // Settings opens at its minimum width, whose paddings and gaps stay in pixels.
            Assert.Equal(379, settings.Width);
            Assert.Equal(560, settings.Height);
            Assert.Equal(592, DefaultSizes.StripWidth(Now));
            Assert.Equal(512, DefaultSizes.PanelHeight(Now));
        }

        [Fact]
        public void At_the_old_1_2_scale_the_defaults_are_still_the_widened_sizes()
        {
            Assert.Equal(500, DefaultSizes.Mission(1.2f, 0, 0).Width);
            Assert.Equal(800, DefaultSizes.Mission(1.2f, 0, 0).Height);
            Assert.Equal(740, DefaultSizes.StripWidth(1.2f));
            Assert.Equal(640, DefaultSizes.PanelHeight(1.2f));
        }

        [Fact]
        public void The_position_passes_through()
        {
            var mission = DefaultSizes.Mission(Now, 260, 80);
            Assert.Equal(260, mission.X);
            Assert.Equal(80, mission.Y);
        }

        [Fact]
        public void A_bigger_scale_makes_every_default_bigger()
        {
            Assert.True(DefaultSizes.Mission(1.2f, 0, 0).Width > 400);
            Assert.True(DefaultSizes.Mission(1.2f, 0, 0).Height > 640);
            Assert.True(DefaultSizes.Settings(1.2f, 0, 0).Width > 379);
            Assert.True(DefaultSizes.Settings(1.2f, 0, 0).Height > 560);
            Assert.True(DefaultSizes.StripWidth(1.2f) > 592);
            Assert.True(DefaultSizes.PanelHeight(1.2f) > 512);
        }

        [Fact]
        public void At_the_base_scale_every_default_is_0_8_of_its_1_2_size_less_its_fixed_pixels()
        {
            AssertShrinks(DefaultSizes.Mission(1.2f, 0, 0).Width, DefaultSizes.Mission(Now, 0, 0).Width, 0);
            AssertShrinks(DefaultSizes.Mission(1.2f, 0, 0).Height, DefaultSizes.Mission(Now, 0, 0).Height, 0);
            AssertShrinks(DefaultSizes.Settings(1.2f, 0, 0).Width, DefaultSizes.Settings(Now, 0, 0).Width, DefaultSizes.SettingsMinWidthFixedPx);
            AssertShrinks(DefaultSizes.Settings(1.2f, 0, 0).Height, DefaultSizes.Settings(Now, 0, 0).Height, 0);
            AssertShrinks(DefaultSizes.StripWidth(1.2f), DefaultSizes.StripWidth(Now), 0);
            AssertShrinks(DefaultSizes.PanelHeight(1.2f), DefaultSizes.PanelHeight(Now), 0);
        }

        [Fact]
        public void At_the_base_scale_every_minimum_is_0_8_of_its_1_2_size_less_its_fixed_pixels()
        {
            AssertShrinks(DefaultSizes.MissionMin(1.2f).Width, DefaultSizes.MissionMin(Now).Width, DefaultSizes.MissionMinWidthFixedPx);
            AssertShrinks(DefaultSizes.MissionMin(1.2f).Height, DefaultSizes.MissionMin(Now).Height, 0);
            AssertShrinks(DefaultSizes.SettingsMin(1.2f).Width, DefaultSizes.SettingsMin(Now).Width, DefaultSizes.SettingsMinWidthFixedPx);
            AssertShrinks(DefaultSizes.SettingsMin(1.2f).Height, DefaultSizes.SettingsMin(Now).Height, 0);
            AssertShrinks(DefaultSizes.StripMinWidth(1.2f), DefaultSizes.StripMinWidth(Now), DefaultSizes.StripMinWidthFixedPx);
            AssertShrinks(DefaultSizes.PanelMinHeight(1.2f), DefaultSizes.PanelMinHeight(Now), DefaultSizes.PanelMinHeightFixedPx);
        }

        // The part over the fixed pixels at the base scale is 0.8 of that at 1.2, within the pixel the rounding leaves.
        private static void AssertShrinks(float at12, float atBase, float fixedPx)
        {
            var expected = 0.8f * (at12 - fixedPx) + fixedPx;
            Assert.InRange(atBase, expected - 1, expected + 1);
        }

        [Fact]
        public void At_scale_one_the_defaults_are_the_design_sizes()
        {
            Assert.Equal(DefaultSizes.StripWidthDesign, DefaultSizes.StripWidth(1f));
            Assert.Equal(DefaultSizes.MissionWidthDesign, DefaultSizes.Mission(1f, 0, 0).Width);
        }

        [Fact]
        public void At_the_base_scale_the_minimums_are_20_percent_under_the_old_hand_picked_sizes()
        {
            // The widths keep their fixed pixels: 132, 96 and 110 of the 430, 450 and 624 at 1.2.
            Assert.Equal(370, DefaultSizes.MissionMin(Now).Width);
            Assert.Equal(368, DefaultSizes.MissionMin(Now).Height);
            Assert.Equal(379, DefaultSizes.SettingsMin(Now).Width);
            Assert.Equal(320, DefaultSizes.SettingsMin(Now).Height);
            Assert.Equal(521, DefaultSizes.StripMinWidth(Now));
        }

        [Fact]
        public void At_the_old_1_2_scale_the_minimums_are_the_old_hand_picked_sizes()
        {
            Assert.Equal(430, DefaultSizes.MissionMin(1.2f).Width);
            Assert.Equal(460, DefaultSizes.MissionMin(1.2f).Height);
            Assert.Equal(450, DefaultSizes.SettingsMin(1.2f).Width);
            Assert.Equal(400, DefaultSizes.SettingsMin(1.2f).Height);
            Assert.Equal(624, DefaultSizes.StripMinWidth(1.2f));
        }

        [Fact]
        public void At_UI_scale_150_percent_the_minimum_widths_scale_all_but_their_fixed_pixels()
        {
            Assert.Equal(579, DefaultSizes.MissionMin(1.8f).Width);
            Assert.Equal(627, DefaultSizes.SettingsMin(1.8f).Width);
            Assert.Equal(881, DefaultSizes.StripMinWidth(1.8f));
        }

        [Fact]
        public void The_fixed_pixels_are_the_sums_of_the_adapters_paddings_and_gaps()
        {
            Assert.Equal(132, DefaultSizes.MissionMinWidthFixedPx);
            Assert.Equal(96, DefaultSizes.SettingsMinWidthFixedPx);
            Assert.Equal(110, DefaultSizes.StripMinWidthFixedPx);
            Assert.Equal(71, DefaultSizes.PanelMinHeightFixedPx);
        }

        [Theory]
        [InlineData(0.8f)]
        [InlineData(0.96f)]
        [InlineData(1f)]
        [InlineData(1.248f)]
        [InlineData(1.5f)]
        public void A_default_is_never_clamped_by_its_minimum(float scale)
        {
            Assert.True(DefaultSizes.Mission(scale, 0, 0).Width >= DefaultSizes.MissionMin(scale).Width);
            Assert.True(DefaultSizes.Mission(scale, 0, 0).Height >= DefaultSizes.MissionMin(scale).Height);
            Assert.True(DefaultSizes.Settings(scale, 0, 0).Width >= DefaultSizes.SettingsMin(scale).Width);
            Assert.True(DefaultSizes.Settings(scale, 0, 0).Height >= DefaultSizes.SettingsMin(scale).Height);
            Assert.True(DefaultSizes.StripWidth(scale) >= DefaultSizes.StripMinWidth(scale));
            Assert.True(DefaultSizes.PanelHeight(scale) >= DefaultSizes.PanelMinHeight(scale));
        }

        // ---- The scale factor from the game's UI Scale and Apps scale ----

        [Fact]
        public void At_100_percent_UI_and_apps_the_factor_is_0_96_20_percent_under_the_old_1_2()
        {
            Assert.Equal(0.96f, UiScale.Factor(1f, 1f), 4);
            Assert.Equal(UiScale.OldBase * 0.8f, UiScale.Base, 4);
        }

        [Fact]
        public void At_UI_scale_130_percent_the_factor_is_1_248()
        {
            Assert.Equal(1.248f, UiScale.Factor(1.3f, 1f), 4);
        }

        [Fact]
        public void Apps_150_percent_makes_every_size_one_and_a_half_times_the_base()
        {
            var factor = UiScale.Factor(1f, 1.5f);
            Assert.Equal(1.44f, factor, 4);
            // Within a pixel: the design sizes are rounded.
            Assert.InRange(DefaultSizes.StripWidth(factor), 592 * 1.5f - 1, 592 * 1.5f + 1);
            Assert.InRange(DefaultSizes.PanelHeight(factor), 512 * 1.5f - 1, 512 * 1.5f + 1);
            Assert.InRange(DefaultSizes.Mission(factor, 0, 0).Width, 400 * 1.5f - 1, 400 * 1.5f + 1);
            Assert.InRange(DefaultSizes.Mission(factor, 0, 0).Height, 640 * 1.5f - 1, 640 * 1.5f + 1);
            // The minimum's fixed 132 px don't grow: the rest, 238 at the base, is 358 at 1.5 times it.
            Assert.Equal(490, DefaultSizes.MissionMin(factor).Width);
        }

        [Fact]
        public void UI_scale_and_apps_scale_multiply()
        {
            Assert.Equal(0.96f * 1.3f * 1.2f, UiScale.Factor(1.3f, 1.2f), 4);
        }

        [Fact]
        public void Apps_80_percent_shrinks_every_size()
        {
            var factor = UiScale.Factor(1f, 0.8f);
            Assert.True(factor < Now);
            Assert.True(DefaultSizes.StripWidth(factor) < 592);
            Assert.True(DefaultSizes.Mission(factor, 0, 0).Width < 400);
            Assert.True(DefaultSizes.SettingsMin(factor).Height < 320);
        }

        [Theory]
        [InlineData(0f, 1f)]
        [InlineData(1f, -1f)]
        [InlineData(float.NaN, 1f)]
        [InlineData(1f, float.PositiveInfinity)]
        public void A_setting_that_is_not_a_positive_number_counts_as_100_percent(float ui, float apps)
        {
            Assert.Equal(0.96f, UiScale.Factor(ui, apps), 4);
        }

        [Fact]
        public void At_the_base_scale_the_unmoved_strip_falls_back_to_56_px_down_and_a_5_px_margin()
        {
            // 70 px and 6 px at the old 1.2, 20% under that now.
            Assert.Equal(56, DefaultSizes.StripTop(Now));
            Assert.Equal(5, DefaultSizes.StripMargin(Now));
            Assert.Equal(70, DefaultSizes.StripTop(1.2f));
            Assert.Equal(6, DefaultSizes.StripMargin(1.2f));
            Assert.True(DefaultSizes.StripTop(1.248f) > 56);
        }

        [Fact]
        public void A_bigger_scale_makes_every_minimum_bigger()
        {
            Assert.True(DefaultSizes.MissionMin(1.2f).Width > 370);
            Assert.True(DefaultSizes.MissionMin(1.2f).Height > 368);
            Assert.True(DefaultSizes.SettingsMin(1.2f).Width > 379);
            Assert.True(DefaultSizes.SettingsMin(1.2f).Height > 320);
            Assert.True(DefaultSizes.StripMinWidth(1.2f) > 521);
        }

        [Fact]
        public void The_docked_panels_floor_keeps_its_unscaled_padding_so_the_transcript_keeps_room()
        {
            // 200 at the old 1.2 as before; the part that holds text shrinks with it, the paddings in pixels don't.
            Assert.Equal(200, DefaultSizes.PanelMinHeight(1.2f));
            Assert.Equal(174, DefaultSizes.PanelMinHeight(Now));
            Assert.Equal(205, DefaultSizes.PanelMinHeight(1.248f));
            Assert.Equal(264, DefaultSizes.PanelMinHeight(1.8f));
        }
    }
}
