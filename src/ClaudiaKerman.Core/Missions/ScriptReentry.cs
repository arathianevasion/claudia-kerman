// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The running Mission Script's <c>reentry</c> call (#970), for <see cref="EntryCraft"/>: <c>deorbit</c> and <c>return</c> aim before <c>reentry</c>
    /// runs, so they can't see its options. Flight m20261109-200745-96f6 aimed for the craft without its stack while its <c>reentry</c> kept it
    /// (separate "none"). Read from the Mission Script only, comments and strings aside; any call but every one giving separate "none" is
    /// <c>reentry</c>'s default, "auto", as before, and so is a "none" after the script separates parts itself since deorbit or return. <c>jettison_stage</c> is read the same way, "none" or "auto".
    /// </summary>
    public sealed class ScriptReentry
    {
        /// <summary>No Mission Script, or none whose <c>reentry</c> keeps its stack: the craft as <c>reentry</c>'s defaults fly it.</summary>
        public static readonly ScriptReentry Auto = new ScriptReentry(false, "auto");

        private static readonly Regex Call = new Regex(@"\breentry\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Every <c>reentry</c> call gives separate "none": the stack below the Entry Shield stays on.</summary>
        public readonly bool KeepsStack;
        /// <summary>"none" when every <c>reentry</c> call gives jettison_stage "none"; "auto" otherwise.</summary>
        public readonly string JettisonStage;

        private ScriptReentry(bool keepsStack, string jettisonStage)
        {
            KeepsStack = keepsStack;
            JettisonStage = jettisonStage;
        }

        /// <summary>What the <c>reentry</c> calls in <paramref name="script"/> (a Mission Script, or a bundle whose library it skips) give.</summary>
        public static ScriptReentry Of(string script)
        {
            var code = CoreLibrary.MissionScript(script);
            var masked = CoreLibrary.MissionScript(script, true);
            var separates = new List<string>();
            var jettisons = new List<string>();
            foreach (Match call in Call.Matches(masked))
            {
                var args = Args(code, call.Index + call.Length);
                separates.Add(Sheds(masked, call.Index) ? "auto" : Word(args, "separate"));
                jettisons.Add(Word(args, "jettison_stage"));
            }
            return new ScriptReentry(All(separates), All(jettisons) ? "none" : "auto");
        }

        private static readonly Regex Anchor = new Regex(@"\b(deorbit|return)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Review of #970: a script can shed the stack itself between the blocks, then ask reentry for separate "none" with nothing left
        // to drop. Anything that can separate parts between the last deorbit/return call and this reentry (from the script's start
        // with none) reads as "auto": a stage, a decoupler event, an action group or abort, or a block that can stage or drop
        // (drop_parts, and every block that burns, with its drop_empty).
        private static readonly Regex Separation = new Regex(
            @"(?<![\w:])stage\s*\.(?!\w)|decouple|\b(doevent|doaction|abort|ag\d+)\b|\b(" + string.Join("|",
                CoreLibrary.Blocks.Select(b => b.Name).Where(n => !Array.Exists(NeverSeparates, s => s == n)).ToArray()) + @")\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string[] NeverSeparates
        {
            get { return new[] { "coast", "science", "deorbit", "return", "reentry" }; }
        }

        // Read with comments and strings blanked: a doevent("decouple") is caught by its doevent.
        private static bool Sheds(string masked, int reentryAt)
        {
            var start = 0;
            foreach (Match anchor in Anchor.Matches(masked.Substring(0, reentryAt)))
            {
                var open = anchor.Index + anchor.Length;
                start = open + Args(masked, open).Length;
            }
            return start < reentryAt && Separation.IsMatch(masked.Substring(start, reentryAt - start));
        }

        // Every call gives "none" (and there is at least one).
        private static bool All(List<string> words)
        {
            return words.Count > 0 && words.TrueForAll(w => string.Equals(w, "none", StringComparison.OrdinalIgnoreCase));
        }

        // The string an option is given, or "" when it is absent or not a string.
        private static string Word(string args, string key)
        {
            var match = Regex.Match(args, "\"" + key + "\"\\s*,\\s*\"(?<text>[^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["text"].Value : "";
        }

        // A call's arguments: from just after its "(" to the matching ")", skipping string literals.
        private static string Args(string code, int start)
        {
            int depth = 1, i = start;
            for (; i < code.Length && depth > 0; i++)
            {
                if (code[i] == '"')
                {
                    var close = code.IndexOf('"', i + 1);
                    if (close < 0) break;
                    i = close;
                }
                else if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
            }
            return code.Substring(start, Math.Max(0, Math.Min(i, code.Length) - start));
        }
    }
}
