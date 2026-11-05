// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
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

        /// <summary>A capture into orbit at the end of a trip (#757).</summary>
        public static readonly Regex Capture = new Regex(@"\b(captur(e|es|ed|ing)|circulari[sz](e|es|ed|ing)|insertion)\b", RegexOptions.IgnoreCase);
        /// <summary>A trip to another body (#757): a transfer, an ejection or a return. The body it goes to is read separately.</summary>
        public static readonly Regex Trip = new Regex(@"\b(transfer(s|ring)?|eject(s|ion|ing)?|depart(s|ure|ing)?|intercept|return(s|ing)?|(go|fly|head|travel)\s+(back\s+)?to)\b", RegexOptions.IgnoreCase);
        /// <summary>A trip that passes the body and never stops there (#757).</summary>
        public static readonly Regex Flyby = new Regex(@"\b(fly ?-?by|flies by|fly past|free return)\b", RegexOptions.IgnoreCase);

        /// <summary>The sequence's words, as the matchers read them.</summary>
        public static string Of(PlannedSequence sequence)
        {
            return sequence.Name + " " + sequence.Intent;
        }

        /// <summary>
        /// The words the Delta-v Map's leg matching reads (#757): the name when it says what the leg does, else the intent. An intent
        /// is prose that names what comes next ("Launch ... then circularize", "Return to Duna for the Kerbin transfer"), so it never
        /// adds to a name that already says.
        /// </summary>
        public static string ActionWords(PlannedSequence sequence)
        {
            return Acts(sequence.Name) ? sequence.Name : sequence.Intent;
        }

        private static bool Acts(string words)
        {
            return Surface.IsMatch(words) || Trip.IsMatch(words) || Capture.IsMatch(words) || Reentry.IsMatch(words) || Aero.IsMatch(words) || Flyby.IsMatch(words);
        }

        /// <summary>The body named first in <paramref name="words"/>, or null for none.</summary>
        public static SnapshotBody NamedBody(GameSnapshot snapshot, string words)
        {
            return FirstBody(snapshot, words, false);
        }

        /// <summary>
        /// The first body <paramref name="words"/> names, earliest occurrence first. With <paramref name="away"/>, Kerbin is skipped
        /// and so is an occurrence the return comes from ("Return from the Mun to Kerbin", "come home from Minmus"): that is where
        /// a come-home leg's return departs, not where its surface work is (#726).
        /// </summary>
        private static SnapshotBody FirstBody(GameSnapshot snapshot, string words, bool away)
        {
            SnapshotBody first = null;
            var firstAt = int.MaxValue;
            foreach (var body in snapshot.Bodies.Where(b => b.Name.Length > 0 && !(away && string.Equals(b.Name, "Kerbin", StringComparison.OrdinalIgnoreCase))))
            {
                foreach (Match match in Regex.Matches(words, @"\b" + Regex.Escape(body.Name) + @"\b", RegexOptions.IgnoreCase))
                {
                    if (away && ReturnsFrom.IsMatch(words.Substring(0, match.Index))) continue;
                    if (match.Index < firstAt)
                    {
                        first = body;
                        firstAt = match.Index;
                    }
                    break;
                }
            }
            return first;
        }

        /// <summary>The text before a body names a return's origin: "come home from", "return to Kerbin from", "return from" (same clause).</summary>
        private static readonly Regex ReturnsFrom = new Regex(@"\b(?:com(?:e|es|ing)|return(?:s|ing|ed)?|go(?:es|ing)?|head(?:s|ing|ed)?|fl(?:y|ies|ying)|back|home)\b[^.,;]*\bfrom\s+(?:the\s+)?$", RegexOptions.IgnoreCase);

        /// <summary>
        /// The body a leg's surface work is at (#726, #740, the Margin check's thrust): a return home is at Kerbin, unless the leg
        /// also lands, touches down or ascends (<see cref="Surface"/>) at the first body it names other than Kerbin, in any word
        /// order ("Land on the surface of the Mun and return to Kerbin" is at the Mun). Otherwise the body its words name, else the
        /// <paramref name="previous"/> leg's.
        /// </summary>
        public static SnapshotBody LegBody(GameSnapshot snapshot, string words, SnapshotBody previous)
        {
            if (!ReturnHome.IsMatch(words)) return NamedBody(snapshot, words) ?? previous;
            if (Surface.IsMatch(words) && FirstBody(snapshot, words, true) is SnapshotBody away) return away;
            return Body(snapshot, "Kerbin") ?? previous;
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
