// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #831: before Approval, a <c>deorbit</c> given no target (or "auto") where the block's own answer (<see cref="HomeLanding.DefaultTarget"/>, behind
    /// <c>ADDONS:CLAUDIA:LANDINGTARGET</c>) is none: the block would refuse it in flight with "target is required". It judges by the body the craft is at,
    /// so a <c>deorbit</c> after a block that leaves it (a trip, or a coast to the next patch) is not judged. Calls are read through <see cref="BlockOptionCheck"/>.
    /// </summary>
    public static class DeorbitTargets
    {
        /// <summary>
        /// The body of the first <c>deorbit</c> call that would be refused for having no target; null when none is, or when the body can't be read.
        /// <paramref name="snapshot"/> carries the Mission Goal and the craft's body (null: no check).
        /// </summary>
        public static string Missing(string script, GameSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(script) || string.IsNullOrEmpty(snapshot.Body)) return null;
            foreach (var call in BlockOptionCheck.Calls(script))
            {
                if (call.Block == "transfer_to" || call.Block == "return" || (call.Block == "coast" && LeavesTheBody(call))) return null;
                if (call.Block != "deorbit") continue;
                var target = call.Option("target");
                if (target == null || (target.Trim() != "\"auto\"")) continue;
                // The block asks for "planet" alone: the options a return adds (target_pe, target_inc, window) are not asked.
                if (HomeLanding.DefaultTarget(snapshot, new LandingTargetAsk { Planet = snapshot.Body }) == HomeLanding.None) return snapshot.Body;
            }
            return null;
        }

        /// <summary>A coast whose until waits for the next patch, or can't be read: the craft may be at another body after it.</summary>
        private static bool LeavesTheBody(BlockCall call)
        {
            var until = call.Option("until");
            return until == null || until.IndexOf("nextpatch", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
