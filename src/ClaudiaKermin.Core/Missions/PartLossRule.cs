// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Decides whether a broken joint is a part lost. A joint break is only a candidate: joints break and the part stays
    /// attached, and a decoupler firing in staging (or a block dropping a part) breaks joints on purpose, which the
    /// Staging event already covers.
    /// </summary>
    public static class PartLossRule
    {
        /// <summary>Seconds around a joint break within which a stage firing or decoupler separation explains it.</summary>
        public const double ExplainSeconds = 2.0;

        /// <summary>
        /// True when the candidate is to be logged as "broke off": checked a frame after the break, the part is no longer on
        /// the craft, and no stage firing or decoupler separation explains it.
        /// </summary>
        public static bool BrokeOff(bool stillOnCraft, bool explainedByStagingOrDecoupler)
        {
            return !stillOnCraft && !explainedByStagingOrDecoupler;
        }

        /// <summary>True when an explaining event at <paramref name="explainedAt"/> is near the break at <paramref name="brokeAt"/> (both in the same clock, seconds).</summary>
        public static bool Explains(double explainedAt, double brokeAt)
        {
            return explainedAt >= brokeAt - ExplainSeconds && explainedAt <= brokeAt + ExplainSeconds;
        }
    }
}
