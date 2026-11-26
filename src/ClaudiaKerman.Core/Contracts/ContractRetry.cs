// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>A Contract Requirement missed and still open at the body the craft is at (#1306), and the Mission Plan step that serves it.</summary>
    public sealed class OpenRequirement
    {
        public ChosenRequirement Requirement;
        /// <summary>1-based Mission Plan step that serves it.</summary>
        public int Step;
        /// <summary>How many Debriefs have shown it not met.</summary>
        public int Misses;
    }

    /// <summary>
    /// #1306, the player's rulings (grill, 2026-11-23): a Requirement open at its body is retried before any leg leaves it, as a Sequence
    /// Plan flown under Auto Mode like any other, until the Margin check, the Spending Cap or the Contract's deadline stops it; then Claudia
    /// proposes dropping the Contract in a revised Mission Plan, for the player's Approval. From the second miss the chat names it.
    /// </summary>
    public static class ContractRetry
    {
        /// <summary>The Sequence Plan request's ask, which a test finds it by.</summary>
        public const string RetryWords = "Retry it before any leg that leaves the body";

        /// <summary>The revised Mission Plan request's ask when a stop says no more retries.</summary>
        public const string StopWords = "Stop retrying";

        /// <summary>The Sequence Plan request's line: each open Requirement, and the retry asked for.</summary>
        public static string RetryLine(IList<OpenRequirement> open, string body)
        {
            return "Contract Requirements missed and still open at " + body + " (the mod's reading of the game, data): " + Named(open) + ". "
                + RetryWords + " (" + body + "): this Sequence Plan flies the retry, with plan_step "
                + string.Join(" or ", open.Select(o => o.Step.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray())
                + " (the step that serves it). It is approved like any other Sequence Plan, under Auto Mode too.\n";
        }

        /// <summary>The revised Mission Plan request's text when <paramref name="why"/> stops the retries.</summary>
        public static string StopLine(IList<OpenRequirement> open, string body, string why)
        {
            return "Contract Requirements missed and still open at " + body + " (the mod's reading of the game, data): " + Named(open) + ". "
                + StopWords + ": " + why + ". Propose dropping their Contracts instead, with the reason: a revised Mission Plan with no leg that serves them. "
                + "It waits for the player's Approval; under Auto Mode it is approved at once and the drop is posted with its reason.\n";
        }

        /// <summary>The chat notice from the second miss of the same Requirement; Auto Mode carries on.</summary>
        public static string MissedAgain(RequirementReading reading, int misses)
        {
            return "Contract Requirement missed again (" + misses.ToString(CultureInfo.InvariantCulture) + " times): \"" + reading.Requirement + "\" ("
                + reading.Contract + "). The flight carries on.";
        }

        private static string Named(IEnumerable<OpenRequirement> open)
        {
            return string.Join("; ", open.Select(o => "\"" + o.Requirement.Words + "\" (" + o.Requirement.Title + "), missed "
                + o.Misses.ToString(CultureInfo.InvariantCulture) + (o.Misses == 1 ? " time" : " times")).ToArray());
        }
    }
}
