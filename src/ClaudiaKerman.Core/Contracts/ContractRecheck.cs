// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>A chosen Contract whose planned completion now breaks the spare rule.</summary>
    public sealed class LateContract
    {
        public string Guid = "";
        public string Title = "";
        /// <summary>When the route now finishes it, UT s.</summary>
        public double DoneUt;
        public double DeadlineUt;
        /// <summary>"planned to finish about Y3, D40, too near its deadline Y3, D52": the finding's words, and the reason a revised plan may drop it.</summary>
        public string Reason = "";
    }

    /// <summary>
    /// #1026: the deadline recheck at every Debrief and Due Time (ADR 0021: the mod's own logic, no Claude call; the finding reaches Claudia
    /// as one line of data, never a raw Contract). It prices what the chosen Contracts still need from where the craft is now with the
    /// route pricer's figures (<see cref="ContractRoutes"/>), never a planner search, and applies the same spare rule as the Contract check
    /// (<see cref="ContractCheck.BreaksSpare"/>). A Contract that is completed, ended or has no deadline gives nothing; Requirements the game
    /// has already marked met no longer count against the time.
    /// </summary>
    public static class ContractRecheck
    {
        /// <param name="chosen">The Requirements the player chose.</param>
        /// <param name="words">The player's own words for the Mission.</param>
        /// <param name="closed">The chosen Contracts that are completed or ended, by GUID: nothing to recheck.</param>
        public static List<LateContract> Check(GameSnapshot snapshot, IList<ChosenRequirement> chosen, string words, string home, ICollection<string> closed, out List<string> rechecked)
        {
            rechecked = new List<string>();
            var late = new List<LateContract>();
            if (snapshot == null || snapshot.Contracts == null) return late;
            var open = new List<KeyValuePair<SnapshotContract, List<ChosenRequirement>>>();
            foreach (var group in chosen.GroupBy(r => ContractWatch.GuidOf(r.Key)))
            {
                var contract = snapshot.Contracts.Accepted.FirstOrDefault(c => c.Guid == group.Key && c.State == "Active");
                if (contract == null || closed.Contains(group.Key) || !contract.DeadlineUt.HasValue) continue;
                var unmet = group.Where(r => { var found = ContractWatch.Find(contract.Requirements, r.Key); return found == null || found.State != "Complete"; }).ToList();
                rechecked.Add(group.Key);
                if (unmet.Count > 0) open.Add(new KeyValuePair<SnapshotContract, List<ChosenRequirement>>(contract, unmet));
            }
            if (open.Count == 0) return late;
            var needs = new Dictionary<string, ContractReach>();
            foreach (var requirement in open.SelectMany(o => o.Value))
            {
                ContractReach had;
                if (!needs.TryGetValue(requirement.Body, out had) || requirement.Reach > had) needs[requirement.Body] = requirement.Reach;
            }
            var route = new ContractRoutes(snapshot, words, home).Best(needs, open.SelectMany(o => o.Value).Any(r => r.Return), r => true);
            foreach (var one in open)
            {
                var done = one.Value.Select(r => route.DoneUt(new ReadRequirement { Body = r.Body, Reach = r.Reach, Return = r.Return }, home))
                    .Where(d => d.HasValue).Select(d => d.Value).DefaultIfEmpty(snapshot.Ut).Max();
                var deadline = one.Key.DeadlineUt.Value;
                if (!ContractCheck.BreaksSpare(done, deadline, snapshot.Ut)) continue;
                var title = one.Value[0].Title;
                late.Add(new LateContract
                {
                    Guid = one.Key.Guid, Title = title, DoneUt = done, DeadlineUt = deadline,
                    Reason = "planned to finish about " + KerbinTime.ShortDate(done) + ", too near its deadline " + KerbinTime.ShortDate(deadline),
                });
            }
            return late;
        }
    }
}
