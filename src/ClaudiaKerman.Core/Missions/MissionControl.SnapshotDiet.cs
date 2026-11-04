// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The craft's parts only when they change (#256): a turn's text goes out with its Game Snapshot shaped by
    /// <see cref="SnapshotDiet"/> against the conversation it is sent behind, so a compaction, which drops the earlier full
    /// snapshot from what is sent, brings the full one back on the next turn. The turn keeps its full text for Retry and a load.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>
            /// A turn with a Game Snapshot was committed and nothing has flown the craft since (a run or Manual Control). Not
            /// saved: after a load the next turn sends a snapshot of its own.
            /// </summary>
            public bool NoRunSinceSnapshot;
        }

        /// <summary>The turn's text as Claudia is sent it: with the full Game Snapshot, the dynamic one, or a pointer to the one above.</summary>
        private string ShapedUserText(Mission mission, Turn turn)
        {
            var sent = Sent(mission, turn.CompactionBase, turn.CompactedBase).Where(m => m.Role == Role.User)
                .Select(m => string.Concat(m.Content.OfType<TextBlock>().Select(b => b.Text))).ToList();
            return SnapshotDiet.Shape(turn.UserText, sent, mission.NoRunSinceSnapshot && !IsRunning(mission));
        }

        /// <summary>A committed turn that carried a Game Snapshot starts a leg; a run or Manual Control ends it.</summary>
        private static void SnapshotCommitted(Mission mission, Turn turn)
        {
            if (turn.UserText.Contains(MissionPrompts.SnapshotHeader)) mission.NoRunSinceSnapshot = true;
        }
    }
}
