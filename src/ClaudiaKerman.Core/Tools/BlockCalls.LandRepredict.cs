// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKerman.Core.Tools
{
    public static partial class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDREPREDICT</c>, for <c>land</c> (#511, #1364, ADR 0014; was land's <c>land_repredict_verdict</c>): what it does once it predicts its
        /// braking again from the real state as braking begins. From <c>planned_clears</c> (the planned Descent Shape still clears the terrain),
        /// <c>best_shape</c> (the cheapest that does; -1 for none), <c>pe</c> (m, periapsis), <c>ground_ahead</c> (m, the highest terrain on the predicted track) and <c>arc</c> (true when land started from a suborbital arc).
        /// Gives "fly" the planned shape, "switch" to the cheapest that clears, or, with none clearing, "hold": don't brake, keeping an orbit whose
        /// periapsis is above the terrain on the track, or "steepest": brake on the steepest shape anyway rather than fall into it. An arc never holds:
        /// it has no orbit to keep, and its plan no predicted track, so its periapsis would read as over the terrain and the craft be left falling (#1364). Pure.
        /// </summary>
        public static string LandRepredict(IDictionary<string, object> input)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (Flag(ask, "planned_clears", false)) return "fly";
            if (Number(ask, "best_shape", -1) > 0) return "switch";
            if (!Flag(ask, "arc", false) && Number(ask, "pe", 0) > Number(ask, "ground_ahead", 0)) return "hold";
            return "steepest";
        }
    }
}
