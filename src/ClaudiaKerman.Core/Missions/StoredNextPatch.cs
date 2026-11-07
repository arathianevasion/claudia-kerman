// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #852: a coast's until that names a variable holding the next-patch time, for <see cref="BurnPoints"/>: <c>t [+ n]</c> after <c>local t is time:seconds +
    /// ship:orbit:nextpatcheta [+ m]</c>, or <c>time:seconds + e [+ n]</c> after <c>local e is ship:orbit:nextpatcheta [+ m]</c>. It reads text, not a running script, so it
    /// answers only when nothing is in doubt: the script's one assignment to the name holds the whole form, the coast is where that assignment reaches and runs once, and
    /// nothing between them can pass time or burn. Anything else stays unread and the plan unchecked, which is safe; a wrong reading isn't. It also says when a coast's until
    /// is a time on this orbit, for <see cref="DeorbitTargets"/>: anything it can't read so may be a next patch's.
    /// </summary>
    internal static class StoredNextPatch
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Offset = @"(\s*\+\s*(?<s>[0-9]+(\.[0-9]+)?))?$";
        private const string Name = @"(?<name>[a-z_][a-z0-9_]*)";
        private const string NextPatchEta = @"ship\s*:\s*orbit\s*:\s*nextpatcheta";
        private const string TimeSecondsPlus = @"time\s*:\s*seconds\s*\+\s*";

        /// <summary><c>time:seconds + ship:orbit:nextpatcheta [+ n]</c>: the UT of the patch's end, plus n.</summary>
        internal static readonly Regex UtForm = new Regex("^" + TimeSecondsPlus + NextPatchEta + Offset, Options);
        private static readonly Regex WaitForm = new Regex("^" + NextPatchEta + Offset, Options);
        private static readonly Regex NamesUt = new Regex("^" + Name + Offset, Options);
        private static readonly Regex NamesWait = new Regex("^" + TimeSecondsPlus + Name + Offset, Options);
        /// <summary>Between the assignment and the coast, what may pass time, burn or start a loop: any call or suffix (kOS calls a suffix method with no brackets) and these words.</summary>
        private static readonly Regex TakesTime = new Regex(@"\(|:|\b(wait|until|for|from|when|on|warp|warpmode|throttle|stage|run|runpath|runoncepath)\b", Options);
        /// <summary>
        /// Before the assignment, what may take the craft past the snapshot's patch or move its end: these words, a loop or trigger (what's in one isn't counted as it's written; <c>on</c>
        /// with nothing after it only switches something), or any call but those that build a value or are counted (an earlier coast). <see cref="SuffixBefore"/> has the rest.
        /// </summary>
        private static readonly Regex WorkBefore = new Regex(@"\b(wait|until|for|from|when|warp|warpmode|warpto|throttle|stage|run|runpath|runoncepath)\b|\bon\b\s*[^.\s]|\b(?!(lexicon|lex|list|node|coast)\b)[a-z_][a-z0-9_]*\s*\(", Options);
        /// <summary>The one loop <see cref="WorkBefore"/> lets by, as the recorded scripts clear old nodes with it: it does nothing but remove what it walks.</summary>
        private static readonly Regex ClearsNodes = new Regex(@"\bfor\s+(?<walked>[a-z_][a-z0-9_]*)\s+in\s+[a-z_][a-z0-9_]*\s*\{\s*remove\s+\k<walked>\s*\.\s*\}", Options);
        /// <summary>The one suffix read anywhere before the assignment: an earlier coast's until, or a node's time.</summary>
        private static readonly Regex TheClock = new Regex(@"\btime\s*:\s*seconds\b", Options);
        private static readonly Regex DelegateCall = new Regex(@"\bcall\b", Options);
        private static readonly Regex QuotedText = new Regex("\"[^\"]*\"", Options);
        /// <summary>A time on this orbit: nothing but numbers, the clock, the orbit's period and its apsides' etas, in plain arithmetic.</summary>
        private static readonly Regex OnThisOrbit = new Regex(@"^(?>\s+|[0-9]+(\.[0-9]+)?(e[+-]?[0-9]+)?\b|time\s*:\s*seconds\b|ship\s*:\s*orbit\s*:\s*period\b|eta\s*:\s*(apoapsis|periapsis)\b|[-+*/()])+$", Options);
        /// <summary>What repeats its body or runs it later, anywhere in the statement that opens a block.</summary>
        private static readonly Regex NotOnce = new Regex(@"\b(until|for|from|when|on|function)\b", Options);
        private static readonly Regex FirstWord = new Regex(@"\G\s*(?<word>[a-z_][a-z0-9_]*)", Options);

        /// <summary>The seconds past the patch's end that <paramref name="until"/> means, for the coast <paramref name="call"/> in <paramref name="masked"/> (strings and comments blanked); null when it can't be read.</summary>
        internal static double? SecondsPastTheEnd(string until, string masked, BlockCall call)
        {
            var named = NamesUt.Match(until);
            var form = UtForm;
            if (!named.Success)
            {
                named = NamesWait.Match(until);
                form = WaitForm;
            }
            if (!named.Success) return null;
            var callAt = call.Index;
            var assignment = OnlyAssignment(named.Groups["name"].Value, masked, callAt);
            if (assignment == null) return null;
            var value = form.Match(assignment.Groups["value"].Value.Trim());
            if (!value.Success) return null;

            var end = assignment.Index + assignment.Length;
            if (TakesTime.IsMatch(masked.Substring(end, callAt - end)) || OtherOptionsTakeTime(call) || IsWorkBefore(masked.Substring(0, assignment.Index))) return null;
            if (!RunsOnce(masked, callAt)) return null;
            return Seconds(value) + Seconds(named);
        }

        /// <summary>Whether <paramref name="until"/> is a time on this orbit, written out or in a variable whose one assignment holds it; null or unread says no. The time needn't be fixed, only never a next patch's.</summary>
        internal static bool StaysOnThisOrbit(string until, string masked, int callAt)
        {
            if (until == null) return false;
            var trimmed = until.Trim();
            if (OnThisOrbit.IsMatch(trimmed)) return true;
            var named = NamesUt.Match(trimmed);
            if (!named.Success) named = NamesWait.Match(trimmed);
            if (!named.Success) return false;
            var assignment = OnlyAssignment(named.Groups["name"].Value, masked, callAt);
            return assignment != null && OnThisOrbit.IsMatch(assignment.Groups["value"].Value.Trim());
        }

        /// <summary>The name's one plain assignment in <paramref name="masked"/>, when nothing else writes it and it reaches the coast at <paramref name="callAt"/>; null otherwise.</summary>
        private static Match OnlyAssignment(string name, string masked, int callAt)
        {
            if (StringAcrossLines(masked)) return null;
            var writes = Writes(name).Matches(masked);
            if (writes.Count != 1) return null;
            var assignment = Assignment(name).Match(masked, writes[0].Index);
            if (!assignment.Success || assignment.Index + assignment.Length > callAt) return null;
            return StartsAStatement(masked, assignment.Index) && Reaches(masked, assignment.Index, callAt) ? assignment : null;
        }

        /// <summary>The coast's other options run before it holds, after the assignment. A string in one is only text.</summary>
        private static bool OtherOptionsTakeTime(BlockCall call)
        {
            return call.Options.Any(option => !string.Equals(option.Key, "until", System.StringComparison.OrdinalIgnoreCase) && TakesTime.IsMatch(QuotedText.Replace(option.Value, "")));
        }

        private static bool IsWorkBefore(string before)
        {
            var text = TheClock.Replace(ClearsNodes.Replace(before, " "), " ");
            return WorkBefore.IsMatch(text) || SuffixBefore(text);
        }

        /// <summary>
        /// Whether a suffix before the assignment may do something: kOS calls a suffix method with no brackets, and a write may set the throttle or the warp, so one reads as a read
        /// only in the condition of an <c>if</c> opening a block, and not as a delegate's <c>call</c> even there. <see cref="TheClock"/> is out of the text by here.
        /// </summary>
        private static bool SuffixBefore(string text)
        {
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (!EndsAStatement(text, i)) continue;
                var statement = text.Substring(start, i - start);
                start = i + 1;
                if (statement.IndexOf(':') < 0) continue;
                if (text[i] != '{' || !Branch(FirstWord.Match(statement).Groups["word"].Value.ToLowerInvariant()) || DelegateCall.IsMatch(statement)) return true;
            }
            return false;
        }

        /// <summary>A kOS string may span lines, and the masking restarts at each one: what it shows after such a string can't be trusted.</summary>
        private static bool StringAcrossLines(string masked)
        {
            return masked.Split('\n').Any(line => line.Count(c => c == '"') % 2 != 0);
        }

        /// <summary>The n of a form's <c>+ n</c>; 0 without one.</summary>
        internal static double Seconds(Match form)
        {
            return form.Groups["s"].Success ? double.Parse(form.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>Everything that gives the name a value, or is a thing of that name: the assignment, a second one anywhere, a lock, a function, a parameter, a loop variable, <c>list ... in</c>, <c>unset all</c>.</summary>
        private static Regex Writes(string name)
        {
            return new Regex(@"\b(set|local|global|declare|lock|unset|for|function|toggle)\s+((local|global|lock|function)\s+)*" + name + @"\b"
                + @"|\bparameter\b([^.]|\.[0-9])*\b" + name + @"\b"
                + @"|\bin\s+" + name + @"\b"
                + @"|\b" + name + @"\s+(on|off)\b"
                + @"|\bunset\s+all\b", Options);
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

        /// <summary>Whether the coast runs at most once in the order it's written: inside nothing but plain <c>if</c> and <c>else</c> blocks. An unbraced loop on the coast's own statement is <see cref="TakesTime"/>'s to catch.</summary>
        private static bool RunsOnce(string masked, int callAt)
        {
            return Blocks(masked, callAt).All(open => Branch(HeadOf(masked, open)) && PlainUpTo(masked, open));
        }

        /// <summary>Whether the statement up to <paramref name="at"/> has no loop or trigger behind its first word (<c>if a until b {</c>), and <paramref name="at"/> isn't inside a call's brackets (a function passed to one).</summary>
        private static bool PlainUpTo(string masked, int at)
        {
            var start = StatementStart(masked, at);
            var text = masked.Substring(start, at - start);
            return !NotOnce.IsMatch(text) && text.Count(c => c == '(') == text.Count(c => c == ')');
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
