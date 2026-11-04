// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// What a Maneuver Sequence of the Mission Plan does, read from its own words (name and intent): a landing, an ascent, a
    /// reentry, a return home (to Kerbin, or "come home" and the like), and the body it names. The Margin check (#574) and the Chute Allocation (#626) share it.
    /// </summary>
    internal static class SequenceWords
    {
        public static readonly Regex Surface = new Regex(@"\b(land(s|ed|ing)?|touch ?down|ascen(d|ds|t|ding)|lift ?off|take ?off|launch(es|ing)?)\b", RegexOptions.IgnoreCase);
        public static readonly Regex Landing = new Regex(@"\b(land(s|ed|ing)?|touch ?down)\b", RegexOptions.IgnoreCase);
        public static readonly Regex Ascent = new Regex(@"\b(ascen(d|ds|t|ding)|lift ?off|take ?off|launch(es|ing)?)\b", RegexOptions.IgnoreCase);
        public static readonly Regex Reentry = new Regex(@"\b(re-?ent(er|ry|ers|ering)|splash ?down)\b", RegexOptions.IgnoreCase);
        /// <summary>
        /// The return to the home planet (#689): a return that names Kerbin, or "come/return/go/head home", "back home" and "fly home"
        /// (inflected where natural), whole words. "home in on" and "home stretch" are not returns.
        /// </summary>
        public static readonly Regex ReturnHome = new Regex(@"\breturn(s|ing)?\b.*\bKerbin\b|\bKerbin\b.*\breturn(s|ing)?\b|\b(?:com(?:e|es|ing)|return(?:s|ing|ed)?|go(?:es|ing)?|head(?:s|ing|ed)?|fl(?:y|ies|ying)|back)\s+home(?![\w-])(?![\s,.;:-]+(?:in\s+on|stretch)\b)", RegexOptions.IgnoreCase);
        /// <summary>Docking with the Target Vessel: the dock block (#456).</summary>
        public static readonly Regex Docking = new Regex(@"\bdock(s|ed|ing)?\b", RegexOptions.IgnoreCase);
        /// <summary>Aerobraking and aerocapture: through the air, but not down to the ground.</summary>
        public static readonly Regex Aero = new Regex(@"\baero ?(brak(e|es|ed|ing)|captur(e|es|ed|ing))\b", RegexOptions.IgnoreCase);

        /// <summary>The sequence's words, as the matchers read them.</summary>
        public static string Of(PlannedSequence sequence)
        {
            return sequence.Name + " " + sequence.Intent;
        }

        /// <summary>The body named first in <paramref name="words"/>, or null for none.</summary>
        public static SnapshotBody NamedBody(GameSnapshot snapshot, string words)
        {
            SnapshotBody first = null;
            var firstAt = int.MaxValue;
            foreach (var body in snapshot.Bodies.Where(b => b.Name.Length > 0))
            {
                var match = Regex.Match(words, @"\b" + Regex.Escape(body.Name) + @"\b", RegexOptions.IgnoreCase);
                if (match.Success && match.Index < firstAt)
                {
                    first = body;
                    firstAt = match.Index;
                }
            }
            return first;
        }

        public static SnapshotBody Body(GameSnapshot snapshot, string name)
        {
            return snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A leg that comes down through Kerbin's air: a reentry or splashdown, or a return home (#689).</summary>
        public static bool ReturnsHome(string words)
        {
            return Reentry.IsMatch(words) || ReturnHome.IsMatch(words);
        }
    }
}
