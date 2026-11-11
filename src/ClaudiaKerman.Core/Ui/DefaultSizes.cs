// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKerman.Core.Ui
{
    /// <summary>
    /// The default size of each window and the Capcom strip, derived from the UI's font scale so that changing the scale
    /// resizes them too. Each is its design size (at scale 1) times the scale, rounded to whole pixels; the design sizes
    /// are chosen so the 1.2 scale gives the 500×800 Mission window, 450×700 Settings window, 740 px strip and 640 px panel.
    /// </summary>
    public static class DefaultSizes
    {
        public const float MissionWidthDesign = 417, MissionHeightDesign = 667;
        public const float SettingsWidthDesign = 375, SettingsHeightDesign = 583;
        public const float StripWidthDesign = 617;
        public const float PanelHeightDesign = 533;
        // Minimums, chosen so the 1.2 scale gives the 430×460 Mission and 450×400 Settings minimums.
        public const float MissionMinWidthDesign = 358, MissionMinHeightDesign = 383;
        public const float SettingsMinWidthDesign = 375, SettingsMinHeightDesign = 333;

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
            return new WindowBox(x, y, Scale(SettingsWidthDesign, fontScale), Scale(SettingsHeightDesign, fontScale));
        }

        public static WindowBox MissionMin(float fontScale)
        {
            return new WindowBox(0, 0, Scale(MissionMinWidthDesign, fontScale), Scale(MissionMinHeightDesign, fontScale));
        }

        public static WindowBox SettingsMin(float fontScale)
        {
            return new WindowBox(0, 0, Scale(SettingsMinWidthDesign, fontScale), Scale(SettingsMinHeightDesign, fontScale));
        }

        public static float StripWidth(float fontScale) { return Scale(StripWidthDesign, fontScale); }

        public static float PanelHeight(float fontScale) { return Scale(PanelHeightDesign, fontScale); }

        // The unmoved strip's top when the stock UI can't be read (70 px at 1.2), and its margin from the altimeter and the app bar (6 px at 1.2).
        public const float StripTopDesign = 70f / UiScale.Base, StripMarginDesign = 5;

        public static float StripTop(float fontScale) { return Scale(StripTopDesign, fontScale); }

        public static float StripMargin(float fontScale) { return Scale(StripMarginDesign, fontScale); }

        // Below this the docked panel's prompt and control bar leave the transcript no room (200 px at 1.2).
        public const float PanelMinHeightDesign = 200f / UiScale.Base;

        public static float PanelMinHeight(float fontScale) { return Scale(PanelMinHeightDesign, fontScale); }
    }

    /// <summary>
    /// The UI's one scale factor, from the game's settings: UI Scale times Apps scale times <see cref="Base"/>, so that
    /// 100% and 100% keep the 1.2 look from before Claudia followed the stock UI (#991).
    /// </summary>
    public static class UiScale
    {
        public const float Base = 1.2f;

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
