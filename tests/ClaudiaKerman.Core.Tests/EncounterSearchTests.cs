// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The encounter search's expected failure (#439): running out of steps comes back as <c>error</c> text, never an exception,
    /// so a block call has a reason to fail with.
    /// </summary>
    public sealed class EncounterSearchTests
    {
        private static GameSnapshot Snapshot()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "kerbin-orbit-100km.json")));
        }

        private static void Search(int maxSteps, out double? found, out string error, out Patch patch)
        {
            var snapshot = Snapshot();
            var system = new BodySystem(snapshot.Bodies, maxSteps);
            var kerbin = system.Find("Kerbin");
            var orbit = Conic.FromState(kerbin.Gm, new Vec3(700000, 0, 0), new Vec3(0, 2300, 0), 0);
            SnapshotBody hit;
            found = system.FirstEncounter(kerbin, orbit, 0, double.PositiveInfinity, null, out hit, out error);
            patch = system.Follow(kerbin, orbit, 0, 3).Last();
        }

        [Fact]
        public void A_search_that_exceeds_its_steps_gives_error_text_and_no_exception()
        {
            double? found;
            string error;
            Patch patch;
            Search(1, out found, out error, out patch);
            Assert.Null(found);
            Assert.Equal("the encounter search took too many steps", error);
            Assert.Equal("the encounter search took too many steps", patch.Error);
        }

        [Fact]
        public void A_search_within_its_steps_has_no_error()
        {
            double? found;
            string error;
            Patch patch;
            Search(200000, out found, out error, out patch);
            Assert.Null(error);
            Assert.Null(patch.Error);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Following_a_Kerbin_escape_onto_a_solar_hyperbola_ends_on_a_stable_patch_without_throwing(int maxPatches)
        {
            var system = new BodySystem(Snapshot().Bodies);
            var kerbin = system.Find("Kerbin");
            // Fast enough that the craft leaves the Sun's reach too, so the last patch has an infinite end time (#471).
            var orbit = Conic.FromState(kerbin.Gm, new Vec3(-700000, 0, 0), new Vec3(0, -6000, 0), 0);
            var patches = system.Follow(kerbin, orbit, 0, maxPatches);
            Assert.Equal(Math.Min(maxPatches, 2), patches.Count);
            Assert.Equal("Kerbin", patches[0].Body.Name);
            if (maxPatches == 1) return;
            Assert.Equal(PatchEnd.Escape, patches[0].End);
            Assert.Equal("Sun", patches[1].Body.Name);
            Assert.Equal(PatchEnd.Stable, patches[1].End);
            Assert.True(double.IsPositiveInfinity(patches[1].EndUt));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void Following_a_path_runs_in_milliseconds()
        {
            // The patched conics of the golden in-orbit snapshot (#441): its own orbit, stable, searched for an encounter over a
            // period; then an ejection that meets the Mun and leaves its SOI again; and the closest approach of a burn that misses.
            // Measured at about 0.8 ms alone in a Debug build and 2.7 ms in the parallel full run; the budget is 25 times the first.
            var golden = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(golden, "Mun", out error);
            Assert.NotNull(setup);
            var system = setup.System;
            var at = golden.Ut + 300;
            Vec3 r, v;
            setup.Start.State(at, out r, out v);
            var ejected = Conic.FromState(setup.Body.Gm, r, v * (1 + 860 / v.Length), at);
            var missed = Conic.FromState(setup.Body.Gm, r, v * (1 + 800 / v.Length), at);
            Timing.Within(20, () =>
            {
                system.Follow(setup.Body, setup.Start, golden.Ut, 8);
                system.Follow(setup.Body, ejected, at, 8);
                double distance, ut;
                system.ClosestApproach(system.Follow(setup.Body, missed, at, 1)[0], setup.Target, out distance, out ut);
            });
        }
    }
}
