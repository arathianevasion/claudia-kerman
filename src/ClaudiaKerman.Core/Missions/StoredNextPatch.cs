// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #852: a coast's until that names a variable holding the next-patch time, for <see cref="BurnPoints"/>: <c>t [+ n]</c> after <c>local t is time:seconds +
    /// ship:orbit:nextpatcheta [+ m]</c>, or <c>time:seconds + e [+ n]</c> after <c>local e is ship:orbit:nextpatcheta [+ m]</c>. It reads text, not a running script, so it
    /// answers only when nothing is in doubt: the script's one assignment to the name holds the whole form, the coast is where that assignment reaches and runs once, and
    /// nothing between them can pass time or burn. Anything else stays unread and the plan unchecked, which is safe; a wrong reading isn't.
    /// </summary>
    internal static class StoredNextPatch
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Offset = @"(\s*\+\s*(?<s>[0-9]+(\.[0-9]+)?))?$";
        private const string Name = @"(?<name>[a-z_][a-z0-9_]*)";
        private const string NextPatchEta = @"ship\s*:\s*orbit\s*:\s*nextpatcheta";
        private const string Now = @"time\s*:\s*seconds\s*\+\s*";

        /// <summary><c>time:seconds + ship:orbit:nextpatcheta [+ n]</c>: the UT of the patch's end, plus n.</summary>
        internal static readonly Regex UtForm = new Regex("^" + Now + NextPatchEta + Offset, Options);
        private static readonly Regex WaitForm = new Regex("^" + NextPatchEta + Offset, Options);
        private static readonly Regex NamesUt = new Regex("^" + Name + Offset, Options);
        private static readonly Regex NamesWait = new Regex("^" + Now + Name + Offset, Options);
        /// <summary>What may pass time, burn or start a loop: any call (a block's, or a function's that may hold any of these) and these words.</summary>
        private static readonly Regex TakesTime = new Regex(@"\(|\b(wait|until|for|from|when|on|warp|warpmode|throttle|stage|run|runpath|runoncepath)\b", Options);
        private static readonly Regex FirstWord = new Regex(@"\G\s*(?<word>[a-z_][a-z0-9_]*)", Options);

        /// <summary>The seconds past the patch's end that <paramref name="until"/> means, for the coast at <paramref name="callAt"/> in <paramref name="masked"/> (strings and comments blanked); null when it can't be read.</summary>
        internal static double? SecondsPastTheEnd(string until, string masked, int callAt)
        {
            var named = NamesUt.Match(until);
            var form = UtForm;
            if (!named.Success)
            {
                named = NamesWait.Match(until);
                form = WaitForm;
            }
            if (!named.Success) return null;
            var name = named.Groups["name"].Value;

            var writes = Writes(name).Matches(masked);
            if (writes.Count != 1) return null;
            var assignment = Assignment(name).Match(masked, writes[0].Index);
            if (!assignment.Success) return null;
            var end = assignment.Index + assignment.Length;
            if (end > callAt || !StartsAStatement(masked, assignment.Index)) return null;
            var value = form.Match(assignment.Groups["value"].Value.Trim());
            if (!value.Success) return null;

            if (TakesTime.IsMatch(masked.Substring(end, callAt - end))) return null;
            if (!Reaches(masked, assignment.Index, callAt) || !RunsOnce(masked, callAt)) return null;
            return Seconds(value) + Seconds(named);
        }

        private static double Seconds(Match form)
        {
            return form.Groups["s"].Success ? double.Parse(form.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>Everything that gives the name a value, or is a thing of that name: the assignment, a second one anywhere, a lock, a function, a parameter, a loop variable, <c>list ... in</c>.</summary>
        private static Regex Writes(string name)
        {
            return new Regex(@"\b(set|local|global|declare|lock|unset|for|function)\s+((local|global|lock|function)\s+)*" + name + @"\b"
                + @"|\bparameter\b([^.]|\.[0-9])*\b" + name + @"\b"
                + @"|\bin\s+" + name + @"\b"
                + @"|\b" + name + @"\s+(on|off|toggle)\b", Options);
        }

        /// <summary>A plain assignment at the match's start, up to the full stop that ends it (a decimal's point doesn't).</summary>
        private static Regex Assignment(string name)
        {
            return new Regex(@"\G(set\s+" + name + @"\s+to|(local|global|declare(\s+(local|global))?)\s+" + name + @"\s+(is|to))\s+(?<value>([^.]|\.[0-9])*)\.", Options);
        }

        /// <summary>Whether a statement starts here, so nothing unbraced (<c>until x set t to ...</c>) governs it.</summary>
        private static bool StartsAStatement(string masked, int at)
        {
            var start = StatementStart(masked, at);
            return masked.Substring(start, at - start).Trim().Length == 0;
        }

        /// <summary>Whether a <c>local</c> at <paramref name="assigned"/> is still in scope at <paramref name="used"/>: the block it's in holds the coast.</summary>
        private static bool Reaches(string masked, int assigned, int used)
        {
            var outer = Blocks(masked, assigned);
            return outer.SequenceEqual(Blocks(masked, used).Take(outer.Count));
        }

        /// <summary>Whether the coast runs at most once in the order it's written: inside nothing but <c>if</c> and <c>else</c> blocks, and no unbraced loop.</summary>
        private static bool RunsOnce(string masked, int callAt)
        {
            if (Blocks(masked, callAt).Any(open => !Branch(HeadOf(masked, open)))) return false;
            var head = HeadOf(masked, callAt);
            return Branch(head) || head == "local" || head == "set" || head == "global" || head == "declare" || head == "coast";
        }

        private static bool Branch(string head)
        {
            return head == "if" || head == "else";
        }

        /// <summary>The first word of the statement <paramref name="at"/> is in, lower case.</summary>
        private static string HeadOf(string masked, int at)
        {
            return FirstWord.Match(masked, StatementStart(masked, at)).Groups["word"].Value.ToLowerInvariant();
        }

        private static int StatementStart(string masked, int at)
        {
            var start = at;
            while (start > 0 && !EndsAStatement(masked, start - 1)) start--;
            return start;
        }

        private static bool EndsAStatement(string masked, int i)
        {
            var c = masked[i];
            return c == '{' || c == '}' || (c == '.' && !(i + 1 < masked.Length && char.IsDigit(masked[i + 1])));
        }

        /// <summary>The opening braces of the blocks around <paramref name="at"/>, outermost first.</summary>
        private static List<int> Blocks(string masked, int at)
        {
            var open = new List<int>();
            for (var i = 0; i < at; i++)
            {
                if (masked[i] == '{') open.Add(i);
                else if (masked[i] == '}' && open.Count > 0) open.RemoveAt(open.Count - 1);
            }
            return open;
        }
    }
}
