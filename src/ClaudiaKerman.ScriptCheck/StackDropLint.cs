// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.ScriptCheck
{
    /// <summary>
    /// A warn-only lint (#666, fitted to ADR 0018 by #823): a <c>deorbit(...)</c> with no <c>separate</c> (or <c>"none"</c>) followed by
    /// a <c>reentry(...)</c> whose separate is not <c>"none"</c>. On the Mission's Last Entry, reentry then drops what deorbit aimed with;
    /// on an earlier Entry it may drop nothing, and a script can't tell which it is, so the warning says so. #557's rule, a reentry with no
    /// drop named, is gone: <c>"auto"</c> decides from the heat shields, so naming a drop is no longer wanted.
    /// It reads the Mission Script only, never the Core Library bundled after it, ignoring comments; it never refuses a script.
    /// </summary>
    public static class StackDropLint
    {
        private static readonly Regex Reentry = new Regex(@"\breentry\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Deorbit = new Regex(@"\bdeorbit\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Separate = new Regex("\"separate\"\\s*,\\s*(\"(?<text>[^\"]*)\"|(?<other>[^,)\\s]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The warnings, or none, for <paramref name="script"/>: a Mission Script, or a bundle whose library lines it skips.</summary>
        public static IList<string> Warnings(string script)
        {
            var warnings = new List<string>();
            var code = CoreLibrary.MissionScript(script);
            DeorbitThenDrop(code, warnings);
            return warnings;
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
                warnings.Add("line " + LineOf(code, deorbit.Index) + ": deorbit aims for the whole stack. If this is the Mission's Last Entry, reentry drops what is below its heat shield, so the craft that re-enters is not the one aimed: "
                    + "there, give deorbit a separate for the same parts. With burns still to come, leave deorbit's separate off: the stack is needed.");
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
