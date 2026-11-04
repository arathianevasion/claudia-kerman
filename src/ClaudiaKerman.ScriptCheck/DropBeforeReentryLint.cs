// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.ScriptCheck
{
    /// <summary>
    /// A warn-only lint (#608): a <c>drop_parts(...)</c> whose next Building Block call is <c>reentry(...)</c>. A107 dropped its
    /// engine that way with its periapsis at 601 km and stranded its crew; <c>reentry</c>'s own <c>separate</c> checks that the orbit
    /// reaches the atmosphere before it drops anything. It reads the Mission Script only, never the Core Library bundled after
    /// it, ignoring comments and strings; it never refuses a script.
    /// </summary>
    public static class DropBeforeReentryLint
    {
        private static readonly Regex BlockCall = new Regex(
            @"\b(" + string.Join("|", CoreLibrary.Blocks.Select(b => Regex.Escape(b.Name))) + @")\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The warnings, or none, for <paramref name="script"/>: a Mission Script, or a bundle whose library lines it skips.</summary>
        public static IList<string> Warnings(string script)
        {
            var warnings = new List<string>();
            var code = CoreLibrary.MissionScript(script, true);
            Match previous = null;
            foreach (Match call in BlockCall.Matches(code))
            {
                if (previous != null && Is(previous, "drop_parts") && Is(call, "reentry"))
                {
                    var line = 1;
                    for (var i = 0; i < previous.Index; i++)
                        if (code[i] == '\n') line++;
                    warnings.Add("line " + line + ": drop_parts just before reentry; let reentry's separate drop the stack instead, since it checks the orbit reaches the atmosphere before anything leaves.");
                }
                previous = call;
            }
            return warnings;
        }

        private static bool Is(Match call, string block)
        {
            return call.Groups[1].Value.Equals(block, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
