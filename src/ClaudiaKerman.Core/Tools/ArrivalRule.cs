// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// #758: whether a transfer to <c>target</c> is priced with a capture burn when the caller doesn't say. A trip to the home body
    /// ends in reentry (the reentry block's entry periapsis, inside the air), so it needs no capture burn: the arrival is an entry,
    /// <c>arrival_dv</c> 0. Anywhere else the capture is priced. An explicit <c>capture</c> always wins. One rule for plan_transfer,
    /// the per-turn transfer rows and the Delta-v Map's return legs (#757).
    /// </summary>
    internal static class ArrivalRule
    {
        /// <summary>The home body when <paramref name="target"/> is it and it has air, else null.</summary>
        private static SnapshotBody EntryBody(GameSnapshot snapshot, string target)
        {
            if (snapshot == null || string.IsNullOrEmpty(target)) return null;
            return snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.Name, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase) && b.AtmosphereHeight > 0);
        }

        /// <summary>True when a trip to <paramref name="target"/> ends in reentry, so no capture burn is flown.</summary>
        public static bool EndsInReentry(GameSnapshot snapshot, string target)
        {
            return EntryBody(snapshot, target) != null;
        }

        /// <summary>Whether to price the capture: the caller's <paramref name="explicitCapture"/>, else yes unless the trip ends in reentry.</summary>
        public static bool Capture(GameSnapshot snapshot, string target, bool? explicitCapture)
        {
            return explicitCapture ?? !EndsInReentry(snapshot, target);
        }

        /// <summary>The arrival note for an entry priced without a capture, or null (a capture is priced, or the target has no entry).</summary>
        public static string Note(GameSnapshot snapshot, string target, bool capture)
        {
            var body = EntryBody(snapshot, target);
            if (body == null || capture) return null;
            var km = Math.Round(DeorbitBurn.EntryPeriapsisShare * body.AtmosphereHeight / 1000);
            return "arrival: entry at " + km.ToString("0", CultureInfo.InvariantCulture) + " km, no capture burn";
        }
    }
}
