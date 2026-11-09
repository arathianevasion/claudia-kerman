// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #934: <see cref="AscentSim"/> flies #932's burn-through by calling <see cref="LaunchBurnThrough"/>, so <c>plan_ascent</c> and the Craft Check
    /// describe what <c>launch</c> flies. The weak-upper-stage craft is the Duna ship's shape (m20261108-220659-ac85): a strong first stage, then 60 kN
    /// nuclear stages at about 0.19 g, a cutoff that can't coast to apoapsis and circularize.
    /// </summary>
    public sealed class AscentSimBurnThroughTests
    {
        private readonly ITestOutputHelper _out;

        public AscentSimBurnThroughTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private sealed class ExpAir : IAscentAir
        {
            public void Air(double altitude, out double atm, out double density, out double speedOfSound)
            {
                atm = altitude < 70000 ? Math.Exp(-altitude / 5600) : 0;
                density = 1.225 * atm;
                speedOfSound = 340 - 0.004 * Math.Min(altitude, 12000);
            }
        }

        private static AscentStage Stage(int number, double vacKn, double aslKn, double isp, double startT, double endT)
        {
            return new AscentStage
            {
                Number = number, ThrustVac = vacKn * 1000, ThrustSeaLevel = aslKn * 1000, Flow = vacKn * 1000 / (isp * AscentSim.G0),
                StartMass = startT * 1000, EndMass = endT * 1000, IspVac = isp, DeltaVVac = isp * AscentSim.G0 * Math.Log(startT / endT),
            };
        }

        private AscentFlight Fly(double targetAp, List<AscentStage> stages, int stageLimit = 0)
        {
            var options = new AscentOptions
            {
                Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, Spin = 174.94, StartAltitude = 70, TargetAp = targetAp, StageLimit = stageLimit,
            };
            var flight = AscentSim.Fly(options, new ExpAir(), AscentSim.Table(new List<double[]> { new[] { 0.0, 1.0 }, new[] { 1.1, 2.0 }, new[] { 3.0, 1.0 } }), stages);
            _out.WriteLine((flight.ReachesOrbit ? "orbit" : flight.Failure) + ", coast_ap " + Math.Round(flight.CoastAp) + ", end stage " + flight.LaunchEndStage
                + ", ap " + Math.Round(flight.OrbitAp) + ", pe " + Math.Round(flight.OrbitPe));
            return flight;
        }

        /// <summary>A 2000 kN first stage that ends at <paramref name="firstStageEndT"/> t, then two 60 kN LV-N stages (800 s) on 32 t: 0.19 g.</summary>
        private static List<AscentStage> WeakUpper(double firstStageEndT)
        {
            return new List<AscentStage> { Stage(3, 2000, 1800, 310, 100, firstStageEndT), Stage(2, 60, 60, 800, 32.2, 28.2), Stage(1, 60, 60, 800, 26.2, 22) };
        }

        [Fact]
        public void A_weak_upper_stage_burns_through_to_a_periapsis_above_the_air_with_no_fall_back()
        {
            var flight = Fly(80000, WeakUpper(46));
            Assert.True(flight.ReachesOrbit, flight.Failure);
            Assert.DoesNotContain("fell back", flight.Failure);
            Assert.True(flight.OrbitPe > 70000, "pe " + flight.OrbitPe);
            // It never coasts: the burn runs on through the nuclear stages and launch ends in the last of them.
            Assert.Equal(0, flight.CoastAp);
            Assert.Equal(1, flight.LaunchEndStage);
            Assert.All(flight.Lit.Skip(1), l => Assert.True(l.Stage < 3));
            // Steered on orbital prograde, as launch.ks does: 277 km. On surface prograde it came out 293 km.
            Assert.InRange(flight.OrbitAp, 270000, 285000);
        }

        [Fact]
        public void A_burn_through_that_runs_out_of_the_stages_launch_may_burn_fails_as_out_of_fuel_rather_than_coasting_on()
        {
            // #937: launch ends the block with claudia_tick's reason, as anywhere else in the ascent.
            var flight = Fly(80000, WeakUpper(46), 2);
            Assert.False(flight.ReachesOrbit);
            Assert.Equal("out of fuel at stage limit 2", flight.Failure);
            Assert.Equal(0, flight.CoastAp);
        }

        [Fact]
        public void A_craft_that_coasts_is_flown_as_before()
        {
            // The same first stage with fuel to spare: the decision says coast, and the cutoff, coast and circularize are the old flight's.
            var flight = Fly(80000, WeakUpper(33));
            Assert.True(flight.ReachesOrbit, flight.Failure);
            Assert.InRange(flight.CoastAp, 79000, 80100);
            Assert.Equal(3, flight.LaunchEndStage);
        }

        [Fact]
        public void A_coast_the_decision_allows_still_fails_as_before_when_the_circularize_cant_finish()
        {
            var flight = Fly(80000, WeakUpper(43));
            Assert.False(flight.ReachesOrbit);
            Assert.InRange(flight.CoastAp, 79000, 80100);
            Assert.Contains("circulariz", flight.Failure);
        }

        [Fact]
        public void The_sim_and_launch_ask_one_decision_in_Core_and_the_sim_keeps_no_copy()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ClaudiaKerman.Core", "Tools", "AscentSim.cs"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var sim = File.ReadAllText(Path.Combine(dir.FullName, "src", "ClaudiaKerman.Core", "Tools", "AscentSim.cs"));
            var launch = File.ReadAllText(Path.Combine(dir.FullName, "kerboscript", "library", "launch.ks"));
            Assert.Contains("LaunchBurnThrough.Call(", sim);
            Assert.Contains("addons:claudia:burnthrough(", launch);
            // The rule's own figures live in LaunchBurnThrough alone.
            Assert.DoesNotContain("half_burn", sim);
            Assert.DoesNotContain("StagedBurnTime", sim);
            Assert.DoesNotContain("SecondsToTurn", sim);
        }
    }
}
