// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.ScriptCheck
{
    /// <summary>
    /// A warn-only lint (#557): a <c>reentry(...)</c> reached after <c>return</c>, <c>transfer_to</c> or <c>coast</c> in a Mission
    /// Script that says nothing about what leaves before the atmosphere: no <c>drop_parts</c>, and no <c>separate</c> (deorbit's or
    /// reentry's) other than <c>"none"</c>. Three Missions (b5f3, 0de6, 44e9) flew reentry with the service module on that way. It
    /// reads the Mission Script only, never the Core Library bundled after it, ignoring comments; it never refuses a script.
    /// </summary>
    public static class StackDropLint
    {
        private static readonly Regex Leg = new Regex(@"\b(return|transfer_to|coast)\s*\(\s*(\)|lexicon\b|lex\b|[A-Za-z_]\w*\s*\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Reentry = new Regex(@"\breentry\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DropParts = new Regex(@"\bdrop_parts\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Separate = new Regex("\"separate\"\\s*,\\s*(\"(?<text>[^\"]*)\"|(?<other>[^,)\\s]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The warning, or none, for <paramref name="script"/>: a Mission Script, or a bundle whose library lines it skips.</summary>
        public static IList<string> Warnings(string script)
        {
            var warnings = new List<string>();
            var code = CoreLibrary.MissionScript(script);
            if (DropParts.IsMatch(code)) return warnings;
            foreach (Match s in Separate.Matches(code))
                if (!s.Groups["text"].Success || !s.Groups["text"].Value.Equals("none", System.StringComparison.OrdinalIgnoreCase)) return warnings;
            var leg = Leg.Match(code);
            if (!leg.Success) return warnings;
            var reentry = Reentry.Match(code, leg.Index + leg.Length);
            if (!reentry.Success) return warnings;
            var line = 1;
            for (var i = 0; i < reentry.Index; i++)
                if (code[i] == '\n') line++;
            warnings.Add("line " + line + ": reentry after " + leg.Groups[1].Value.ToLowerInvariant()
                + " with no drop named; say what leaves before the atmosphere with reentry's separate (\"auto\", its default, drops the stack below the heat shield).");
            return warnings;
        }
    }
}
