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
        }

        [Fact]
        public void LegUnderMapJudgesThePadToDunaTripByThePickedWindow()
        {
            const string goal = "Land on Duna";
            var snapshot = OnPad(NewGame);
            var optimal = Pick(snapshot, goal, "Duna", WindowKind.Optimal);
            snapshot.WindowPick = new WindowPick("Duna", WindowKind.Optimal);

            var legs = PlanLegs.Judge(snapshot, new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Launch to orbit", DeltaV = 3400 },
                new PlannedSequence { Name = "Transfer to Duna and capture", DeltaV = 2000 },
            }, "");

            Assert.Equal("transfer:Duna", legs[1].Legs);
            Assert.Equal(optimal.TotalDv, legs[1].Figure, 0);
        }
    }
}
