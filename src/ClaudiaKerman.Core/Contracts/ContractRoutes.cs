// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

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
        /// <summary>When the launch is done (in orbit round home, or at a hop's apoapsis); null when the route has none.</summary>
        public double? LaunchUt;
        /// <summary>#1048: the route is a hop below orbit, in words ("reach space over Kerbin"); "" when it isn't.</summary>
        public string Hop = "";
        /// <summary>#1029: the apoapsis a hop flies straight up to, m; null when the route isn't a hop.</summary>
        public double? HopAp;
        /// <summary>#1028: the Maneuver Sequences it was priced as, for the planners' re-pricing; null for a hop, which its own sim priced.</summary>
        public List<PlannedSequence> Legs;
        /// <summary>#1030: what the survey waypoints add over the legs (plane changes, raises, hops between sites), m/s; in <see cref="DeltaV"/>.</summary>
        public double Surveys;
        /// <summary>#1030: when each survey waypoint is taken (UT, s), by its Requirement's key.</summary>
        public readonly Dictionary<string, double> WaypointUt = new Dictionary<string, double>();
        /// <summary>#1030: the survey waypoints in the order flown: by their passes in a space band, by least travel on the ground.</summary>
        public readonly List<SurveyWaypoint> Waypoints = new List<SurveyWaypoint>();
        /// <summary>#1031: what the satellite orbits add over the low orbit (raises and plane turns), m/s; in <see cref="DeltaV"/>.</summary>
        public double Orbits;
        /// <summary>#1031: when each satellite orbit is reached (UT, s), by its Requirement's key.</summary>
        public readonly Dictionary<string, double> OrbitReachedUt = new Dictionary<string, double>();

        /// <summary>When the leg serving <paramref name="requirement"/> finishes; null when the route doesn't serve it.</summary>
        public double? DoneUt(ReadRequirement requirement, string home)
        {
            if (requirement.Return) return HomeUt;
            double taken;
            if (requirement.Waypoint != null && WaypointUt.TryGetValue(requirement.Waypoint.Key, out taken)) return taken;
            if (requirement.Orbit != null && OrbitReachedUt.TryGetValue(requirement.Orbit.Key, out taken)) return taken;
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
    /// <para>
    /// #1048: from the pad at home, a route whose only need is a hop below orbit over home (<see cref="ContractReach.Hop"/>), with no
    /// words to fly, is that hop, priced by <see cref="SuborbitalHop"/> to the highest apoapsis its bands ask; any other route launches
    /// to orbit as before, and its hops are met on the way up and add nothing. A hop the sim can't price is priced as the launch to orbit.
    /// </para>
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
        /// <summary>#1036: the player's words as the needs and limits they put on each body.</summary>
        public readonly WordsReading Reading;
        private readonly Dictionary<string, SnapshotBody> _bodies;
        private readonly Dictionary<string, PricedRoute> _priced = new Dictionary<string, PricedRoute>();
        /// <summary>#1030: the Delta-v Map, for a survey's polar row and hops; worked out when a survey first needs it.</summary>
        private DeltaVMap _map;

        public ContractRoutes(GameSnapshot snapshot, string words, string home)
        {
            _snapshot = snapshot;
            _home = home;
            Reading = WordsReading.Read(snapshot, words, home);
            // The clauses read as needs are flown by the route's own legs, once, not again as the words' legs.
            _words = ChuteGroups.GoalLegs(snapshot, words).Skip(snapshot.IsGrounded ? 1 : 0).Where(l => !Reading.Consumed.Contains(l.Name)).ToList();
            _wordsReturn = SequenceWords.ReturnHome.IsMatch(words ?? "");
            _bodies = snapshot.Bodies.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>The typed words alone, as the Craft Check would price them: the launch and their clauses.</summary>
        public double WordsAlone()
        {
            return Best(new Dictionary<string, ContractReach>(), false, r => true).DeltaV;
        }

        /// <summary>
        /// The cheapest order of the route serving <paramref name="needs"/> (the furthest each body is reached), coming home when
        /// <paramref name="home"/>; among the orders that keep every deadline in <paramref name="keeps"/> when any does.
        /// </summary>
        /// <param name="waypoints">#1030: the survey waypoints the route takes at the bodies it reaches; none when null.</param>
        public PricedRoute Best(IDictionary<string, ContractReach> contractNeeds, bool home, Func<PricedRoute, bool> keeps, IEnumerable<HopBand> hops = null, IEnumerable<SurveyWaypoint> waypoints = null, IEnumerable<ContractOrbit> orbits = null)
        {
            var surveys = (waypoints ?? new SurveyWaypoint[0]).ToList();
            var satellites = (orbits ?? new ContractOrbit[0]).ToList();
            var needs = Needs(contractNeeds);
            var hop = Hop(needs, hops);
            if (hop != null) return hop;
            var bodies = needs.Keys.Where(b => b != _home).OrderBy(b => b, StringComparer.Ordinal).ToList();
            // #1060: bodies the words put in an order keep it; the rest go where they are cheapest. Words that contradict themselves keep no order.
            var rules = Reading.Precedes.Where(p => bodies.Contains(p.Key) && bodies.Contains(p.Value)).ToList();
            if (!Orderable(bodies, rules)) rules.Clear();
            var orders = bodies.Count <= MostBodiesToPermute ? Permutations(bodies).Where(o => Keeps(o, rules)) : new[] { NearestNext(bodies, rules) };
            PricedRoute best = null, bestKept = null;
            foreach (var order in orders)
            {
                var route = Price(order, needs, home, surveys, satellites);
                if (best == null || route.DeltaV < best.DeltaV) best = route;
                if (keeps(route) && (bestKept == null || route.DeltaV < bestKept.DeltaV)) bestKept = route;
            }
            return bestKept ?? best;
        }

        /// <summary>
        /// #1028: <paramref name="route"/> re-priced with the planners (plan_ascent, plan_transfer, <see cref="RoutePlanners"/>), m/s: the
        /// Contract check runs at Start Mission, where the route is priced up front. A hop is its own sim's figure already.
        /// </summary>
        public double Planned(PricedRoute route)
        {
            return route.Legs == null ? route.DeltaV : PlanLegs.Need(PlanLegs.Judge(_snapshot, route.Legs, "", true)) + route.Surveys + route.Orbits;
        }

        /// <summary>What the Contracts need with what the words need: where both reach a body, the further reach serves both.</summary>
        internal Dictionary<string, ContractReach> Needs(IDictionary<string, ContractReach> contractNeeds)
        {
            var needs = new Dictionary<string, ContractReach>(contractNeeds);
            foreach (var word in Reading.Needs)
            {
                ContractReach had;
                if (!needs.TryGetValue(word.Key, out had) || word.Value > had) needs[word.Key] = word.Value;
            }
            return needs;
        }

        /// <summary>#1163: the words order the bodies in a loop ("A then B then A"), so the route keeps none of it and takes the cheapest order.</summary>
        public bool ConflictingOrders
        {
            get
            {
                // As Best reads them: a rule naming the home body orders no leg of the route.
                var rules = Reading.Precedes.Where(p => p.Key != _home && p.Value != _home).ToList();
                return rules.Count > 0 && !Orderable(rules.SelectMany(r => new[] { r.Key, r.Value }).Distinct().ToList(), rules);
            }
        }

        /// <summary>Whether the words themselves ask for the trip home.</summary>
        internal bool WordsReturn
        {
            get { return _wordsReturn; }
        }

        /// <summary>The hop that serves <paramref name="needs"/> on its own; null when they need more than a hop, or the hop can't be priced.</summary>
        private PricedRoute Hop(IDictionary<string, ContractReach> needs, IEnumerable<HopBand> hops)
        {
            var bands = (hops ?? new HopBand[0]).ToList();
            ContractReach reach;
            SnapshotBody body;
            if (bands.Count == 0 || needs.Count != 1 || !needs.TryGetValue(_home, out reach) || reach != ContractReach.Hop || _words.Count > 0
                || !_snapshot.IsGrounded || Start() != _home || !_bodies.TryGetValue(_home, out body)) return null;
            var highest = bands.OrderByDescending(b => b.TargetAp(body)).First();
            var targetAp = highest.TargetAp(body);
            var key = "hop|" + Math.Round(targetAp);
            PricedRoute cached;
            if (_priced.TryGetValue(key, out cached)) return cached;
            string why;
            var deltaV = SuborbitalHop.DeltaV(_snapshot, targetAp, out why);
            var route = deltaV.HasValue ? new PricedRoute { DeltaV = deltaV.Value, Hop = highest.Words(_home), HopAp = targetAp, LaunchUt = _snapshot.Ut + LaunchSeconds, HomeUt = _snapshot.Ut + LaunchSeconds } : null;
            _priced[key] = route;
            return route;
        }

        private PricedRoute Price(IList<string> order, IDictionary<string, ContractReach> needs, bool home, List<SurveyWaypoint> surveys, List<ContractOrbit> satellites)
        {
            var key = string.Join(",", order.Select(b => b + ":" + (int)needs[b]).ToArray()) + "|" + home + "|" + string.Join(",", surveys.Concat<object>(satellites).Select(w => w is SurveyWaypoint ? ((SurveyWaypoint)w).Key : ((ContractOrbit)w).Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
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
            if (route.LaunchUt.HasValue) Satellites(route, _home, satellites.Where(o => o.Body == _home).ToList(), _snapshot.IsGrounded, route.LaunchUt.Value);
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
                Satellites(route, body, satellites.Where(o => o.Body == body).ToList(), false, ut);
                ut = Survey(route, body, surveys.Where(w => !w.Ground && w.Body == body).ToList(), false, ut);
                if (reach == ContractReach.Land)
                {
                    ut += SurfaceSeconds;
                    route.LandUt[body] = ut;
                    ut = Survey(route, body, surveys.Where(w => w.Ground && w.Body == body).ToList(), true, ut);
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
            route.Legs = legs;
            route.DeltaV = PlanLegs.Need(PlanLegs.Judge(_snapshot, legs, "")) + route.Surveys + route.Orbits;
            _priced[key] = route;
            return route;
        }

        /// <summary>#1030: the body's survey waypoints added to <paramref name="route"/> from <paramref name="ut"/>; the time they are all taken.</summary>
        private double Survey(PricedRoute route, string body, List<SurveyWaypoint> waypoints, bool ground, double ut)
        {
            SnapshotBody at;
            if (waypoints.Count == 0 || !_bodies.TryGetValue(body, out at)) return ut;
            var map = _map ?? (_map = DeltaVMap.Compute(_snapshot.Bodies, _home));
            var leg = ground ? SurveyPasses.Ground(at, map, waypoints) : SurveyPasses.Space(at, map, waypoints);
            route.Surveys += leg.DeltaV;
            route.Waypoints.AddRange(leg.Order);
            foreach (var taken in leg.After) route.WaypointUt[taken.Key] = ut + taken.Value;
            return ut + leg.Seconds;
        }

        /// <summary>
        /// #1031: the satellite orbits round <paramref name="body"/> added to <paramref name="route"/>, each from the low orbit reached at
        /// <paramref name="ut"/> (<see cref="SatelliteOrbits"/>); the climb is the satellite's, so the route goes on from the low orbit.
        /// </summary>
        private void Satellites(PricedRoute route, string body, List<ContractOrbit> orbits, bool launched, double ut)
        {
            SnapshotBody at;
            if (orbits.Count == 0 || !_bodies.TryGetValue(body, out at)) return;
            var map = _map ?? (_map = DeltaVMap.Compute(_snapshot.Bodies, _home));
            foreach (var orbit in orbits)
            {
                double seconds;
                route.Orbits += SatelliteOrbits.Price(orbit, at, map, launched && body == _home, out seconds);
                route.OrbitReachedUt[orbit.Key] = ut + seconds;
            }
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

        private static bool Keeps(IList<string> order, IEnumerable<KeyValuePair<string, string>> rules)
        {
            return rules.All(r => order.IndexOf(r.Key) < order.IndexOf(r.Value));
        }

        /// <summary>Whether some order keeps every rule: the rules hold no loop.</summary>
        private static bool Orderable(List<string> bodies, List<KeyValuePair<string, string>> rules)
        {
            var left = bodies.ToList();
            while (left.Count > 0)
            {
                var free = left.FirstOrDefault(b => !rules.Any(r => r.Value == b && left.Contains(r.Key)));
                if (free == null) return false;
                left.Remove(free);
            }
            return true;
        }

        private List<string> NearestNext(List<string> bodies, List<KeyValuePair<string, string>> rules)
        {
            var order = new List<string>();
            var left = bodies.ToList();
            var at = Start();
            while (left.Count > 0)
            {
                var next = left.Where(b => !rules.Any(r => r.Value == b && left.Contains(r.Key))).OrderBy(b => Transfer(at, b)).First();
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
