// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;
using Xunit;
using static ClaudiaKerman.Core.Tests.Contracts.ContractCheckTests;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1028: the Contract Route's order of bodies, as the card's legs show it. Launch-phase Contract Requirements come first and the
    /// return home last; between them every order of up to 6 bodies is tried and the least delta-v kept, above 6 the nearest body next.
    /// A deadline the least-delta-v order would break moves its body earlier.
    /// </summary>
    public sealed class ContractRouteOrderTests
    {
        private static string[] Legs(ContractCheck check)
        {
            return check.Legs.Select(l => l.Text).ToArray();
        }

        [Fact]
        public void TwoMoonsAreVisitedInTheLeastDeltaVOrderAndTheReturnHomeComesLast()
        {
            var free = ContractCheck.Work(With(Exploration("mun", 9000, "Mun:ORBIT"), Exploration("minmus", 9000, "Minmus:ORBIT")), "", false, Margin);

            var legs = Legs(free);
            Assert.Equal(3, legs.Length);
            Assert.Equal(new[] { "orbit Minmus", "orbit the Mun" }.OrderBy(l => l), legs.Take(2).OrderBy(l => l));
            Assert.Equal("return to Kerbin", legs[2]);
        }

        [Fact]
        public void ADeadlineTheLeastDeltaVOrderWouldBreakMovesItsBodyEarlierAtSomeDeltaV()
        {
            var free = ContractCheck.Work(With(Exploration("mun", 9000, "Mun:ORBIT"), Exploration("minmus", 9000, "Minmus:ORBIT")), "", false, Margin);
            var second = Legs(free)[1] == "orbit the Mun" ? "Mun" : "Minmus";
            var first = second == "Mun" ? "Minmus" : "Mun";

            // A deadline on the second body's Contract that keeps the spare only when it is visited first.
            var snapshot = Pad();
            var routes = new ContractRoutes(snapshot, "", "Kerbin");
            var visitedFirst = ContractRoutes.LaunchSeconds + routes.Wait("Kerbin", second) + routes.Transfer("Kerbin", second);
            var visitedSecond = ContractRoutes.LaunchSeconds + routes.Wait("Kerbin", first) + routes.Transfer("Kerbin", first) + routes.Wait(first, second) + routes.Transfer(first, second);
            var deadline = Math.Max(visitedFirst / (1 - ContractCheck.DeadlineSpareFraction), visitedFirst + KerbinTime.SecondsPerDay) + 1;
            Assert.True(ContractCheck.BreaksSpare(snapshot.Ut + visitedSecond, snapshot.Ut + deadline, snapshot.Ut), "visited second, it still keeps the spare");
            var urgent = Exploration("urgent", 9000, second + ":ORBIT");
            urgent.DeadlineUt = snapshot.Ut + deadline;
            snapshot.Contracts.Accepted.AddRange(new[] { urgent, Exploration("other", 9000, first + ":ORBIT") });

            var forced = ContractCheck.Work(snapshot, "", false, Margin);

            Assert.Equal(2, forced.Fitting.Count(r => r.Ticked));
            Assert.Equal(Legs(free)[1], Legs(forced)[0]);
            Assert.Equal(Legs(free)[0], Legs(forced)[1]);
            Assert.Equal("return to Kerbin", Legs(forced)[2]);
            Assert.True(forced.RouteDeltaV > free.RouteDeltaV, "the forced order costs " + forced.RouteDeltaV + " against " + free.RouteDeltaV);
        }

        [Fact]
        public void AboveSixBodiesTheNearestBodyIsNext()
        {
            var bodies = new[] { "Jool", "Duna", "Eve", "Minmus", "Dres", "Mun", "Moho" };
            var check = ContractCheck.Work(With(bodies.Select(b => Exploration(b, 1000, b + ":FLYBY")).ToArray()), "", false, Margin);
            foreach (var body in bodies) check.Tick(body, true);

            // From the pad: the Mun, then Minmus round Kerbin; then round the Sun, the shortest trip on each time.
            Assert.Equal(new[] { "fly by the Mun", "fly by Minmus", "fly by Moho", "fly by Eve", "fly by Duna", "fly by Dres", "fly by Jool", "return to Kerbin" }, Legs(check));
        }
    }
}
