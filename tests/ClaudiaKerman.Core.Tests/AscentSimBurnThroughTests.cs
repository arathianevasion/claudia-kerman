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

        private AscentFlight Fly(double targetAp, List<AscentStage> stages, int stageLimit = 0, bool eccStop = true)
        {
            var options = new AscentOptions
            {
                Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, Spin = 174.94, StartAltitude = 70, TargetAp = targetAp, StageLimit = stageLimit, EccStop = eccStop,
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

        /// <summary>The 2000 kN first stage and two LV-N-like stages of <paramref name="kn"/> kN (800 s): weaker than 0.19 g's 60 kN, so the climb sinks.</summary>
        private static List<AscentStage> SinkingUpper(double kn)
        {
            return new List<AscentStage> { Stage(3, 2000, 1800, 310, 100, 46), Stage(2, kn, kn, 800, 32.2, 28.2), Stage(1, kn, kn, 800, 26.2, 22) };
        }

        [Fact]
        public void A_burn_through_descending_past_the_fall_pressure_with_its_periapsis_in_the_air_fails_with_launchs_reason()
        {
            // #939: the one decision's guard (#936), not the sim's own check: the reason is launch's, naming the periapsis and q.
            var flight = Fly(80000, SinkingUpper(20));
            Assert.False(flight.ReachesOrbit);
            Assert.StartsWith("fell back into the atmosphere: periapsis ", flight.Failure);
            Assert.Contains("at or below 70000 m, q ", flight.Failure);
            Assert.DoesNotContain("vertical speed", flight.Failure);
        }

        [Fact]
        public void A_burn_through_descending_under_the_air_top_with_q_at_or_under_the_fall_pressure_keeps_burning_and_reaches_orbit()
        {
            // 54 kN sinks (it falls at up to 250 m/s) in air thinner than the fall pressure. The q guard alone decides, so it burns on.
            // launch adds a burn-through's own time back onto an "auto" max_duration (launch.ks, #932), and circularize's "auto" budget
            // (start_in + 2 * burn_time + 300, exec_node.ks) grows with the coast, so the sim holds it to that, not 3600 s (#943).
            var flight = Fly(80000, SinkingUpper(54));
            Assert.True(flight.MinVerticalSpeed < -100, "min vertical speed " + flight.MinVerticalSpeed);
            Assert.True(flight.ReachesOrbit, flight.Failure);
            Assert.InRange(flight.OrbitAp, 2300000, 2370000);
            Assert.InRange(flight.OrbitPe, 2290000, 2360000);
            Assert.InRange(flight.DeltaVLeft, 340, 410);
        }

        [Fact]
        public void A_healthy_craft_with_a_3000_km_target_no_longer_fails_on_a_coast_cap()
        {
            var flight = Fly(3000000, WeakUpper(33));
            Assert.True(flight.ReachesOrbit, flight.Failure);
            Assert.InRange(flight.CoastAp, 2900000, 3050000);
        }

        [Fact]
        public void A_burn_that_outruns_the_auto_budget_still_fails_with_the_budget_in_the_reason()
        {
            // A 5 kN, 800 s stage burns a 1,000 km circularize slower than its own estimate (the lead and the steering lose time), so the budget runs out.
            var stages = new List<AscentStage> { Stage(3, 2000, 1800, 310, 100, 30), Stage(2, 5, 5, 800, 24, 10) };
            var flight = Fly(1000000, stages, 0, false);
            Assert.False(flight.ReachesOrbit);
            Assert.Matches(@"^still circularizing after \d+ s$", flight.Failure);
            Assert.Equal(300 + 2 * 120 + 5000, AscentSim.CircularizeBudget(5000, 120));
            Assert.Equal(300 + 2 * 120, AscentSim.CircularizeBudget(-4, 120));
        }

        [Fact]
        public void A_coast_above_the_air_costs_a_bounded_number_of_steps_however_far_the_target()
        {
            // Today's worst case was 36,000 steps (3600 s at dt 0.1). A coast of hours or days must stay under that.
            var stages = new List<AscentStage> { Stage(2, 4000, 4000, 310, 160, 80), Stage(1, 400, 400, 310, 70, 8) };
            foreach (var target in new[] { 3000000.0, 84000000.0, 2e9 })
            {
                var flight = Fly(target, stages);
                _out.WriteLine("target " + target + ": coast steps " + flight.CoastSteps);
                Assert.InRange(flight.CoastSteps, 0, 2000);
            }
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
