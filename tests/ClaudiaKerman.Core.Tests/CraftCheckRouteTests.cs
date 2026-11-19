// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #998: the Craft Check priced every Mission from Kerbin's pad. Mun Shot v2 landed on the Mun with the goal "land in the greater flats on
    /// minmus" was given launch:Kerbin and the trip from low Kerbin orbit, judged at 4,532 m/s against its 1,518, and a Kerbin liftoff's TWR.
    /// The route now starts where the craft is: the ascent by plan_ascent (the Map's, marked an estimate, where it refuses), a trip that isn't a
    /// planet to its own moon by plan_transfer at the player's pick, landings by the Map; and leg_under_map judges by the same figures.
    /// </summary>
    public sealed class CraftCheckRouteTests
    {
        private const string MinmusGoal = "land in the greater flats on minmus";
        private const double NewGame = 1203.48;

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", name));
        }

        /// <summary>The request's own Game Snapshot (craft-checks/20261111-175744-913): landed on the Mun, 1,518 m/s, with the stock body table.</summary>
        private static GameSnapshot OnTheMun()
        {
            return GameSnapshot.Parse(Fixture("mun-shot-v2-landed-mun.json"));
        }

        private static WindowRow Pick(GameSnapshot snapshot, string goal, string body, WindowKind kind)
        {
            var offer = WindowChoice.Work(snapshot, goal, 15, snapshot.Ut);
            var row = offer.RowFor(new WindowPick(body, kind));
            Assert.True(row != null, offer.Problem);
            return row;
        }

        private static string Line(string text, string id)
        {
            return text.Split('\n').Single(l => l.StartsWith("- " + id + ":", StringComparison.Ordinal));
        }

        private static bool HasLine(string text, string id)
        {
            return text.Split('\n').Any(l => l.StartsWith("- " + id + ":", StringComparison.Ordinal));
        }

        private static double Figure(string line)
        {
            var at = line.IndexOf(" m/s", StringComparison.Ordinal);
            var start = line.LastIndexOf(": ", at, StringComparison.Ordinal) + 2;
            return double.Parse(line.Substring(start, at - start).Replace(",", ""));
        }

        private static double Twr(GameSnapshot snapshot, string body)
        {
            var stage = snapshot.Stages.Single(s => s.Number == 2);
            return CraftCheckPrompts.ThrustOverWeight(snapshot, stage, snapshot.Bodies.Single(b => b.Name == body));
        }

        [Fact]
        public void LandedOnTheMunTheRouteStartsThereAndPassesWithTheMargin()
        {
            var snapshot = OnTheMun();
            var window = Pick(snapshot, MinmusGoal, "Minmus", WindowKind.Optimal);
            var text = CraftCheckPrompts.Facts(MinmusGoal, snapshot, 15, null, false, window);
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            // No Kerbin launch and no trip from low Kerbin orbit: the planners' rows replace them, not stand beside them.
            Assert.False(HasLine(text, "launch:Kerbin"));
            Assert.False(HasLine(text, "transfer:Minmus"));
            Assert.DoesNotContain("from Kerbin's surface", text);

            // The Mun ascent: plan_ascent refuses a body with no air, so it is the Map's with this craft's gravity loss, marked an estimate.
            var ascent = Line(text, "ascend:Mun");
            Assert.Contains("estimate", ascent);
            Assert.Contains("plan_ascent", ascent);
            Assert.Equal(map.WithGravityLoss("ascend:Mun", Twr(snapshot, "Mun")), Figure(ascent), 0);

            // Mun to Minmus is two moons of Kerbin: plan_transfer's figure at the player's Optimal window, the transfer options' row: its ejection
            // of 257 m/s (the figure the report quoted) and the 88 m/s capture into low Minmus orbit the landing starts from.
            var trip = Line(text, "transfer:Mun>Minmus");
            Assert.Contains("plan_transfer, Optimal pick", trip);
            Assert.Equal(Math.Round(window.TotalDv), Figure(trip), 0);
            Assert.Contains("ejection 257 m/s", window.Text);
            Assert.InRange(window.TotalDv, 330, 360);
            Assert.Contains("includes the capture, 88 m/s", trip);

            // The Minmus landing stays the Map's, with the gravity loss at this craft's TWR there.
            var land = Line(text, "land:Minmus");
            Assert.DoesNotContain("plan_", land);
            Assert.Equal(map.WithGravityLoss("land:Minmus", Twr(snapshot, "Minmus")), Figure(land), 0);

            // 680 + 346 + 198 = 1,224 m/s, about 1,410 with the 15% margin, against the craft's 1,518: the route fits (it was judged at 4,532).
            var route = Figure(ascent) + Figure(trip) + Figure(land);
            Assert.InRange(route * 1.15, 1350, 1460);
            Assert.True(route * 1.15 < snapshot.Stages.Sum(s => s.DeltaVVac));
        }

        [Fact]
        public void LandedOnTheMunTheTwrIsJudgedOnTheMunNotKerbin()
        {
            var snapshot = OnTheMun();
            var text = CraftCheckPrompts.Facts(MinmusGoal, snapshot, 15, null);
            var twr = text.Split('\n').SkipWhile(l => !l.StartsWith("Thrust over weight", StringComparison.Ordinal)).Take(2).ToList();

            Assert.Contains("the body the craft lifts off from (Mun)", twr[0]);
            Assert.DoesNotContain("Kerbin", twr[0]);
            Assert.Contains("\"Mun\":", twr[1]);
            Assert.Contains("\"Minmus\":", twr[1]);
            Assert.DoesNotContain("\"Kerbin\":", twr[1]);
        }

        [Fact]
        public void FromThePadToTheMunTheRowsAndFiguresAreAsBefore()
        {
            var snapshot = GameSnapshot.Parse(Fixture("mun-shot-pad.json"));
            snapshot.Bodies = DeltaVMapTests.StockSystem();
            const string goal = "Land on the Mun and return to Kerbin";
            var text = CraftCheckPrompts.Facts(goal, snapshot, 15, null);
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            var rows = text.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l.Substring(2, l.IndexOf(": ", StringComparison.Ordinal) - 2)).ToList();
            Assert.Equal(new[] { "launch:Kerbin", "land:Kerbin", "transfer:Mun", "polar:Mun", "land:Mun", "ascend:Mun", "return:Mun" }, rows);
            // A planet to its own moon is the Map's trip, capture included; nothing is a planner's or an estimate.
            Assert.Equal(map.Leg("launch:Kerbin").DeltaV, Figure(Line(text, "launch:Kerbin")), 0);
            Assert.Equal(map.Leg("transfer:Mun").DeltaV, Figure(Line(text, "transfer:Mun")), 0);
            Assert.Equal(map.Leg("return:Mun").DeltaV, Figure(Line(text, "return:Mun")), 0);
            Assert.DoesNotContain("plan_transfer", text.Substring(0, text.IndexOf("Craft facts", StringComparison.Ordinal)));
            Assert.DoesNotContain("estimate", text.Substring(0, text.IndexOf("Craft facts", StringComparison.Ordinal)));
            // The TWR table is the Kerbin system's, as it was.
            Assert.Contains("on each body of the Kerbin system and each body the Mission Goal names", text);
        }

        [Fact]
        public void InKerbinOrbitTheLaunchIsFlownSoItsRowIsGone()
        {
            var snapshot = GameSnapshot.Parse(Fixture("mun-shot-v2-kerbin-orbit.json"));
            var text = CraftCheckPrompts.Facts("Land on the Mun", snapshot, 15, null);
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            Assert.False(HasLine(text, "launch:Kerbin"));
            Assert.Equal(map.Leg("transfer:Mun").DeltaV, Figure(Line(text, "transfer:Mun")), 0);
            Assert.Contains("on each body of the Kerbin system", text);
        }

        private static GameSnapshot OnPad(double ut)
        {
            var snapshot = GameSnapshot.Parse(Fixture("on-pad.json"));
            snapshot.Bodies = GameSnapshot.Parse(Fixture("mun-shot-v2-kerbin-orbit.json")).Bodies;
            snapshot.Ut = ut;
            snapshot.Stages[0].DeltaVVac = 9000;
            return snapshot;
        }

        [Fact]
        public void FromThePadToDunaTheTripIsPlanTransfersAtThePickedWindow()
        {
            const string goal = "Land on Duna";
            var snapshot = OnPad(NewGame);
            var optimal = Pick(snapshot, goal, "Duna", WindowKind.Optimal);
            var soonest = Pick(snapshot, goal, "Duna", WindowKind.Soonest);

            var text = CraftCheckPrompts.Facts(goal, snapshot, 15, null, false, optimal);
            var trip = Line(text, "transfer:Duna");
            Assert.Contains("plan_transfer", trip);
            Assert.Contains("Optimal", trip);
            Assert.Equal(Math.Round(optimal.TotalDv), Figure(trip), 0);
            Assert.Equal(Math.Round(soonest.TotalDv), Figure(Line(CraftCheckPrompts.Facts(goal, snapshot, 15, null, false, soonest), "transfer:Duna")), 0);
            Assert.True(HasLine(text, "launch:Kerbin"));
        }

        [Fact]
        public void APlannerThatRefusesFallsBackToTheMapsRowMarkedAnEstimate()
        {
            // From the Mun's surface plan_transfer plans no trip to another planet (its direct route starts in the moon's orbit).
            var snapshot = OnTheMun();
            const string goal = "Land on Duna";
            var text = CraftCheckPrompts.Facts(goal, snapshot, 15, null);
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            var trip = Line(text, "transfer:Mun>Duna");
            Assert.Contains("estimate", trip);
            Assert.Equal(map.Trip("Mun", "Duna").DeltaV, Figure(trip), 0);
            Assert.False(HasLine(text, "transfer:Duna"));
            Assert.False(HasLine(text, "launch:Kerbin"));
            // The estimate is a figure to judge by like any other: nothing says to fail for it.
            Assert.DoesNotContain("fail", trip, StringComparison.OrdinalIgnoreCase);

            // leg_under_map judges by the same estimate, with no error.
            var legs = PlanLegs.Judge(snapshot, new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Ascend from the Mun", DeltaV = 700 },
                new PlannedSequence { Name = "Transfer to Duna", DeltaV = 1500 },
            }, "");
            Assert.Equal("transfer:Mun>Duna", legs[1].Legs);
        }

        [Fact]
        public void LegUnderMapJudgesTheMunToMinmusRouteByTheCraftChecksFigures()
        {
            var snapshot = OnTheMun();
            var window = Pick(snapshot, MinmusGoal, "Minmus", WindowKind.Optimal);
            var text = CraftCheckPrompts.Facts(MinmusGoal, snapshot, 15, null, false, window);
            // A Mission's snapshot carries the player's pick.
            snapshot.WindowPick = new WindowPick("Minmus", WindowKind.Optimal);

            var legs = PlanLegs.Judge(snapshot, new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Ascend from the Mun", DeltaV = 700 },
                new PlannedSequence { Name = "Transfer to Minmus and capture", DeltaV = 350 },
                new PlannedSequence { Name = "Land on Minmus", DeltaV = 200 },
            }, "");

            Assert.Equal("ascend:Mun", legs[0].Legs);
            Assert.Equal(Figure(Line(text, "ascend:Mun")), legs[0].Figure, 0);
            Assert.Equal("transfer:Mun>Minmus", legs[1].Legs);
            Assert.Equal(Figure(Line(text, "transfer:Mun>Minmus")), legs[1].Figure, 0);
            Assert.Equal("land:Minmus", legs[2].Legs);
            Assert.Equal(Figure(Line(text, "land:Minmus")), legs[2].Figure, 0);
            // A budget at the planner's figure is not under it.
            Assert.False(legs[1].Under);

            // The plan's proposal prices again: the row the Craft Check showed serves it, with no new search.
            RoutePlanners.Searches = 0;
            var priced = PlanLegs.Judge(snapshot, legs.Select(l => l.Sequence).ToList(), "", true);
            Assert.Equal(0, RoutePlanners.Searches);
            Assert.Equal(legs[1].Figure, priced[1].Figure, 6);
        }

        [Fact]
        public void LegUnderMapJudgesThePadToDunaTripByThePickedWindow()
        {
            const string goal = "Land on Duna";
            var snapshot = OnPad(NewGame);
            var optimal = Pick(snapshot, goal, "Duna", WindowKind.Optimal);
            snapshot.WindowPick = new WindowPick("Duna", WindowKind.Optimal);

            snapshot.CraftPersistentId = 9981;
            var plan = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Launch to orbit", DeltaV = 3400 },
                new PlannedSequence { Name = "Transfer to Duna and capture", DeltaV = 2000 },
            };

            // Priced as the plan is proposed; in flight, the figure priced then.
            var legs = PlanLegs.Judge(snapshot, plan, "", true);
            Assert.Equal("transfer:Duna", legs[1].Legs);
            Assert.Equal(optimal.TotalDv, legs[1].Figure, 0);
            Assert.Equal(legs[1].Figure, PlanLegs.Judge(snapshot, plan, "")[1].Figure, 6);
        }

        private static readonly List<PlannedSequence> DunaPlan = new List<PlannedSequence>
        {
            new PlannedSequence { Name = "Transfer to Duna and capture", DeltaV = 2000 },
            new PlannedSequence { Name = "Land on Duna", DeltaV = 300 },
        };

        private static GameSnapshot InKerbinOrbitForDuna(uint id)
        {
            var snapshot = GameSnapshot.Parse(Fixture("mun-shot-v2-kerbin-orbit.json"));
            snapshot.CraftPersistentId = id;
            snapshot.Stages[0].DeltaVVac = 9000;
            snapshot.WindowPick = new WindowPick("Duna", WindowKind.Optimal);
            return snapshot;
        }

        [Fact]
        public void InFlightPlanLegsAndTheDropGuardReadThePricedFigureAndNeverSearch()
        {
            // A pad Mission to Duna after launch: every Margin check and drop judged the plan, each a search (the sma and dv kept changing).
            var snapshot = InKerbinOrbitForDuna(9982);
            RoutePlanners.Searches = 0;
            var first = PlanLegs.Judge(snapshot, DunaPlan, "");
            snapshot.Orbit.SemiMajorAxis += 2000;
            snapshot.Stages[0].DeltaVVac -= 50;
            var second = PlanLegs.Judge(snapshot, DunaPlan, "");
            DropGuard.Check(snapshot, DunaPlan, -1, 5000, 4000);
            Assert.Equal(0, RoutePlanners.Searches);
            // Nothing priced yet: the Map's figure.
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");
            Assert.Equal(map.Trip("Kerbin", "Duna").DeltaV, first[0].Figure, 0);
            Assert.Equal(first[0].Figure, second[0].Figure, 6);

            // Priced once (the plan's proposal): one search, and the checks in flight read it, with no more.
            var priced = PlanLegs.Judge(snapshot, DunaPlan, "", true);
            Assert.Equal(1, RoutePlanners.Searches);
            snapshot.Orbit.SemiMajorAxis += 2000;
            Assert.Equal(priced[0].Figure, PlanLegs.Judge(snapshot, DunaPlan, "")[0].Figure, 6);
            Assert.Equal(PlanLegs.Need(priced), DropGuard.Check(snapshot, DunaPlan, -1, 5000, 1000).Need, 6);
            Assert.Equal(1, RoutePlanners.Searches);
            Assert.NotEqual(map.Trip("Kerbin", "Duna").DeltaV, priced[0].Figure, 2);

            // Another save's craft (another persistent id, the same body table) has no priced figure.
            Assert.Equal(first[0].Figure, PlanLegs.Judge(InKerbinOrbitForDuna(9983), DunaPlan, "")[0].Figure, 6);
        }

        [Fact]
        public void FromKerbinOrbitWithThePickAndNoRowTheCraftCheckAndPlanLegsAgree()
        {
            var snapshot = InKerbinOrbitForDuna(9984);
            var text = CraftCheckPrompts.Facts("Land on Duna", snapshot, 15, null);
            var row = Line(text, "transfer:Duna");
            Assert.Contains("plan_transfer, Optimal pick", row);
            Assert.Equal(Figure(row), Math.Round(PlanLegs.Judge(snapshot, DunaPlan, "")[0].Figure), 0);
            Assert.Equal(Figure(row), Math.Round(PlanLegs.Judge(snapshot, DunaPlan, "", true)[0].Figure), 0);
        }

        [Fact]
        public void LandedOnTheMunAGoalThatReturnsHomePlansOnlyTheHopFromTheStart()
        {
            var snapshot = OnTheMun();
            const string goal = "Land on Minmus and return to Kerbin";
            var text = CraftCheckPrompts.Facts(goal, snapshot, 15, null);
            var alone = CraftCheckPrompts.Facts(MinmusGoal, snapshot, 15, null);
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            Assert.False(HasLine(text, "return:Mun"));
            Assert.False(HasLine(text, "launch:Kerbin"));
            Assert.True(HasLine(text, "transfer:Mun>Minmus"));
            // The trips on from Minmus are the Map's.
            Assert.Equal(map.Leg("return:Minmus").DeltaV, Figure(Line(text, "return:Minmus")), 0);
            // Nothing extra was spent before the Minmus landing: its gravity loss is as for the goal that names Minmus alone.
            Assert.Equal(Figure(Line(alone, "land:Minmus")), Figure(Line(text, "land:Minmus")), 0);
            var route = new[] { "ascend:Mun", "transfer:Mun>Minmus", "land:Minmus", "ascend:Minmus", "return:Minmus" }.Sum(id => Figure(Line(text, id)));
            var all = text.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal) && !l.StartsWith("- land:Kerbin", StringComparison.Ordinal)
                && !l.StartsWith("- polar:", StringComparison.Ordinal)).Sum(l => Figure(l));
            Assert.Equal(route, all, 0);
        }

        private static GameSnapshot RoundTheMun(double eccentricity)
        {
            var snapshot = OnTheMun();
            snapshot.Situation = eccentricity < 1 ? "ORBITING" : "ESCAPING";
            snapshot.Orbit.Eccentricity = eccentricity;
            snapshot.Orbit.SemiMajorAxis = eccentricity < 1 ? 230000 : -400000;
            snapshot.Orbit.Periapsis = 30000;
            snapshot.Orbit.Apoapsis = eccentricity < 1 ? 30000 : -1;
            snapshot.Altitude = 30000;
            return snapshot;
        }

        [Fact]
        public void OrbitingTheMunTheTripToMinmusIsPlanTransfersAndNoAscent()
        {
            var snapshot = RoundTheMun(0);
            var text = CraftCheckPrompts.Facts(MinmusGoal, snapshot, 15, null);

            Assert.False(HasLine(text, "launch:Kerbin"));
            Assert.False(HasLine(text, "ascend:Mun"));
            Assert.False(HasLine(text, "transfer:Minmus"));
            Assert.Contains("plan_transfer", Line(text, "transfer:Mun>Minmus"));
            Assert.Contains("on Mun, which the craft orbits,", text);
        }

        private static List<string> Rows(string text)
        {
            return text.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l.Substring(2, l.IndexOf(": ", StringComparison.Ordinal) - 2)).ToList();
        }

        [Fact]
        public void EscapingTheMunOrRoundTheSunTheRouteIsPricedAsBefore()
        {
            var escaping = RoundTheMun(1.3);
            var sun = RoundTheMun(0.1);
            sun.Orbit.Body = "Sun";
            sun.Body = "Sun";
            sun.Orbit.SemiMajorAxis = 14000000000;
            foreach (var snapshot in new[] { escaping, sun })
            {
                var named = MissionPrompts.GoalBodies(snapshot, MinmusGoal);
                var text = CraftCheckPrompts.Facts(MinmusGoal, snapshot, 15, null);
                Assert.Equal(Rows(DeltaVMap.Compute(snapshot.Bodies, "Kerbin").ToText(named)), Rows(text));
                var map = text.Substring(0, text.IndexOf("Craft facts", StringComparison.Ordinal));
                Assert.DoesNotContain("plan_", map);
                Assert.DoesNotContain("estimate", map);
                Assert.Contains("on each body of the Kerbin system", text);
            }
        }

        [Fact]
        public void APlannerRefusalWithAWindowPassedAddsNoSecondWindowLine()
        {
            var snapshot = OnTheMun();
            var stale = new WindowRow { Kind = WindowKind.Soonest, From = "Kerbin", Body = "Duna", TotalDv = 1700, CaptureDv = 600, DepartureUt = snapshot.Ut + 1e6 };
            var text = CraftCheckPrompts.Facts("Land on Duna", snapshot, 15, null, false, stale);

            Assert.Contains("estimate", Line(text, "transfer:Mun>Duna"));
            Assert.DoesNotContain("the player's Soonest window", text);
            Assert.Single(text.Split('\n'), l => l.Contains(">Duna:"));
        }

        [Fact]
        public void ASoonestWindowThatHasDepartedIsSearchedAgainTheSameGameDay()
        {
            // In low Mun orbit, so a window can depart within hours of the pricing.
            var snapshot = RoundTheMun(0);
            snapshot.CraftPersistentId = 9985;
            snapshot.WindowPick = new WindowPick("Minmus", WindowKind.Soonest);
            var minmus = snapshot.Bodies.Single(b => b.Name == "Minmus");
            string why;
            RoutePlanners.Searches = 0;
            var found = RoutePlanners.Transfer(snapshot, minmus, null, 15, out why, true);
            Assert.True(found != null, why);
            Assert.Equal(1, RoutePlanners.Searches);
            Assert.True(found.DepartureUt > snapshot.Ut && !double.IsInfinity(found.DepartureUt));
            // Priced again before it departs, the same game day: the kept answer.
            RoutePlanners.Transfer(snapshot, minmus, null, 15, out why, true);
            Assert.Equal(1, RoutePlanners.Searches);

            // Priced shortly before the window departs, then again just after it, the same game day: searched again.
            PlannedFigure day = null;
            var seen = "";
            foreach (var before in new[] { 600.0, 1800, 3600, 7200, 14400 })
            {
                snapshot.Ut = found.DepartureUt - before;
                day = RoutePlanners.Transfer(snapshot, minmus, null, 15, out why, true);
                seen += " " + snapshot.Ut + ">" + (day == null ? why : day.DepartureUt.ToString());
                if (day != null && day.DepartureUt > snapshot.Ut && Math.Floor(snapshot.Ut / 21600) == Math.Floor((day.DepartureUt + 1) / 21600)) break;
                day = null;
            }
            Assert.True(day != null, "no window departs the day it is priced" + seen);
            var searched = RoutePlanners.Searches;
            snapshot.Ut = day.DepartureUt + 1;
            RoutePlanners.Transfer(snapshot, minmus, null, 15, out why, true);
            Assert.Equal(searched + 1, RoutePlanners.Searches);
        }
    }
}
