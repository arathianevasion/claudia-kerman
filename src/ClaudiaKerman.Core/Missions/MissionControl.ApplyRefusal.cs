// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #875: a reply refused at Apply time, after it and its turn joined the conversation and nothing was in hand. The first time
    /// the refused turn goes again carrying only the reason (what it asked is already in the conversation, #876); the second
    /// time the Mission Pauses with a turn in hand, so Retry and Retry with a note have something to send.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <param name="corrected">The refused turn was itself the automatic correction.</param>
        /// <param name="reason">What the correction (or the Retry) says to Claudia; the committed user text is not repeated.</param>
        /// <param name="pauseReason">The Pause's reason, for a second refusal.</param>
        /// <param name="notice">The log's notice, for the first.</param>
        /// <param name="correction">The correction turn, built without the committed user text; its UserText is the reason.</param>
        private void RefusedAtApply(Mission mission, Turn turn, bool corrected, string reason, string pauseReason, string notice, Func<Turn> correction)
        {
            if (corrected)
            {
                // The turn goes back in hand as a request of its own: its messages were committed with the refused reply.
                ResendFromScratch(mission, turn, t => t.UserText = reason);
                mission.Pending = turn;
                Failed(mission, turn, pauseReason);
                // Retry with a note is how the player answers: it takes a Refusal pause.
                if (mission.Stage == MissionStage.Paused) mission.PauseCause = PauseCause.Refusal;
                return;
            }
            Note(LogKind.Notice, notice);
            Begin(correction());
        }
    }
}
