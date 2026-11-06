// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Whether a Mission Goal plans for an Engineer to repack the chutes (a Kerbal on EVA does it, in stock). The Margin Check
    /// (#629) and the Craft Check rules (#641) both ask. A negation word within two words before "engineer" or "repack"
    /// ("no engineer", "without a repack", "can't repack") cancels that word; there is no wider parsing of the goal.
    /// </summary>
    internal static class RepackPlan
    {
        private static readonly Regex Negated = new Regex(
            @"\b(no|not|never|without|cannot|can[’']?t|won[’']?t|wont|don[’']?t|doesn[’']?t|isn[’']?t|nor)\b([ \t]+\w+){0,2}?[ \t]+(engineers?|repack(s|ed|ing)?)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex Plan = new Regex(
            @"(?=.*\bengineer)(?=.*\brepack(s|ed|ing)?\b)(?=.*\bchutes?\b|.*\bparachutes?\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public static bool IsPlanned(string goal)
        {
            return Plan.IsMatch(Negated.Replace(goal ?? "", " "));
        }
    }
}
