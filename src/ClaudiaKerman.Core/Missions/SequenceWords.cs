// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// What a Maneuver Sequence of the Mission Plan does, read from its own words (name and intent): a landing, an ascent, a
    /// reentry, a return home (to Kerbin, or "come home" and the like), and the body it names. The Margin check (#574) and the Entries reader share it.
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

        /// <summary>An entry that says it comes straight in from a transfer, with no orbit first (#823).</summary>
        public static readonly Regex StraightIn = new Regex(@"\b(straight[ -]in|direct (re-?)?entry|direct descent)\b", RegexOptions.IgnoreCase);

        /// <summary>A capture into orbit at the end of a trip (#757).</summary>
        public static readonly Regex Capture = new Regex(@"\b(captur(e|es|ed|ing)|circulari[sz](e|es|ed|ing)|insertion)\b", RegexOptions.IgnoreCase);
        /// <summary>A trip to another body (#757): a transfer, an ejection or a return. The body it goes to is read separately.</summary>
        public static readonly Regex Trip = new Regex(@"\b(transfer(s|ring)?|eject(s|ion|ing)?|inject(s|ion|ing)?|depart(s|ure|ing)?|intercept|return(s|ing)?|(go|fly|head|travel)\s+(back\s+)?to)\b", RegexOptions.IgnoreCase);
        /// <summary>A return that names nowhere to return to: home (#757).</summary>
        public static readonly Regex BareReturn = new Regex(@"\breturn(s|ing)?\b", RegexOptions.IgnoreCase);
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

        internal static bool Acts(string words)
        {
            return Surface.IsMatch(words) || Trip.IsMatch(words) || ReturnHome.IsMatch(words) || Capture.IsMatch(words) || Reentry.IsMatch(words) || Aero.IsMatch(words) || Flyby.IsMatch(words);
        }

        /// <summary>The body named first in <paramref name="words"/>, or null for none.</summary>
        public static SnapshotBody NamedBody(GameSnapshot snapshot, string words)
        {
            return FirstBody(snapshot, words, false);
        }

        /// <summary>The one body <paramref name="words"/> name other than Kerbin, or null for none or several.</summary>
        public static SnapshotBody SoleAwayBody(GameSnapshot snapshot, string words)
        {
            var named = snapshot.Bodies.Where(b => b.Name.Length > 0 && !string.Equals(b.Name, "Kerbin", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(words, @"\b" + Regex.Escape(b.Name) + @"\b", RegexOptions.IgnoreCase)).ToList();
            return named.Count == 1 ? named[0] : null;
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

        /// <summary>The text before a body names where a trip starts, not where it goes: "from the", "depart", "leave", "eject from".</summary>
        private static readonly Regex Origin = new Regex(@"\b(?:from|depart(?:s|ing|ure)?|leav(?:e|es|ing)|eject(?:s|ion|ing)?)\s+(?:the\s+)?$", RegexOptions.IgnoreCase);

        /// <summary>
        /// The text before a body names a body to keep clear of (#894), not to go to: "clear of (the)", "avoid(ing)", "away from", "below/under/outside
        /// (the) (SOI/orbit of)", "miss(ing)". The avoid word governs the body as "from" governs an origin: it comes right before the name.
        /// </summary>
        private static readonly Regex Avoids = new Regex(@"\b(?:clear\s+of|away\s+from|out\s+of|outside|avoid(?:s|ing|ed)?|miss(?:es|ing|ed)?|without|don['’]t|do\s+not|not)\b[\s:(""]*(?:(?:a|an|the|any)\s+)?(?:(?:enter(?:s|ing)?|encounter(?:s|ing)?(?:\s+with)?|hit(?:s|ting)?)\s+)?(?:(?:a|an|the)\s+)?(?:(?:SOI|sphere\s+of\s+influence|orbit)\s+of\s+(?:the\s+)?)?$", RegexOptions.IgnoreCase);

        /// <summary>"below/under/outside the SOI/orbit of" before a body: a place to keep out of only when the SOI or orbit is named.</summary>
        private static readonly Regex BelowSoiOf = new Regex(@"\b(?:below|under|outside)\s+(?:the\s+)?(?:SOI|sphere\s+of\s+influence|orbit)\s+of\s+(?:the\s+)?$", RegexOptions.IgnoreCase);

        /// <summary>"below/under (the)" before a body that is followed by "'s SOI/orbit/sphere".</summary>
        private static readonly Regex BelowBefore = new Regex(@"\b(?:below|under)\s+(?:the\s+)?$", RegexOptions.IgnoreCase);

        private static readonly Regex PossessiveSoi = new Regex(@"^(?:['’]s?\s+(?:SOI|sphere|orbit)|\s+(?:SOI|sphere\s+of\s+influence))\b", RegexOptions.IgnoreCase);

        /// <summary>"no (the)" before a body followed by "encounter": "no Ike encounter" (#903).</summary>
        private static readonly Regex NoBefore = new Regex(@"\bno\s+(?:the\s+)?$", RegexOptions.IgnoreCase);

        private static readonly Regex EncounterAfter = new Regex(@"^\s+encounter\b", RegexOptions.IgnoreCase);

        /// <summary>Peels a moon and its joiner off the end of <paramref name="before"/> ("Ike and the ", "Ike, "), to find the avoid word governing a list; null if none.</summary>
        private static string PeelListItem(GameSnapshot snapshot, string before)
        {
            var names = string.Join("|", snapshot.Bodies.Where(b => b.Name.Length > 0).Select(b => Regex.Escape(b.Name)));
            if (names.Length == 0) return null;
            var m = Regex.Match(before, @"\b(?:the\s+)?(?:" + names + @")(?:['’]s?(?:\s+(?:SOI|sphere|orbit))?)?\s*(?:,\s*(?:and\s+|or\s+)?|\s+and\s+|\s+or\s+)(?:the\s+)?$", RegexOptions.IgnoreCase);
            return m.Success ? before.Substring(0, m.Index) : null;
        }

        private static bool IsAvoided(GameSnapshot snapshot, string words, Match m)
        {
            var before = words.Substring(0, m.Index);
            if (BelowSoiOf.IsMatch(before) || BelowBefore.IsMatch(before) && PossessiveSoi.IsMatch(words.Substring(m.Index + m.Length))) return true;
            if (NoBefore.IsMatch(before) && EncounterAfter.IsMatch(words.Substring(m.Index + m.Length))) return true;
            for (var guard = 0; before != null && guard < 20; guard++)
            {
                if (Avoids.IsMatch(before)) return true;
                before = PeelListItem(snapshot, before);
            }
            return false;
        }

        /// <summary>The bodies <paramref name="words"/> name as a place to be at or go to (#871): every body with an occurrence that isn't an origin ("Return from Ike to Duna" names Duna, not Ike) or one named only to be avoided (#894: "raise Ap clear of Ike's SOI").</summary>
        public static System.Collections.Generic.IList<SnapshotBody> Destinations(GameSnapshot snapshot, string words)
        {
            return snapshot.Bodies.Where(b => b.Name.Length > 0 && Regex.Matches(words, @"\b" + Regex.Escape(b.Name) + @"\b", RegexOptions.IgnoreCase).Cast<Match>()
                .Any(m => !Origin.IsMatch(words.Substring(0, m.Index)) && !ReturnsFrom.IsMatch(words.Substring(0, m.Index)) && !IsAvoided(snapshot, words, m))).ToList();
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
        /// the leg's <see cref="LegBody"/>. The Entries reader takes a return's entry here too.
        /// </summary>
        public static SnapshotBody NextBody(GameSnapshot snapshot, string words, SnapshotBody previous)
        {
            return ReturnHome.IsMatch(words) ? Body(snapshot, "Kerbin") ?? previous : LegBody(snapshot, words, previous);
        }

        /// <summary>The body a trip's words go to, from <paramref name="at"/>: the one after "to", else the one they name, else <paramref name="home"/> for a bare return.</summary>
        public static SnapshotBody TripTarget(GameSnapshot snapshot, string words, SnapshotBody at, string home)
        {
            SnapshotBody first = null;
            var firstAt = int.MaxValue;
            foreach (var body in snapshot.Bodies.Where(b => b.Name.Length > 0))
            {
                var match = Regex.Match(words, @"\bto\s+(?:the\s+)?" + Regex.Escape(body.Name) + @"\b", RegexOptions.IgnoreCase);
                if (match.Success && match.Index < firstAt)
                {
                    first = body;
                    firstAt = match.Index;
                }
            }
            if (first != null) return first;
            if (ReturnHome.IsMatch(words)) return Body(snapshot, home);
            // The first body named that isn't where the craft is ("Kerbin-Duna transfer" goes to Duna).
            var named = NamedBody(snapshot, at == null ? words : Regex.Replace(words, @"\b" + Regex.Escape(at.Name) + @"\b", " ", RegexOptions.IgnoreCase));
            if (named == null && BareReturn.IsMatch(words)) return Body(snapshot, home);
            return named ?? NamedBody(snapshot, words);
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
