// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>One candidate Contract Route priced: its delta-v and when each body's visit is done.</summary>
    internal sealed class PricedRoute
    {
        /// <summary>The route's delta-v, m/s, by the Craft Check's route pricer (<see cref="PlanLegs"/>) on the Delta-v Map's figures.</summary>
        public double DeltaV;
        /// <summary>The bodies in the order flown.</summary>
        public List<string> Order = new List<string>();
        /// <summary>When the craft is past, in orbit round or landed on each body (UT, s), by the estimate.</summary>
        public readonly Dictionary<string, double> FlybyUt = new Dictionary<string, double>();
        public readonly Dictionary<string, double> OrbitUt = new Dictionary<string, double>();
        public readonly Dictionary<string, double> LandUt = new Dictionary<string, double>();
        /// <summary>When the craft is home again; null when the route doesn't come home.</summary>
        public double? HomeUt;
        /// <summary>When the launch is done (in orbit round home); null when the route has none.</summary>
        public double? LaunchUt;

        /// <summary>When the leg serving <paramref name="requirement"/> finishes; null when the route doesn't serve it.</summary>
        public double? DoneUt(ReadRequirement requirement, string home)
        {
            if (requirement.Return) return HomeUt;
            if (requirement.Body == home) return LaunchUt;
            var times = requirement.Reach == ContractReach.Land ? LandUt : requirement.Reach == ContractReach.Orbit ? OrbitUt : FlybyUt;
            double ut;
            return times.TryGetValue(requirement.Body, out ut) ? ut : (double?)null;
        }
    }

    /// <summary>
    /// #1022: Contract Routes priced as one Mission by the Craft Check's route pricer, from where the craft is (#998): the route is
    /// written as the Maneuver Sequences it needs and judged by <see cref="PlanLegs"/>, so shared legs count once. The player's typed
    /// words are the fixed part of every route: their clauses fly after the Contracts' visits and before the trip home. Each leg carries
    /// a game-time estimate (<see cref="Estimate"/>). The search prices on the Map's figures (no planner search, spec #1021 decision 5).
    /// </summary>
    internal sealed class ContractRoutes
    {
        /// <summary>A launch to low orbit, s.</summary>
        internal const double LaunchSeconds = 600;
        /// <summary>A landing from low orbit, or an ascent back to it, s.</summary>
        internal const double SurfaceSeconds = 3600;
        /// <summary>The entry and landing at home after the trip back, s.</summary>
        internal const double EntrySeconds = 3600;
        /// <summary>Above this many bodies the order is nearest-next, not every order (spec #1021 decision 6).</summary>
        private const int MostBodiesToPermute = 6;

        private readonly GameSnapshot _snapshot;
        private readonly string _home;
        private readonly List<PlannedSequence> _words;
        private readonly bool _wordsReturn;
        private readonly Dictionary<string, SnapshotBody> _bodies;
        private readonly Dictionary<string, PricedRoute> _priced = new Dictionary<string, PricedRoute>();

        public ContractRoutes(GameSnapshot snapshot, string words, string home)
        {
            _snapshot = snapshot;
            _home = home;
            _words = ChuteGroups.GoalLegs(snapshot, words).Skip(snapshot.IsGrounded ? 1 : 0).ToList();
            _wordsReturn = SequenceWords.ReturnHome.IsMatch(words ?? "");
            _bodies = snapshot.Bodies.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>The typed words alone, as the Craft Check would price them: the launch and their clauses.</summary>
        public double WordsAlone()
        {
            var legs = new List<PlannedSequence>();
            if (_snapshot.IsGrounded) legs.Add(Launch());
            legs.AddRange(_words);
            return PlanLegs.Need(PlanLegs.Judge(_snapshot, legs, ""));
        }

        /// <summary>
        /// The cheapest order of the route serving <paramref name="needs"/> (the furthest each body is reached), coming home when
        /// <paramref name="home"/>; among the orders that keep every deadline in <paramref name="keeps"/> when any does.
        /// </summary>
        public PricedRoute Best(IDictionary<string, ContractReach> needs, bool home, Func<PricedRoute, bool> keeps)
        {
            var bodies = needs.Keys.Where(b => b != _home).OrderBy(b => b, StringComparer.Ordinal).ToList();
            var orders = bodies.Count <= MostBodiesToPermute ? Permutations(bodies) : new[] { NearestNext(bodies) };
            PricedRoute best = null, bestKept = null;
            foreach (var order in orders)
            {
                var route = Price(order, needs, home);
                if (best == null || route.DeltaV < best.DeltaV) best = route;
                if (keeps(route) && (bestKept == null || route.DeltaV < bestKept.DeltaV)) bestKept = route;
            }
            return bestKept ?? best;
        }

        private PricedRoute Price(IList<string> order, IDictionary<string, ContractReach> needs, bool home)
        {
            var key = string.Join(",", order.Select(b => b + ":" + (int)needs[b]).ToArray()) + "|" + home;
            PricedRoute cached;
            if (_priced.TryGetValue(key, out cached)) return cached;
            var legs = new List<PlannedSequence>();
            var route = new PricedRoute { Order = order.ToList() };
            var ut = _snapshot.Ut;
            var at = Start();
            if (_snapshot.IsGrounded)
            {
                legs.Add(Launch());
                ut += at == _home ? LaunchSeconds : SurfaceSeconds;
                if (at == _home) route.LaunchUt = ut;
            }
            else if (at == _home) route.LaunchUt = ut;
            for (var i = 0; i < order.Count; i++)
            {
                var body = order[i];
                var reach = needs[body];
                var named = ContractKinds.Named(body);
                if (body != at)
                {
                    ut += Wait(at, body) + Transfer(at, body);
                    route.FlybyUt[body] = ut;
                }
                else route.FlybyUt[body] = ut;
                if (reach == ContractReach.Flyby) legs.Add(Sequence("Transfer to " + named + " for a flyby"));
                else if (reach == ContractReach.Orbit) legs.Add(Sequence("Transfer to " + named + " and capture into low orbit"));
                else legs.Add(Sequence("Transfer to " + named + ", capture into low orbit and land"));
                if (reach >= ContractReach.Orbit) route.OrbitUt[body] = ut;
                if (reach == ContractReach.Land)
                {
                    ut += SurfaceSeconds;
                    route.LandUt[body] = ut;
                    if (i < order.Count - 1 || home || _words.Count > 0)
                    {
                        legs.Add(Sequence("Ascend from " + named + " to low orbit"));
                        ut += SurfaceSeconds;
                    }
                }
                at = body;
            }
            legs.AddRange(_words);
            if (home)
            {
                if (at != _home)
                {
                    // Words that come home already hold the trip.
                    if (!_wordsReturn) legs.Add(Sequence("Return to " + _home));
                    ut += Transfer(at, _home) + EntrySeconds;
                }
                route.HomeUt = ut;
            }
            route.DeltaV = PlanLegs.Need(PlanLegs.Judge(_snapshot, legs, ""));
            _priced[key] = route;
            return route;
        }

        private static PlannedSequence Sequence(string name)
        {
            return new PlannedSequence { Name = name };
        }

        private PlannedSequence Launch()
        {
            return Sequence(Start() == _home ? "Launch to low " + _home + " orbit" : "Ascend from " + ContractKinds.Named(Start()) + " to low orbit");
        }

        private string Start()
        {
            return _snapshot.Orbit != null && _snapshot.Orbit.Body.Length > 0 ? _snapshot.Orbit.Body : _snapshot.Body;
        }

        // ---- Time estimates ----

        /// <summary>Half the Hohmann orbit between the two bodies' low orbits, round the body whose system holds both, s; 0 when unknown.</summary>
        internal double Transfer(string from, string to)
        {
            double r1, r2;
            SnapshotBody top;
            if (!Ends(from, to, out top, out r1, out r2)) return 0;
            var a = (r1 + r2) / 2;
            return Math.PI * Math.Sqrt(a * a * a / top.Gm);
        }

        /// <summary>The longest wait for the bodies to line up for the trip: their synodic period round the body that holds both, s.</summary>
        internal double Wait(string from, string to)
        {
            double r1, r2;
            SnapshotBody top;
            if (!Ends(from, to, out top, out r1, out r2)) return 0;
            var p1 = 2 * Math.PI * Math.Sqrt(r1 * r1 * r1 / top.Gm);
            var p2 = 2 * Math.PI * Math.Sqrt(r2 * r2 * r2 / top.Gm);
            return Math.Abs(p1 - p2) < 1e-6 ? 0 : 1 / Math.Abs(1 / p1 - 1 / p2);
        }

        private bool Ends(string from, string to, out SnapshotBody top, out double r1, out double r2)
        {
            top = null;
            r1 = r2 = 0;
            var up = Ancestry(from);
            var down = Ancestry(to);
            top = up.FirstOrDefault(down.Contains);
            if (top == null || top.Gm <= 0) return false;
            var upTop = up.IndexOf(top);
            var downTop = down.IndexOf(top);
            r1 = upTop == 0 ? DeltaVMap.LowOrbit(top) : up[upTop - 1].Sma;
            r2 = downTop == 0 ? DeltaVMap.LowOrbit(top) : down[downTop - 1].Sma;
            return r1 > 0 && r2 > 0;
        }

        private List<SnapshotBody> Ancestry(string name)
        {
            var chain = new List<SnapshotBody>();
            SnapshotBody at;
            for (_bodies.TryGetValue(name ?? "", out at); at != null && !chain.Contains(at); _bodies.TryGetValue(at.Parent ?? "", out at))
                chain.Add(at);
            return chain;
        }

        private List<string> NearestNext(List<string> bodies)
        {
            var order = new List<string>();
            var left = bodies.ToList();
            var at = Start();
            while (left.Count > 0)
            {
                var next = left.OrderBy(b => Transfer(at, b)).First();
                order.Add(next);
                left.Remove(next);
                at = next;
            }
            return order;
        }

        private static IEnumerable<List<string>> Permutations(List<string> items)
        {
            if (items.Count <= 1)
            {
                yield return items.ToList();
                yield break;
            }
            for (var i = 0; i < items.Count; i++)
            {
                var rest = items.Where((_, j) => j != i).ToList();
                foreach (var tail in Permutations(rest))
                {
                    tail.Insert(0, items[i]);
                    yield return tail;
                }
            }
        }
    }
}
