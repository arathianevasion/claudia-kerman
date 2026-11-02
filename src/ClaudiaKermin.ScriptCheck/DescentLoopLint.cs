// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.ScriptCheck
{
    /// <summary>
    /// A warn-only lint (#627): a hand-written descent loop, a <c>lock throttle</c> inside an <c>until</c> loop whose condition tests
    /// <c>alt:radar</c> or <c>LANDED</c>. Claudia wrote one for Duna (an atmosphere) instead of calling <c>reentry</c>. It reads the
    /// Mission Script only, never the Core Library bundled after it (whose <c>land</c> flies such a loop itself), ignoring comments and
    /// strings; it never refuses a script.
    /// </summary>
    public static class DescentLoopLint
    {
        private static readonly Regex Until = new Regex(@"\buntil\b([^{\n]*)\{", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Test = new Regex(@"\balt\s*:\s*radar\b|\blanded\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Throttle = new Regex(@"\block\s+throttle\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The warnings, or none, for <paramref name="script"/>: a Mission Script, or a bundle whose library lines it skips.</summary>
        public static IList<string> Warnings(string script)
        {
            var warnings = new List<string>();
            var code = CoreLibrary.MissionScript(script, true);
            var unmasked = CoreLibrary.MissionScript(script);
            foreach (Match loop in Until.Matches(code))
            {
                // A string in the condition ("LANDED") counts; the loop is found in the masked text, so a print of one does not.
                if (!Test.IsMatch(unmasked.Substring(loop.Groups[1].Index, loop.Groups[1].Length))) continue;
                var depth = 1;
                var end = loop.Index + loop.Length;
                while (end < code.Length && depth > 0)
                {
                    if (code[end] == '{') depth++;
                    else if (code[end] == '}') depth--;
                    end++;
                }
                if (!Throttle.IsMatch(code.Substring(loop.Index, end - loop.Index))) continue;
                var line = 1;
                for (var i = 0; i < loop.Index; i++)
                    if (code[i] == '\n') line++;
                warnings.Add("line " + line + ": a hand-written descent loop; use reentry for a landing through air, land on an airless body.");
            }
            return warnings;
        }
    }
}
