// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using Xunit;
using static ClaudiaKerman.Core.Tests.Contracts.ContractCheckTests;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1028: the Contract search at scale. Above 10 plannable Contracts the search groups them by the bodies they need; above 10 groups it
    /// adds groups greedily by reward per extra m/s. Either way a suggestion comes within <see cref="ContractCheck.SearchBudget"/>.
    /// </summary>
    public sealed class ContractSearchTests
    {
        private static ContractCheck Check(params SnapshotContract[] contracts)
        {
            return ContractCheck.Work(With(contracts), "", false, Margin);
        }

        [Fact]
        public void FourteenPlannableContractsInFewerThanTenGroupsAreAllSearchedWithinTheBudget()
        {
            var contracts = new[]
            {
                Exploration("mun-fly", 4000, "Mun:FLYBY"), Exploration("mun-orbit", 9000, "Mun:ORBIT"), Exploration("mun-orbit-2", 8000, "Mun:ORBIT"),
                Exploration("mun-land", 20000, "Mun:LANDING"), Exploration("mun-land-2", 15000, "Mun:LANDING"),
                Exploration("min-fly", 3000, "Minmus:FLYBY"), Exploration("min-orbit", 7000, "Minmus:ORBIT"), Exploration("min-orbit-2", 6000, "Minmus:ORBIT"),
                Exploration("min-land", 12000, "Minmus:LANDING"), Exploration("kerbin-orbit", 2000, "Kerbin:ORBIT"), Exploration("kerbin-orbit-2", 2500, "Kerbin:ORBIT"),
                Exploration("duna-fly", 30000, "Duna:FLYBY"), Exploration("eeloo-land", 90000, "Eeloo:LANDING"), Exploration("mun-min", 18000, "Mun:ORBIT", "Minmus:ORBIT"),
            };

            var clock = Stopwatch.StartNew();
            var check = Check(contracts);
            clock.Stop();

            Assert.True(clock.Elapsed < ContractCheck.SearchBudget, "took " + clock.Elapsed);
            Assert.DoesNotContain(check.NotThisFlight, l => l.Kind == LeftOutKind.NotSearched);
            Assert.Equal(contracts.Length, check.Fitting.Count + check.NotThisFlight.Count);
            Assert.True(check.SetFits);
            // The pad craft flies the Mun and Minmus landings and comes home: every Kerbin-system Contract fits together.
            Assert.Contains(check.Fitting, r => r.Guid == "mun-land" && r.Ticked);
            Assert.Contains(check.Fitting, r => r.Guid == "min-land" && r.Ticked);
            Assert.Contains(check.NotThisFlight, l => l.Guid == "eeloo-land" && l.Kind == LeftOutKind.CraftCant);
        }
    }
}
