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

        /// <summary>A landing, touchdown or takeoff verb with its preposition: "land on the", "ascend from", "take off from".</summary>
        private const string SurfaceVerb = @"\b(?:land(?:s|ed|ing)?|touch ?down|ascen(?:d|ds|t|ding)|lift ?off|take ?off|launch(?:es|ing)?)\s+(?:on|at|near|over|from|off)\s+(?:the\s+)?";

        /// <summary>The body named first in <paramref name="words"/>, or null for none.</summary>
        public static SnapshotBody NamedBody(GameSnapshot snapshot, string words)
        {
            return FirstBody(snapshot, words, "");
        }

        /// <summary>The body a landing or takeoff names ("Land on the Mun", "ascend from Minmus"), or null for none.</summary>
        private static SnapshotBody SurfaceBody(GameSnapshot snapshot, string words)
        {
            return FirstBody(snapshot, words, SurfaceVerb);
        }

        private static SnapshotBody FirstBody(GameSnapshot snapshot, string words, string before)
        {
            SnapshotBody first = null;
            var firstAt = int.MaxValue;
            foreach (var body in snapshot.Bodies.Where(b => b.Name.Length > 0))
            {
                var match = Regex.Match(words, before + @"\b" + Regex.Escape(body.Name) + @"\b", RegexOptions.IgnoreCase);
                if (match.Success && match.Index < firstAt)
                {
                    first = body;
                    firstAt = match.Index;
                }
            }
            return first;
        }

        /// <summary>
        /// The body a leg's surface work is at (#726, the Margin check's thrust): a return home is at Kerbin, unless the leg lands or
        /// takes off from another body first ("Land on the Mun and return to Kerbin" is at the Mun). Otherwise the body its words
        /// name, else the <paramref name="previous"/> leg's.
        /// </summary>
        public static SnapshotBody LegBody(GameSnapshot snapshot, string words, SnapshotBody previous)
        {
            if (!ReturnHome.IsMatch(words)) return NamedBody(snapshot, words) ?? previous;
            return SurfaceBody(snapshot, words) ?? Body(snapshot, "Kerbin") ?? previous;
        }

        /// <summary>
        /// The body the leg after this one starts from (#726): Kerbin once a leg returns home, since the craft has come home; else
        /// the leg's <see cref="LegBody"/>. Chute Allocation takes a return's entry here too.
        /// </summary>
        public static SnapshotBody NextBody(GameSnapshot snapshot, string words, SnapshotBody previous)
        {
            return ReturnHome.IsMatch(words) ? Body(snapshot, "Kerbin") ?? previous : LegBody(snapshot, words, previous);
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
