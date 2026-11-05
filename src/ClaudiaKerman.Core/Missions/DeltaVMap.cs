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
        /// <summary>The speed it arrives with relative to the destination, m/s, before the destination's gravity: what a polar orbit's turn is costed at.</summary>
        internal double ArrivalExcess;
        /// <summary>The bodies the leg joins, for <see cref="DeltaVMap.ToText(IList{SnapshotBody})"/>.</summary>
        internal string[] Bodies = new string[0];
    }

    /// <summary>
    /// The Delta-v Map (#757: every body, not only the home body's moons): the delta-v each leg of travel needs, against which a Mission
    /// Plan is budgeted. Computed from body data (patched-conic Hohmann transfers between low orbits, plus a simple ascent-loss allowance)
    /// rather than copied from community charts, so it follows whatever planet pack the game runs. The figures carry no margin; the Craft
    /// Check adds it. A trip's plane change is priced at the transfer's apoapsis, its slower end. A return home is priced to an entry where
    /// <see cref="ArrivalRule"/> says the trip ends in one. <see cref="PlanLegs"/> matches a Mission Plan's Maneuver Sequences to these
    /// legs by their words and position, and judges each budget against the figure at its tolerance: it states both rules.
    /// </summary>
    public sealed class DeltaVMap
    {
        /// <summary>Ascent losses (gravity and drag) as a fraction of low-orbit speed, with an atmosphere of up to Kerbin's pressure.</summary>
        private const double AtmosphericAscentLoss = 0.5;
        /// <summary>The further loss per atmosphere of sea-level pressure above Kerbin's: thick air's drag, set by Eve's ascent.</summary>
        private const double ThickAirLossPerAtm = 0.25;
        /// <summary>The same without an atmosphere.</summary>
        private const double AirlessAscentLoss = 0.1;
        /// <summary>Low orbit above an atmosphere clears it by this much (m).</summary>
        private const double AtmosphereClearance = 10000;
        /// <summary>Low orbit round an airless body, as a fraction of its radius above the surface.</summary>
        private const double AirlessOrbitFraction = 0.07;

        /// <summary>The inclination of a polar orbit (degrees).</summary>
        private const double PolarInclination = 90;

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
            // A polar orbit round the body, from the equatorial one the transfer captures into: turned at the highest point
            // of the capture, where the craft moves at the speed it arrived with (the hyperbolic excess).
            var polar = OrbitChange.PlaneChangeDeltaV(inbound.ArrivalExcess, PolarInclination);
            Legs.Add(new DeltaVLeg
            {
                Id = "polar:" + body.Name,
                Description = "Extra, added to its capture, for a polar orbit of " + body.Name + " instead of an equatorial one (plane change costed at the arrival speed relative to " + body.Name + ", an upper bound on a turn at the capture orbit's highest point; return:" + body.Name + " does not include it)",
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
        /// A landing's or an ascent's figure with the craft's thrust over weight there: on an airless body the leg's figure plus v/TWR,
        /// gravity acting for the whole burn, the upper bound on the loss (a TWR under 1 counts as 1). With no TWR known, or on a body
        /// with air (whose allowance already holds gravity and drag), the leg's own figure. 0 for a leg the Map doesn't have.
        /// </summary>
        public double Surface(string id, double? twr)
        {
            var leg = Leg(id);
            if (leg == null) return 0;
            var body = _bodies.FirstOrDefault(b => b.Name == leg.Bodies.FirstOrDefault());
            if (body == null || body.AtmosphereHeight > 0 || !twr.HasValue || twr.Value <= 0) return leg.DeltaV;
            return leg.DeltaV * (1 + 1 / Math.Max(1, twr.Value));
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
            return Legs.Where(l => l.Bodies.All(route.Contains) && (!OnOneBody(l) || names.Contains(l.Bodies[0]) || (l.Bodies[0] == Home && !IsPolar(l)))).ToList();
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

        /// <summary>The legs for the bodies a Mission Goal names, as lines; per-turn text, never the cached prefix.</summary>
        public string ToText(IList<SnapshotBody> named)
        {
            return Lines(LegsFor(named));
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
                if (leg.Id.StartsWith("capture:", StringComparison.Ordinal) && legs.Any(l => l.Id == "transfer:" + leg.Id.Substring(8))) continue;
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
