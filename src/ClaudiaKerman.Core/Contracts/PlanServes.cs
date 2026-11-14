// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>One Contract Requirement the player chose on the Contract check card, kept for the plan check (#1024).</summary>
    public sealed class ChosenRequirement
    {
        /// <summary>The Requirement's key: its Contract's GUID and its place in the tree.</summary>
        public string Key = "";
        public string Title = "";
        /// <summary>What it asks, "orbit the Mun".</summary>
        public string Words = "";
        public string Body = "";
        public ContractReach Reach;
        /// <summary>Met only once the craft is home again.</summary>
        public bool Return;
    }

    /// <summary>The Contract Route's record of one chosen Requirement: the leg of the Mission Plan that serves it, -1 when none does.</summary>
    public sealed class ServedRequirement
    {
        public string Key = "";
        public string Title = "";
        public string Words = "";
        public int Leg = -1;
        public string LegName = "";
    }

    /// <summary>
    /// #1024: Core's check of Claudia's Mission Plan against the Contract Requirements the player chose (ADR 0021: the mod's own logic;
    /// ADR 0012: at the one plan proposal). Each leg is read by what its words do (go to a body, capture, land, come home, as the Craft
    /// Check reads them), and each Requirement is assigned the first leg that can meet it: a leg that takes the craft to that body, at
    /// least as far as the Requirement asks (a landing meets an orbit; a flyby doesn't). A Requirement no leg meets is a problem for
    /// Claudia to fix before the player sees the plan. The assignment is the Serves tags, so they come from the Requirements and the
    /// plan's legs, never from anything Claudia says a leg serves. The player's own typed words are tagged too, where a leg serves them.
    /// </summary>
    internal sealed class PlanServes
    {
        private static readonly Regex Orbit = new Regex(@"\borbit(?:s|ing|ed)?\b", RegexOptions.IgnoreCase);

        /// <summary>What Claudia must fix, one finding per Requirement left unserved or unmeetable; empty when the plan covers them all.</summary>
        public readonly List<string> Problems = new List<string>();
        /// <summary>The Serves tags of each leg of the plan, in the plan's order (the player's words first).</summary>
        public readonly List<string[]> Legs = new List<string[]>();
        /// <summary>Each chosen Requirement and the leg that serves it.</summary>
        public readonly List<ServedRequirement> Record = new List<ServedRequirement>();

        private sealed class Touch
        {
            public int Leg;
            public int Order;
            public string Body = "";
            public ContractReach Reach;
            /// <summary>The craft comes home at this touch (Body is home).</summary>
            public bool Home;
        }

        /// <param name="from">#1025: the first leg that may serve a Requirement; the legs before it are walked only for where the craft is.</param>
        public static PlanServes Check(GameSnapshot snapshot, MissionPlan plan, IList<ChosenRequirement> chosen, string words, string home, int from = 0)
        {
            var result = new PlanServes();
            var touches = Read(snapshot, plan, home).Where(t => t.Leg >= from).ToList();
            var tags = plan.Sequences.Select(_ => new List<string>()).ToList();
            var reading = WordsReading.Read(snapshot, words, home);
            foreach (var need in reading.Needs)
            {
                var leg = touches.FirstOrDefault(t => !t.Home && t.Body == need.Key && t.Reach >= need.Value);
                if (leg != null) Tag(tags[leg.Leg], ContractCheck.WordsTag);
            }
            if (SequenceWords.ReturnHome.IsMatch(words ?? ""))
            {
                var back = touches.FirstOrDefault(t => t.Home);
                if (back != null) Tag(tags[back.Leg], ContractCheck.WordsTag);
            }
            foreach (var requirement in chosen)
            {
                var served = new ServedRequirement { Key = requirement.Key, Title = requirement.Title, Words = requirement.Words };
                result.Record.Add(served);
                var by = ServingTouch(touches, requirement, result.Problems, plan);
                if (by == null) continue;
                served.Leg = by.Leg;
                served.LegName = plan.Sequences[by.Leg].Name;
                Tag(tags[by.Leg], requirement.Title);
            }
            result.Legs.AddRange(tags.Select(t => t.ToArray()));
            return result;
        }

        private static void Tag(List<string> tags, string tag)
        {
            if (tag.Length > 0 && !tags.Contains(tag)) tags.Add(tag);
        }

        /// <summary>The leg that serves <paramref name="requirement"/>; null, with the finding, when none can.</summary>
        private static Touch ServingTouch(IList<Touch> touches, ChosenRequirement requirement, List<string> problems, MissionPlan plan)
        {
            var visit = touches.FirstOrDefault(t => !t.Home && t.Body == requirement.Body && t.Reach >= requirement.Reach);
            var what = "\"" + requirement.Title + "\" needs \"" + requirement.Words + "\"";
            if (visit == null)
            {
                var near = touches.Where(t => !t.Home && t.Body == requirement.Body).OrderByDescending(t => t.Reach).FirstOrDefault();
                problems.Add(near == null
                    ? what + ", and no leg of the plan goes there: add a leg that does"
                    : what + ", but leg " + (near.Leg + 1) + " \"" + plan.Sequences[near.Leg].Name + "\" only " + Does(near.Reach) + " "
                        + ContractKinds.Named(requirement.Body) + ": make that leg " + Needs(requirement.Reach) + ", or add one that does");
                return null;
            }
            if (!requirement.Return) return visit;
            var back = touches.FirstOrDefault(t => t.Home && t.Order >= visit.Order);
            if (back == null) problems.Add(what + ", and no leg of the plan brings the craft home after it: add the return to Kerbin");
            return back;
        }

        private static string Does(ContractReach reach)
        {
            return reach == ContractReach.Flyby ? "flies by" : reach == ContractReach.Orbit ? "orbits" : "lands on";
        }

        private static string Needs(ContractReach reach)
        {
            return reach == ContractReach.Flyby ? "fly by it" : reach == ContractReach.Orbit ? "capture into orbit round it" : "land on it";
        }

        /// <summary>Where each leg takes the craft, in the plan's order; the Craft Check's reading of a leg's words (<see cref="SequenceWords"/>).</summary>
        private static List<Touch> Read(GameSnapshot snapshot, MissionPlan plan, string home)
        {
            var touches = new List<Touch>();
            var homeBody = SequenceWords.Body(snapshot, home);
            // The whole plan is walked, flown legs included (a revision keeps them), so it starts where a Mission does: at home.
            var at = homeBody;
            for (var i = 0; i < plan.Sequences.Count; i++)
            {
                var sequence = plan.Sequences[i];
                if (!MissionCards.FliesCraft(snapshot.Craft.Name, sequence.Vessel)) continue;
                var words = SequenceWords.ActionWords(sequence);
                var landing = SequenceWords.Landing.IsMatch(words);
                var captures = SequenceWords.Capture.IsMatch(words) || Orbit.IsMatch(words);
                var trips = SequenceWords.Trip.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words);
                var target = trips ? SequenceWords.TripTarget(snapshot, words, at, home) : null;
                var goesHome = trips && target != null && target.Name == home && at != null && at.Name != home;
                var add = new System.Action<string, ContractReach, bool>((body, reach, back) =>
                    touches.Add(new Touch { Leg = i, Order = touches.Count, Body = body, Reach = reach, Home = back }));
                if (goesHome && landing && SequenceWords.Landing.Match(words).Index < SequenceWords.Trip.Match(words).Index)
                {
                    // "Land on the Mun and return to Kerbin": down there first, then home.
                    var named = SequenceWords.NamedBody(snapshot, words);
                    var site = named != null && named.Name != home ? named : at;
                    if (site != null && site.Name != home) add(site.Name, ContractReach.Land, false);
                    landing = false;
                }
                if (goesHome)
                {
                    // Coming home passes the body it leaves, at least as a flyby; a capture there is its own leg's.
                    add(home, ContractReach.Land, true);
                    at = target;
                    continue;
                }
                if (trips && target != null && at != null && target != at)
                {
                    var reach = SequenceWords.Flyby.IsMatch(words) ? ContractReach.Flyby : landing ? ContractReach.Land : captures ? ContractReach.Orbit : ContractReach.Flyby;
                    add(target.Name, reach, false);
                    at = target;
                    continue;
                }
                var here = SequenceWords.LegBody(snapshot, words, at);
                if (here == null) continue;
                if (landing) add(here.Name, ContractReach.Land, false);
                else if (captures || (SequenceWords.Ascent.IsMatch(words) && here.Name == home)) add(here.Name, ContractReach.Orbit, false);
                else if (SequenceWords.Flyby.IsMatch(words)) add(here.Name, ContractReach.Flyby, false);
                at = here;
            }
            return touches;
        }
    }
}
