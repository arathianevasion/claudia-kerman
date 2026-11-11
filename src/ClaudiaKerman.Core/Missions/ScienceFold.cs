// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #983: a Mission Plan step that is only science (0 m/s, no wait, no action of its own) right before an ascent of the same vessel
    /// gets flown as the first lines of the ascent's script: a <c>science</c> call there runs landed, before lift-off. Only an ascent: ahead
    /// of an orbital departure the call would hide the burn point the Due Time and a return's window are read from (BurnPoints.First). Its own Sequence Plan, Approval and Debrief would cost three model rounds and a player click.
    /// The mod says so in one line on the Sequence Plan request when that step is the next to fly; the Mission Plan is left as approved,
    /// and the folded sequence names the later step as its plan_step, so the science step counts as passed (#977's numbering holds).
    /// </summary>
    internal static class ScienceFold
    {
        private static readonly Regex Science = new Regex(@"\b(science|experiments?|crew ?reports?)\b", RegexOptions.IgnoreCase);

        /// <summary>EVA reports and samples are the player's job (science.ks): folded into an ascent they'd be lost at lift-off.</summary>
        private static readonly Regex PlayersJob = new Regex(@"\b(eva|samples?)\b", RegexOptions.IgnoreCase);

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
            if (!ScienceOnly(science) || next.Manual || next.DueTimeUt.HasValue || PlanLegs.IsFlown(next) || !Ascends(next)
                || !string.Equals(Vessel(science), Vessel(next), StringComparison.OrdinalIgnoreCase)) return "";
            var n = step.ToString(CultureInfo.InvariantCulture);
            var into = (step + 1).ToString(CultureInfo.InvariantCulture);
            return "Step " + n + " \"" + science.Name + "\" is science only, 0 m/s, where step " + into + " \"" + next.Name + "\" starts: fly it as the first lines of step "
                + into + "'s script (plan_step " + into + "), not as a sequence of its own.";
        }

        private static bool ScienceOnly(PlannedSequence leg)
        {
            var words = SequenceWords.Of(leg);
            return leg.DeltaV <= 0 && !leg.Manual && !leg.DueTimeUt.HasValue && Science.IsMatch(words) && !PlayersJob.IsMatch(words)
                && !Waits.IsMatch(words + " " + leg.Timing) && !Acts(leg.Name);
        }

        private static bool Ascends(PlannedSequence leg)
        {
            return SequenceWords.Ascent.IsMatch(leg.Name) || SequenceWords.Ascent.IsMatch(SequenceWords.ActionWords(leg));
        }

        /// <summary>
        /// The step a Sequence Plan sent as <paramref name="sent"/> flies: the next one when <paramref name="sent"/> is a step to fold and the
        /// script holds more than science (the ascent it rides), else <paramref name="sent"/>.
        /// </summary>
        public static int StepFlown(IList<PlannedSequence> legs, int sent, SequencePlan plan)
        {
            if (plan == null || plan.Manual || Line(legs, sent).Length == 0) return sent;
            return BlockOptionCheck.Calls(plan.Script ?? "").Any(c => c.Block != "science") ? sent + 1 : sent;
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
