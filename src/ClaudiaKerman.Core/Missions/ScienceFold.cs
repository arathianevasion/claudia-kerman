// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #983: a Mission Plan step that is only science (0 m/s, no wait, no action of its own) right before a sequence of the same vessel that
    /// starts where the craft already is gets flown as the first lines of that sequence's script: a <c>science</c> call at the head of an
    /// ascent runs landed, before lift-off. Its own Sequence Plan, Approval and Debrief would cost three model rounds and a player click.
    /// The mod says so in one line on the Sequence Plan request when that step is the next to fly; the Mission Plan is left as approved,
    /// and the folded sequence names the later step as its plan_step, so the science step counts as passed (#977's numbering holds).
    /// </summary>
    internal static class ScienceFold
    {
        private static readonly Regex Science = new Regex(@"\b(science|experiments?|crew ?reports?|eva ?reports?|surface ?samples?)\b", RegexOptions.IgnoreCase);

        /// <summary>Science that waits for a place or a time ("next pass over the Highlands") is no head of a script.</summary>
        private static readonly Regex Waits = new Regex(@"\b(coast(s|ing)?|wait(s|ing)?|warp(s|ing)?|pass(es)?|until)\b", RegexOptions.IgnoreCase);

        /// <summary>The first step a revised plan doesn't list as flown (<see cref="PlanLegs.IsFlown"/>), 1-based; 0 when every step is.</summary>
        public static int FirstToFly(IList<PlannedSequence> legs)
        {
            for (var i = 0; i < legs.Count; i++)
                if (!PlanLegs.IsFlown(legs[i])) return i + 1;
            return 0;
        }

        /// <summary>The line telling Claudia to fold 1-based <paramref name="step"/> into the step after it, or "" when it isn't one to fold.</summary>
        public static string Line(IList<PlannedSequence> legs, int step)
        {
            if (legs == null || step < 1 || step >= legs.Count) return "";
            var science = legs[step - 1];
            var next = legs[step];
            if (!ScienceOnly(science) || next.Manual || next.DueTimeUt.HasValue || PlanLegs.IsFlown(next) || ScienceOnly(next)
                || !string.Equals(Vessel(science), Vessel(next), StringComparison.OrdinalIgnoreCase)) return "";
            var n = step.ToString(CultureInfo.InvariantCulture);
            var into = (step + 1).ToString(CultureInfo.InvariantCulture);
            return "Step " + n + " \"" + science.Name + "\" is science only, 0 m/s, where step " + into + " \"" + next.Name + "\" starts: fly it as the first lines of step "
                + into + "'s script (plan_step " + into + "), not as a sequence of its own.";
        }

        private static bool ScienceOnly(PlannedSequence leg)
        {
            var words = SequenceWords.Of(leg);
            return leg.DeltaV <= 0 && !leg.Manual && !leg.DueTimeUt.HasValue && Science.IsMatch(words) && !Waits.IsMatch(words + " " + leg.Timing)
                && !Acts(leg.Name);
        }

        /// <summary>Where the craft already is ("Crew report (landed)", "after touchdown"): no action of the step's own.</summary>
        private static readonly Regex AlreadyThere = new Regex(@"\b(landed|after (the )?(landing|touch ?down))\b", RegexOptions.IgnoreCase);

        /// <summary>The name says the sequence flies somewhere (the intent may well say "landed pod", so it isn't read).</summary>
        private static bool Acts(string name)
        {
            var words = AlreadyThere.Replace(name, "");
            return SequenceWords.Acts(words) || SequenceWords.Docking.IsMatch(words);
        }

        private static string Vessel(PlannedSequence leg)
        {
            var vessel = (leg.Vessel ?? "").Trim();
            return vessel.Length == 0 ? "craft" : vessel;
        }
    }
}
