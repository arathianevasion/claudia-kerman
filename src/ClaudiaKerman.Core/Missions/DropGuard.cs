// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #825: refuses a drop that takes delta-v the rest of the Mission Plan still needs. A Building Block's drop (drop_parts, deorbit's
    /// and reentry's separate) asks through <c>ADDONS:CLAUDIA:DROPCHECK</c> with the delta-v the craft has before it and would have after.
    /// <para>
    /// <b>The rule.</b> Refused when the drop takes more than <see cref="Allowance"/> m/s (an empty tank takes nothing, so it always goes,
    /// even on a craft already short) and what is left is under what the plan still needs: <see cref="PlanLegs.Need"/> of the sequences
    /// after the one in hand, the Margin Check's per-leg figure with no margin. No Mission Plan, no check. Only the sequences of the
    /// vessel the drop is on count: a Child Vessel's own sequences are not the main craft's, and the main craft's are not the child's.
    /// The player's own staging is never gated, and no option passes it; Claudia revises the Mission Plan first, which lowers the need.
    /// </para>
    /// </summary>
    public static class DropGuard
    {
        /// <summary>m/s a drop may take with no check: the two estimates differ by noise, and an empty tank gains.</summary>
        public const double Allowance = 10;

        /// <summary>
        /// The verdict for a drop. <paramref name="plan"/> is the whole Mission Plan (null: none) and <paramref name="step"/> the index of
        /// the sequence in hand (-1 before the first); <paramref name="before"/> and <paramref name="left"/> the craft's delta-v, m/s,
        /// before the drop and after it.
        /// <paramref name="spent"/> is delta-v a burn still to fly will use (#835): it comes off what is left, so a drop before a burn is judged
        /// by what remains after both. It is the estimate of whole-craft stock figures, good to about <see cref="Allowance"/>.
        /// </summary>
        public static DropVerdict Check(GameSnapshot snapshot, IList<PlannedSequence> plan, int step, double before, double left, double spent = 0)
        {
            var taken = Math.Max(0, before - left);
            if (snapshot == null || plan == null || taken <= Allowance) return new DropVerdict { Taken = taken };
            left = Math.Max(0, left - Math.Max(0, spent));
            var rest = plan.Skip(Math.Max(0, step + 1)).Where(s => IsOnThisVessel(plan, snapshot.Craft.Name, s)).ToList();
            var need = PlanLegs.Need(PlanLegs.Judge(snapshot, rest, ""));
            if (left >= need) return new DropVerdict { Taken = taken, Need = need };
            return new DropVerdict
            {
                Refused = true,
                Taken = taken,
                Need = need,
                Reason = "this drop would take " + Whole(taken) + " m/s and leave " + Whole(left) + "; the plan still needs " + Whole(need)
                    + " m/s: revise the Mission Plan first",
            };
        }

        /// <summary>
        /// The sequence belongs to the vessel the drop is on. A craft some sequence names as its Child Vessel is that child: it has only
        /// those sequences; any other craft is the Mission's, with the sequences that name no vessel (or "craft").
        /// </summary>
        private static bool IsOnThisVessel(IList<PlannedSequence> plan, string craft, PlannedSequence sequence)
        {
            var own = (sequence.Vessel ?? "").Trim();
            var isChild = plan.Any(s => string.Equals((s.Vessel ?? "").Trim(), craft, StringComparison.OrdinalIgnoreCase) && craft.Length > 0
                && !string.Equals(craft, "craft", StringComparison.OrdinalIgnoreCase));
            if (isChild) return string.Equals(own, craft, StringComparison.OrdinalIgnoreCase);
            return own.Length == 0 || string.Equals(own, "craft", StringComparison.OrdinalIgnoreCase) || string.Equals(own, craft, StringComparison.OrdinalIgnoreCase);
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What <see cref="DropGuard.Check"/> found.</summary>
    public sealed class DropVerdict
    {
        public bool Refused;
        /// <summary>m/s the drop takes (0 when it gains).</summary>
        public double Taken;
        /// <summary>m/s the rest of the plan needs; 0 when not worked out.</summary>
        public double Need;
        /// <summary>Why it is refused; "" otherwise.</summary>
        public string Reason = "";
    }
}
