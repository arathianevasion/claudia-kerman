// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Mission Goal as its steps (#581): the Mission Plan defines them once (goal_steps), and each Debrief ticks them by
    /// number (goal_steps_done). The loop and <see cref="MissionLogRebuild"/> both keep them by these rules, so a Mission
    /// loaded from its transcript has the ticks it had.
    /// </summary>
    internal static class GoalSteps
    {
        /// <summary>A Mission Plan's steps replace the list, and a step whose text is unchanged keeps its tick; a plan with none leaves <paramref name="current"/> as it is.</summary>
        public static List<GoalItem> Replace(List<GoalItem> current, IList<string> steps)
        {
            if (steps == null || steps.Count == 0) return current;
            var ticked = new HashSet<string>(current.Where(s => s.Done).Select(s => s.Item), StringComparer.Ordinal);
            return steps.Select(s => new GoalItem { Item = s, Done = ticked.Contains(s) }).ToList();
        }

        /// <summary>Ticks the steps numbered in <paramref name="done"/>, 1 for the first; a number outside the list is ignored, and no tick is taken back.</summary>
        public static void Tick(IList<GoalItem> steps, IEnumerable<int> done)
        {
            foreach (var number in done)
                if (number >= 1 && number <= steps.Count) steps[number - 1].Done = true;
        }

        /// <summary>
        /// The steps after a Debrief: an inline goal_checklist (a transcript from before #581) is the list as it stood then;
        /// otherwise <paramref name="current"/> with goal_steps_done ticked.
        /// </summary>
        public static List<GoalItem> After(List<GoalItem> current, Debrief debrief)
        {
            if (debrief.Checklist.Count > 0) return Copy(debrief.Checklist);
            Tick(current, debrief.GoalStepsDone);
            return current;
        }

        public static List<GoalItem> Copy(IEnumerable<GoalItem> steps)
        {
            return steps.Select(s => new GoalItem { Item = s.Item, Done = s.Done }).ToList();
        }
    }
}
