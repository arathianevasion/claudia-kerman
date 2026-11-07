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
    /// A manual stretch stands if the player asked for one (one word list, read in order from the Mission Goal and the player's typed
    /// words, a later "no" cancelling an earlier yes; or they took Manual Control in this Mission), if the Mission Plan's step is
    /// itself manual (the player approved that plan), or if it serves EVA science the plan or a Debrief already names. Otherwise it
    /// is refused once with the reason; a second one stops the Mission with a request to the player (Auto Mode included).
    /// </para>
    /// </summary>
    public static class ManualGrounds
    {
        private static readonly string[] Asks = { "by hand", "manually", "manual", "i'll fly", "let me fly", "i will fly" };

        private static readonly string[] Declines = { "no manual", "don't hand over", "do not hand over", "dont hand over", "not by hand", "without manual" };

        /// <summary>A negation shortly before an ask word in one clause: "don't fly it manually", "never take manual control".</summary>
        private static readonly Regex NegatedAsk = new Regex(@"\b(no|not|never|without|don't|dont|do not|doesn't|won't|wont)\b[^.!?;,]{0,30}\b(manual|manually|by hand)\b", RegexOptions.CultureInvariant);

        private static readonly string[] Precision = { "precision", "land exactly", "land on target", "pinpoint" };

        private static readonly Regex WithinDistance = new Regex(@"\bwithin\s+\d+(\.\d+)?\s*(km|kilomet|met(er|re)s?\b|m(?![\w/]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Eva = new Regex(@"\bEVA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] LandingBlocks = { "return", "reentry", "deorbit", "land" };

        /// <summary>The log lines of the player that are not their typed words.</summary>
        private static readonly string[] NotTyped = { "Mission Goal: ", "Reduced Mission Goal approved: ", "Mission Settings: ", "Choose model: ", "Transfer Window to ", "EVA science done by hand" };

        /// <summary>The player asked to fly it: the last of the goal's and the player's words to say yes or no to it is a yes, or they took Manual Control.</summary>
        public static bool Asked(string goal, IEnumerable<string> playerWords, bool tookManual)
        {
            if (tookManual) return true;
            var asked = false;
            foreach (var words in new[] { goal }.Concat(playerWords ?? Enumerable.Empty<string>()))
            {
                var said = Normalize(words);
                if (Declines.Any(said.Contains) || NegatedAsk.IsMatch(said)) asked = false;
                else if (Asks.Any(said.Contains)) asked = true;
            }
            return asked;
        }

        /// <summary>The player's typed words in the Mission's log, in order (it is rebuilt from the transcript on a load).</summary>
        public static IEnumerable<string> TypedWords(IEnumerable<LogEntry> log)
        {
            return log.Where(e => e.Kind == LogKind.Player && !NotTyped.Any(p => e.Text.StartsWith(p, StringComparison.Ordinal))).Select(e => e.Text);
        }

        /// <summary>The plan names EVA science, or a Debrief's eva_science is still named.</summary>
        public static bool ServesEva(SequencePlan plan, string evaScience)
        {
            if (!string.IsNullOrEmpty(evaScience)) return true;
            return new[] { plan.Name, plan.Intent, plan.Why, plan.Target, plan.Tolerances }.Any(t => t != null && Eva.IsMatch(t));
        }

        /// <summary>The Mission Goal makes the landing spot the point.</summary>
        public static bool PrecisionGoal(string goal)
        {
            var said = Normalize(goal);
            return Precision.Any(said.Contains) || WithinDistance.IsMatch(said);
        }

        /// <summary>The script calls a landing block: <c>return</c>, <c>reentry</c>, <c>deorbit</c> or <c>land</c>.</summary>
        public static bool LandingStep(string script)
        {
            return !string.IsNullOrWhiteSpace(script) && BlockOptionCheck.Calls(script).Any(c => LandingBlocks.Contains(c.Block));
        }

        /// <summary>The craft is landed or splashed down and every Kerbal aboard at the run's start still is.</summary>
        public static bool DownAlive(GameSnapshot snapshot, IEnumerable<string> crewAtStart)
        {
            // Null: the crew at the start was never read, so none can be called alive.
            if (snapshot == null || crewAtStart == null || (snapshot.Situation != "LANDED" && snapshot.Situation != "SPLASHED")) return false;
            var now = snapshot.Crew.Select(c => c.Name).ToList();
            return crewAtStart.All(now.Contains);
        }

        public static string Refusal(string planName)
        {
            return "Refused: the Sequence Plan " + MissionPrompts.Quoted(planName) + " is a manual stretch, and the player didn't ask to fly one, the Mission Goal doesn't need one and it isn't EVA science."
                + " Plan it with a script, or fly the best you can and report the miss.";
        }

        public const string SendBack = "A landing miss alone is Off-Nominal; if something else fell short, reply Failed again and name it.";

        private static string Normalize(string text)
        {
            return (text ?? "").Replace('’', '\'').ToLowerInvariant();
        }
    }
}
