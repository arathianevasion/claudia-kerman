// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #979: what may leave with an empty tank in an "auto" drop, besides trim (the player's rule, 2026-11-10). The block judges each
    /// decoupler's parts against these kinds (<c>claudia_part_kind</c>); engines, parachutes, crew and the rest always keep it aboard.
    /// <para>
    /// <b>A heat shield always.</b> If the parts it shields go with it, it doesn't matter. A shield on the crew part's side of a decoupler
    /// only leaves with the crew part, and a part holding a kerbal blocks the drop; a shield past a decoupler from the crew part (a drop
    /// tank's) goes with that decoupler's parts, as staging would take it.
    /// </para>
    /// <para>
    /// <b>Landing legs once no landing is left</b> (<see cref="LegsFree"/>). No Mission Plan (a block test): they stay.
    /// </para>
    /// </summary>
    public static class AutoDrop
    {
        /// <summary>The kinds that may leave: "shield", and "leg" when <see cref="LegsFree"/>.</summary>
        public static IList<string> FreeKinds(IList<PlannedSequence> plan, int step)
        {
            var free = new List<string> { "shield" };
            if (LegsFree(plan, step)) free.Add("leg");
            return free;
        }

        /// <summary>
        /// True when the Mission Plan (<paramref name="plan"/>, null: none) has no landing still to fly from the sequence in hand
        /// (<paramref name="step"/>, -1 before the first) on, that one included. A landing, read from the words as <see cref="SequenceWords"/>
        /// reads them: land or touch down, a deorbit, or a reentry, manual legs too. The Kerbin return (a return home whose words don't say it
        /// lands or touches down) is not one: it comes down on the crew part's chutes, as this flight's "Return to KSC" did.
        /// </summary>
        public static bool LegsFree(IList<PlannedSequence> plan, int step)
        {
            if (plan == null) return false;
            for (var i = Math.Max(0, step); i < plan.Count; i++)
                if (Lands(SequenceWords.Of(plan[i]))) return false;
            return true;
        }

        private static bool Lands(string words)
        {
            if (SequenceWords.Landing.IsMatch(words)) return true;
            return (SequenceWords.Deorbit.IsMatch(words) || SequenceWords.Reentry.IsMatch(words)) && !SequenceWords.ReturnHome.IsMatch(words);
        }
    }
}
