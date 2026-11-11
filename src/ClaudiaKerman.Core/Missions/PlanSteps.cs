// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #977: the Mission Plan step a Sequence Plan flies, read from its name as well as its <c>plan_step</c>. A <c>plan_step</c> still to
    /// fly gives way only to a later step that alone clearly matches, and only when its own sequence shares no word but body names and no
    /// action with the plan's name; one naming a sequence already flown gives way to the one still to fly that matches. Otherwise the step
    /// sent stands.
    /// </summary>
    internal static class PlanSteps
    {
        private static readonly HashSet<string> Small = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "to", "at", "from", "of", "on", "in", "into", "onto", "for", "and", "with", "by",
        };

        /// <summary>The kinds of action <see cref="SequenceWords"/> reads in a name.</summary>
        private static readonly Regex[] Actions =
        {
            SequenceWords.Landing, SequenceWords.Ascent, SequenceWords.Reentry, SequenceWords.ReturnHome, SequenceWords.Docking,
            SequenceWords.Aero, SequenceWords.Capture, SequenceWords.Trip, SequenceWords.Flyby,
        };

        /// <summary>
        /// The 1-based step the plan named <paramref name="name"/> flies when its <paramref name="sent"/> <c>plan_step</c> is a mismatch,
        /// or null when the step sent stands. <paramref name="lastBegun"/> is the last step begun before this plan (0 for none): a step
        /// before it is flown, and the step itself may be retried. <paramref name="bodies"/> are the body names a name may hold.
        /// </summary>
        public static int? Resolve(IList<PlannedSequence> legs, string name, int sent, int lastBegun, IEnumerable<string> bodies)
        {
            // A step outside the Mission Plan stays the Script Check's problem (#837): Claudia names it again.
            if (legs == null || sent < 1 || sent > legs.Count || string.IsNullOrWhiteSpace(name)) return null;
            var names = (bodies ?? Enumerable.Empty<string>()).Where(b => !string.IsNullOrEmpty(b)).ToList();
            var matches = Enumerable.Range(1, legs.Count).Where(step => Clear(name, legs[step - 1].Name, names)).ToList();
            var toFly = matches.Where(step => step >= lastBegun).ToList();
            if (sent >= lastBegun)
            {
                // A step still to fly gives way only forward, and only when its sequence clearly isn't this plan: a wrong override is worse.
                if (matches.Contains(sent) || !Unlike(name, legs[sent - 1].Name, names)) return null;
                return toFly.Count == 1 && toFly[0] > sent ? toFly[0] : (int?)null;
            }
            if (matches.Contains(sent) && toFly.Count == 0) return null;
            if (toFly.Count == 1) return toFly[0];
            // A leg already flown is taken only in place of another flown one: never back over legs still to fly.
            if (matches.Count == 1 && !matches.Contains(sent)) return matches[0];
            return null;
        }

        /// <summary>The one line the mod tells Claudia (and logs) when <paramref name="sent"/> gave way to <paramref name="step"/>.</summary>
        public static string Line(IList<PlannedSequence> legs, int sent, int step)
        {
            return "plan_step " + sent.ToString(CultureInfo.InvariantCulture) + " is \"" + legs[sent - 1].Name + "\"; this plan flies step " + step.ToString(CultureInfo.InvariantCulture) + ", \"" + legs[step - 1].Name + "\".";
        }

        /// <summary>
        /// The plan's name clearly names the sequence's: the same words before any comma ("Transfer to Kerbin, next window" is
        /// "Transfer to Kerbin, soonest window"), or one's two or more words all in the other's, or the same actions at the same bodies.
        /// </summary>
        private static bool Clear(string plan, string sequence, IList<string> bodies)
        {
            var a = Words(Head(plan));
            var b = Words(Head(sequence));
            if (a.Count == 0 || b.Count == 0) return false;
            if (a.SetEquals(b)) return true;
            var small = a.Count <= b.Count ? a : b;
            var big = a.Count <= b.Count ? b : a;
            if (small.Count >= 2 && small.IsSubsetOf(big)) return true;
            var acts = Acts(Head(plan));
            var at = Named(Head(plan), bodies);
            return acts.Count > 0 && at.Count > 0 && acts.SetEquals(Acts(Head(sequence))) && at.SetEquals(Named(Head(sequence), bodies));
        }

        /// <summary>
        /// The plan's name and the sequence's share no word but body names (every leg at Duna names Duna) and no action
        /// <see cref="SequenceWords"/> reads: the sequence clearly isn't this plan.
        /// </summary>
        private static bool Unlike(string plan, string sequence, IList<string> bodies)
        {
            var a = Words(Unnumbered(plan));
            a.ExceptWith(bodies.Select(b => b.ToLowerInvariant()));
            return !a.Overlaps(Words(Unnumbered(sequence))) && !Acts(Unnumbered(plan)).Overlaps(Acts(Unnumbered(sequence)));
        }

        /// <summary>A leading "Leg 2:", "Step 3." or "4)" numbers the name, which is plan_step's job: it is left out.</summary>
        private static readonly Regex Numbered = new Regex(@"^\s*(?:(?:leg|step|sequence|stage|phase|part)\s*)?#?\d+\s*(?:[:)]|[.\-–—](?=\s))\s*", RegexOptions.IgnoreCase);

        private static string Unnumbered(string name)
        {
            return Numbered.Replace(name ?? "", "");
        }

        /// <summary>A name, unnumbered, before its first comma or bracket: what the sequence is, not how this try goes.</summary>
        private static string Head(string name)
        {
            var text = Unnumbered(name);
            var cut = text.IndexOfAny(new[] { ',', '(', ';' });
            return cut < 0 ? text : text.Substring(0, cut);
        }

        private static HashSet<string> Words(string text)
        {
            return new HashSet<string>(Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Cast<Match>().Select(m => m.Value).Where(w => !Small.Contains(w)));
        }

        private static HashSet<int> Acts(string text)
        {
            return new HashSet<int>(Enumerable.Range(0, Actions.Length).Where(i => Actions[i].IsMatch(text)));
        }

        private static HashSet<string> Named(string text, IList<string> bodies)
        {
            return new HashSet<string>(bodies.Where(b => Regex.IsMatch(text, @"\b" + Regex.Escape(b) + @"\b", RegexOptions.IgnoreCase)), StringComparer.OrdinalIgnoreCase);
        }
    }
}
