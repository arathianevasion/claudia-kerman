// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #867: the one check of whether an orbit crosses a moon's sphere of influence over a wait, and the next-patch watch that reads it.
    /// Flight m20261107-055424-908b: a 100 × 2,144 km Duna orbit (apoapsis 2,464 km from the centre) waited 12 million s for Kerbin's window, and
    /// Ike, whose sphere of influence reaches down to 2,054 km, flung the craft out of Duna.
    /// </summary>
    public sealed class MoonCrossingTests
    {
        internal const double Wait908b = 12e6;

        /// <summary>908b's Duna orbit: <paramref name="peAlt"/> × <paramref name="apAlt"/> m, equatorial, periapsis at <paramref name="argPe"/> degrees.</summary>
        internal static TransferRig DunaOrbit(double peAlt = 100000, double apAlt = 2144000, double argPe = 0, double ut = 168850200)
        {
            var rig = TransferRig.Circular("Duna", peAlt, ut);
            var rp = rig.Body.Radius + peAlt;
            var ra = rig.Body.Radius + apAlt;
            rig.Orbit = Conic.FromElements(rig.Body.Gm, (rp + ra) / 2, (ra - rp) / (ra + rp), 0, 0, argPe, 0, ut);
            return rig;
        }

        [Fact]
        public void Orbit908bOverItsWaitCrossesIkeByGeometryAndTheFixIsSetApBelowTheEdgeForAbout25()
        {
            var rig = DunaOrbit();
            var crossing = MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b);

            Assert.NotNull(crossing);
            Assert.Equal("Ike", crossing.Moon.Name);
            Assert.True(crossing.Inside);
            // Ike's lowest edge: 3,104 km less its 1,050 km sphere of influence.
            Assert.InRange(crossing.EdgeR, 2.054e6, 2.055e6);
            Assert.Equal("set_ap", crossing.FixOption);
            Assert.True(crossing.FixAltitude.Value + rig.Body.Radius < 2.054e6, "set_ap " + crossing.FixAltitude + " m is below the edge");
            Assert.InRange(crossing.FixDv, 20, 30);
            Assert.InRange(crossing.CircleDv, 240, 280);
            Assert.Contains("change_orbit set_ap 1,520 km (about 26 m/s; a circle costs about 260 m/s)", crossing.Line());
            Assert.Contains("Ike's sphere of influence (from 1,734 km up)", crossing.Line());
        }

        [Fact]
        public void TheSameOrbitWithItsApoapsisLoweredBelowTheEdgeIsSafe()
        {
            var crossing = MoonCrossings.Judge(DunaOrbit().System, DunaOrbit().Body, DunaOrbit(apAlt: 1520000).Orbit, 168850200, 168850200 + Wait908b);
            Assert.Null(crossing);
        }

        [Fact]
        public void TheFixFlownIsSafe()
        {
            var rig = DunaOrbit();
            var fix = MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b);
            var fixedRig = DunaOrbit(apAlt: fix.FixAltitude.Value);
            Assert.Null(MoonCrossings.Judge(fixedRig.System, fixedRig.Body, fixedRig.Orbit, fixedRig.Ut, fixedRig.Ut + Wait908b));
        }

        [Fact]
        public void AnOrbitOutsideTheMoonIsFixedByRaisingItsPeriapsis()
        {
            var rig = DunaOrbit(peAlt: 3700000, apAlt: 6000000);
            var crossing = MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b);
            Assert.NotNull(crossing);
            Assert.False(crossing.Inside);
            Assert.Equal("set_pe", crossing.FixOption);
            Assert.True(crossing.FixAltitude.Value + rig.Body.Radius > crossing.EdgeR);
            Assert.True(crossing.FixDv > 0);
        }

        /// <summary>A short wait is followed loop by loop: the moon met within it counts, a meeting after it doesn't, and a planned one never.</summary>
        [Fact]
        public void AWaitOfUpToThreeLoopsIsJudgedByFollowingThePath()
        {
            var rig = DunaOrbit();
            var met = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + MoonCrossings.LikelyLoops * rig.Orbit.Period);
            Assert.NotNull(met);
            Assert.Equal("Ike", met.Moon.Name);
            var at = met.Ut.Value;

            // A wait ending just after the meeting, at most three loops from its start, crosses at that time.
            var from = Math.Max(rig.Ut, at - 2.5 * rig.Orbit.Period);
            var shortWait = MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, from, at + 60);
            Assert.NotNull(shortWait);
            Assert.Equal(at, shortWait.Ut.Value, 0);

            // One that ends before it doesn't, though the orbit reaches Ike's edge: the path, not the geometry, judges it.
            Assert.Null(MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, from, at - 60));

            // The planned moon, at a planned encounter, is no crossing; an unplanned one is.
            Assert.Null(MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, from, at + 60, new[] { "Ike" }));
            Assert.NotNull(MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, from, at + 60, new[] { "Mun" }));
        }

        [Fact]
        public void ByGeometryThePlannedMoonIsNoCrossing()
        {
            var rig = DunaOrbit();
            Assert.Null(MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b, new[] { "ike" }));
        }

        [Fact]
        public void AnOrbitRoundABodyWithNoMoonsNeverCrosses()
        {
            var rig = TransferRig.Circular("Ike", 20000, 0);
            Assert.Null(MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, 0, Wait908b));
        }

        [Fact]
        public void TheGeometryCheckGivesTheLikelyTimeFromThePathFollowed()
        {
            var rig = DunaOrbit();
            var crossing = MoonCrossings.Judge(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b);
            var met = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + MoonCrossings.LikelyLoops * rig.Orbit.Period);
            Assert.Equal(met.Ut, crossing.Ut);
            Assert.Contains("about UT " + MoonCrossing.Num(met.Ut.Value), crossing.What());
        }

        // ---- The next-patch watch ----

        [Fact]
        public void TheWatchDropsAMarginBeforeAnUnplannedMeetingAndNotBefore()
        {
            var rig = DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b).Ut.Value;
            var margin = NextPatchWatch.Margin(rig.Orbit);
            Assert.Equal(rig.Orbit.Period, margin);
            var watch = new NextPatchWatch("");
            string line = "";
            var checks = 0;
            // The adapter polls every frame at warp; here, every 600 s of game time, which overshoots a due check as a fast warp does.
            while (rig.Ut < at && line.Length == 0)
            {
                if (watch.Due(rig.Ut))
                {
                    checks++;
                    line = watch.Check(rig.Snapshot());
                }
                if (line.Length == 0) rig.MoveTo(rig.Ut + 600);
            }
            Assert.NotEqual("", line);
            Assert.InRange(at - rig.Ut, margin - NextPatchWatch.MinGap - 600, margin);
            Assert.StartsWith("Warp stopped: the craft would meet Ike's sphere of influence at UT " + MoonCrossing.Num(at), line);
            Assert.Contains("change_orbit set_ap", line);
            // Checks are spent only every 6 h of game time until the meeting is near: this wait of days took a handful.
            Assert.InRange(checks, 1, (int)((at - 168850200) / NextPatchWatch.MaxGap) + 4);
            // One meeting is answered once.
            Assert.False(watch.Due(rig.Ut + 1));
        }

        /// <summary>
        /// A new warp re-arms the watch, so an orbit changed at 1x (a burn by hand into Ike's reach) is read at once, not 6 h later; and a meeting
        /// already answered is not answered again when the player warps on.
        /// </summary>
        [Fact]
        public void ANewWarpReadsAtOnceButTheSameMeetingIsAnsweredOnce()
        {
            var safe = DunaOrbit(apAlt: 1520000);
            var watch = new NextPatchWatch("");
            Assert.Equal("", watch.Check(safe.Snapshot()));
            Assert.False(watch.Due(safe.Ut + 1));

            var rig = DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b).Ut.Value;
            rig.MoveTo(at - 1000);
            watch.Rearm();
            Assert.True(watch.Due(rig.Ut));
            Assert.NotEqual("", watch.Check(rig.Snapshot()));

            watch.Rearm();
            rig.MoveTo(at - 500);
            Assert.Equal("", watch.Check(rig.Snapshot()));
        }

        [Fact]
        public void APathRoundTheStarMeetsNoMoon()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var sun = rig.System.Find("Sun");
            var dunaPath = rig.System.OrbitOf(rig.System.Find("Duna"));
            Assert.Null(MoonCrossings.Judge(rig.System, sun, dunaPath, 0, 1e8));
            Assert.Null(MoonCrossings.Follow(rig.System, sun, dunaPath, 0, 1e8));
        }

        [Fact]
        public void TheWatchLetsAPlannedEncounterPass()
        {
            var rig = DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b).Ut.Value;
            rig.MoveTo(at - 600);
            Assert.Equal("", new NextPatchWatch("Duna,Ike").Check(rig.Snapshot()));
            Assert.NotEqual("", new NextPatchWatch("").Check(rig.Snapshot()));
        }

        [Fact]
        public void TheWatchOnASafeOrbitFindsNothingAndWaitsAKerbinDay()
        {
            var rig = DunaOrbit(apAlt: 1520000);
            var watch = new NextPatchWatch("");
            Assert.Equal("", watch.Check(rig.Snapshot()));
            Assert.False(watch.Due(rig.Ut + NextPatchWatch.MaxGap - 1));
            Assert.True(watch.Due(rig.Ut + NextPatchWatch.MaxGap));
        }

        [Fact]
        public void TheWatchSaysSoWhenKspAlreadyHasTheCraftRoundTheMoon()
        {
            var rig = DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b).Ut.Value;
            var watch = new NextPatchWatch("");
            rig.MoveTo(at - 3 * rig.Orbit.Period);
            Assert.Equal("", watch.Check(rig.Snapshot()));
            // The rig follows one loop per move, as the path's first patch searches one.
            rig.MoveTo(at - 600);
            rig.MoveTo(at + 600);
            Assert.Equal("Ike", rig.Body.Name);
            var line = watch.Check(rig.Snapshot());
            Assert.StartsWith("Warp stopped: KSP has the craft in Ike's sphere of influence", line);
        }

        /// <summary>
        /// What one check costs (#867 asks it stated): it follows the path 6 h plus the margin ahead, here two loops of 908b's orbit with Ike in reach.
        /// It runs once per 6 h of game time at most, so at 100,000x about once every 0.2 s of real time.
        /// </summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void OneWatchCheckTakesUnderTwoMilliseconds()
        {
            var rig = DunaOrbit();
            var snapshot = rig.Snapshot();
            Timing.Within(2, () => new NextPatchWatch("").Check(snapshot));
        }

        /// <summary>Fix round (4): the meeting the watch dropped warp for, let through by the player, is not answered again when KSP has the craft round the moon.</summary>
        [Fact]
        public void AMeetingAnsweredBeforeTheSoiChangeIsNotAnsweredAgainAfterIt()
        {
            var rig = DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b).Ut.Value;
            var watch = new NextPatchWatch("");
            rig.MoveTo(at - 600);
            Assert.StartsWith("Warp stopped: the craft would meet Ike's", watch.Check(rig.Snapshot()));
            rig.MoveTo(at + 600);
            Assert.Equal("Ike", rig.Body.Name);
            Assert.Equal("", watch.Check(rig.Snapshot()));
        }

        /// <summary>A Kerbin orbit from <paramref name="peAlt"/> to <paramref name="apR"/> m from the centre, starting at <paramref name="meanAnomaly"/> rad.</summary>
        private static TransferRig KerbinEllipse(double peAlt, double apR, double inc, double lan, double argPe, double meanAnomaly)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var rp = rig.Body.Radius + peAlt;
            rig.Orbit = Conic.FromElements(rig.Body.Gm, (rp + apR) / 2, (apR - rp) / (apR + rp), inc, lan, argPe, meanAnomaly, 0);
            return rig;
        }

        /// <summary>
        /// Fix round (3): after transfer_to Minmus the 680 × 47,000 km ellipse reaches the Mun's band too, but its first loop ends at Minmus, planned:
        /// no crossing by geometry (the Debrief's fact). Unplanned, the same path still crosses.
        /// </summary>
        [Fact]
        public void APathWhoseFirstLoopEndsAtAPlannedEncounterIsNoCrossing()
        {
            var probe = TransferRig.Circular("Kerbin", 100000, 0);
            var minmus = probe.System.Find("Minmus");
            var minmusOrbit = probe.System.OrbitOf(minmus);
            TransferRig found = null;
            // A transfer ellipse in Minmus's plane whose apoapsis meets it on the first loop.
            for (var argPe = 0; argPe < 360 && found == null; argPe += 2)
            {
                var rig = KerbinEllipse(80000, minmus.Sma, minmus.Inclination, minmus.Lan, argPe, 0);
                var path = SingleShooting.Follow(rig.System, rig.Body, rig.Orbit, 0, 1);
                if (path != null && path.Count > 0 && path[0].End == PatchEnd.Encounter && path[0].Next.Name == "Minmus") found = rig;
            }
            Assert.NotNull(found);
            Assert.True(minmusOrbit.Closed);
            Assert.Null(MoonCrossings.Judge(found.Snapshot(), new[] { "Minmus" }));
            Assert.Null(MoonCrossings.Judge(found.System, found.Body, found.Orbit, 0, Wait908b, new[] { "Minmus" }));
            Assert.NotNull(MoonCrossings.Judge(found.System, found.Body, found.Orbit, 0, Wait908b));
        }

        /// <summary>Fix round (3): a return from the Mun with its periapsis at 35 km meets the atmosphere on its first loop: its apoapsis near the Mun is no crossing.</summary>
        [Fact]
        public void APathWhoseFirstLoopEndsInTheAtmosphereIsNoCrossing()
        {
            var probe = TransferRig.Circular("Kerbin", 100000, 0);
            var mun = probe.System.Find("Mun");
            TransferRig found = null;
            // Just past the apoapsis, with the Mun elsewhere, so the first loop runs down into the atmosphere.
            for (var argPe = 0; argPe < 360 && found == null; argPe += 30)
            {
                var rig = KerbinEllipse(35000, mun.Sma - 1.5e6, 0, 0, argPe, Math.PI + 0.1);
                var path = SingleShooting.Follow(rig.System, rig.Body, rig.Orbit, 0, 1);
                if (path != null && path.Count > 0 && path[0].End == PatchEnd.Atmosphere) found = rig;
            }
            Assert.NotNull(found);
            Assert.True(found.Orbit.ApR > mun.Sma * (1 - mun.Eccentricity) - mun.SoiRadius, "the apoapsis reaches the Mun's band");
            Assert.Null(MoonCrossings.Judge(found.Snapshot(), new string[0]));
            Assert.Null(MoonCrossings.Judge(found.System, found.Body, found.Orbit, 0, Wait908b));
        }

        /// <summary>
        /// The dearest check: a low Kerbin orbit (100 km, about 1,960 s a loop) has a margin of an hour, so one check follows 25,200 s, about 13
        /// loops, each an encounter search against both the Mun and Minmus: about 1.2 ms measured, against 0.3 ms for 908b's orbit.
        /// </summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void OneWatchCheckOfALowKerbinOrbitTakesUnderFiveMilliseconds()
        {
            var snapshot = TransferRig.Circular("Kerbin", 100000, 0).Snapshot();
            Timing.Within(5, () => new NextPatchWatch("").Check(snapshot));
        }

        [Fact]
        public void TheBlocksFailureNamesTheMoonAndTheTime()
        {
            var rig = DunaOrbit();
            var met = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + Wait908b);
            Assert.Equal("would meet Ike at UT " + MoonCrossing.Num(met.Ut.Value), NextPatchWatch.WouldMeet(met));
        }
    }
}
