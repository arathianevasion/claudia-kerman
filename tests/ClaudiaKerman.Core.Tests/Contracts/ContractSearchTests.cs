// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
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
            // Every Contract was searched: the ones left out are left out for the craft, none for the search.
            Assert.All(check.NotThisFlight, l => Assert.Equal(LeftOutKind.CraftCant, l.Kind));
            Assert.Equal(contracts.Length, check.Fitting.Count + check.NotThisFlight.Count);
            Assert.True(check.SetFits);
            // The pad craft flies the Mun and Minmus landings and comes home: every Kerbin-system Contract fits together.
            Assert.Contains(check.Fitting, r => r.Guid == "mun-land" && r.Ticked);
            Assert.Contains(check.Fitting, r => r.Guid == "min-land" && r.Ticked);
            Assert.Contains(check.NotThisFlight, l => l.Guid == "eeloo-land" && l.Kind == LeftOutKind.CraftCant);
        }

        /// <summary>Twelve groups, each a different set of bodies and reaches, all in Kerbin's system; every one fits alone.</summary>
        private static SnapshotContract[] TwelveGroups(double munLandFunds, double minmusLandFunds)
        {
            return new[]
            {
                Exploration("mun-land", munLandFunds, "Mun:LANDING"), Exploration("min-land", minmusLandFunds, "Minmus:LANDING"),
                Exploration("mun-fly", 1, "Mun:FLYBY"), Exploration("mun-orbit", 1, "Mun:ORBIT"), Exploration("min-fly", 1, "Minmus:FLYBY"),
                Exploration("min-orbit", 1, "Minmus:ORBIT"), Exploration("kerbin", 1, "Kerbin:ORBIT"), Exploration("both-fly", 1, "Mun:FLYBY", "Minmus:FLYBY"),
                Exploration("both-orbit", 1, "Mun:ORBIT", "Minmus:ORBIT"), Exploration("mun-o-min-f", 1, "Mun:ORBIT", "Minmus:FLYBY"),
                Exploration("mun-f-min-o", 1, "Mun:FLYBY", "Minmus:ORBIT"), Exploration("kerbin-mun", 1, "Kerbin:ORBIT", "Mun:FLYBY"),
            };
        }

        [Fact]
        public void AboveTenGroupsTheGroupWithTheMostRewardPerExtraMetrePerSecondGoesInFirst()
        {
            // What each landing adds to the launch alone, on the Map.
            var launch = Check(Exploration("k", 1, "Kerbin:ORBIT")).RouteDeltaV;
            var mun = Check(Exploration("m", 1, "Mun:LANDING")).RouteDeltaV - launch;
            var minmus = Check(Exploration("n", 1, "Minmus:LANDING")).RouteDeltaV - launch;
            Assert.True(mun > minmus * 1.1, "the Mun landing adds " + mun + " m/s, Minmus's " + minmus);
            // The Mun landing pays more, the Minmus landing more per m/s; a craft with room for one landing only.
            var munFunds = 30000.0;
            var minmusFunds = munFunds * minmus / mun * 1.05;
            var snapshot = With(TwelveGroups(munFunds, minmusFunds));
            var both = Check(Exploration("m", 1, "Mun:LANDING"), Exploration("n", 1, "Minmus:LANDING")).RouteDeltaV;
            var scale = both * (1 + Margin / 100.0) * 0.97 / snapshot.Stages.Sum(s => s.DeltaVVac);
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= scale;

            var clock = Stopwatch.StartNew();
            var check = ContractCheck.Work(snapshot, "", false, Margin);
            clock.Stop();

            Assert.True(clock.Elapsed < ContractCheck.SearchBudget, "took " + clock.Elapsed);
            Assert.True(check.SetFits);
            Assert.Contains(check.Fitting, r => r.Guid == "min-land" && r.Ticked);
            Assert.DoesNotContain(check.Fitting, r => r.Guid == "mun-land" && r.Ticked);
            Assert.Equal(12, check.Fitting.Count + check.NotThisFlight.Count);
        }

        [Fact]
        public void AboveTenGroupsWithRoomForAllEveryGroupIsSuggested()
        {
            var check = Check(TwelveGroups(30000, 20000));

            Assert.Equal(12, check.Fitting.Count(r => r.Ticked));
            Assert.True(check.SetFits);
        }

        // ---- The winning set re-priced with the planners ----

        /// <summary>
        /// The pad craft with the player's Soonest pick for Duna: the trip there is plan_transfer's when re-priced, about 160 m/s over the
        /// Delta-v Map's (probed 2026-11-14: 5,279 against 5,120 m/s for a Duna flyby and home). The craft is set to fit the flyby on the
        /// Map's figures, with the reserve and 40 m/s over, so the planners' figures leave it short.
        /// </summary>
        internal static GameSnapshot DunaOnTheEdge(uint craftId, params SnapshotContract[] contracts)
        {
            var snapshot = With(contracts);
            // Its own craft, so no other test's kept planner answer prices its search (RoutePlanners keeps them by craft).
            snapshot.CraftPersistentId = craftId;
            snapshot.WindowPick = new WindowPick("Duna", WindowKind.Soonest);
            var map = ContractCheck.Work(With(Exploration("probe", 1, "Duna:FLYBY")), "", false, Margin).RouteDeltaV;
            var scale = (map * (1 + Margin / 100.0) + 40) / snapshot.Stages.Sum(s => s.DeltaVVac);
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= scale;
            return snapshot;
        }

        [Fact]
        public void AWinningSetThePlannersPriceShortFallsToTheNextBestSet()
        {
            var snapshot = DunaOnTheEdge(10281, Exploration("duna", 30000, "Duna:FLYBY"), Exploration("mun", 5000, "Mun:FLYBY"));

            var check = ContractCheck.Work(snapshot, "", false, Margin);

            Assert.Equal("mun", Assert.Single(check.Fitting).Guid);
            Assert.True(check.Fitting[0].Ticked);
            var duna = check.NotThisFlight.Single(l => l.Guid == "duna");
            Assert.Equal(LeftOutKind.CraftCant, duna.Kind);
            Assert.Matches(@"^this craft can't: [0-9,]+ m/s short priced with the planners$", duna.Reason);
            Assert.Matches(@"^Priced with the planners, the best set by the Delta-v Map came out [0-9,]+ m/s short; the next best is suggested\.$", check.SearchNote);
        }

        [Fact]
        public void AfterThreeFallbacksThePlannersShortfallIsReportedAndNothingIsSuggested()
        {
            // Five Duna flybys: the best sets are all Duna's, and all short on the planners' figures. The Mun flyby would fit, but it ranks
            // below the fourth set tried.
            var snapshot = DunaOnTheEdge(10282,
                Exploration("d1", 9000, "Duna:FLYBY"), Exploration("d2", 9000, "Duna:FLYBY"), Exploration("d3", 9000, "Duna:FLYBY"),
                Exploration("d4", 9000, "Duna:FLYBY"), Exploration("d5", 9000, "Duna:FLYBY"), Exploration("mun", 1, "Mun:FLYBY"));

            var check = ContractCheck.Work(snapshot, "", false, Margin);

            Assert.Empty(check.Fitting);
            Assert.DoesNotContain(check.NotThisFlight, l => l.Ticked);
            Assert.Matches(@"^Priced with the planners, the 4 best sets by the Delta-v Map came out short \(([0-9,]+, ){3}[0-9,]+ m/s\); none is suggested\. Tick Contracts to price them\.$", check.SearchNote);
        }
    }
}
