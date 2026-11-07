// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>One remaining Entry of the Mission Plan.</summary>
    public sealed class Entry
    {
        /// <summary>The Entry's Maneuver Sequence: its index in the Mission Plan (0 first) and name.</summary>
        public int Step;
        public string Sequence = "";
        public string Body = "";
        /// <summary>The Kerbin return: the last Entry on Kerbin.</summary>
        public bool Return;
    }

    /// <summary>
    /// The Entries of a Mission Plan, read from its words (<see cref="SequenceWords"/>): legs that land on a body with air, reenter, or
    /// return to Kerbin. Manual legs, airless bodies and aerobraking aren't counted. And which heat shield an Entry uses: counted from
    /// the pod outward, the Entry Shield is shield number n (<see cref="ShieldNumber"/>), so shields are spent working back from the
    /// Last Entry (ADR 0018).
    /// </summary>
    internal static class Entries
    {
        /// <summary>
        /// The remaining Entries, in plan order, of the legs from <paramref name="firstStep"/> on that <paramref name="flown"/> takes (all, by
        /// default). The body is the one the words name, else the one the plan is at by then (as the Margin check follows it). A leg on the same
        /// body as the Entry before it, with no ascent between, is the same Entry (a deorbit leg, then its landing leg); the later leg names it.
        /// </summary>
        public static List<Entry> Find(GameSnapshot snapshot, IList<PlannedSequence> sequences, int firstStep, Func<PlannedSequence, bool> flown = null)
        {
            var entries = new List<Entry>();
            var body = SequenceWords.Body(snapshot, At(snapshot));
            var ascended = false;
            for (var i = firstStep; i < sequences.Count; i++)
            {
                var leg = sequences[i];
                if (flown != null && !flown(leg)) continue;
                var words = SequenceWords.Of(leg);
                var stays = BeforeLaterReturn(snapshot, sequences, i, flown);
                var returnsHome = stays == null && SequenceWords.ReturnHome.IsMatch(words);
                body = stays ?? SequenceWords.NextBody(snapshot, words, body);
                if (SequenceWords.Ascent.IsMatch(words)) ascended = true;
                if (leg.Manual || body == null) continue;
                if (!SaysEntry(words, returnsHome)) continue;
                if (body.AtmosphereHeight <= 0 && body.SeaLevelAtm <= 0) continue;
                var last = entries.Count > 0 ? entries[entries.Count - 1] : null;
                if (last != null && last.Body == body.Name && !ascended) entries.Remove(last);
                ascended = false;
                entries.Add(new Entry { Step = i, Sequence = leg.Name, Body = body.Name, Return = body.Name == "Kerbin" });
            }
            // Only the last Kerbin Entry is the return; an earlier one (a Kerbin landing before a relaunch) is like any other.
            var home = entries.LastOrDefault(e => e.Return);
            foreach (var entry in entries) entry.Return = entry == home;
            return entries;
        }

        /// <summary>The words say this leg comes down through air: a landing, a reentry or a return home, but not aerobraking alone.</summary>
        private static bool SaysEntry(string words, bool returnsHome)
        {
            var landing = SequenceWords.Landing.IsMatch(words);
            return (landing || SequenceWords.Reentry.IsMatch(words) || returnsHome) && (landing || !SequenceWords.Aero.IsMatch(words));
        }

        /// <summary>
        /// The body a leg stays at when the return home in its intent is only prose about a later leg (#838: "Land on Duna before the return to
        /// Kerbin"), else null. Landings and reentries in an intent still count, and so does a return no later leg flies, as a recorded plan's
        /// leg often goes home in its intent alone ("Ascend from the Mun", "then return toward Kerbin"): a missed Entry drops too much, and a drop
        /// can't be undone. So the return gives way only when the name doesn't say it or place the leg at Kerbin, the leg doesn't ascend before it
        /// lands, the words name one other body and name it before the return, and a later leg of this vessel is an Entry at Kerbin, followed as
        /// the reader follows it, with no launch from Kerbin before it. Any doubt reads the return as this leg's, which only ever keeps a stage.
        /// </summary>
        private static SnapshotBody BeforeLaterReturn(GameSnapshot snapshot, IList<PlannedSequence> sequences, int step, Func<PlannedSequence, bool> flown)
        {
            var leg = sequences[step];
            var words = SequenceWords.Of(leg);
            var said = SequenceWords.ReturnHome.Match(words);
            if (!said.Success || SequenceWords.ReturnHome.IsMatch(leg.Name) || IsKerbin(SequenceWords.NamedBody(snapshot, leg.Name))) return null;
            var action = SequenceWords.ActionWords(leg);
            var up = SequenceWords.Ascent.Match(action);
            var down = SequenceWords.Landing.Match(action);
            if (up.Success && (!down.Success || up.Index < down.Index)) return null;
            var away = SequenceWords.SoleAwayBody(snapshot, words);
            if (away == null || SequenceWords.NamedBody(snapshot, words.Substring(0, said.Index)) != away) return null;
            var at = away;
            for (var i = step + 1; i < sequences.Count; i++)
            {
                var later = sequences[i];
                if (flown != null && !flown(later)) continue;
                var laterWords = SequenceWords.Of(later);
                var returns = SequenceWords.ReturnHome.IsMatch(laterWords);
                var from = at;
                at = SequenceWords.NextBody(snapshot, laterWords, at);
                if (SequenceWords.Ascent.IsMatch(laterWords) && (IsKerbin(from) || FromKerbin.IsMatch(laterWords) || (IsKerbin(at) && !returns))) return null;
                if (!later.Manual && IsKerbin(at) && SaysEntry(laterWords, returns)) return away;
            }
            return null;
        }

        private static readonly Regex FromKerbin = new Regex(@"\bfrom\s+Kerbin\b", RegexOptions.IgnoreCase);

        private static bool IsKerbin(SnapshotBody body)
        {
            return body != null && body.Name == "Kerbin";
        }

        /// <summary>
        /// n for the Entry a reentry flown in step <paramref name="step"/> (0 first) is, by the vessel it flies (<paramref name="child"/>, a
        /// Child Vessel's name, or "" for the craft): the Entries left, this one included, plus one if a sequence after the final Entry has
        /// delta-v. This reentry is the plan's next Entry when that is on the body the craft is at; otherwise it is one more: words the reader
        /// can't place, a hop before a trip, or a step counted ahead of the craft. A doubt keeps the stage, as dropping it can't be undone. No
        /// Mission Plan is 1, the first shield's rule.
        /// </summary>
        public static int ShieldNumber(GameSnapshot snapshot, IList<PlannedSequence> sequences, int step, string child)
        {
            if (sequences == null || sequences.Count == 0) return 1;
            var first = Math.Max(0, step);
            Func<PlannedSequence, bool> flown = s => Flies(snapshot, s, child);
            var entries = Find(snapshot, sequences, first, flown);
            var placed = entries.Count > 0 && string.Equals(entries[0].Body, At(snapshot), StringComparison.OrdinalIgnoreCase);
            var left = placed ? entries.Count : entries.Count + 1;
            var final = entries.Count > 0 ? entries[entries.Count - 1].Step : first;
            return left + (BurnsAfter(sequences, final, s => flown(s) && s.DeltaV > 0) ? 1 : 0);
        }

        /// <summary>A sequence after step <paramref name="final"/> burns: so the final Entry isn't the Last Entry, and each Entry's shield number is one more.</summary>
        public static bool BurnsAfter(IList<PlannedSequence> sequences, int final, Func<PlannedSequence, bool> burns)
        {
            return sequences.Skip(final + 1).Any(burns);
        }

        /// <summary>
        /// Whether the sequence in step <paramref name="step"/> (0 first), for the vessel it names, leads to the Last Entry on the body the craft is at (#828): one Entry left, with no
        /// sequence after it that has delta-v. A doubt <see cref="ShieldNumber"/> counts as another Entry says no, and so does no Mission Plan.
        /// </summary>
        public static bool IsLastEntry(GameSnapshot snapshot, IList<PlannedSequence> sequences, int step)
        {
            if (sequences == null || step < 0 || step >= sequences.Count) return false;
            var vessel = sequences[step].Vessel;
            var child = MissionCards.FliesCraft(snapshot.Craft.Name, vessel) ? "" : vessel.Trim();
            return Find(snapshot, sequences, step, s => Flies(snapshot, s, child)).Count > 0 && ShieldNumber(snapshot, sequences, step, child) == 1;
        }

        /// <summary>
        /// The Craft Check's Warning (#823): each Entry of the Mission Goal's <paramref name="legs"/> with no Entry Shield that the player
        /// named as hot: on Eve or Laythe, or in words that say it comes straight in from a transfer. By name, with no figure: no heating
        /// has been measured off Kerbin, so Duna from low orbit stays quiet until it is (ADR 0018). The goal's clauses carry no budget, so a
        /// later ascent, trip or docking in words counts as burns to come.
        /// </summary>
        public static IList<MarginFinding> HotWithNoShield(GameSnapshot snapshot, IList<PlannedSequence> legs)
        {
            var findings = new List<MarginFinding>();
            var entries = Find(snapshot, legs, 0);
            if (entries.Count == 0) return findings;
            var rule = CraftCheckRules.Find(CraftCheckRules.HotEntry);
            var shields = snapshot.Craft.Parts.Count(p => p.Tags.Contains("heat_shield"));
            var tail = BurnsAfter(legs, entries[entries.Count - 1].Step, BurnsInWords) ? 1 : 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var number = entries.Count - i + tail;
                if (number <= shields || !IsHot(entry, legs[entry.Step])) continue;
                findings.Add(new MarginFinding
                {
                    Code = rule.Code, Severity = rule.Severity, Title = rule.Title,
                    Explanation = "The " + entry.Body + " entry for \"" + entry.Sequence + "\" needs heat shield number " + number + " from the pod and the craft has " + shields
                        + ", so it flies with no Entry Shield",
                });
            }
            return findings;
        }

        private static bool IsHot(Entry entry, PlannedSequence leg)
        {
            return HotBodies.Contains(entry.Body) || SequenceWords.StraightIn.IsMatch(SequenceWords.Of(leg));
        }

        private static readonly HashSet<string> HotBodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Eve", "Laythe" };

        internal static bool BurnsInWords(PlannedSequence leg)
        {
            var words = SequenceWords.Of(leg);
            return leg.DeltaV > 0 || SequenceWords.Ascent.IsMatch(words) || SequenceWords.Trip.IsMatch(words) || SequenceWords.Docking.IsMatch(words);
        }

        /// <summary>Whether the sequence flies this vessel: <paramref name="child"/>'s own, or with "" the Mission's craft's.</summary>
        internal static bool Flies(GameSnapshot snapshot, PlannedSequence sequence, string child)
        {
            return string.IsNullOrEmpty(child)
                ? MissionCards.FliesCraft(snapshot.Craft.Name, sequence.Vessel)
                : string.Equals((sequence.Vessel ?? "").Trim(), child, StringComparison.OrdinalIgnoreCase);
        }

        internal static string At(GameSnapshot snapshot)
        {
            return snapshot.Orbit != null && snapshot.Orbit.Body.Length > 0 ? snapshot.Orbit.Body : snapshot.Body;
        }
    }
}
