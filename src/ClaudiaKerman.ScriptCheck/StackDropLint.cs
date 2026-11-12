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
    /// #959 adds a second rule: a <c>reentry</c> with <c>separate "none"</c> keeps whatever stack <c>"auto"</c> would drop. Script Check never sees the craft, so it warns on any "none".
    /// #971 turns that second rule into a refusal when the plan gives no custom_script_reason (<see cref="Refusals"/>): a stack kept on purpose needs its reason.
    /// It reads the Mission Script only, never the Core Library bundled after it, ignoring comments; the first rule only warns.
    /// </summary>
    public static class StackDropLint
    {
        private static readonly Regex Reentry = new Regex(@"\breentry\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Deorbit = new Regex(@"\bdeorbit\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Separate = new Regex("\"separate\"\\s*,\\s*(\"(?<text>[^\"]*)\"|(?<other>[^,)\\s]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The warnings, or none, for <paramref name="script"/>: a Mission Script, or a bundle whose library lines it skips.</summary>
        public static IList<string> Warnings(string script, string customScriptReason = null)
        {
            var warnings = new List<string>();
            var code = CoreLibrary.MissionScript(script);
            DeorbitThenDrop(code, warnings);
            if (customScriptReason == null || customScriptReason.Trim().Length > 0) KeptStack(code, warnings);
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

        // The #959 rule: reentry with separate "none". Script Check sees only the script, not the craft, so it can't tell whether "auto"
        // would drop a stack here; it warns on any "none" and says what to look for. A question to Claudia, never a refusal.
        private static void KeptStack(string code, List<string> warnings)
        {
            foreach (Match reentry in Reentry.Matches(code))
            {
                var args = Args(code, reentry.Index + reentry.Length);
                if (!SeparateIsNone(args, absentIsNone: false)) continue;
                warnings.Add("line " + LineOf(code, reentry.Index) + ": reentry with separate \"none\" keeps the whole stack. If the craft has a stack below its heat shield, "
                    + "check its mass and the engine that would land on it: the stage that lands and lifts off is the one above the shield, and \"auto\" drops the rest. "
                    + "\"none\" is right only if nothing is below the shield, or the stack has no decoupler.");
            }
        }

        /// <summary>
        /// #971: each <c>reentry</c> with <c>separate "none"</c> when <paramref name="customScriptReason"/> is empty. Flight m20261109-200745-96f6 kept a 41 t stack
        /// after the warning; the plan must now say why the stack stays. None when the reason is given, or null (no plan to ask).
        /// </summary>
        public static IList<string> Refusals(string script, string customScriptReason)
        {
            var refusals = new List<string>();
            if (customScriptReason == null || customScriptReason.Trim().Length > 0) return refusals;
            var code = CoreLibrary.MissionScript(script);
            foreach (Match reentry in Reentry.Matches(code))
                if (SeparateIsNone(Args(code, reentry.Index + reentry.Length), absentIsNone: false))
                    refusals.Add("line " + LineOf(code, reentry.Index) + ": reentry with separate \"none\" keeps the whole stack, and the plan gives no custom_script_reason. "
                        + "Either drop the stack: leave separate off or set it to \"auto\", which drops what is below the heat shield; "
                        + "or, if the stack must stay, say why in custom_script_reason.");
            return refusals;
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
