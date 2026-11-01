// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Text;
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
            var code = MissionLines(script ?? "");
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
                + " with nothing dropped first; say what leaves before the atmosphere: reentry's separate (\"auto\" drops the stack below the heat shield) or drop_parts.");
            return warnings;
        }

        /// <summary>The Mission Script's lines (all of it when it is not a bundle), with comments blanked and strings kept.</summary>
        private static string MissionLines(string script)
        {
            script = script.Replace("\r\n", "\n");
            var start = CoreLibrary.LibraryStartLine(script);
            var lines = script.Split('\n');
            var text = new StringBuilder();
            for (var i = 0; i < lines.Length && i + 1 < start; i++)
                text.Append(WithoutComment(lines[i])).Append('\n');
            return text.ToString();
        }

        private static string WithoutComment(string line)
        {
            var inString = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inString = !inString;
                else if (!inString && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') return line.Substring(0, i);
            }
            return line;
        }
    }
}
