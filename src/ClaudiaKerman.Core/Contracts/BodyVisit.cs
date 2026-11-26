// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1377: where a Body Visit falls in the Contract Route.</summary>
    public enum BodyVisitStop
    {
        /// <summary>Home at the start: the launch, and the work done in orbit round home.</summary>
        Launch,
        /// <summary>A body away from home, visited once.</summary>
        Visit,
        /// <summary>Home at the end: the return, and what is met once the craft is home again.</summary>
        Return,
    }

    /// <summary>#1377: one Chosen Requirement in a Body Visit, with its Contract and whether the game shows it met.</summary>
    public sealed class VisitRequirement
    {
        public string Key = "";
        /// <summary>What it asks, "orbit the Mun".</summary>
        public string Words = "";
        /// <summary>Its Contract's title.</summary>
        public string Title = "";
        /// <summary>The game shows it Complete; open otherwise.</summary>
        public bool Met;
    }

    /// <summary>
    /// #1377 (ADR 0022): one stop at a body on the Contract Route, holding every Chosen Requirement served there, whichever Contract it
    /// comes from, in the order the Route prices the visit: arrive or capture, work in orbit, land, work on the ground.
    /// </summary>
    public sealed class BodyVisit
    {
        public string Body = "";
        public BodyVisitStop Stop;
        public List<VisitRequirement> Requirements = new List<VisitRequirement>();
    }

    /// <summary>#1377: the Contract Route's bodies grouped into Body Visits (<see cref="PricedRoute.Visits"/>).</summary>
    internal static class BodyVisits
    {
        /// <summary>
        /// The Body Visits of <paramref name="route"/> for <paramref name="chosen"/> (each Requirement with its Contract's title): home's
        /// launch first when it serves anything, each body in the Route's order, then home's return when the Route comes home.
        /// </summary>
        public static List<BodyVisit> Of(PricedRoute route, IEnumerable<KeyValuePair<string, ReadRequirement>> chosen, string home)
        {
            var launch = new BodyVisit { Body = home, Stop = BodyVisitStop.Launch };
            var away = route.Order.Where(b => b != home).Distinct().Select(b => new BodyVisit { Body = b, Stop = BodyVisitStop.Visit }).ToList();
            var back = new BodyVisit { Body = home, Stop = BodyVisitStop.Return };
            var held = new Dictionary<BodyVisit, List<KeyValuePair<string, ReadRequirement>>>();
            foreach (var one in chosen)
            {
                var read = one.Value;
                var visit = read.Return ? back : read.Placement != null ? launch : away.FirstOrDefault(v => v.Body == read.Body) ?? launch;
                List<KeyValuePair<string, ReadRequirement>> list;
                if (!held.TryGetValue(visit, out list)) held[visit] = list = new List<KeyValuePair<string, ReadRequirement>>();
                list.Add(one);
            }
            var visits = new List<BodyVisit>();
            if (held.ContainsKey(launch)) visits.Add(launch);
            visits.AddRange(away);
            if (held.ContainsKey(back) || (route.HomeUt.HasValue && away.Count > 0)) visits.Add(back);
            foreach (var visit in visits)
            {
                List<KeyValuePair<string, ReadRequirement>> list;
                if (!held.TryGetValue(visit, out list)) continue;
                // OrderBy is stable: Requirements at one step keep the Contracts' order.
                visit.Requirements = list.OrderBy(r => Step(r.Value)).ThenBy(r => WaypointPlace(route, r.Value))
                    .Select(r => new VisitRequirement { Key = r.Value.Requirement.Key, Words = r.Value.Words, Title = r.Key, Met = r.Value.Requirement.State == "Complete" }).ToList();
            }
            return visits;
        }

        /// <summary>Where in a visit the Route prices it: 0 arrive or capture, 1 work in orbit, 2 land, 3 work on the ground.</summary>
        private static int Step(ReadRequirement read)
        {
            var work = read.Waypoint != null || read.Orbit != null || read.RendezvousOrbit != null || read.Experiment != null || read.Manual.Length > 0 || read.Rendezvous;
            if (read.Reach == ContractReach.Land) return work ? 3 : 2;
            return work ? 1 : 0;
        }

        /// <summary>A survey waypoint's place in the order the Route takes them; any other Requirement sorts as the first.</summary>
        private static int WaypointPlace(PricedRoute route, ReadRequirement read)
        {
            return read.Waypoint == null ? -1 : route.Waypoints.FindIndex(w => w.Key == read.Waypoint.Key);
        }
    }
}
