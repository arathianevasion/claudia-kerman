// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1029: part tests and hauls on the Contract check, on #1037's fixtures and the Mun Shot pad craft. A haul is the visit it names, a
    /// test's band is judged on the craft's own ascent, hop and way down, a chute's test only by ADR 0019's group and reentry's timing, and
    /// a test needing level flight in the air is "not yet".
    /// </summary>
    public sealed class PartTestContractTests
    {
        private static ContractCheck Check(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
        }

        /// <summary>The PartTest fixture with its part, situation and conditions set: "minAlt" and the like, a missing one dropped.</summary>
        private static SnapshotContract Test(string id, string part, string situation, Dictionary<string, string> values)
        {
            var contract = ContractCheckTests.Fixture("PartTest.json");
            contract.Guid = id;
            var test = contract.Requirements[0];
            test.Key = id + "/0";
            test.Settings = test.Settings.Select(s => s.Name == "part" ? new ContractSetting("part", part) : s.Name == "situation" ? new ContractSetting("situation", situation) : s).ToList();
            test.Requirements = test.Requirements
                .Where(c => c.Kind != "ReachAltitudeEnvelope" || values.ContainsKey("minAlt"))
                .Where(c => c.Kind != "ReachSpeedEnvelope" || values.ContainsKey("minSpd"))
                .ToList();
            foreach (var condition in test.Requirements)
                condition.Settings = condition.Settings.Select(s => values.ContainsKey(s.Name) ? new ContractSetting(s.Name, values[s.Name]) : s).ToList();
            return contract;
        }

        private static Dictionary<string, string> Band(double minAlt, double maxAlt, double? minSpd = null, double? maxSpd = null)
        {
            var band = new Dictionary<string, string> { { "minAlt", minAlt.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "maxAlt", maxAlt.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            if (minSpd.HasValue)
            {
                band["minSpd"] = minSpd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                band["maxSpd"] = maxSpd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return band;
        }

        /// <summary>The pad craft's Mk16 with a real chute's figures (#462's ReentrySim), so the predictor opens it.</summary>
        private static GameSnapshot WithMk16(GameSnapshot snapshot)
        {
            foreach (var part in snapshot.Craft.Parts.Where(p => p.Name == "parachuteSingle")) part.Chute = ReentrySim.Mk16();
            return snapshot;
        }

        [Fact]
        public void AHaulIntoTheMunsOrbitIsPlannedAsThatVisit()
        {
            var check = Check(ContractCheckTests.With(ContractCheckTests.Fixture("PartTest-haul.json")));

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Mun", row.Phase);
            Assert.Contains(check.Legs, l => l.Text == "orbit the Mun");
            var chosen = Assert.Single(check.Choose(new[] { row.Guid }).Requirements);
            Assert.True(chosen.Test.Haul);
            Assert.Equal(TestWindow.None, chosen.Test.Window);
        }

        [Fact]
        public void APartNotOnTheCraftIsThisCraftCant()
        {
            var haul = ContractCheckTests.Fixture("PartTest-haul.json");
            haul.Requirements[0].Settings = haul.Requirements[0].Settings.Select(s => s.Name == "part" ? new ContractSetting("part", "largeSolarPanel") : s).ToList();

            var check = Check(ContractCheckTests.With(haul));

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: the part largeSolarPanel isn't on the craft", left.Reason);
        }

        [Fact]
        public void ATestAlongTheGroundAtSpeedIsAnAircraftsAndNotYet()
        {
            var check = Check(ContractCheckTests.With(Test("c-roll", "landingLeg1", "LANDED", new Dictionary<string, string> { { "minSpd", "50" }, { "maxSpd", "100" } })));

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: needs sustained flight in the atmosphere (aircraft)", left.Reason);
        }

        [Fact]
        public void AFlyingTestNoAscentOrDescentPassesNeedsAnAircraft()
        {
            // Under 500 m at 900 m/s: a rocket is far slower that low, climbing or falling.
            var check = Check(ContractCheckTests.With(Test("c-low", "liquidEngine.v2", "FLYING", Band(0, 500, 900, 1000))));

            Assert.Equal(ContractKinds.AircraftNotYet, Assert.Single(check.NotThisFlight).Reason);
        }

        [Fact]
        public void ATestInOrbitIsNotYet()
        {
            var check = Check(ContractCheckTests.With(Test("c-orbit", "liquidEngine.v2", "ORBITING", new Dictionary<string, string>())));

            Assert.Equal(ContractKinds.TestAwayNotYet, Assert.Single(check.NotThisFlight).Reason);
        }

        [Fact]
        public void ABandTheClimbPassesIsTestedInLaunchAndTheHopIsFlownStraightUpAsPriced()
        {
            var check = Check(ContractCheckTests.With(Test("c-low", "liquidEngine.v2", "FLYING", Band(1000, 3000, 50, 400))));

            var row = Assert.Single(check.Fitting);
            var choice = check.Choose(new[] { row.Guid });
            Assert.Equal(TestWindow.Ascent, Assert.Single(choice.Requirements).Test.Window);
            Assert.Contains("Fly the hop straight up, as it is priced: launch with target_ap ", choice.Section);
            Assert.Contains("and turn_start_speed 100000.", choice.Section);
            Assert.Contains("test liquidEngine.v2 through 1,000–3,000 m at 50–400 m/s over Kerbin in launch", choice.Section);
        }

        /// <summary>
        /// ADR 0019: the pod's Mk16 is in the Kerbin return's group (the Last Entry opens every chute aboard); this heavy craft is still over
        /// 250 m/s at 1,500 m, where reentry's height rule opens it, so a band round that opening plans the test for reentry.
        /// </summary>
        [Fact]
        public void AChuteTestIsPlannedForReentryWhenItsGroupOpensItInTheBand()
        {
            var check = Check(WithMk16(ContractCheckTests.With(Test("c-chute", "parachuteSingle", "FLYING", Band(0, 20000, 0, 400)))));

            var row = Assert.Single(check.Fitting);
            var chosen = Assert.Single(check.Choose(new[] { row.Guid }).Requirements);
            Assert.True(chosen.Test.Chute);
            Assert.Equal(TestWindow.Descent, chosen.Test.Window);
        }

        [Fact]
        public void AChuteTestWhoseNormalOpeningMissesTheBandIsThisCraftCant()
        {
            var check = Check(WithMk16(ContractCheckTests.With(Test("c-chute", "parachuteSingle", "FLYING", Band(30000, 40000)))));

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.StartsWith("this craft can't: reentry opens the test chute at ", left.Reason);
            Assert.EndsWith(", outside 30–40 km", left.Reason);
        }

        [Fact]
        public void AChuteNotInTheEntrysGroupIsThisCraftCant()
        {
            // An open chute is in no group: ADR 0019 counts only stowed ones.
            var snapshot = WithMk16(ContractCheckTests.With(Test("c-chute", "parachuteSingle", "FLYING", Band(0, 20000))));
            snapshot.Craft.Parts.First(p => p.Name == "parachuteSingle").Chute.State = ReentryChuteState.Deployed;

            var check = Check(snapshot);

            Assert.Equal("this craft can't: " + PartTestJudge.ChuteNotInGroup, Assert.Single(check.NotThisFlight).Reason);
        }

        [Fact]
        public void ATestHomeAfterSplashdownIsRunInReentry()
        {
            var check = Check(ContractCheckTests.With(Test("c-splash", "liquidEngine.v2", "SPLASHED", new Dictionary<string, string>())));

            var row = Assert.Single(check.Fitting);
            var chosen = Assert.Single(check.Choose(new[] { row.Guid }).Requirements);
            Assert.True(chosen.Return);
            Assert.Equal(TestWindow.Descent, chosen.Test.Window);
        }
    }
}
