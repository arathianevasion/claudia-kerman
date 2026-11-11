// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #979: what may leave with an empty tank in an "auto" drop, besides trim (the player's rule, 2026-11-10). The block judges each
    /// decoupler's parts against these kinds (<c>claudia_part_kind</c>); engines, parachutes, crew and the rest always keep it aboard.
    /// <para>
    /// <b>A heat shield a radial decoupler takes.</b> If the parts it shields go with it, it doesn't matter. The block holds a shield a stack
    /// decoupler would take as it holds a kerbal: that one may be the crew part's Entry Shield (pod, decoupler, shield, #940/#948), and Core
    /// never counts a radial shield as one (#843).
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
        /// reads them: land or touch down, a deorbit, a reentry, or descend / descent, manual legs too. The Kerbin return (a return home whose
        /// words don't say it lands or touches down) is not one: it comes down on the crew part's chutes, as this flight's "Return to KSC" did.
        /// Unless it also names another stock body, not as where it comes from (the player's ruling: "Deorbit to the Mun crater, then return
        /// to Kerbin" lands on the Mun; "Return from the Mun to Kerbin" doesn't).
        /// </summary>
        public static bool LegsFree(IList<PlannedSequence> plan, int step)
        {
            if (plan == null) return false;
            for (var i = Math.Max(0, step); i < plan.Count; i++)
                if (Lands(SequenceWords.Of(plan[i]))) return false;
            return true;
        }

        /// <summary>The stock bodies a craft can come down on, Kerbin aside (Core reads the plan's words only, no Game Snapshot, twice a second).</summary>
        private static readonly Regex AwayBody = new Regex(@"\b(Moho|Eve|Gilly|Mun|Minmus|Duna|Ike|Dres|Laythe|Vall|Tylo|Bop|Pol|Eeloo)\b", RegexOptions.IgnoreCase);

        private static bool Lands(string words)
        {
            if (SequenceWords.Landing.IsMatch(words)) return true;
            if (!SequenceWords.Deorbit.IsMatch(words) && !SequenceWords.Reentry.IsMatch(words) && !SequenceWords.Descent.IsMatch(words)) return false;
            return !SequenceWords.ReturnHome.IsMatch(words)
                || AwayBody.Matches(words).Cast<Match>().Any(m => !SequenceWords.Departs(words.Substring(0, m.Index)));
        }
    }
}
