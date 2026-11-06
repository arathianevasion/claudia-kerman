// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

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
                var returnsHome = SequenceWords.ReturnHome.IsMatch(words);
                body = SequenceWords.NextBody(snapshot, words, body);
                if (SequenceWords.Ascent.IsMatch(words)) ascended = true;
                if (leg.Manual || body == null) continue;
                var landing = SequenceWords.Landing.IsMatch(words);
                var entry = landing || SequenceWords.Reentry.IsMatch(words) || returnsHome;
                if (!entry || (SequenceWords.Aero.IsMatch(words) && !landing)) continue;
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

        /// <summary>
        /// n for the Entry a reentry flown in step <paramref name="step"/> (0 first) is, by the vessel it flies (<paramref name="child"/>, a
        /// Child Vessel's name, or "" for the craft): the Entries left, this one included, plus one if a sequence after the final Entry has
        /// delta-v. The Entry is the step's own or the next on the body the craft is at; with neither (words the reader can't place) this
        /// reentry is one more. No Mission Plan is 1, the first shield's rule.
        /// </summary>
        public static int ShieldNumber(GameSnapshot snapshot, IList<PlannedSequence> sequences, int step, string child)
        {
            if (sequences == null || sequences.Count == 0) return 1;
            var first = Math.Max(0, step);
            Func<PlannedSequence, bool> flown = s => Flies(snapshot, s, child);
            var entries = Find(snapshot, sequences, first, flown);
            var at = At(snapshot);
            var own = entries.FirstOrDefault(e => string.Equals(e.Body, at, StringComparison.OrdinalIgnoreCase));
            var left = own == null ? entries.Count + 1 : entries.Count(e => e.Step >= own.Step);
            var final = entries.Count > 0 ? entries[entries.Count - 1].Step : first;
            return left + (Enumerable.Range(final + 1, Math.Max(0, sequences.Count - final - 1)).Any(i => flown(sequences[i]) && sequences[i].DeltaV > 0) ? 1 : 0);
        }

        /// <summary>Whether the sequence flies this vessel: <paramref name="child"/>'s own, or with "" the Mission's craft's.</summary>
        private static bool Flies(GameSnapshot snapshot, PlannedSequence sequence, string child)
        {
            return string.IsNullOrEmpty(child)
                ? MissionCards.FliesCraft(snapshot.Craft.Name, sequence.Vessel)
                : string.Equals((sequence.Vessel ?? "").Trim(), child, StringComparison.OrdinalIgnoreCase);
        }

        private static string At(GameSnapshot snapshot)
        {
            return snapshot.Orbit != null && snapshot.Orbit.Body.Length > 0 ? snapshot.Orbit.Body : snapshot.Body;
        }
    }
}
