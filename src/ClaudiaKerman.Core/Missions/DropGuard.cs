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
            var spent = burn == null ? 0 : burn.Spent;
            if (burn != null) left = AfterBurn(left, burn);
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
        /// The delta-v the kept craft has once <paramref name="burn"/> is flown, from <paramref name="left"/>, what it has now (#835).
        /// <para>
        /// <b>Who pays.</b> When every burning engine goes with the drop, the dropped tanks pay first and cost the kept craft nothing: stock's stack
        /// flow drains the higher stage first and a stock decoupler's crossfeed is off by default, so such a stage burns itself out before the kept
        /// tanks are touched. When a burning engine stays it may be cut off from the dropped tanks, so the kept tanks pay. What the dropped tanks
        /// can't cover comes from the kept tanks at the kept engines' isp (read from <paramref name="left"/>), with the dry dropped parts still aboard,
        /// which costs the lighter kept craft more than the burn's own figure.
        /// </para>
        /// <para>
        /// <b>Accuracy.</b> The same rocket equation and masses as the check after the burn, so with the right payer the two differ by what the flight
        /// changes: the burn flown against the one planned, and a stage or dry tank let go mid-burn. Who pays is a rule, not a measurement. It errs
        /// towards a refusal for a kept engine on crossfed drop tanks, for a kept and a dropped engine burning together, and for a stage let go
        /// mid-burn (#825: a refusal costs one plan revision, where a wrong drop can lose the crew). It errs the other way, and the check after the
        /// burn remains, for a dropped tank the burning engine can't draw on: cut off from it, or holding only one of its propellants.
        /// Without the masses the burn comes off flat, as nothing better is known; Kerboscript always gives them.
        /// </para>
        /// </summary>
        public static double AfterBurn(double left, DropBurn burn)
        {
            var spent = Math.Max(0, burn.Spent);
            if (spent <= 0) return left;
            if (burn.Whole <= 0 || burn.Kept <= 0) return Math.Max(0, left - spent);
            var massAtKeptTanks = burn.Whole;
            var gone = burn.BurnersKept > 0 || burn.Isp <= 0 ? 0 : Math.Min(Math.Max(0, burn.GonePropellant), burn.Whole);
            if (gone > 0)
            {
                if (gone >= burn.Whole) return left;
                massAtKeptTanks = burn.Whole - gone;
                spent -= burn.Isp * StandardGravity * Math.Log(burn.Whole / massAtKeptTanks);
                if (spent <= 0) return left;
            }
            var dry = burn.Kept - burn.KeptPropellant;
            if (left <= 0 || dry <= 0 || burn.KeptPropellant <= 0) return 0;
            var keptExhaust = left / Math.Log(burn.Kept / dry);
            var fromKept = massAtKeptTanks * (1 - Math.Exp(-spent / keptExhaust));
            if (fromKept >= burn.KeptPropellant) return 0;
            return keptExhaust * Math.Log((burn.Kept - fromKept) / dry);
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
        /// <summary>Tonnes of the propellants the burning engines use, in the parts that go.</summary>
        public double GonePropellant;
        /// <summary>How many burning engines stay. Any at all and <see cref="DropGuard.AfterBurn"/> has the kept tanks pay.</summary>
        public double BurnersKept;
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
