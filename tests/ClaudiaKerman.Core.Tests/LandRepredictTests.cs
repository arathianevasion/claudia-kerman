// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:LANDREPREDICT</c> (#511, #1364, ADR 0014; was land's <c>land_repredict_verdict</c>): what <c>land</c> does once it predicts
    /// its braking again from the real state as braking begins. From an orbit, with no Descent Shape clearing the terrain, it keeps an orbit whose
    /// periapsis is over the terrain on the track; from a suborbital arc there is no orbit to keep, so it brakes on the steepest shape.
    /// </summary>
    public sealed class LandRepredictTests
    {
        private static string Verdict(bool plannedClears, double bestShape, double pe, double groundAhead, bool arc)
        {
            return BlockCalls.LandRepredict(new Dictionary<string, object>
            {
                { "planned_clears", plannedClears }, { "best_shape", bestShape }, { "pe", pe }, { "ground_ahead", groundAhead }, { "arc", arc },
            });
        }

        [Fact]
        public void ThePlannedShapeThatStillClearsIsFlown()
        {
            Assert.Equal("fly", Verdict(true, -1, 12000, 3000, false));
            Assert.Equal("fly", Verdict(true, -1, -59532, -1e9, true));
        }

        [Fact]
        public void WhenThePlannedShapeNoLongerClearsTheCheapestThatDoesIsFlown()
        {
            Assert.Equal("switch", Verdict(false, 0.38, 12000, 3000, false));
            Assert.Equal("switch", Verdict(false, 0.38, -59532, -1e9, true));
        }

        [Fact]
        public void FromAnOrbitOverTheTerrainWithNoShapeClearingItDoesntBrakeAndKeepsTheOrbit()
        {
            Assert.Equal("hold", Verdict(false, -1, 12000, 3000, false));
        }

        [Fact]
        public void FromAnOrbitThatMeetsTheTerrainWithNoShapeClearingItBrakesOnTheSteepestShape()
        {
            Assert.Equal("steepest", Verdict(false, -1, 2000, 3000, false));
        }

        [Fact]
        public void TheMinmusSiteHopArcWithNoShapeClearingBrakesOnTheSteepestShapeRatherThanFalling()
        {
            // m20261125-143524-2df5: site_hop flew 1.4 km from survey site 1 and handed the touchdown to land on its arc, ending 198 m
            // from the aim. As braking began the planned shape didn't clear the terrain and no shape did. The arc's plan has no
            // predicted path, so the ground ahead read as none (-1e9) and its periapsis (-59532 m) as over it: land kept an
            // "orbit" it didn't have and ended with the craft falling.
            Assert.Equal("steepest", Verdict(false, -1, -59532, -1e9, true));
        }

        [Fact]
        public void AnArcNeverHoldsWhateverItsPeriapsisReads()
        {
            Assert.Equal("steepest", Verdict(false, -1, 12000, 3000, true));
        }
    }
}
