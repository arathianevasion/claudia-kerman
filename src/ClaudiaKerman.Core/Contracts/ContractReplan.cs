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
    /// #1025: the plan check's Contracts on a revised Mission Plan (ADR 0021, at the one plan proposal of ADR 0012). A replan only ever drops
    /// Contracts: a Contract the watch found can't complete (failed, expired, fallen back, a hold that timed out) may be left unserved, and
    /// the plan then drops it with the mod's reason. A Contract the player didn't choose is added only when the player asked for it in their
    /// typed words during the Mission (they name a body it needs) and Claudia names its title in the plan; it is then priced like any chosen
    /// one, by the plan check's legs. One Claudia names unasked is a problem for her to fix. A completed Contract needs no leg any more.
    /// </summary>
    public sealed class ContractReplan
    {
        public readonly List<string> Problems = new List<string>();
        /// <summary>The Serves tags of each leg of the plan.</summary>
        public List<string[]> Legs = new List<string[]>();
        /// <summary>Each Requirement kept or added and the leg serving it.</summary>
        public List<ServedRequirement> Record = new List<ServedRequirement>();
        /// <summary>The Requirements of the Contracts the plan adds at the player's asking.</summary>
        public readonly List<ChosenRequirement> Added = new List<ChosenRequirement>();
        /// <summary>The Contracts the plan drops, by GUID, with the mod's reason.</summary>
        public readonly Dictionary<string, string> Drops = new Dictionary<string, string>();

        /// <param name="droppable">The chosen Contracts that can't complete, or may not, by GUID with the reason (<see cref="ContractWatch.Droppable"/>).</param>
        /// <param name="completed">The chosen Contracts the game has completed.</param>
        /// <param name="asked">What the player typed during the Mission, after the Contract check.</param>
        /// <param name="ended">The droppable Contracts that can't complete at all (failed, expired, cancelled, gone): always dropped.</param>
        /// <param name="firstUnflown">The plan's first leg still to fly: a droppable Contract that may still complete is kept only when one of these serves it.</param>
        public static ContractReplan Check(GameSnapshot snapshot, MissionPlan plan, IList<ChosenRequirement> chosen, string words, string home,
            IDictionary<string, string> droppable, ICollection<string> completed, IList<string> asked, ICollection<string> ended = null, int firstUnflown = 0)
        {
            var result = new ContractReplan();
            var open = chosen.Where(r => !completed.Contains(ContractWatch.GuidOf(r.Key))).ToList();
            // A kept one is served by a leg still to fly (a retry or trim): that leg, not the flown one, is its record.
            var retries = new List<ServedRequirement>();
            foreach (var contract in open.Where(r => droppable.ContainsKey(ContractWatch.GuidOf(r.Key))).GroupBy(r => ContractWatch.GuidOf(r.Key)))
            {
                // #1032: only what the game hasn't marked met needs a leg still to fly: a flag retried after a landing already met.
                var unmet = contract.Where(r => !MetInGame(snapshot, contract.Key, r.Key)).ToList();
                var ahead = PlanServes.Check(snapshot, plan, unmet, words, home, firstUnflown).Record;
                var stillToFly = (ended == null || !ended.Contains(contract.Key)) && ahead.All(r => r.Leg >= 0);
                if (stillToFly) retries.AddRange(ahead);
                else result.Drops[contract.Key] = contract.First().Title + ": " + droppable[contract.Key];
            }
            var kept = open.Where(r => !result.Drops.ContainsKey(ContractWatch.GuidOf(r.Key))).ToList();
            AddAsked(snapshot, plan, chosen, home, asked, result);
            var served = PlanServes.Check(snapshot, plan, kept.Concat(result.Added).ToList(), words, home);
            result.Problems.AddRange(served.Problems);
            result.Problems.AddRange(ServingNothing(snapshot, plan, kept.Concat(result.Added).ToList(), words, home, asked, firstUnflown));
            result.Legs = served.Legs;
            result.Record = served.Record.Select(r => retries.FirstOrDefault(x => x.Key == r.Key) ?? r).ToList();
            MoveTags(served.Record, result.Record, result.Legs);
            return result;
        }

        /// <summary>
        /// #1379 (ADR 0022): a leg still to fly that takes the craft to a body away from home and serves no Requirement still open (the game
        /// doesn't show it met) and none of the player's words is sent back. Launch and return are legs at home, so they are never read here.
        /// </summary>
        private static List<string> ServingNothing(GameSnapshot snapshot, MissionPlan plan, IList<ChosenRequirement> kept, string words, string home, IList<string> asked, int firstUnflown)
        {
            var problems = new List<string>();
            if (kept.Count == 0) return problems;
            var open = kept.Where(r => !MetInGame(snapshot, ContractWatch.GuidOf(r.Key), r.Key)).ToList();
            var tags = PlanServes.Check(snapshot, plan, open, string.Join("\n", new[] { words ?? "" }.Concat(asked).ToArray()), home, firstUnflown).Legs;
            // A body with a Requirement still open keeps its legs: one may set up the work another serves (a transfer, then the science there).
            var wanted = new HashSet<string>(open.Select(r => r.Body).Concat(open.Where(r => r.Orbit != null).Select(r => r.Orbit.Body)));
            foreach (var leg in PlanServes.BodiesReached(snapshot, plan, home, firstUnflown).Where(r => !wanted.Contains(r.Body)).Select(r => r.Leg).Distinct())
                if (tags[leg].Length == 0)
                    problems.Add("leg " + (leg + 1) + " \"" + plan.Sequences[leg].Name + "\" serves no Requirement still open and none of your words: take it out, or say which open Requirement it serves");
            return problems;
        }

        private static bool MetInGame(GameSnapshot snapshot, string guid, string key)
        {
            var contract = snapshot == null || snapshot.Contracts == null ? null : snapshot.Contracts.Accepted.FirstOrDefault(c => c.Guid == guid);
            var found = contract == null ? null : ContractWatch.Find(contract.Requirements, key);
            return found != null && found.State == "Complete";
        }

        /// <summary>#1032: a retry's Serves tag goes on the leg still to fly that serves it, off the flown one unless another Requirement of its Contract is served there.</summary>
        private static void MoveTags(List<ServedRequirement> served, List<ServedRequirement> record, List<string[]> legs)
        {
            foreach (var was in served)
            {
                var now = record.First(r => r.Key == was.Key);
                if (now.Leg == was.Leg || now.Leg < 0 || now.Leg >= legs.Count) continue;
                if (was.Leg >= 0 && was.Leg < legs.Count && !record.Any(r => r.Title == was.Title && r.Leg == was.Leg))
                    legs[was.Leg] = legs[was.Leg].Where(t => t != was.Title).ToArray();
                if (!legs[now.Leg].Contains(now.Title)) legs[now.Leg] = legs[now.Leg].Concat(new[] { now.Title }).ToArray();
            }
        }

        private static void AddAsked(GameSnapshot snapshot, MissionPlan plan, IList<ChosenRequirement> chosen, string home, IList<string> asked, ContractReplan result)
        {
            if (snapshot == null || snapshot.Contracts == null) return;
            var text = string.Join("\n", new[] { plan.Why ?? "" }.Concat(plan.Sequences.SelectMany(s => new[] { s.Name ?? "", s.Intent ?? "" })).ToArray());
            var chosenGuids = new HashSet<string>(chosen.Select(r => ContractWatch.GuidOf(r.Key)));
            var playerWords = string.Join("\n", asked.ToArray());
            foreach (var contract in snapshot.Contracts.Accepted.Where(c => !chosenGuids.Contains(c.Guid)))
            {
                var title = ContractCheck.TitleOf(contract, home);
                if (text.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var plannable = ContractKinds.Read(contract, home).Where(r => r.Plannable).ToList();
                if (plannable.Count == 0 || !plannable.Any(r => Names(playerWords, r.Body)))
                {
                    result.Problems.Add("the plan names \"" + title + "\", a Contract the player didn't choose or ask for: a revised plan only ever drops Contracts, "
                        + "so take it out (the player can ask for it)");
                    continue;
                }
                result.Added.AddRange(plannable.Select(r => new ChosenRequirement { Key = r.Requirement.Key, Title = title, Words = r.Words, Body = r.Body, Reach = r.Reach, Return = r.Return, Test = r.Test == null ? null : r.Test.Copy(), Experiment = r.Experiment, Orbit = r.Orbit, ChildMay = r.ChildMay, Manual = r.Manual, Waypoint = r.Waypoint }));
            }
        }

        private static bool Names(string words, string body)
        {
            return body.Length > 0 && Regex.IsMatch(words, @"\b" + Regex.Escape(body) + @"\b", RegexOptions.IgnoreCase);
        }
    }
}
