// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1036: the player's typed words read into the same body-and-reach model the Contract Requirements are read into, so one leg can serve
    /// both. A clause that goes somewhere ("land on the Mun", "orbit Minmus", "fly to Minmus and capture into orbit") is a need of that
    /// body at that reach; a clause that rules something out ("don't land on the Mun", "only fly by", "without landing") is a limit on the
    /// reach. Clauses that are neither are left to the Craft Check's own reading of the words. The words always win: a limit is never
    /// loosened for a Contract, and a need is never dropped for one.
    /// </summary>
    internal sealed class WordsReading
    {
        /// <summary>The limit that holds at every body.</summary>
        public const string Anywhere = "*";

        private static readonly Regex Clause = new Regex(@"[.;:,!?]|\b(?:and|then)\b", RegexOptions.IgnoreCase);
        private static readonly Regex Negated = new Regex(@"\b(?:don['’]?t|do\s+not|never|no|not|without|avoid(?:ing)?|skip(?:ping)?)\b", RegexOptions.IgnoreCase);
        private static readonly Regex Only = new Regex(@"\b(?:only|just|merely)\b", RegexOptions.IgnoreCase);
        private static readonly Regex Orbit = new Regex(@"\borbit(?:s|ing|ed)?\b", RegexOptions.IgnoreCase);
        private static readonly Regex Goes = new Regex(@"\b(?:(?:go|fly|head|travel|transfer|get|burn)\s+(?:back\s+)?to|transfer|intercept|visit)\b", RegexOptions.IgnoreCase);

        /// <summary>The furthest the words take each body.</summary>
        public readonly Dictionary<string, ContractReach> Needs = new Dictionary<string, ContractReach>();
        /// <summary>The player's own clause behind each need, "land on the Mun".</summary>
        public readonly Dictionary<string, string> NeedWords = new Dictionary<string, string>();
        /// <summary>The most each body may be taken (<see cref="Anywhere"/> for all), and the clause that says so.</summary>
        public readonly Dictionary<string, ContractReach> Limits = new Dictionary<string, ContractReach>();
        public readonly Dictionary<string, string> LimitWords = new Dictionary<string, string>();
        /// <summary>The clauses read here, trimmed: the Craft Check's legs for them are priced as needs instead.</summary>
        public readonly HashSet<string> Consumed = new HashSet<string>();

        public static WordsReading Read(GameSnapshot snapshot, string words, string home)
        {
            var read = new WordsReading();
            var bodies = snapshot.Bodies.Select(b => b.Name).Where(n => n.Length > 0 && n != home).Distinct().ToList();
            string lastBody = null;
            foreach (var raw in Clause.Split(words ?? ""))
            {
                var clause = raw.Trim();
                if (clause.Length == 0) continue;
                var named = bodies.Where(b => Regex.IsMatch(clause, @"\b" + Regex.Escape(b) + @"\b", RegexOptions.IgnoreCase)).ToList();
                var landing = SequenceWords.Landing.IsMatch(clause);
                var flyby = SequenceWords.Flyby.IsMatch(clause);
                var orbit = Orbit.IsMatch(clause) || SequenceWords.Capture.IsMatch(clause);
                if (Negated.IsMatch(clause) && (landing || orbit || flyby))
                {
                    // "don't land" holds the body to an orbit; "don't orbit" to a flyby.
                    read.Limit(named, landing ? ContractReach.Orbit : ContractReach.Flyby, clause);
                    continue;
                }
                if (Only.IsMatch(clause) && flyby)
                {
                    read.Limit(named, ContractReach.Flyby, clause);
                    read.Need(named, ContractReach.Flyby, clause);
                    continue;
                }
                var reach = landing ? ContractReach.Land : orbit ? ContractReach.Orbit : flyby || Goes.IsMatch(clause) ? ContractReach.Flyby : (ContractReach?)null;
                if (reach == null) continue;
                // "fly to Minmus and capture into orbit": the clause that names no body is about the one before it.
                if (named.Count == 0 && lastBody != null && reach != ContractReach.Flyby) named = new List<string> { lastBody };
                if (named.Count == 0) continue;
                read.Need(named, reach.Value, clause);
                lastBody = named[named.Count - 1];
            }
            return read;
        }

        private void Need(IEnumerable<string> bodies, ContractReach reach, string clause)
        {
            foreach (var body in bodies)
            {
                Consumed.Add(clause);
                ContractReach had;
                if (Needs.TryGetValue(body, out had) && had >= reach) continue;
                Needs[body] = reach;
                NeedWords[body] = clause;
            }
        }

        private void Limit(IList<string> bodies, ContractReach most, string clause)
        {
            Consumed.Add(clause);
            foreach (var body in bodies.Count == 0 ? new[] { Anywhere } : bodies.ToArray())
            {
                ContractReach had;
                if (Limits.TryGetValue(body, out had) && had <= most) continue;
                Limits[body] = most;
                LimitWords[body] = clause;
            }
        }

        /// <summary>The player's words that rule out <paramref name="reach"/> at <paramref name="body"/>; null when they don't.</summary>
        public string Forbids(string body, ContractReach reach)
        {
            foreach (var key in new[] { body, Anywhere })
            {
                ContractReach most;
                if (Limits.TryGetValue(key, out most) && reach > most) return LimitWords[key];
            }
            return null;
        }
    }
}
