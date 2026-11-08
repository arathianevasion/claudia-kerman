// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #818: when a manual stretch may be planned, and when a Failed Debrief is only a landing miss. A mod rule, not a prompt rule: the
    /// words below cost nothing in the prefix, and the refusal's and the send-back's lines are sent only when they fire.
    /// <para>
    /// A manual stretch stands if the player asked for one (one word list, read in order from the Mission Goal, the player's typed
    /// words and their taking Manual Control, a later "no" cancelling an earlier yes), or if it serves EVA science the plan or a
    /// Debrief already names. Otherwise it is refused once with the reason; a second one stops the Mission with a request to the
    /// player (Auto Mode included). The answer is kept in the Mission as it goes (<see cref="Fold"/>), so a load keeps it.
    /// </para>
    /// </summary>
    public static class ManualGrounds
    {
        private static readonly Regex Asks = new Regex(@"\b(by hand|manually|manual|i'll fly|let me fly|i will fly)\b", RegexOptions.CultureInvariant);

        /// <summary>A negation shortly before an ask word in one clause: "don't fly it manually", "avoid manual stretches", "nothing manual".</summary>
        private static readonly Regex NegatedAsk = new Regex(@"\b(no|not|never|nothing|without|avoid|avoiding|instead of|don't|dont|do not|doesn't|won't|wont|nor)\b[^;,]{0,30}\b(manual|manually|by hand)\b|\b(don't|dont|do not) hand over\b", RegexOptions.CultureInvariant);

        private static readonly string[] Precision = { "precision", "land exactly", "land on target", "pinpoint" };

        /// <summary>A distance tied to the landing, in one clause: "land within 100 m of the pad".</summary>
        private static readonly Regex LandWithin = new Regex(@"\b(land|landing|touch ?down|set down)\b[^.!?;,]{0,40}\bwithin\s+\d+(\.\d+)?\s*(km|kilomet|met(er|re)s?\b|m(?![\w/]))", RegexOptions.CultureInvariant);

        private static readonly Regex EvaScience = new Regex(@"(?<!\b(no|not|without|nor)\s)\bEVA science\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] LandingBlocks = { "return", "reentry", "deorbit", "land" };

        /// <summary>
        /// The player's typed words in a Mission log (#873): the lines flagged <see cref="LogEntry.Typed"/>. A log with no flagged line
        /// (a Mission saved before the flag) falls back to its Player lines that don't start with a <see cref="NotTyped"/> prefix.
        /// </summary>
        public static IEnumerable<string> TypedWords(IEnumerable<LogEntry> log)
        {
            var lines = log.Where(e => e.Kind == LogKind.Player).ToList();
            return lines.Any(e => e.Typed)
                ? lines.Where(e => e.Typed).Select(e => e.Text)
                : lines.Where(e => !NotTyped.Any(p => e.Text.StartsWith(p, StringComparison.Ordinal))).Select(e => e.Text);
        }

        /// <summary>The answer after reading a Mission log's typed words in order, from the Mission Goal's.</summary>
        public static bool Asked(string goal, IEnumerable<LogEntry> log)
        {
            return TypedWords(log).Aggregate(Asked(goal), Fold);
        }

        /// <summary>The log lines of the player that are not their typed words; read only for a log saved before the Typed flag.</summary>
        public static readonly string[] NotTyped = { "Mission Goal: ", "Reduced Mission Goal approved: ", "Mission Settings: ", "Choose model: ", "Transfer Window to ", "EVA science done by hand", "Re-plan: ", "Abort." };

        /// <summary>The words say yes (true) or no (false) to flying a manual stretch; null when they say neither. A question is no answer.</summary>
        public static bool? Says(string words)
        {
            bool? said = null;
            foreach (var sentence in Regex.Split(Normalize(words), @"(?<=[.!?\n])"))
            {
                var s = sentence.Trim();
                if (s.EndsWith("?", StringComparison.Ordinal)) continue;
                if (NegatedAsk.IsMatch(s)) said = false;
                else if (Asks.IsMatch(s)) said = true;
            }
            return said;
        }

        /// <summary>The answer after one more of the player's words: theirs if they say yes or no, else the one before.</summary>
        public static bool Fold(bool asked, string words)
        {
            return Says(words) ?? asked;
        }

        /// <summary>The Mission Goal's own word on it.</summary>
        public static bool Asked(string goal)
        {
            return Fold(false, goal);
        }

        /// <summary>The plan names EVA science, or a Debrief's eva_science is still named.</summary>
        public static bool ServesEva(SequencePlan plan, string evaScience)
        {
            if (!string.IsNullOrEmpty(evaScience)) return true;
            return new[] { plan.Name, plan.Intent, plan.Why, plan.Target, plan.Tolerances }.Any(t => t != null && EvaScience.IsMatch(t));
        }

        /// <summary>The Mission Goal makes the landing spot the point.</summary>
        public static bool PrecisionGoal(string goal)
        {
            var said = Normalize(goal);
            return Precision.Any(said.Contains) || LandWithin.IsMatch(said);
        }

        /// <summary>The script calls a landing block: <c>return</c>, <c>reentry</c>, <c>deorbit</c> or <c>land</c>.</summary>
        public static bool LandingStep(string script)
        {
            return !string.IsNullOrWhiteSpace(script) && BlockOptionCheck.Calls(script).Any(c => LandingBlocks.Contains(c.Block));
        }

        /// <summary>The craft is landed or splashed down and no Kerbal aboard at the run's start has a recorded death since (#874); one on EVA or on another vessel is alive.</summary>
        public static bool DownAlive(GameSnapshot snapshot, IEnumerable<string> crewAtStart, IDictionary<string, double> deaths, double runStartUt)
        {
            // Null: the crew at the start was never read, so none can be called alive.
            if (snapshot == null || crewAtStart == null || (snapshot.Situation != "LANDED" && snapshot.Situation != "SPLASHED")) return false;
            return !crewAtStart.Any(name => deaths.ContainsKey(name) && deaths[name] >= runStartUt);
        }

        public static string Refusal(string planName)
        {
            return "Refused: the Sequence Plan " + MissionPrompts.Quoted(planName) + " is a manual stretch, and the player didn't ask to fly one and the Mission Goal doesn't need one."
                + " Plan it with a script, or fly the best you can and report the miss.";
        }

        public const string SendBack = "A landing miss alone is Off-Nominal; if something else fell short, reply Failed again and name it.";

        private static string Normalize(string text)
        {
            return (text ?? "").Replace('’', '\'').ToLowerInvariant();
        }
    }
}
