// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1022: the Contract check for exploration Contracts. Core reads the accepted Contracts of a Game Snapshot (#1037's fixtures on the
    /// Mun Shot pad craft, 8,036 m/s), recognises the Contract Requirement kinds it can plan, prices whole Contract Routes from where the
    /// craft is and suggests the most Contracts that fit with the Craft Check's reserve.
    /// </summary>
    public sealed class ContractCheckTests
    {
        internal const int Margin = 15;

        internal static GameSnapshot Pad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-pad.json")));
            snapshot.GameMode = "CAREER";
            snapshot.Contracts = new SnapshotContracts { Limit = 7, Active = 0 };
            return snapshot;
        }

        /// <summary>The first accepted Contract of a #1037 fixture.</summary>
        internal static SnapshotContract Fixture(string file)
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", file))).Contracts.Accepted[0];
        }

        /// <summary>
        /// The ExplorationContract fixture with its own GUID and its Requirements set to <paramref name="targets"/> ("Mun:ORBIT"), each
        /// a copy of the fixture's first ProgressTrackingParameter; "?" before one makes it optional.
        /// </summary>
        internal static SnapshotContract Exploration(string id, double funds, params string[] targets)
        {
            var contract = Fixture("ExplorationContract.json");
            var template = contract.Requirements[0];
            contract.Guid = id;
            contract.Funds = funds;
            contract.Science = 0;
            contract.Reputation = 0;
            contract.Requirements = targets.Select((t, i) =>
            {
                var optional = t.StartsWith("?", StringComparison.Ordinal);
                var parts = t.TrimStart('?').Split(':');
                return new ContractRequirement
                {
                    Key = ContractRequirement.KeyOf(id, new[] { i }),
                    Kind = template.Kind,
                    State = "Incomplete",
                    Optional = optional,
                    Settings = template.Settings.Select(s => new ContractSetting(s.Name,
                        s.Name == "targetBody" ? parts[0] : s.Name == "targetType" ? parts[1] : s.Value)).ToList(),
                };
            }).ToList();
            return contract;
        }

        internal static GameSnapshot With(params SnapshotContract[] contracts)
        {
            var snapshot = Pad();
            snapshot.Contracts.Accepted.AddRange(contracts);
            snapshot.Contracts.Active = contracts.Length;
            return snapshot;
        }

        private static ContractCheck Check(GameSnapshot snapshot, string words = "")
        {
            return ContractCheck.Work(snapshot, words, false, Margin);
        }

        private static double CraftDeltaV(GameSnapshot snapshot)
        {
            return snapshot.Stages.Sum(s => s.DeltaVVac);
        }

        /// <summary>The craft's delta-v scaled to <paramref name="total"/> m/s, stage by stage.</summary>
        private static void SetDeltaV(GameSnapshot snapshot, double total)
        {
            var scale = total / CraftDeltaV(snapshot);
            foreach (var stage in snapshot.Stages) stage.DeltaVVac *= scale;
        }

        private static DeltaVMap Map(GameSnapshot snapshot)
        {
            return DeltaVMap.Compute(snapshot.Bodies, "Kerbin");
        }

        [Fact]
        public void TheFixturesExplorationContractFitsPreTickedWithItsPhaseAddedDeltaVAndTheRouteAgainstTheCraft()
        {
            var snapshot = With(Fixture("ExplorationContract.json"));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal("0d5e8a10-1037-4a01-9f00-000000000003", row.Guid);
            Assert.Contains("Mun", row.Title);
            Assert.Equal("Mun", row.Phase);
            Assert.Null(row.DeadlineUt);
            // Launch, the trip to the Mun with its capture, and the return: the Map's legs, priced once.
            var map = Map(snapshot);
            var route = map.Leg("launch:Kerbin").DeltaV + map.Leg("transfer:Mun").DeltaV + map.Leg("return:Mun").DeltaV;
            Assert.InRange(check.RouteDeltaV, route - 1, route + 1);
            Assert.InRange(row.AddedDeltaV, map.Leg("transfer:Mun").DeltaV + map.Leg("return:Mun").DeltaV - 1, route);
            Assert.Equal(CraftDeltaV(snapshot), check.CraftDeltaV, 3);
            Assert.Equal(Margin, check.ReservePercent);
            Assert.Equal(check.RouteDeltaV * 0.15, check.Reserve, 3);
            Assert.Equal(check.CraftDeltaV - check.RouteDeltaV - check.Reserve, check.Spare, 3);
            Assert.Empty(check.NotThisFlight);
        }

        [Fact]
        public void TwoContractsSharingATripShareItsCost()
        {
            var flyby = Exploration("c-flyby", 10000, "Mun:FLYBY");
            var orbit = Exploration("c-orbit", 10000, "Mun:ORBIT");

            var alone = Check(With(Exploration("c-orbit", 10000, "Mun:ORBIT")));
            var both = Check(With(flyby, orbit));

            Assert.Equal(2, both.Fitting.Count);
            // The orbit's trip passes the Mun: the flyby adds nothing to it.
            Assert.Equal(alone.RouteDeltaV, both.RouteDeltaV, 3);
            Assert.Equal(0, both.Fitting.Single(r => r.Guid == "c-flyby").AddedDeltaV, 3);
        }

        [Fact]
        public void EveryContractTypeNotPlannedYetIsNotYetWithItsReason()
        {
            var files = new[] { "SurveyContract.json", "SatelliteContract.json", "PlantFlag.json", "RecoverAsset.json" };
            var snapshot = With(files.Select(Fixture).ToArray());

            var check = Check(snapshot);

            Assert.Empty(check.Fitting);
            Assert.Equal(files.Length, check.NotThisFlight.Count);
            Assert.All(check.NotThisFlight, left => Assert.StartsWith("not yet: ", left.Reason));
            Assert.All(check.NotThisFlight, left => Assert.Equal(LeftOutKind.NotYet, left.Kind));
        }

        /// <summary>#1048: reaching space over Kerbin is met on a hop out of the air, not on a trip to orbit.</summary>
        [Fact]
        public void AKerbinWorldFirstMetBelowOrbitIsPricedAsAHop()
        {
            var snapshot = With(Exploration("c-space", 5000, "Kerbin:REACHSPACE"));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.Equal("c-space", row.Guid);
            Assert.Equal("Kerbin hop", row.Phase);
            Assert.True(check.RouteDeltaV > 1000, "a hop out of the air is not free: " + check.RouteDeltaV);
            Assert.True(check.RouteDeltaV < Map(snapshot).Leg("launch:Kerbin").DeltaV - 500, "priced " + check.RouteDeltaV + " against the launch to orbit");
            Assert.Equal("reach space over Kerbin", Assert.Single(check.Legs).Text);
        }

        /// <summary>
        /// The player's PartTest of 2026-11-14 (#1048): FLYING over Kerbin, 51,000–56,000 m at 100–260 m/s; the part is the pad craft's
        /// own Terrier (liquidEngine.v2) for #1029, as the part to test must be aboard.
        /// </summary>
        internal static SnapshotContract BoosterTest(string id)
        {
            var contract = Fixture("PartTest.json");
            contract.Guid = id;
            var test = contract.Requirements[0];
            test.Settings = test.Settings.Select(s => s.Name == "part" ? new ContractSetting("part", "liquidEngine.v2") : s).ToList();
            var values = new Dictionary<string, string> { { "minAlt", "51000" }, { "maxAlt", "56000" }, { "minSpd", "100" }, { "maxSpd", "260" } };
            foreach (var condition in test.Requirements)
                condition.Settings = condition.Settings.Select(s => values.ContainsKey(s.Name) ? new ContractSetting(s.Name, values[s.Name]) : s).ToList();
            return contract;
        }

        /// <summary>The pad craft with its three upper stages emptied: about 3,300 m/s, short of the launch to orbit with the reserve.</summary>
        internal static void BoosterOnly(GameSnapshot snapshot)
        {
            foreach (var stage in snapshot.Stages.Where(s => s.Number >= 2 && s.Number <= 4))
            {
                stage.DeltaVVac = 0;
                stage.ThrustVacKn = 0;
                stage.StartMassT = stage.EndMassT;
            }
        }

        [Fact]
        public void ASuborbitalPartTestIsPricedAsAHopBelowTheLaunchToOrbit()
        {
            var snapshot = With(BoosterTest("c-test"));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Kerbin hop", row.Phase);
            Assert.True(check.RouteDeltaV > 1000, "a hop to 51 km is not free: " + check.RouteDeltaV);
            Assert.True(check.RouteDeltaV < Map(snapshot).Leg("launch:Kerbin").DeltaV - 500, "priced " + check.RouteDeltaV + " against the launch to orbit");
            Assert.Equal("hop through 51,000–56,000 m at 100–260 m/s over Kerbin", Assert.Single(check.Legs).Text);
            Assert.Contains("test liquidEngine.v2 through 51,000–56,000 m at 100–260 m/s over Kerbin", check.Choose(new[] { "c-test" }).Section);
        }

        [Fact]
        public void ACraftThatReachesTheBandButNotOrbitHasTheSuborbitalPartTestInThisFlight()
        {
            var snapshot = With(BoosterTest("c-test"), Exploration("c-orbit", 9000, "Kerbin:ORBIT"));
            BoosterOnly(snapshot);
            Assert.True(CraftDeltaV(snapshot) < Map(snapshot).Leg("launch:Kerbin").DeltaV * (1 + Margin / 100.0));

            var check = Check(snapshot);

            Assert.Equal("c-test", Assert.Single(check.Fitting).Guid);
            var orbit = check.NotThisFlight.Single(l => l.Guid == "c-orbit");
            Assert.Equal(LeftOutKind.CraftCant, orbit.Kind);
        }

        /// <summary>
        /// #1029 (triage of #1048): the launch to orbit passes 51–56 km far faster than 260 m/s, so the orbit Contract's route doesn't meet
        /// the test: the two don't fly together, and the one left out says how the ascent misses the band.
        /// </summary>
        [Fact]
        public void AnAscentToOrbitTooFastThroughTheBandDoesNotMeetThePartTestAndSaysWhy()
        {
            var check = Check(With(BoosterTest("c-test"), Exploration("c-orbit", 9000, "Kerbin:ORBIT")));

            Assert.Single(check.Fitting);
            check.Tick("c-test", true);
            check.Tick("c-orbit", true);

            Assert.False(check.SetFits);
            Assert.Matches(@"^this craft can't: on the ascent: speed [\d,]+ m/s, above the band, through 51–56 km", check.Problem);
        }

        /// <summary>#1091: <paramref name="contract"/>'s first Requirement with the record's target setting (<c>altitude</c> m, <c>speed</c> m/s).</summary>
        private static SnapshotContract Aiming(SnapshotContract contract, string name, string value)
        {
            contract.Requirements[0].Settings.Add(new ContractSetting(name, value));
            return contract;
        }

        [Fact]
        public void AnAltitudeRecordBelowOrbitIsAHopAndInThisFlightForACraftThatReachesIt()
        {
            var snapshot = With(Aiming(Exploration("c-alt", 5000, "Kerbin:ALTITUDERECORD"), "altitude", "30000"));
            BoosterOnly(snapshot);

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Kerbin hop", row.Phase);
            Assert.True(check.RouteDeltaV > 500, "a hop to 30 km is not free: " + check.RouteDeltaV);
            Assert.True(check.RouteDeltaV < Map(snapshot).Leg("launch:Kerbin").DeltaV - 500, "priced " + check.RouteDeltaV + " against the launch to orbit");
            Assert.Empty(check.NotThisFlight);
        }

        [Fact]
        public void AnAltitudeRecordAboveTheAtmosphereIsMetOnTheWayToOrbitAtNoExtraCost()
        {
            var orbitOnly = Check(With(Exploration("c-orbit", 9000, "Kerbin:ORBIT")));
            var snapshot = With(Aiming(Exploration("c-alt", 5000, "Kerbin:ALTITUDERECORD"), "altitude", "100000"), Exploration("c-orbit", 9000, "Kerbin:ORBIT"));

            var check = Check(snapshot);

            Assert.Equal(2, check.Fitting.Count);
            Assert.Equal(orbitOnly.RouteDeltaV, check.RouteDeltaV, 3);
        }

        [Fact]
        public void TheFirstLaunchAddsNothingToAnyRoutesPrice()
        {
            var orbitOnly = Check(With(Exploration("c-orbit", 9000, "Kerbin:ORBIT")));
            var withFirst = Check(With(Exploration("c-first", 1000, "Kerbin:FIRSTLAUNCH"), Exploration("c-orbit", 9000, "Kerbin:ORBIT")));
            var munOnly = Check(With(Exploration("c-mun", 9000, "Mun:ORBIT")));
            var munWithFirst = Check(With(Exploration("c-first", 1000, "Kerbin:FIRSTLAUNCH"), Exploration("c-mun", 9000, "Mun:ORBIT")));

            Assert.Equal(2, withFirst.Fitting.Count);
            Assert.Equal(orbitOnly.RouteDeltaV, withFirst.RouteDeltaV, 3);
            Assert.Equal(munOnly.RouteDeltaV, munWithFirst.RouteDeltaV, 3);
        }

        [Fact]
        public void ASpeedRecordIsAHopThroughItsSpeed()
        {
            var check = Check(With(Aiming(Exploration("c-speed", 5000, "Kerbin:SPEEDRECORD"), "speed", "300")));

            Assert.Equal("Kerbin hop", Assert.Single(check.Fitting).Phase);
        }

        [Fact]
        public void ADepthRecordIsNotYetWithItsReasonAndAnOptionalOneIsSkipped()
        {
            var required = Exploration("c-deep", 5000, "Kerbin:DEPTHRECORD");
            var optional = Exploration("c-mun", 5000, "Mun:ORBIT", "?Kerbin:DEPTHRECORD");

            var check = Check(With(required, optional));

            Assert.Equal(ContractKinds.DepthNotYet, check.NotThisFlight.Single(l => l.Guid == "c-deep").Reason);
            var mun = check.Fitting.Single(r => r.Guid == "c-mun");
            Assert.Contains(ContractKinds.DepthNotYet.Substring("not yet: ".Length), mun.Note);
        }

        [Fact]
        public void ARecordOverAnotherBodyIsNotYet()
        {
            var check = Check(With(Exploration("c-away", 5000, "Mun:ALTITUDERECORD")));

            Assert.Equal(ContractKinds.RecordAwayNotYet, Assert.Single(check.NotThisFlight).Reason);
        }

        [Fact]
        public void AContractWhoseDeadlineBreaksTheSpareRuleCompletesLater()
        {
            var snapshot = Pad();
            // The trip to the Mun takes over a Kerbin day; a deadline a day off leaves no day to spare.
            var tight = Exploration("c-tight", 50000, "Mun:ORBIT");
            tight.DeadlineUt = snapshot.Ut + 21600;
            var easy = Exploration("c-easy", 1000, "Minmus:ORBIT");
            easy.DeadlineUt = snapshot.Ut + 100 * 21600;
            snapshot.Contracts.Accepted.AddRange(new[] { tight, easy });

            var check = Check(snapshot);

            Assert.Equal("c-easy", Assert.Single(check.Fitting).Guid);
            Assert.Equal(easy.DeadlineUt, check.Fitting[0].DeadlineUt);
            Assert.StartsWith("completes later", check.NotThisFlight.Single(l => l.Guid == "c-tight").Reason);
            Assert.Equal(LeftOutKind.CompletesLater, check.NotThisFlight.Single(l => l.Guid == "c-tight").Kind);
        }

        [Fact]
        public void AContractTheCraftCantReachSaysHowShortItIs()
        {
            var check = Check(With(Exploration("c-eeloo", 90000, "Eeloo:LANDING")));

            Assert.Empty(check.Fitting);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Matches(@"^this craft can't: [0-9,]+ m/s short$", left.Reason);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
        }

        /// <summary>A craft that flies either the Mun or Minmus orbit, but not both: the tie-break picks one.</summary>
        internal static GameSnapshot EitherMoon(SnapshotContract mun, SnapshotContract minmus)
        {
            var snapshot = With(mun, minmus);
            var munAlone = Check(With(Exploration("m", 0, "Mun:ORBIT"))).RouteDeltaV;
            var minmusAlone = Check(With(Exploration("n", 0, "Minmus:ORBIT"))).RouteDeltaV;
            SetDeltaV(snapshot, Math.Max(munAlone, minmusAlone) * 1.15 + 1);
            return snapshot;
        }

        [Theory]
        [InlineData(10000, 0, 0, 20000, 0, 0, "minmus")]
        [InlineData(20000, 0, 0, 10000, 0, 0, "mun")]
        [InlineData(10000, 5, 0, 10000, 9, 0, "minmus")]
        [InlineData(10000, 5, 3, 10000, 5, 1, "mun")]
        public void TiesBreakByFundsThenScienceThenReputation(double munFunds, double munScience, double munRep, double minFunds, double minScience, double minRep, string winner)
        {
            var mun = Exploration("mun", munFunds, "Mun:ORBIT");
            mun.Science = munScience;
            mun.Reputation = munRep;
            var minmus = Exploration("minmus", minFunds, "Minmus:ORBIT");
            minmus.Science = minScience;
            minmus.Reputation = minRep;

            var check = Check(EitherMoon(mun, minmus));

            Assert.Equal(winner, Assert.Single(check.Fitting).Guid);
            Assert.StartsWith("this craft can't: ", check.NotThisFlight.Single().Reason);
        }

        [Fact]
        public void EqualRewardsGoToTheLeastDeltaV()
        {
            var map = Map(Pad());
            var cheaper = map.Leg("transfer:Mun").DeltaV + map.Leg("return:Mun").DeltaV < map.Leg("transfer:Minmus").DeltaV + map.Leg("return:Minmus").DeltaV ? "mun" : "minmus";

            var check = Check(EitherMoon(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("minmus", 10000, "Minmus:ORBIT")));

            Assert.Equal(cheaper, Assert.Single(check.Fitting).Guid);
        }

        [Fact]
        public void TypedRequirementsAreTheFixedPartAndNeverDroppedToFitMore()
        {
            var snapshot = With(Exploration("mun", 10000, "Mun:ORBIT"));
            var words = Check(snapshot, "fly to Minmus and capture into orbit");
            var bare = Check(snapshot);

            Assert.True(words.WordsFit);
            Assert.True(words.RouteDeltaV > bare.RouteDeltaV);
            // Too small for both: the words stay and the Contract goes.
            SetDeltaV(snapshot, words.RouteDeltaV * 1.15 - 1);
            var tight = Check(snapshot, "fly to Minmus and capture into orbit");
            Assert.True(tight.WordsFit);
            Assert.Empty(tight.Fitting);
            Assert.StartsWith("this craft can't: ", tight.NotThisFlight.Single().Reason);
        }

        [Fact]
        public void WhenTheTypedRequirementsAloneDontFitThereIsNoSuggestion()
        {
            var snapshot = With(Exploration("mun", 10000, "Mun:ORBIT"));
            SetDeltaV(snapshot, 2000);

            var check = Check(snapshot, "land on Eeloo and return to Kerbin");

            Assert.False(check.WordsFit);
            Assert.Empty(check.Fitting);
        }
        // ---- #1023: ticking, unticking and Swap in re-price the card exactly, with no new search ----

        [Fact]
        public void UntickingReSurfacesTheRouteExactlyAsThatSetPricedAloneAndSearchesNothing()
        {
            var snapshot = With(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("minmus", 20000, "Minmus:ORBIT"));
            var check = Check(snapshot);
            Assert.Equal(2, check.Fitting.Count(r => r.Ticked));

            check.Tick("mun", false);

            var minmusAlone = Check(With(Exploration("minmus", 20000, "Minmus:ORBIT")));
            Assert.False(check.Fitting.Single(r => r.Guid == "mun").Ticked);
            Assert.Equal(minmusAlone.RouteDeltaV, check.RouteDeltaV, 6);
            Assert.Equal(minmusAlone.Reserve, check.Reserve, 6);
            Assert.Equal(minmusAlone.Spare, check.Spare, 6);
            Assert.Equal(minmusAlone.Fitting.Single().AddedDeltaV, check.Fitting.Single(r => r.Guid == "minmus").AddedDeltaV, 6);
            Assert.StartsWith("1 of 2 Contracts · route ", check.Headline(1));
            Assert.True(check.SetFits);
            Assert.Equal(1, check.Searches);
        }

        [Fact]
        public void TickingAnUntickedContractAddsItsDeltaVBack()
        {
            var snapshot = With(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("minmus", 20000, "Minmus:ORBIT"));
            var check = Check(snapshot);
            var both = check.RouteDeltaV;
            check.Tick("mun", false);
            check.Tick("mun", true);

            Assert.Equal(both, check.RouteDeltaV, 6);
            Assert.Equal(1, check.Searches);
        }

        [Fact]
        public void TickingALeftOutContractThatDoesntFitShowsTheShortfall()
        {
            var check = Check(EitherMoon(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("minmus", 20000, "Minmus:ORBIT")));
            Assert.Equal("minmus", Assert.Single(check.Fitting).Guid);
            Assert.True(check.NotThisFlight.Single().Tickable);

            check.Tick("mun", true);

            Assert.False(check.SetFits);
            Assert.True(check.Spare < 0);
            Assert.Matches(@"^[0-9,]+ m/s short", check.Problem);
            Assert.Contains("m/s short", check.Headline(2));
            Assert.True(check.NotThisFlight.Single().Ticked);
            Assert.Equal(1, check.Searches);
        }

        [Fact]
        public void SwapInIsOfferedForALeftOutContractThatFitsInPlaceOfOthersAndAppliesInOneClick()
        {
            var check = Check(EitherMoon(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("minmus", 20000, "Minmus:ORBIT")));

            var swap = check.NotThisFlight.Single().Swap;
            Assert.NotNull(swap);
            Assert.Equal(new[] { "minmus" }, swap.Drop);
            Assert.Contains("Mun", swap.Label);

            check.SwapIn("mun");

            Assert.True(check.SetFits);
            Assert.False(check.Fitting.Single().Ticked);
            Assert.True(check.NotThisFlight.Single().Ticked);
            Assert.Null(check.NotThisFlight.Single().Swap);
            Assert.NotNull(check.Fitting.Single().Swap);
            Assert.Equal(1, check.Searches);
        }

        [Fact]
        public void NoSwapInWhereTheContractFitsAsAPlainTickOrNotEvenAlone()
        {
            var snapshot = EitherMoon(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("minmus", 20000, "Minmus:ORBIT"));
            var check = Check(snapshot);
            check.Tick("minmus", false);
            // Room again: ticking Mun just fits, so there is nothing to swap.
            Assert.Null(check.NotThisFlight.Single().Swap);

            var eeloo = Check(With(Exploration("mun", 10000, "Mun:ORBIT"), Exploration("eeloo", 90000, "Eeloo:LANDING")));
            Assert.Null(eeloo.NotThisFlight.Single().Swap);
        }
    }
}
