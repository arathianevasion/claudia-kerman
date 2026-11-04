// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.ScriptCheck
{
    /// <summary>
    /// A warn-only lint (#557): a <c>reentry(...)</c> reached after <c>return</c>, <c>transfer_to</c> or <c>coast</c> in a Mission
    /// Script that says nothing about what leaves before the atmosphere: no <c>drop_parts</c>, and no <c>separate</c> (deorbit's or
    /// reentry's) other than <c>"none"</c>. Three Missions (b5f3, 0de6, 44e9) flew reentry with the service module on that way.
    /// A second rule (#666), read per call: a <c>deorbit(...)</c> with no <c>separate</c> (or <c>"none"</c>) followed by a
    /// <c>reentry(...)</c> whose separate is not <c>"none"</c>, so deorbit aimed for the whole stack and reentry drops part of it.
    /// It reads the Mission Script only, never the Core Library bundled after it, ignoring comments; it never refuses a script.
    /// </summary>
    public static class StackDropLint
    {
        private static readonly Regex Leg = new Regex(@"\b(return|transfer_to|coast)\s*\(\s*(\)|lexicon\b|lex\b|[A-Za-z_]\w*\s*\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Reentry = new Regex(@"\breentry\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Deorbit = new Regex(@"\bdeorbit\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DropParts = new Regex(@"\bdrop_parts\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Separate = new Regex("\"separate\"\\s*,\\s*(\"(?<text>[^\"]*)\"|(?<other>[^,)\\s]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The warnings, or none, for <paramref name="script"/>: a Mission Script, or a bundle whose library lines it skips.</summary>
        public static IList<string> Warnings(string script)
        {
            var warnings = new List<string>();
            var code = CoreLibrary.MissionScript(script);
            AfterLeg(code, warnings);
            DeorbitThenDrop(code, warnings);
            return warnings;
        }

        private static void AfterLeg(string code, List<string> warnings)
        {
            if (DropParts.IsMatch(code)) return;
            foreach (Match s in Separate.Matches(code))
                if (!s.Groups["text"].Success || !s.Groups["text"].Value.Equals("none", System.StringComparison.OrdinalIgnoreCase)) return;
            var leg = Leg.Match(code);
            if (!leg.Success) return;
            var reentry = Reentry.Match(code, leg.Index + leg.Length);
            if (!reentry.Success) return;
            warnings.Add("line " + LineOf(code, reentry.Index) + ": reentry after " + leg.Groups[1].Value.ToLowerInvariant()
                + " with no drop named; say what leaves before the atmosphere with reentry's separate (\"auto\", its default, drops the stack below the heat shield).");
        }

        // The #666 rule: each call's own separate is read, so a reentry's implicit "auto" does not silence it.
        private static void DeorbitThenDrop(string code, List<string> warnings)
        {
            foreach (Match deorbit in Deorbit.Matches(code))
            {
                var after = deorbit.Index + deorbit.Length;
                if (!SeparateIsNone(Args(code, after))) continue;
                var reentry = Reentry.Match(code, after);
                if (!reentry.Success || SeparateIsNone(Args(code, reentry.Index + reentry.Length), absentIsNone: false)) continue;
                warnings.Add("line " + LineOf(code, deorbit.Index) + ": deorbit aims for the whole stack, but reentry's separate drops the stack below the heat shield, so the craft that re-enters is not the one aimed. "
                    + "Give deorbit the same separate (the stage number or part names reentry drops), or give reentry separate \"none\".");
                return;
            }
        }

        private static int LineOf(string code, int index)
        {
            var line = 1;
            for (var i = 0; i < index; i++)
                if (code[i] == '\n') line++;
            return line;
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
            return code.Substring(start, i - start);
        }

        // True when the arguments give separate "none", or no separate where that means none (deorbit's default; reentry's is "auto").
        private static bool SeparateIsNone(string args, bool absentIsNone = true)
        {
            var s = Separate.Match(args);
            return !s.Success ? absentIsNone : (s.Groups["text"].Success && s.Groups["text"].Value.Equals("none", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
