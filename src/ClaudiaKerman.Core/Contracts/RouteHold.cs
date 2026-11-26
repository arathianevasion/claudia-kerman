// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1378 (ADR 0022): a proposed Mission Plan held to the Contract Route's Body Visits. Away from home, each body is visited once, in the
    /// Route's order. The plan's visits are read from its legs as the plan check reads them: legs in a row at one body are one visit (its first leg names it), and
    /// a flyby is passing the body, not a visit, unless the plan never stops there. A body the Route doesn't visit isn't ordered here.
    /// </summary>
    internal static class RouteHold
    {
        /// <summary>A body the craft reaches away from home on one leg of the plan, and how far it goes there.</summary>
        internal sealed class Reached
        {
            public int Leg;
            public string Body = "";
            public ContractReach Reach;
        }

        /// <summary>What Claudia must fix: each repeated visit, and the first pair of bodies out of the Route's order; empty when the plan follows the Route.</summary>
        /// <param name="route">The Route's bodies away from home, in the order flown.</param>
        /// <param name="from">#1379: the plan's first leg still to fly; the legs before it are flown and not held to the Route.</param>
        public static List<string> Problems(GameSnapshot snapshot, MissionPlan plan, IList<string> route, string home, int from = 0)
        {
            var problems = new List<string>();
            var visits = Visits(PlanServes.BodiesReached(snapshot, plan, home, from));
            var firstVisits = new List<Reached>();
            foreach (var visit in visits)
            {
                var firstVisit = firstVisits.FirstOrDefault(v => v.Body == visit.Body);
                if (firstVisit == null)
                {
                    firstVisits.Add(visit);
                    continue;
                }
                problems.Add("leg " + (visit.Leg + 1) + " \"" + plan.Sequences[visit.Leg].Name + "\" visits " + ContractKinds.Named(visit.Body) + " a second time: the Contract Route has one Body Visit there, "
                    + "so serve its Requirements in the visit at leg " + (firstVisit.Leg + 1) + " and take the second visit out");
            }
            var order = firstVisits.Select(v => v.Body).Where(route.Contains).ToList();
            for (var i = 0; i + 1 < order.Count; i++)
            {
                if (route.IndexOf(order[i]) < route.IndexOf(order[i + 1])) continue;
                var early = order.Skip(i + 1).First(b => route.IndexOf(b) < route.IndexOf(order[i]));
                problems.Add("the plan flies " + ContractKinds.Named(order[i]) + " before " + ContractKinds.Named(early) + ", but the Contract Route flies " + ContractKinds.Named(early)
                    + " first: fly the Body Visits in the Route's order (" + string.Join(", ", route.Select(ContractKinds.Named).ToArray()) + ")");
                break;
            }
            return problems;
        }

        /// <summary>
        /// #1381 (ADR 0022): the next body the plan goes on to when <paramref name="reached"/>[<paramref name="i"/>] only flies by its body on
        /// the way there (a later leg reaches another body away from home); null when it stops there, or the plan goes no further away.
        /// </summary>
        internal static Reached PassedOnTheWay(IList<Reached> reached, int i)
        {
            var one = reached[i];
            if (one.Reach > ContractReach.Flyby) return null;
            return reached.Skip(i + 1).FirstOrDefault(r => r.Leg > one.Leg && r.Body != one.Body);
        }

        /// <summary>The plan's visits away from home, in order: a run of legs at one body is one visit; a flyby is left out unless it is the only visit of its body.</summary>
        private static List<Reached> Visits(IList<Reached> reached)
        {
            var stopped = new HashSet<string>(reached.Where(r => r.Reach > ContractReach.Flyby).Select(r => r.Body));
            var visits = new List<Reached>();
            foreach (var one in reached)
            {
                // Passing a body is not a visit: a flyby counts only as the first visit of a body the plan never stops at.
                if (one.Reach <= ContractReach.Flyby && (stopped.Contains(one.Body) || visits.Any(v => v.Body == one.Body))) continue;
                if (visits.Count > 0 && visits[visits.Count - 1].Body == one.Body) continue;
                visits.Add(one);
            }
            return visits;
        }
    }
}
