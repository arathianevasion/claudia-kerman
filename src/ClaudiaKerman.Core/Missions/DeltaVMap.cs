// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>One leg of travel in the Delta-v Map.</summary>
    public sealed class DeltaVLeg
    {
        /// <summary>E.g. <c>launch:Kerbin</c>, <c>transfer:Duna</c>, <c>transfer:Duna&gt;Ike</c>, <c>capture:Ike</c>, <c>land:Ike</c>, <c>ascend:Ike</c>, <c>return:Ike</c>, <c>polar:Mun</c>.</summary>
        public string Id = "";
        public string Description = "";
        /// <summary>m/s, without any margin.</summary>
        public double DeltaV;
        /// <summary>For a trip: the burn out of the low orbit it starts from.</summary>
        public double Departure;
        /// <summary>For a trip: the capture burn into the destination's low orbit; 0 for a return home that ends in an entry.</summary>
        public double Arrival;
        /// <summary>For a trip between inclined orbits: the plane change, included in <see cref="DeltaV"/>.</summary>
        public double PlaneChange;
        /// <summary>The speed it arrives with relative to the destination, m/s, before the destination's gravity: what a polar arrival is costed from.</summary>
        internal double ArrivalExcess;
        /// <summary>The bodies the leg joins, for <see cref="DeltaVMap.ToText(IList{SnapshotBody})"/>.</summary>
        internal string[] Bodies = new string[0];
        /// <summary>#866: a trip in to a planet that stops in its high orbit for a moon: what that saves on the capture to the low circle, m/s; 0 otherwise.</summary>
        internal double HighSaving;
    }

    /// <summary>
    /// The Delta-v Map (#757: every body, not only the home body's moons): the delta-v each leg of travel needs, against which a Mission
    /// Plan is budgeted. Computed from body data (patched-conic Hohmann transfers between low orbits, plus a simple ascent-loss allowance)
    /// rather than copied from community charts, so it follows whatever planet pack the game runs. The figures carry no margin; the Craft
    /// Check adds it. A trip's plane change is priced at the transfer's apoapsis, its slower end. A return home is priced to an entry where
    /// <see cref="ArrivalRule"/> says the trip ends in one.
    /// <para>
    /// Matching and tolerance (<see cref="PlanLegs"/> has the whole rule): each Maneuver Sequence is matched to the legs its name (or, when the
    /// name says nothing a leg does, its intent) describes, in plan order from where the craft is; a budget more than 10% and 25 m/s under
    /// their figure is a <c>leg_under_map</c> Warning; the margin counts each leg at the larger of its budget and its figure.
    /// </para>
    /// </summary>
    public sealed class DeltaVMap
    {
        /// <summary>Ascent losses (gravity and drag) as a fraction of low-orbit speed, with an atmosphere of up to Kerbin's pressure.</summary>
        private const double AtmosphericAscentLoss = 0.5;
        /// <summary>The further loss per atmosphere of sea-level pressure above Kerbin's: thick air's drag, set by Eve's ascent.</summary>
        private const double ThickAirLossPerAtm = 0.25;
        /// <summary>The same without an atmosphere.</summary>
        private const double AirlessAscentLoss = 0.1;
        /// <summary>
        /// Over the craft's thrust over weight, on top of <see cref="AirlessAscentLoss"/>, an airless landing's or ascent's loss: fitted to flown legs at
        /// TWR 5 to 8, the Mun's +11% to +19% and Ike's ascent's +13%, and untested below that.
        /// </summary>
        private const double GravityLossOverTwr = 0.4;
        /// <summary>Low orbit above an atmosphere clears it by this much (m).</summary>
        private const double AtmosphereClearance = 10000;
        /// <summary>Low orbit round an airless body, as a fraction of its radius above the surface.</summary>
        private const double AirlessOrbitFraction = 0.07;

        /// <summary>The inclination of a polar orbit (degrees).</summary>
        private const double PolarInclination = 90;
        /// <summary>
        /// A far target's polar arrival over the bare trim (<see cref="PolarArrival"/>): it is all flown as <see cref="Tools.TransferFlight"/>'s trim inside the sphere of
        /// influence. Duna's flew 17.1 m/s for the trim's 16.9 at low orbit and 20.6 at a 180 km periapsis; 1.25 covers that and comes to 21. Source: PolarArrivalTests on TransferRig.
        /// </summary>
        private const double FarApproachFit = 1.25;
        /// <summary>
        /// A moon's over the bare trim: the halfway Course Correction aims the plane too (<see cref="Tools.CorrectionBurn"/>'s TargetNormal), so it pays less than the trim alone (108 m/s at
        /// the Mun, ADR 0016). Flown: the Mun 41 to 50 (and up to 80 for a whole plane, ADR 0016), Ike 60 to 65, Minmus about 1. 0.75 is 80 at the Mun, 76 at Ike and 10 at Minmus.
        /// </summary>
        private const double MoonApproachFit = 0.75;

        /// <summary>The body whose system the Map covers by default.</summary>
        public const string DefaultHome = "Kerbin";

        public readonly List<DeltaVLeg> Legs = new List<DeltaVLeg>();
        /// <summary>The home body's moons.</summary>
        public readonly List<string> Moons = new List<string>();
        public string Home = "";
        private IList<SnapshotBody> _bodies = new List<SnapshotBody>();

        public DeltaVLeg Leg(string id)
        {
            return Legs.FirstOrDefault(l => l.Id == id);
        }

        public static DeltaVMap Compute(IList<SnapshotBody> bodies, string home)
        {
            var map = new DeltaVMap { Home = home, _bodies = bodies };
            var center = bodies.FirstOrDefault(b => b.Name == home);
            if (center == null) return map;

            var lowHome = LowOrbit(center);
            map.Legs.Add(new DeltaVLeg
            {
                Id = "launch:" + home,
                Description = "Launch from " + home + "'s surface to a low orbit (" + Km(lowHome - center.Radius) + ")",
                DeltaV = Circular(center.Gm, lowHome) * (1 + (center.AtmosphereHeight > 0 ? AscentLoss(center) : AirlessAscentLoss)),
                Bodies = new[] { home },
            });
            map.Legs.Add(new DeltaVLeg
            {
                Id = "land:" + home,
                Description = "Deorbit from low " + home + " orbit to the surface",
                DeltaV = Landing(center, lowHome),
                Bodies = new[] { home },
            });

            map.Moons.AddRange(bodies.Where(b => b.Parent == home && b.Sma > 0).Select(b => b.Name));
            foreach (var body in bodies.Where(b => b.Name != home && b.Sma > 0))
                map.AddBody(center, body);
            map.AddTripsBetween(center);
            return map;
        }

        /// <summary>A body's own legs. It is reached from home when it orbits home or home's parent, else from the body it orbits.</summary>
        private void AddBody(SnapshotBody center, SnapshotBody body)
        {
            var fromHome = body.Parent == center.Name || body.Parent == center.Parent;
            var origin = fromHome ? center : _bodies.FirstOrDefault(b => b.Name == body.Parent);
            var inbound = origin == null ? null : Trip(origin, body);
            if (inbound == null) return;
            var low = LowOrbit(body);
            if (fromHome)
            {
                inbound.Id = "transfer:" + body.Name;
                inbound.Description = "Low " + center.Name + " orbit to low " + body.Name + " orbit (departure, plane change and capture)";
                inbound.Bodies = new[] { center.Name, body.Name };
                Legs.Add(inbound);
            }
            Legs.Add(new DeltaVLeg
            {
                Id = "capture:" + body.Name,
                Description = "Capture into low " + body.Name + " orbit, arriving from " + origin.Name
                    + (body.AtmosphereHeight > 0 ? "; it has air, so an aerocapture or a direct entry saves this burn" : ""),
                DeltaV = inbound.Arrival,
                Arrival = inbound.Arrival,
                Bodies = new[] { body.Name },
            });
            var polar = PolarArrival(body, inbound.ArrivalExcess, body.Parent == origin.Name);
            Legs.Add(new DeltaVLeg
            {
                Id = "polar:" + body.Name,
                Description = "Extra, added to its capture, for a polar orbit of " + body.Name + " instead of an equatorial one (plane aimed on the approach, an upper bound on that; return:" + body.Name + " does not include it)",
                DeltaV = polar,
                PlaneChange = polar,
                Bodies = new[] { body.Name },
            });
            Legs.Add(new DeltaVLeg
            {
                Id = "land:" + body.Name,
                Description = "Low " + body.Name + " orbit to the surface",
                DeltaV = Landing(body, low),
                Bodies = new[] { body.Name },
            });
            Legs.Add(new DeltaVLeg
            {
                Id = "ascend:" + body.Name,
                Description = "Surface of " + body.Name + " to a low orbit",
                DeltaV = body.AtmosphereHeight > 0 ? Circular(body.Gm, low) * (1 + AscentLoss(body)) : Landing(body, low),
                Bodies = new[] { body.Name },
            });
            var back = Trip(body, center);
            if (back == null) return;
            var capture = ArrivalRule.EndsInReentry(_bodies, center.Name) ? 0 : back.Arrival;
            Legs.Add(new DeltaVLeg
            {
                Id = "return:" + body.Name,
                Description = "Low " + body.Name + " orbit to " + (capture > 0 ? "low " + center.Name + " orbit" : "an entry at " + center.Name + ", no capture burn"),
                Departure = back.Departure,
                PlaneChange = back.PlaneChange,
                Arrival = capture,
                DeltaV = back.Departure + back.PlaneChange + capture,
                Bodies = new[] { body.Name, center.Name },
            });
        }

        /// <summary>
        /// What a polar arrival adds with the plane aimed on the approach (#806, ADR 0016). A burn at the edge of the sphere of influence turns the velocity
        /// about the craft's position, and the velocity is off that line by h / (R v), so a 90 degree turn costs sqrt(2) h / R, h being the arrival's
        /// angular momentum at low orbit and R the sphere's radius. <see cref="FarApproachFit"/> and <see cref="MoonApproachFit"/> scale that to the solves.
        /// Never above the old turn at the capture's highest point, which is what a body whose sphere of influence is tight against its low orbit would pay.
        /// </summary>
        private double PolarArrival(SnapshotBody body, double excess, bool moon)
        {
            var low = LowOrbit(body);
            var angularMomentum = Math.Sqrt(low * low * excess * excess + 2 * body.Gm * low);
            var trim = Math.Sqrt(2) * angularMomentum / Sphere(body);
            return Math.Min((moon ? MoonApproachFit : FarApproachFit) * trim, OrbitChange.PlaneChangeDeltaV(excess, PolarInclination));
        }

        /// <summary>The body's sphere of influence, m; worked out from the masses when the table has none (an older recording).</summary>
        private double Sphere(SnapshotBody body)
        {
            if (body.SoiRadius > 0) return body.SoiRadius;
            var parent = _bodies.First(b => b.Name == body.Parent);
            return body.Sma * Math.Pow(body.Gm / parent.Gm, 0.4);
        }

        /// <summary>The trips that don't touch home: a moon and the planet it orbits, either way, and two bodies with the same parent.</summary>
        private void AddTripsBetween(SnapshotBody center)
        {
            var bodies = _bodies.Where(b => b.Name != center.Name && b.Sma > 0).ToList();
            foreach (var moon in bodies)
            {
                var planet = bodies.FirstOrDefault(b => b.Name == moon.Parent);
                if (planet == null) continue;
                AddTrip(planet, moon);
                AddTrip(moon, planet);
            }
            foreach (var from in bodies)
                foreach (var to in bodies.Where(b => b != from && b.Parent == from.Parent))
                    AddTrip(from, to);
        }

        private void AddTrip(SnapshotBody from, SnapshotBody to)
        {
            var trip = Trip(from, to);
            if (trip == null) return;
            trip.Id = "transfer:" + from.Name + ">" + to.Name;
            trip.Description = "Low " + from.Name + " orbit to low " + to.Name + " orbit (departure, plane change and capture)";
            Legs.Add(trip);
        }

        /// <summary>The trip between two named bodies (<see cref="Trip(SnapshotBody, SnapshotBody)"/>); null when the table lacks one or they are the same.</summary>
        internal DeltaVLeg Trip(string from, string to)
        {
            var start = _bodies.FirstOrDefault(b => string.Equals(b.Name, from, StringComparison.OrdinalIgnoreCase));
            var end = _bodies.FirstOrDefault(b => string.Equals(b.Name, to, StringComparison.OrdinalIgnoreCase));
            return start == null || end == null || start == end ? null : Trip(start, end);
        }

        /// <summary>
        /// The Hohmann trip from low orbit round <paramref name="from"/> to low orbit round <paramref name="to"/>, any two bodies, by way of
        /// the body whose system holds both; null when the table doesn't join them. A moon on the way takes its planet's excess down to the
        /// moon's orbit, so leaving a moon for another planet needs no capture at its own planet.
        /// </summary>
        private DeltaVLeg Trip(SnapshotBody from, SnapshotBody to)
        {
            var up = Ancestry(from);
            var down = Ancestry(to);
            var top = up.FirstOrDefault(down.Contains);
            if (top == null) return null;
            var upTop = up.IndexOf(top);
            var downTop = down.IndexOf(top);
            var r1 = upTop == 0 ? LowOrbit(top) : up[upTop - 1].Sma;
            var r2 = downTop == 0 ? LowOrbit(top) : down[downTop - 1].Sma;
            var a = (r1 + r2) / 2;
            var v1 = Math.Sqrt(top.Gm * (2 / r1 - 1 / a));
            var v2 = Math.Sqrt(top.Gm * (2 / r2 - 1 / a));
            var excess1 = Math.Abs(v1 - Circular(top.Gm, r1));
            var excess2 = Math.Abs(v2 - Circular(top.Gm, r2));
            var inc1 = upTop == 0 ? 0 : up[upTop - 1].Inclination;
            var inc2 = downTop == 0 ? 0 : down[downTop - 1].Inclination;
            var plane = OrbitChange.PlaneChangeDeltaV(Math.Min(v1, v2), Math.Abs(inc1 - inc2));
            var atStart = upTop == 0 ? excess1 : ExcessAt(up, upTop, excess1);
            var atEnd = downTop == 0 ? excess2 : ExcessAt(down, downTop, excess2);
            var departure = upTop == 0 ? excess1 : Eject(from, atStart);
            var arrival = downTop == 0 ? excess2 : Eject(to, atEnd);
            return new DeltaVLeg
            {
                Departure = departure,
                Arrival = arrival,
                PlaneChange = plane,
                DeltaV = departure + arrival + plane,
                ArrivalExcess = atEnd,
                Bodies = new[] { from.Name, to.Name },
            };
        }

        /// <summary>
        /// #866: home to a moon of another planet by the planet's high orbit, as <c>capture</c> keeps it (<see cref="HighRoute"/>): the trip in captures at the
        /// low orbit's height into an ellipse reaching <see cref="HighRoute.TopR"/>, and the trip on lowers its apoapsis to the
        /// moon from there. The two legs, <c>transfer:Duna</c> and <c>transfer:Duna&gt;Ike</c>, priced as one route in place of the low circle's (about 600 m/s
        /// less at Ike); null when the moon's planet isn't another of the star's, or its high orbit doesn't reach past the moon.
        /// </summary>
        internal DeltaVLeg[] HighRouteLegs(string moonName)
        {
            var moon = _bodies.FirstOrDefault(b => string.Equals(b.Name, moonName, StringComparison.OrdinalIgnoreCase));
            var center = _bodies.FirstOrDefault(b => b.Name == Home);
            if (moon == null || center == null || moon.Sma <= 0 || string.IsNullOrEmpty(center.Parent)) return null;
            var planet = _bodies.FirstOrDefault(b => b.Name == moon.Parent);
            if (planet == null || planet == center || planet.Parent != center.Parent) return null;
            var inbound = Trip(center, planet);
            var hop = Trip(planet, moon);
            var low = LowOrbit(planet);
            var high = HighRoute.TopR(Sphere(planet), moon);
            if (inbound == null || hop == null || high <= moon.Sma * (1 + moon.Eccentricity)) return null;
            var atHigh = HighRoute.Speed(planet.Gm, low, high);
            var saving = atHigh - Circular(planet.Gm, low);
            inbound.Id = "transfer:" + planet.Name;
            inbound.Description = "Low " + center.Name + " orbit to high " + planet.Name + " orbit (departure, plane change and capture)";
            inbound.Bodies = new[] { center.Name, planet.Name };
            inbound.Arrival -= saving;
            inbound.DeltaV -= saving;
            inbound.HighSaving = saving;
            hop.Id = "transfer:" + planet.Name + ">" + moon.Name;
            hop.Description = "High " + planet.Name + " orbit to low " + moon.Name + " orbit (departure, plane change and capture)";
            hop.Departure = atHigh - HighRoute.Speed(planet.Gm, low, moon.Sma);
            hop.DeltaV = hop.Departure + hop.PlaneChange + hop.Arrival;
            return new[] { inbound, hop };
        }

        /// <summary>The goal's legs with each named moon reached by its planet's high orbit (<see cref="HighRoute"/>) in place of the low circle's, unless the goal names the planet too.</summary>
        private List<DeltaVLeg> WithHighRoutes(List<DeltaVLeg> legs, IList<SnapshotBody> named)
        {
            foreach (var moon in named)
            {
                if (named.Any(b => b.Name == moon.Parent)) continue;
                var route = HighRouteLegs(moon.Name);
                if (route == null) continue;
                foreach (var leg in route)
                {
                    var at = legs.FindIndex(l => l.Id == leg.Id);
                    if (at >= 0) legs[at] = leg;
                }
            }
            return legs;
        }

        /// <summary>The speed relative to <c>chain[0]</c> of a trip with <paramref name="excess"/> relative to <c>chain[top - 1]</c>, its moons in between.</summary>
        private static double ExcessAt(IList<SnapshotBody> chain, int top, double excess)
        {
            for (var i = top - 1; i > 0; i--)
            {
                var speed = Math.Sqrt(excess * excess + 2 * chain[i].Gm / chain[i - 1].Sma);
                excess = Math.Abs(speed - Circular(chain[i].Gm, chain[i - 1].Sma));
            }
            return excess;
        }

        /// <summary>The burn between low orbit round <paramref name="body"/> and a speed of <paramref name="excess"/> clear of it.</summary>
        private static double Eject(SnapshotBody body, double excess)
        {
            var low = LowOrbit(body);
            return Math.Sqrt(excess * excess + 2 * body.Gm / low) - Circular(body.Gm, low);
        }

        /// <summary>The body, the body it orbits, and so on up to the star.</summary>
        private List<SnapshotBody> Ancestry(SnapshotBody body)
        {
            var chain = new List<SnapshotBody>();
            for (var at = body; at != null && !chain.Contains(at); at = _bodies.FirstOrDefault(b => b.Name == at.Parent))
                chain.Add(at);
            return chain;
        }

        /// <summary>
        /// A landing's or an ascent's figure with the craft's thrust over weight there: on an airless body the leg's figure times
        /// 1 + <see cref="AirlessAscentLoss"/> + <see cref="GravityLossOverTwr"/>/TWR (a TWR under 1 counts as 1). With no TWR known, or on a body
        /// with air (whose allowance already holds gravity and drag), the leg's own figure. 0 for a leg the Map doesn't have.
        /// </summary>
        public double WithGravityLoss(string id, double? twr)
        {
            var leg = Leg(id);
            if (leg == null) return 0;
            var body = _bodies.FirstOrDefault(b => b.Name == leg.Bodies.FirstOrDefault());
            if (body == null || body.AtmosphereHeight > 0 || !twr.HasValue || twr.Value <= 0) return leg.DeltaV;
            return leg.DeltaV * (1 + AirlessAscentLoss + GravityLossOverTwr / Math.Max(1, twr.Value));
        }

        /// <summary>The ascent loss with air: <see cref="AtmosphericAscentLoss"/>, and more where the air is thicker than Kerbin's.</summary>
        private static double AscentLoss(SnapshotBody body)
        {
            return AtmosphericAscentLoss + ThickAirLossPerAtm * Math.Max(0, body.SeaLevelAtm - 1);
        }

        /// <summary>
        /// The legs for the bodies a Mission Goal names (#757): home's launch and landing, each named body's own legs, the trips along the
        /// way to it (by the bodies it orbits) and between the named bodies. With none named, home's system, as the Map has always read.
        /// </summary>
        private List<DeltaVLeg> LegsFor(IList<SnapshotBody> named)
        {
            if (named.Count == 0)
                return Legs.Where(l => l.Bodies.All(b => b == Home || Moons.Contains(b)) && l.Id.IndexOf('>') < 0).ToList();
            var names = new HashSet<string>(named.Select(b => b.Name));
            var route = new HashSet<string> { Home };
            foreach (var at in named.SelectMany(Ancestry).Where(b => b.Sma > 0)) route.Add(at.Name);
            return WithHighRoutes(Legs.Where(l => l.Bodies.All(route.Contains) && (!OnOneBody(l) || names.Contains(l.Bodies[0]) || (l.Bodies[0] == Home && !IsPolar(l)))).ToList(), named);
        }

        private static bool OnOneBody(DeltaVLeg leg)
        {
            return IsPolar(leg) || leg.Id.StartsWith("land:", StringComparison.Ordinal) || leg.Id.StartsWith("ascend:", StringComparison.Ordinal);
        }

        private static bool IsPolar(DeltaVLeg leg)
        {
            return leg.Id.StartsWith("polar:", StringComparison.Ordinal);
        }

        /// <summary>The Map as lines for Claude: id, description and delta-v.</summary>
        public string ToText()
        {
            return Lines(Legs);
        }

        /// <summary>
        /// The legs for the bodies a Mission Goal names, as lines; per-turn text, never the cached prefix. A trip's line holds its capture
        /// (and says so), so no separate capture line follows it: the capture can't be counted twice (#776). With the craft's
        /// <paramref name="snapshot"/>, an airless body's landing or ascent is given with the craft's gravity loss, at the lowest TWR of the
        /// stages that fly it (the legs before it spent in firing order, as <see cref="PlanLegs"/> does); with no TWR known, the bare figure, marked.
        /// With the player's <paramref name="window"/> (#686), that window's trip is at its figures, not the Map's.
        /// </summary>
        public string ToText(IList<SnapshotBody> named, GameSnapshot snapshot = null, WindowRow window = null)
        {
            var legs = LegsFor(named);
            var windowLeg = window == null ? null : legs.FirstOrDefault(l => window.Budgets(l.Id));
            var text = new StringBuilder();
            // Spent before the first landing: the launch and the trips in, the last hop to a named body included.
            var goal = new HashSet<string>(named.Select(b => b.Name));
            double used = legs.Where(l => l.Id == "launch:" + Home || (l.Id.StartsWith("transfer:", StringComparison.Ordinal)
                && (l.Id.IndexOf('>') < 0 || goal.Contains(l.Id.Substring(l.Id.IndexOf('>') + 1))))).Sum(l => l.DeltaV);
            foreach (var leg in legs.Where(l => !HeldByTrip(legs, l)))
            {
                var figure = leg.DeltaV;
                var note = "";
                var sbody = snapshot == null ? null : snapshot.Bodies.FirstOrDefault(b => b.Name == leg.Bodies[0]);
                var airless = sbody != null && named.Count > 0 && IsSurface(leg) && leg.Bodies[0] != Home && AirlessBody(leg.Bodies[0]);
                if (airless)
                {
                    var twr = MarginCheck.LowestTwr(snapshot, sbody, used, used + Math.Max(1, leg.DeltaV));
                    figure = WithGravityLoss(leg.Id, twr);
                    note = twr.HasValue && twr.Value > 0
                        ? " (includes this craft's gravity loss at TWR " + twr.Value.ToString("0.0", CultureInfo.InvariantCulture) + "; " + Whole(leg.DeltaV) + " without)"
                        : " (bare figure: this craft's TWR there is unknown, so no gravity loss is added)";
                }
                if (leg == windowLeg)
                {
                    // A high-orbit route's capture stops short of the low circle the window's is costed to (#866).
                    figure = window.TotalDv - leg.HighSaving;
                    note = " (the player's " + window.Kind + " window)";
                }
                text.Append("- ").Append(leg.Id).Append(": ").Append(leg.Description).Append(": ").Append(Whole(figure)).Append(" m/s").Append(note);
                if (leg.PlaneChange > 0 && leg != windowLeg)
                    text.Append(" (carries a plane change of ").Append(Whole(leg.PlaneChange)).Append(" m/s)");
                var arrival = leg == windowLeg ? window.CaptureDv - leg.HighSaving : leg.Arrival;
                if (leg.Id.StartsWith("transfer:", StringComparison.Ordinal) && arrival > 0)
                    text.Append(" (includes the capture, ").Append(Whole(arrival)).Append(" m/s)");
                text.Append('\n');
                if (IsSurface(leg) && leg.Bodies[0] != Home) used += figure;
            }
            // A trip the Map has no leg for (between two bodies it doesn't start from): the window's own line.
            if (window != null && windowLeg == null)
                text.Append("- transfer:").Append(window.From).Append('>').Append(window.Body).Append(": the player's ").Append(window.Kind.ToString()).Append(" window: ").Append(Whole(window.TotalDv)).Append(" m/s\n");
            return text.ToString();
        }

        private bool AirlessBody(string name)
        {
            var body = _bodies.FirstOrDefault(b => b.Name == name);
            return body != null && body.AtmosphereHeight <= 0;
        }

        private static bool IsSurface(DeltaVLeg leg)
        {
            return leg.Id.StartsWith("land:", StringComparison.Ordinal) || leg.Id.StartsWith("ascend:", StringComparison.Ordinal);
        }

        /// <summary>A capture line a trip's own line already holds (the trip arrives at that body): both would pay the capture twice.</summary>
        private static bool HeldByTrip(IList<DeltaVLeg> legs, DeltaVLeg leg)
        {
            if (!leg.Id.StartsWith("capture:", StringComparison.Ordinal)) return false;
            var body = leg.Id.Substring(8);
            return legs.Any(l => l.Id.StartsWith("transfer:", StringComparison.Ordinal) && l.Arrival > 0
                && (l.Id == "transfer:" + body || l.Id.EndsWith(">" + body, StringComparison.Ordinal)));
        }

        /// <summary>
        /// One line of figures for the per-turn text of a Mission Plan request: "launch:Kerbin 3,418; transfer:Mun 1,170 (capture 310); ...".
        /// "" when the goal names no body, so other Missions' requests are unchanged. Polar legs and home's landing are left out, and a
        /// capture stands alone only where no transfer in the line holds it.
        /// </summary>
        public string CompactLine(IList<SnapshotBody> named)
        {
            if (named.Count == 0) return "";
            var legs = LegsFor(named).Where(l => !IsPolar(l) && l.Id != "land:" + Home).ToList();
            var parts = new List<string>();
            foreach (var leg in legs)
            {
                if (HeldByTrip(legs, leg)) continue;
                var text = leg.Id + " " + Whole(leg.DeltaV);
                if (leg.Id.StartsWith("transfer:", StringComparison.Ordinal) && leg.Arrival > 0) text += " (capture " + Whole(leg.Arrival) + ")";
                parts.Add(text);
            }
            return string.Join("; ", parts.ToArray());
        }

        private static string Lines(IEnumerable<DeltaVLeg> legs)
        {
            var text = new StringBuilder();
            foreach (var leg in legs)
            {
                text.Append("- ").Append(leg.Id).Append(": ").Append(leg.Description).Append(": ").Append(Whole(leg.DeltaV)).Append(" m/s");
                if (leg.PlaneChange > 0)
                    text.Append(" (carries a plane change of ").Append(Whole(leg.PlaneChange)).Append(" m/s)");
                text.Append('\n');
            }
            return text.ToString();
        }

        private static string Whole(double value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture);
        }

        private static double Circular(double gm, double radius)
        {
            return Math.Sqrt(gm / radius);
        }

        /// <summary>The radius of the body's low orbit: clear of the atmosphere, or a fraction of the radius up.</summary>
        internal static double LowOrbit(SnapshotBody body)
        {
            return body.AtmosphereHeight > 0
                ? body.Radius + body.AtmosphereHeight + AtmosphereClearance
                : body.Radius * (1 + AirlessOrbitFraction);
        }

        /// <summary>
        /// From low orbit down to the surface: lower the periapsis, then (airless) stop at it. With an
        /// atmosphere only the periapsis lowering is paid for; drag and parachutes do the rest.
        /// </summary>
        private static double Landing(SnapshotBody body, double lowOrbit)
        {
            var peri = body.AtmosphereHeight > 0 ? body.Radius + body.AtmosphereHeight / 2 : body.Radius;
            var a = (lowOrbit + peri) / 2;
            var lowering = Circular(body.Gm, lowOrbit) - Math.Sqrt(body.Gm * (2 / lowOrbit - 1 / a));
            if (body.AtmosphereHeight > 0) return lowering;
            return lowering + Math.Sqrt(body.Gm * (2 / peri - 1 / a));
        }

        private static string Km(double metres)
        {
            return (metres / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km";
        }
    }
}
