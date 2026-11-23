// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1262: the words KSP.log gets about the Contract check card's life, so a flight can be diagnosed from the logs: the block when the card
    /// posts, a line per tick or swap, a line for each way out of the card. Plain text; the adapter writes it under the mod's prefix.
    /// </summary>
    public static class ContractCardLog
    {
        /// <summary>The card as posted: headline figures, then each accepted Contract the card judged, its GUID, title, verdict, reason and tick state.</summary>
        public static string Posted(ContractCheck check, IList<string> ticked)
        {
            var text = new StringBuilder("Contract check card posted: " + check.Headline(ticked.Count)
                + "\n  craft delta-v " + Whole(check.CraftDeltaV) + " m/s, route delta-v " + Whole(check.RouteDeltaV) + (check.RouteIsEstimate ? " m/s (estimate)" : " m/s")
                + ", reserve " + Whole(check.Reserve) + " m/s (" + check.ReservePercent + "%)"
                + (check.Words.Length > 0 ? ", with the player's words" : ", no words typed"));
            foreach (var row in check.Fitting)
                text.Append("\n  " + row.Guid + " | " + row.Title + " | row | " + (row.Ticked ? "ticked" : "unticked, tickable")
                    + " | " + row.Phase + (row.Note.Length > 0 ? "; " + row.Note : "") + (row.Swap != null ? "; swap in offered" : ""));
            foreach (var left in check.NotThisFlight)
                text.Append("\n  " + left.Guid + " | " + left.Title + " | Not this flight (" + left.Kind + ") | "
                    + (left.Ticked ? "ticked" : left.Tickable ? "unticked, tickable" : "unticked, not tickable") + " | " + left.Reason + (left.Swap != null ? "; swap in offered" : ""));
            return text.ToString();
        }

        /// <summary>A tick, an untick or a swap: the Contract, on or off, the set it leaves and whether "Plan these" can be pressed.</summary>
        public static string Edited(string what, string guid, bool on, IList<string> ticked, bool canPlan, string shortfall)
        {
            return what + " " + guid + ": " + (on ? "on" : "off") + "; ticked now [" + string.Join(", ", ticked.ToArray()) + "]; "
                + (canPlan ? "Plan these can be pressed" : "Plan these can't be pressed" + (shortfall.Length > 0 ? " (" + shortfall + ")" : ticked.Count == 0 ? " (nothing ticked)" : ""));
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
