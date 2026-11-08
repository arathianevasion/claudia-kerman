// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #866: a Mission Goal at a moon reached by an interplanetary arrival at its planet is priced by the planet's high orbit, as <c>capture</c> keeps it:
    /// the Delta-v Map's <c>transfer:Duna</c> and <c>transfer:Duna&gt;Ike</c> are one route, about 600 m/s under the low circle's, and the Craft Check and
    /// the per-leg rule judge by it.
    /// </summary>
    public sealed class HighRoutePricingTests
    {
        private static DeltaVMap Map()
        {
            return DeltaVMap.Compute(DeltaVMapTests.StockSystem(), "Kerbin");
        }

        private static List<SnapshotBody> Named(params string[] names)
        {
            return DeltaVMapTests.StockSystem().Where(b => names.Contains(b.Name)).ToList();
        }

        /// <summary>The low circle's route: Kerbin to low Duna orbit with its capture, then low Duna orbit to low Ike orbit.</summary>
        private static double LowRoute(DeltaVMap map)
        {
            return map.Leg("transfer:Duna").DeltaV + map.Leg("transfer:Duna>Ike").DeltaV;
        }

        [Fact]
        public void TheMapsIkeRouteFromKerbinIsOneFigureAbout600UnderTheLowCircle()
        {
            var map = Map();
            var route = map.HighRouteLegs("Ike");
            Assert.NotNull(route);
            Assert.Equal(new[] { "transfer:Duna", "transfer:Duna>Ike" }, route.Select(l => l.Id));
            var high = route.Sum(l => l.DeltaV);
            Assert.InRange(LowRoute(map) - high, 500, 700);
            // The departure from Kerbin is the same; what changes is Duna's capture and the trip on from it.
            Assert.Equal(map.Leg("transfer:Duna").Departure, route[0].Departure, 6);
            Assert.True(route[0].Arrival > 0 && route[0].Arrival < map.Leg("transfer:Duna").Arrival);
            Assert.True(route[1].Departure < map.Leg("transfer:Duna>Ike").Departure);
            Assert.Equal(map.Leg("transfer:Duna>Ike").Arrival, route[1].Arrival, 6);
        }

        [Fact]
        public void OnlyAMoonOfAnotherPlanetHasAHighRoute()
        {
            var map = Map();
            Assert.Null(map.HighRouteLegs("Mun"));
            Assert.Null(map.HighRouteLegs("Duna"));
            Assert.NotNull(map.HighRouteLegs("Gilly"));
            Assert.NotNull(map.HighRouteLegs("Vall"));
        }

        /// <summary>Gilly's apoapsis (31,500 km × 1.55) is past half of Eve's sphere of influence: the kept ellipse reaches 10% past it, still well inside the sphere.</summary>
        [Fact]
        public void GillysEccentricOrbitIsKeptPastItsApoapsisInsideEvesSphere()
        {
            var gilly = DeltaVMapTests.StockSystem().Single(b => b.Name == "Gilly");
            var eve = DeltaVMapTests.StockSystem().Single(b => b.Name == "Eve");
            var sun = DeltaVMapTests.StockSystem().Single(b => b.Name == "Sun");
            var soi = eve.Sma * System.Math.Pow(eve.Gm / sun.Gm, 0.4);
            var apoapsis = gilly.Sma * (1 + gilly.Eccentricity);
            Assert.True(apoapsis > HighRoute.PriceShare * soi);
            var top = HighRoute.TopR(soi, gilly);
            Assert.Equal(apoapsis * (1 + HighRoute.MoonMargin), top, 3);
            Assert.True(top < HighRoute.MaxShare * soi);
            var route = Map().HighRouteLegs("Gilly");
            Assert.True(route[0].Arrival < Map().Leg("transfer:Eve").Arrival);
            // Ike's sits at half the sphere as before; a moon too near its sphere's edge has none.
            Assert.Equal(HighRoute.PriceShare * 1000e6, HighRoute.TopR(1000e6, DeltaVMapTests.StockSystem().Single(b => b.Name == "Ike")));
            Assert.Equal(0, HighRoute.TopR(apoapsis * 1.4, gilly));
        }

        [Fact]
        public void TheGoalsTextAndCompactLineCarryTheRouteWithNoNewLine()
        {
            var map = Map();
            var route = map.HighRouteLegs("Ike");
            var text = map.ToText(Named("Ike"));
            Assert.Contains("- transfer:Duna: Low Kerbin orbit to high Duna orbit (departure, plane change and capture): " + route[0].DeltaV.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " m/s", text);
            Assert.Contains("- transfer:Duna>Ike: High Duna orbit to low Ike orbit (departure, plane change and capture): " + route[1].DeltaV.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " m/s", text);
            Assert.DoesNotContain("capture:Duna", text);

            var line = map.CompactLine(Named("Ike"));
            Assert.Contains("transfer:Duna " + route[0].DeltaV.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " (capture ", line);
            Assert.Contains("transfer:Duna>Ike " + route[1].DeltaV.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " (capture ", line);
            // The same legs as the low circle's line, so no longer.
            Assert.Equal(line.Split(';').Length, map.CompactLine(Named("Ike", "Duna")).Split(';').Length - 2);

            // A goal naming Duna too keeps the low circle: it may stop there.
            Assert.Contains("- transfer:Duna: Low Kerbin orbit to low Duna orbit", map.ToText(Named("Duna", "Ike")));
        }

        [Fact]
        public void TheCraftCheckIsGivenTheRoutesFigures()
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "PRELAUNCH", Bodies = DeltaVMapTests.StockSystem() };
            var facts = CraftCheckPrompts.Facts("Land on Ike and return to Kerbin", snapshot, 10, null);
            Assert.Contains("- transfer:Duna>Ike: High Duna orbit to low Ike orbit", facts);
            Assert.Contains("- transfer:Duna: Low Kerbin orbit to high Duna orbit", facts);
        }

        private static PlannedSequence Leg(string name, double deltaV)
        {
            return new PlannedSequence { Name = name, DeltaV = deltaV };
        }

        [Fact]
        public void ThePerLegRulePricesDunasCaptureAndTheTripToIkeByTheHighOrbit()
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "PRELAUNCH", Bodies = DeltaVMapTests.StockSystem() };
            var map = Map();
            var route = map.HighRouteLegs("Ike");
            var plan = new List<PlannedSequence>
            {
                Leg("Launch to 80 km orbit", 3400), Leg("Transfer to Duna", 1100), Leg("Capture at Duna", 260), Leg("Transfer to Ike", 100), Leg("Capture at Ike and land", 600),
            };
            var legs = PlanLegs.Judge(snapshot, plan, "");
            Assert.Equal(route[0].Arrival, legs.Single(l => l.Sequence.Name == "Capture at Duna").Figure, 6);
            Assert.Equal(route[1].Departure + route[1].PlaneChange, legs.Single(l => l.Sequence.Name == "Transfer to Ike").Figure, 6);
            // Budgets at the route's figures are not under the Map.
            Assert.DoesNotContain(legs, l => l.Under);

            // Back at Duna from Ike, its capture is priced from Ike, and a second trip to Ike from the low circle: the high route is used once.
            var back = PlanLegs.Judge(snapshot, new List<PlannedSequence>
            {
                Leg("Launch to 80 km orbit", 3400), Leg("Transfer to Duna", 1100), Leg("Capture at Duna", 260), Leg("Transfer to Ike", 100), Leg("Land on Ike", 600),
                Leg("Transfer to Duna", 300), Leg("Capture at Duna", 300), Leg("Transfer to Ike", 300), Leg("Capture at Ike", 300),
            }, "");
            Assert.Equal(route[1].Departure + route[1].PlaneChange, back[3].Figure, 6);
            Assert.Equal(map.Trip("Ike", "Duna").Arrival, back[6].Figure, 6);
            Assert.Equal(map.Trip("Duna", "Ike").Departure + map.Trip("Duna", "Ike").PlaneChange, back[7].Figure, 6);

            // With Duna's surface next instead, its capture is the low circle's.
            var duna = PlanLegs.Judge(snapshot, new List<PlannedSequence> { Leg("Launch to 80 km orbit", 3400), Leg("Transfer to Duna", 1100), Leg("Capture at Duna", 200), Leg("Land on Duna", 100) }, "");
            Assert.Equal(map.Leg("transfer:Duna").Arrival, duna.Single(l => l.Sequence.Name == "Capture at Duna").Figure, 6);
        }
    }
}
