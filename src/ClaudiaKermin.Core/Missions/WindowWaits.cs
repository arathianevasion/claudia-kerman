// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// A wait for a Transfer Window longer than one Kerbin day is a Due Time (#616, the ADR 0004 amendment): the craft waits on
    /// the pad and the first Maneuver Sequence carries a Due Time at the window less the launch lead, so the player can fly other
    /// Missions meanwhile. A shorter wait stays an in-script warp. The window is #458's (<see cref="TransferWindows"/>), picked by
    /// the Mission Setting Time versus delta-v as the Mission request's plan_transfer rows pick it.
    /// </summary>
    internal static class WindowWaits
    {
        /// <summary>The longest window wait the script warps through itself, s: one Kerbin day. Fixed, not a Mission Setting.</summary>
        public const double LongWaitS = 6 * 3600;

        /// <summary>
        /// The launch lead, s: how long before the window's ejection burn the launch starts. An ascent to a low parking orbit
        /// (about 10 minutes) and up to one low Kerbin orbit (about 35 minutes) to reach the ejection point, with room to spare.
        /// </summary>
        public const double LaunchLeadS = 3600;

        /// <summary>The first planet the Mission Goal names that the craft's planet transfers to; null for none.</summary>
        public static SnapshotBody Planet(GameSnapshot snapshot, string goal)
        {
            if (snapshot == null) return null;
            return MissionPrompts.GoalBodies(snapshot, goal).FirstOrDefault(b => TransferWindows.IsPlanetTransfer(snapshot, b.Name));
        }

        /// <summary>The window to <paramref name="planet"/> from <paramref name="snapshot"/>'s UT, picked by the Mission Setting; null when the search found none.</summary>
        public static TransferWindow Window(GameSnapshot snapshot, SnapshotBody planet, TimeVersusDeltaV setting, int marginPercent)
        {
            if (snapshot == null || planet == null) return null;
            var found = TransferWindows.Find(snapshot, planet.Name, MissionPrompts.ArrivalPe(planet), true, marginPercent);
            return found.Error != null ? null : TransferWindows.Pick(found, setting);
        }

        /// <summary>The Due Time for a window leaving at <paramref name="departureUt"/>: the window less the launch lead, when that is more than <see cref="LongWaitS"/> from <paramref name="now"/>; otherwise null (the script warps there).</summary>
        public static double? DueTime(double departureUt, double now)
        {
            var due = departureUt - LaunchLeadS;
            return due - now > LongWaitS ? due : (double?)null;
        }

        /// <summary>The window leaving at <paramref name="departureUt"/> is missed: a launch now would reach orbit after it.</summary>
        public static bool Missed(double departureUt, double now)
        {
            return now > departureUt - LaunchLeadS;
        }

        /// <summary>
        /// The next window fits the Craft Check budget: the Mission Plan's budget sum, with the next window's total in place of the
        /// planned one's, has the Craft Check's margin under the craft's delta-v (the budget sum only: the Craft Check is not run
        /// again). With no delta-v figure for the craft, the next window fits only if it costs no more than the planned one.
        /// </summary>
        public static bool Fits(double budgetSum, double plannedDv, double nextDv, double? craftDeltaV, int marginPercent)
        {
            if (!craftDeltaV.HasValue) return nextDv <= plannedDv;
            var bar = MarginBar.Compute(craftDeltaV, budgetSum - plannedDv + nextDv, marginPercent);
            return bar == null || bar.Level == MarginLevel.Enough;
        }

        /// <summary>"the Duna window", what a Due Time for a window waits for.</summary>
        public static string WaitsFor(string planet)
        {
            return "the " + planet + " window";
        }
    }
}
