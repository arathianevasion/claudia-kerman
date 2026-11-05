// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The Delta-v Map is computed from body data passed in. Expected figures are the community's
    /// well-known Kerbin-system values (ranges, because the Map is computed, not copied from them).
    /// </summary>
    public sealed class DeltaVMapTests
    {
        public static List<SnapshotBody> KerbinSystem()
        {
            return new List<SnapshotBody>
            {
                new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, Sma = 13599840256, Inclination = 0 },
                new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, AtmosphereHeight = 0, Sma = 12000000, Inclination = 0 },
                new SnapshotBody { Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, AtmosphereHeight = 0, Sma = 47000000, Inclination = 6 },
                new SnapshotBody { Name = "Sun", Parent = "", Gm = 1.1723328e18, Radius = 261600000, AtmosphereHeight = 600000, Sma = 0, Inclination = 0 },
            };
        }

        [Fact]
        public void LaunchToKerbinOrbitIsAboutThirtyFourHundred()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("launch:Kerbin").DeltaV, 3300, 3500);
        }

        [Fact]
        public void TransferToTheMunDepartsAbout860AndCaptures310()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("transfer:Mun").Departure, 830, 890);
            Assert.InRange(map.Leg("transfer:Mun").Arrival, 290, 330);
            Assert.InRange(map.Leg("transfer:Mun").DeltaV, 1120, 1220);
        }

        [Fact]
        public void LandingAndAscentFromLowMunOrbitAreAbout580()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("land:Mun").DeltaV, 540, 650);
            Assert.InRange(map.Leg("ascend:Mun").DeltaV, 540, 650);
        }

        [Fact]
        public void ReturnFromLowMunOrbitToKerbinIsAbout310()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("return:Mun").DeltaV, 290, 330);
        }

        [Fact]
        public void MinmusIsDearerToLeaveForAndCheapToLandOn()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("transfer:Minmus").Departure, 900, 960);
            Assert.InRange(map.Leg("transfer:Minmus").Arrival, 140, 190);
            Assert.InRange(map.Leg("land:Minmus").DeltaV, 150, 260);
        }

        [Fact]
        public void APolarMunOrbitBudgetsAPlaneChangeAtTheCapturesHighestPoint()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            var polar = map.Leg("polar:Mun");
            // 2 v sin(45 degrees) at the speed the craft arrives with relative to the Mun, a few hundred m/s.
            Assert.InRange(polar.DeltaV, 300, 600);
            Assert.Equal(polar.DeltaV, polar.PlaneChange);
            Assert.Contains("polar:Mun: ", map.ToText());
            Assert.Contains("carries a plane change", map.ToText().Split('\n').Single(l => l.StartsWith("- polar:Mun")));
        }

        [Fact]
        public void MinmusBudgetsItsSixDegreesInTheTransferAndEquatorialLegsBudgetNone()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            var minmus = map.Leg("transfer:Minmus");
            Assert.InRange(minmus.PlaneChange, 4, 6);
            Assert.Equal(minmus.Departure + minmus.Arrival + minmus.PlaneChange, minmus.DeltaV, 6);
            Assert.Equal(0, map.Leg("transfer:Mun").PlaneChange);
            Assert.Equal(0, map.Leg("land:Mun").PlaneChange);
            var lines = map.ToText().Split('\n');
            Assert.DoesNotContain("carries a plane change", lines.Single(l => l.StartsWith("- transfer:Mun:")));
            Assert.Contains("carries a plane change", lines.Single(l => l.StartsWith("- transfer:Minmus:")));
        }

        [Fact]
        public void EveryMoonOfTheHomeBodyGetsLegsAndNothingIsHardCoded()
        {
            var bodies = KerbinSystem();
            bodies.Add(new SnapshotBody { Name = "Pebble", Parent = "Kerbin", Gm = 1e9, Radius = 30000, Sma = 20000000 });
            var map = DeltaVMap.Compute(bodies, "Kerbin");
            Assert.Equal(new[] { "Minmus", "Mun", "Pebble" }, map.Moons.OrderBy(m => m).ToArray());
            Assert.True(map.Leg("transfer:Pebble").DeltaV > 0);

            bodies.First(b => b.Name == "Kerbin").Gm *= 2;
            Assert.True(DeltaVMap.Compute(bodies, "Kerbin").Leg("launch:Kerbin").DeltaV > map.Leg("launch:Kerbin").DeltaV * 1.3);
        }

        [Fact]
        public void AnUnknownHomeBodyGivesAnEmptyMap()
        {
            Assert.Empty(DeltaVMap.Compute(KerbinSystem(), "Eve").Legs);
            Assert.Null(DeltaVMap.Compute(KerbinSystem(), "Kerbin").Leg("land:Duna"));
        }

        /// <summary>The stock system's bodies, as the game gives them.</summary>
        public static List<SnapshotBody> StockSystem()
        {
            return new List<SnapshotBody>
            {
                new SnapshotBody { Name = "Sun", Parent = "", Gm = 1.1723328e18, Radius = 261600000, AtmosphereHeight = 600000, SeaLevelAtm = 0.16 },
                new SnapshotBody { Name = "Moho", Parent = "Sun", Gm = 1.6860938e11, Radius = 250000, Sma = 5263138304, Inclination = 7 },
                new SnapshotBody { Name = "Eve", Parent = "Sun", Gm = 8.1717302e12, Radius = 700000, AtmosphereHeight = 90000, SeaLevelAtm = 5, Sma = 9832684544, Inclination = 2.1 },
                new SnapshotBody { Name = "Gilly", Parent = "Eve", Gm = 8289449.8, Radius = 13000, Sma = 31500000, Inclination = 12 },
                new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, Sma = 13599840256 },
                new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, Sma = 12000000 },
                new SnapshotBody { Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, Sma = 47000000, Inclination = 6 },
                new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667, Sma = 20726155264, Inclination = 0.06 },
                new SnapshotBody { Name = "Ike", Parent = "Duna", Gm = 1.8568369e10, Radius = 130000, Sma = 3200000, Inclination = 0.2 },
                new SnapshotBody { Name = "Dres", Parent = "Sun", Gm = 2.1484489e10, Radius = 138000, Sma = 40839348203, Inclination = 5 },
                new SnapshotBody { Name = "Jool", Parent = "Sun", Gm = 2.82528e14, Radius = 6000000, AtmosphereHeight = 200000, SeaLevelAtm = 15, Sma = 68773560320, Inclination = 1.304 },
                new SnapshotBody { Name = "Laythe", Parent = "Jool", Gm = 1.962e12, Radius = 500000, AtmosphereHeight = 50000, SeaLevelAtm = 0.6, Sma = 27184000 },
                new SnapshotBody { Name = "Vall", Parent = "Jool", Gm = 2.0748e11, Radius = 300000, Sma = 43152000 },
                new SnapshotBody { Name = "Tylo", Parent = "Jool", Gm = 2.82528e12, Radius = 600000, Sma = 68500000 },
                new SnapshotBody { Name = "Eeloo", Parent = "Sun", Gm = 7.4410815e10, Radius = 210000, Sma = 90118820000, Inclination = 6.15 },
            };
        }

        private static void Near(double chart, double figure)
        {
            Assert.InRange(figure, chart * 0.9, chart * 1.1);
        }

        /// <summary>
        /// #757's sanity bound, within 10% of the community chart's figures (the figures stay computed). The chart's route goes through a
        /// high elliptical orbit, so only legs it prices the same way are compared: a low orbit to a moon of Duna or Eve is not one of them.
        /// </summary>
        [Fact]
        public void StockPlanetAndMoonLegsAreWithinTenPercentOfTheCommunityChart()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");

            Near(950 + 130, map.Leg("transfer:Duna").Departure);
            Near(250 + 360, map.Leg("capture:Duna").DeltaV);
            Near(1450, map.Leg("ascend:Duna").DeltaV);

            Near(180, map.Leg("capture:Ike").DeltaV);
            Near(390, map.Leg("land:Ike").DeltaV);
            Near(390, map.Leg("ascend:Ike").DeltaV);

            Near(950 + 90, map.Leg("transfer:Eve").Departure);
            Near(80 + 1330, map.Leg("capture:Eve").DeltaV);
            Near(8000, map.Leg("ascend:Eve").DeltaV);

            // Low Eve orbit to low Gilly orbit, whole: the chart's 210 capture is from its elliptical orbit and sits on Gilly's eccentricity.
            Near(1330 + 60 + 210, map.Leg("transfer:Eve>Gilly").DeltaV);
            // The chart rounds to 10 m/s, so its 30 is compared at that grain.
            Assert.InRange(map.Leg("land:Gilly").DeltaV, 20, 40);

            Near(1070, map.Leg("capture:Laythe").DeltaV);
            Near(2900, map.Leg("ascend:Laythe").DeltaV);

            Near(950 + 610, map.Leg("transfer:Dres").Departure);
            Near(1290, map.Leg("capture:Dres").DeltaV);
            Near(430, map.Leg("land:Dres").DeltaV);
        }

        [Fact]
        public void TheIkeMissionsFlownLegsAgreeWithTheMap()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            // Flown by m20261105-032927-1d2c: 1,142 to Duna, 588 capture, 305 to Ike.
            Near(1142, map.Leg("transfer:Duna").Departure + map.Leg("transfer:Duna").PlaneChange);
            Near(588, map.Leg("capture:Duna").DeltaV);
            Near(305, map.Leg("transfer:Duna>Ike").Departure);
        }

        [Fact]
        public void EveryPlanetAndEveryMoonHasItsLegsAndTheStarHasNone()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            foreach (var body in new[] { "Moho", "Eve", "Duna", "Dres", "Jool", "Eeloo", "Gilly", "Ike", "Laythe", "Vall", "Tylo", "Mun", "Minmus" })
                foreach (var kind in new[] { "capture:", "land:", "ascend:", "return:" })
                    Assert.True(map.Leg(kind + body) != null && map.Leg(kind + body).DeltaV > 0, kind + body);
            foreach (var id in new[] { "transfer:Duna", "transfer:Jool", "transfer:Jool>Laythe", "transfer:Laythe>Jool", "transfer:Laythe>Tylo", "transfer:Mun>Minmus" })
                Assert.True(map.Leg(id).DeltaV > 0, id);
            Assert.Null(map.Leg("land:Sun"));
            Assert.Null(map.Leg("transfer:Ike"));
        }

        [Fact]
        public void AReturnHomeIsPricedToAnEntryNotACapture()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            var duna = map.Leg("return:Duna");
            // Low Duna orbit out to a Hohmann home, and no burn at Kerbin: only the departure is paid.
            Assert.Equal(0, duna.Arrival);
            Assert.InRange(duna.DeltaV, 590, 660);
            Assert.Contains("entry", duna.Description);
            // From Ike straight home, not by way of a capture at Duna.
            Assert.InRange(map.Leg("return:Ike").DeltaV, 400, 500);
        }

        [Fact]
        public void AnAirlessHomeHasNoEntrySoItsReturnPaysTheCapture()
        {
            var bodies = StockSystem();
            bodies.First(b => b.Name == "Kerbin").AtmosphereHeight = 0;
            var duna = DeltaVMap.Compute(bodies, "Kerbin").Leg("return:Duna");
            Assert.True(duna.Arrival > 500);
            Assert.Equal(duna.Departure + duna.PlaneChange + duna.Arrival, duna.DeltaV, 6);
        }

        [Fact]
        public void ACaptureAtABodyWithAirNotesTheAerocapture()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            Assert.Contains("aerocapture", map.Leg("capture:Duna").Description);
            Assert.DoesNotContain("aerocapture", map.Leg("capture:Dres").Description);
        }

        [Fact]
        public void TheCraftsThrustOverWeightAddsGravityLossOnAnAirlessBodyOnly()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            var land = map.Leg("land:Ike").DeltaV;
            // v/TWR: gravity acting for the whole burn, the upper bound. TWR 5 is 20% on top; under 1 counts as 1; none known leaves the figure.
            Assert.Equal(land * 1.2, map.Surface("land:Ike", 5), 6);
            Assert.Equal(land * 2, map.Surface("land:Ike", 0.5), 6);
            Assert.Equal(land, map.Surface("land:Ike", null), 6);
            Assert.Equal(map.Leg("ascend:Duna").DeltaV, map.Surface("ascend:Duna", 5), 6);
        }

        [Fact]
        public void TheGoalsTextHoldsOnlyItsBodiesAndTheLegsBetweenThem()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            var bodies = StockSystem();
            var text = map.ToText(bodies.Where(b => b.Name == "Ike").ToList());
            foreach (var id in new[] { "launch:Kerbin", "transfer:Duna", "capture:Duna", "transfer:Duna>Ike", "transfer:Ike>Duna", "capture:Ike", "land:Ike", "ascend:Ike", "return:Ike", "return:Duna" })
                Assert.Contains("- " + id + ":", text);
            foreach (var id in new[] { "Eve", "Laythe", "Mun", "land:Duna", "polar:Duna" })
                Assert.DoesNotContain(id, text);

            // A goal naming no body keeps the home system, as the Map always gave it.
            var home = map.ToText(new List<SnapshotBody>());
            Assert.Contains("- polar:Mun:", home);
            Assert.DoesNotContain("Duna", home);
        }

        [Fact]
        public void TheCompactLineIsOneLineOfFigures()
        {
            var map = DeltaVMap.Compute(StockSystem(), "Kerbin");
            var line = map.CompactLine(StockSystem().Where(b => b.Name == "Mun").ToList());
            Assert.DoesNotContain("\n", line);
            Assert.StartsWith("launch:Kerbin 3,4", line);
            Assert.Contains("transfer:Mun 1,1", line);
            Assert.Contains("(capture 3", line);
            Assert.Contains("land:Mun 5", line);
            Assert.DoesNotContain("polar", line);
            Assert.Equal("", map.CompactLine(new List<SnapshotBody>()));
        }

        [Fact]
        public void TheMapReadsAsLinesForClaude()
        {
            var text = DeltaVMap.Compute(KerbinSystem(), "Kerbin").ToText();
            Assert.Contains("launch:Kerbin", text);
            Assert.Contains("m/s", text);
        }
    }
}
