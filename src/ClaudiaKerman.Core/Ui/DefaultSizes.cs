// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKerman.Core.Ui
{
    /// <summary>
    /// The default size of each window and the Capcom strip, derived from the UI's font scale so that changing the scale
    /// resizes them too. Each is its design size (at scale 1) times the scale, rounded to whole pixels; the design sizes
    /// are chosen so the old 1.2 scale gives the 500×800 Mission window, 450×700 Settings window, 740 px strip and 640 px
    /// panel. At 100% and 100% (<see cref="UiScale.Base"/>, 0.96) each is 20% under that: 400×640, 560 high, 592 and 512 (#993).
    /// The minimum widths and the panel's minimum height keep the paddings and gaps the Adapter draws in fixed pixels, so
    /// only the rest of each scales and they shrink less; the Settings window opens at its minimum width, as it did at 1.2.
    /// </summary>
    public static class DefaultSizes
    {
        public const float MissionWidthDesign = 417, MissionHeightDesign = 667;
        public const float SettingsHeightDesign = 583;
        public const float StripWidthDesign = 617;
        public const float PanelHeightDesign = 533;
        // Minimums: at the old 1.2 scale the 430×460 Mission and 450×400 Settings minimums and the 624 px strip.
        public const float MissionMinHeightDesign = 383, SettingsMinHeightDesign = 333;

        // The fixed pixels across each minimum width, from the Adapter's constants:
        // Mission, its control bar (the bare window has no side padding): the bar's Padding and right room (Padding) (24),
        // the Gap after Gear and after Clear (16), the small button's 8 px side paddings on Clear, Dock and Pause Mission
        // (48) and the button style's 10 px ones on End Mission (20): 108.
        public const float MissionMinWidthFixedPx = 24 + 16 + 48 + 20;
        // Settings, its widest fixed row, the Claudia Kerman header: the window's Padding ×2 (24), the header box's Padding
        // ×2 (24), the 30 px portrait, the Gap after it (8) and the 16 px chevron: 102. (The API key's buttons have 96.)
        public const float SettingsMinWidthFixedPx = 24 + 24 + 30 + 8 + 16;
        // The strip, its docked panel's control bar: the panel box's 1 px sides (2), the control bar's Padding and right
        // room (Padding) (24), the Gap after Gear and after Clear (16), the small button's side paddings on Clear, Undock and
        // Pause Mission (48) and End Mission's (20): 110. (The status row's are fewer, 70: the strip's Padding ×2, Gap + 2,
        // Gap ×2, Gap / 2 and the chip's 8 px sides; SegmentGap and PortraitRadius take no width, as the track scrolls.)
        public const float StripMinWidthFixedPx = 2 + 24 + 16 + 48 + 20;
        private const float MissionMinWidthScaledDesign = (430f - MissionMinWidthFixedPx) / UiScale.OldBase;
        private const float SettingsMinWidthScaledDesign = (450f - SettingsMinWidthFixedPx) / UiScale.OldBase;
        private const float StripMinWidthScaledDesign = (624f - StripMinWidthFixedPx) / UiScale.OldBase;

        public static float Scale(float designPx, float fontScale)
        {
            return (float)Math.Round(designPx * fontScale, MidpointRounding.AwayFromZero);
        }

        public static WindowBox Mission(float fontScale, float x, float y)
        {
            return new WindowBox(x, y, Scale(MissionWidthDesign, fontScale), Scale(MissionHeightDesign, fontScale));
        }

        public static WindowBox Settings(float fontScale, float x, float y)
        {
            return new WindowBox(x, y, SettingsMinWidth(fontScale), Scale(SettingsHeightDesign, fontScale));
        }

        public static WindowBox MissionMin(float fontScale)
        {
            return new WindowBox(0, 0, Scale(MissionMinWidthScaledDesign, fontScale) + MissionMinWidthFixedPx, Scale(MissionMinHeightDesign, fontScale));
        }

        public static WindowBox SettingsMin(float fontScale)
        {
            return new WindowBox(0, 0, SettingsMinWidth(fontScale), Scale(SettingsMinHeightDesign, fontScale));
        }

        private static float SettingsMinWidth(float fontScale)
        {
            return Scale(SettingsMinWidthScaledDesign, fontScale) + SettingsMinWidthFixedPx;
        }

        public static float StripWidth(float fontScale) { return Scale(StripWidthDesign, fontScale); }

        public static float StripMinWidth(float fontScale) { return Scale(StripMinWidthScaledDesign, fontScale) + StripMinWidthFixedPx; }

        public static float PanelHeight(float fontScale) { return Scale(PanelHeightDesign, fontScale); }

        // The unmoved strip's top when the stock UI can't be read and its margin from the altimeter and the app bar: 70 px
        // and 6 px at the old 1.2, 56 px and 5 px at 100% and 100% now.
        public const float StripTopDesign = 70f / UiScale.OldBase, StripMarginDesign = 5;

        public static float StripTop(float fontScale) { return Scale(StripTopDesign, fontScale); }

        public static float StripMargin(float fontScale) { return Scale(StripMarginDesign, fontScale); }

        // Below this the docked panel's prompt and control bar leave the transcript no room: 200 px at the old 1.2. Their
        // text and heights follow the scale but these are fixed pixels, so only the rest scales (174 px at 100% and 100%):
        // the Gap / 2 over the transcript and over the control bar (8), the two Dividers (2), the prompt box's Raised
        // padding 10 + 10 (20), its caption's and hint's 2 px paddings and margins (16), the text area's 10 + 10 padding
        // and 2 + 2 margin (24) and the panel box's 1 px bottom (1): 71. The control bar's 6 + 6 padding is inside its
        // scaled height, and the transcript's Padding is at its sides.
        public const float PanelMinHeightFixedPx = 8 + 2 + 20 + 16 + 24 + 1;
        private const float PanelMinHeightScaledDesign = (200f - PanelMinHeightFixedPx) / UiScale.OldBase;

        public static float PanelMinHeight(float fontScale) { return Scale(PanelMinHeightScaledDesign, fontScale) + PanelMinHeightFixedPx; }
    }

    /// <summary>
    /// The UI's one scale factor, from the game's settings: UI Scale times Apps scale times <see cref="Base"/>. At 100%
    /// and 100% Claudia is 20% under the 1.2 look she kept when she first followed the stock UI (#991, #993).
    /// </summary>
    public static class UiScale
    {
        public const float Base = 0.96f;

        /// <summary>The base before #993. A saved window box with no scale on its line was kept at it (<see cref="WindowLayouts"/>).</summary>
        public const float OldBase = 1.2f;

        /// <summary>A setting that isn't a positive number counts as 100%.</summary>
        public static float Factor(float uiScale, float appsScale)
        {
            return Base * Sane(uiScale) * Sane(appsScale);
        }

        private static float Sane(float setting)
        {
            return setting > 0 && !float.IsInfinity(setting) ? setting : 1f;
        }
    }
}
