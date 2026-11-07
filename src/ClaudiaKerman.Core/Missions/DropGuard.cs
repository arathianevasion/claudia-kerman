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

        private const double StandardGravity = 9.80665;

        /// <summary>
        /// The verdict for a drop. <paramref name="plan"/> is the whole Mission Plan (null: none) and <paramref name="step"/> the index of
        /// the sequence in hand (-1 before the first); <paramref name="before"/> and <paramref name="left"/> the craft's delta-v, m/s,
        /// before the drop and after it.
        /// <paramref name="burn"/> is a burn still to fly before the drop (#835; null: none): the drop is judged by what
        /// <see cref="AfterBurn"/> leaves the kept craft.
        /// </summary>
        public static DropVerdict Check(GameSnapshot snapshot, IList<PlannedSequence> plan, int step, double before, double left, DropBurn burn = null)
        {
            var taken = Math.Max(0, before - left);
            if (snapshot == null || plan == null || taken <= Allowance) return new DropVerdict { Taken = taken };
            var spent = burn == null ? 0 : Math.Max(0, burn.Spent);
            if (spent > 0) left = AfterBurn(left, burn);
            var rest = plan.Skip(Math.Max(0, step + 1)).Where(s => IsOnThisVessel(plan, snapshot.Craft.Name, s)).ToList();
            var need = PlanLegs.Need(PlanLegs.Judge(snapshot, rest, ""));
            if (left >= need) return new DropVerdict { Taken = taken, Need = need };
            return new DropVerdict
            {
                Refused = true,
                Taken = taken,
                Need = need,
                Reason = "this drop would take " + Whole(taken) + " m/s and leave " + Whole(left) + (spent > 0 ? " after the " + Whole(spent) + " m/s burn" : "") + "; the plan still needs " + Whole(need)
                    + " m/s: revise the Mission Plan first",
            };
        }

        /// <summary>
        /// The delta-v the kept craft has once <paramref name="burn"/> is flown, from <paramref name="left"/>, what it has now (#835). The burn
        /// is worked out in tonnes on the whole craft's mass. <see cref="DropBurn.GonePropellant"/> pays first and costs the kept craft nothing;
        /// the rest comes from the kept tanks with the dry dropped parts still aboard, which costs the lighter kept craft more than the burn's own figure.
        /// <para>
        /// <b>Accuracy.</b> The same rocket equation and masses as the check after the burn, so the two differ by what the flight changes: the burn
        /// flown against the one planned, and a stage or dry tank let go mid-burn (which this leaves aboard, so it errs towards a refusal). Who pays
        /// is a rule, not a measurement (<see cref="DropBurn.GonePropellant"/>); when unsure the kept tanks pay and it errs towards a refusal too
        /// (#825: that costs one plan revision, where a wrong drop can lose the crew). Without the isp or masses the burn comes off flat.
        /// </para>
        /// </summary>
        public static double AfterBurn(double left, DropBurn burn)
        {
            var spent = Math.Max(0, burn.Spent);
            if (spent <= 0) return left;
            if (burn.Isp <= 0 || burn.Whole <= 0 || burn.Kept <= 0) return Math.Max(0, left - spent);
            var exhaust = burn.Isp * StandardGravity;
            var afterGone = burn.Whole - Math.Min(Math.Max(0, burn.GonePropellant), burn.Whole);
            if (afterGone <= 0) return left;
            var stillToFly = spent - exhaust * Math.Log(burn.Whole / afterGone);
            if (stillToFly <= 0) return left;
            var fromKept = afterGone * (1 - Math.Exp(-stillToFly / exhaust));
            var dry = burn.Kept - burn.KeptPropellant;
            if (left <= 0 || dry <= 0 || fromKept >= burn.KeptPropellant) return 0;
            return left * Math.Log((burn.Kept - fromKept) / dry) / Math.Log(burn.Kept / dry);
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

    /// <summary>A burn still to fly before a drop (#835), in the figures Kerboscript reads off the craft.</summary>
    public sealed class DropBurn
    {
        /// <summary>m/s the burn will use, on the whole craft.</summary>
        public double Spent;
        /// <summary>Tonnes: the whole craft now.</summary>
        public double Whole;
        /// <summary>Tonnes: the craft once the drop has gone, propellant included.</summary>
        public double Kept;
        /// <summary>Tonnes of propellant in <see cref="Kept"/> that its engines burn.</summary>
        public double KeptPropellant;
        /// <summary>The burning engines' isp, s.</summary>
        public double Isp;
        /// <summary>
        /// Tonnes of the burn's propellant in the parts that go, given only when every burning engine goes with the drop: stock's stack flow
        /// drains the higher stage first and a stock decoupler's crossfeed is off by default, so that stage burns itself out before the kept
        /// tanks are touched. 0 when any burning engine stays, as it may be cut off from the dropped tanks, so the kept tanks pay. That
        /// over-counts for a kept engine on crossfed drop tanks: a refusal, settled by one plan revision.
        /// </summary>
        public double GonePropellant;
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
