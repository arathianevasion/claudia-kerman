// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #770, from Mission m20261105-174251-8f20: transfer_to's Encounter Watch, its correction clear of the sphere of influence's
    /// edge and its restore before a recovery. The replay is what KSP.log shows: the correction flown about 190 s past Kerbin's
    /// edge; as the next warp packed the craft, KSP put it back on a Kerbin patch at 83.93 Mm (the edge is 84.16 Mm), moving out at
    /// 1,014 m/s, and held it there for 51,097 s of warp before the next Sun patch.
    /// </summary>
    public sealed class EncounterWatchTests
    {
        /// <summary>From KSP.log: the distance from Kerbin at the re-entry, m, and the time KSP then held the craft round Kerbin, s.</summary>
        private const double ReentryDistance = 83.93e6;
        private const double HeldOnKerbin = 51097;

        private static TransferFlightAsk Ask(string target, double pe)
        {
            return new TransferFlightAsk { Target = target, TargetPe = pe, StartUt = 0, TurnTime = 60, DvLeft = 5000, Auto = true };
        }

        /// <summary>Flies the rig's steps to the first wait into the target's sphere of influence, and gives that wait, not yet waited.</summary>
        private static TransferFlightStep FlyToTheLongCoast(TransferRig rig, TransferFlightAsk ask)
        {
            for (var i = 0; i < 20; i++)
            {
                var step = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.True(step.Error == "", step.Error);
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Wait && step.Phase == "near") return step;
                Assert.NotEqual(TransferFlightStep.Done, step.Do);
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else rig.MoveTo(step.Ut);
                TransferFlight.Carry(ask, step);
            }
            throw new InvalidOperationException("no long coast in 20 steps");
        }

        /// <summary>What KSP did as the warp began: the craft, at the same velocity, on a Kerbin patch at <see cref="ReentryDistance"/>.</summary>
        private static void RePatchIntoKerbin(TransferRig rig)
        {
            var kerbin = rig.System.Find("Kerbin");
            Vec3 r, v, kr, kv;
            rig.Orbit.State(rig.Ut, out r, out v);
            rig.System.OrbitOf(kerbin).State(rig.Ut, out kr, out kv);
            rig.Body = kerbin;
            rig.Orbit = Conic.FromState(kerbin.Gm, (r - kr).Normalized * ReentryDistance, v - kv, rig.Ut);
        }

        /// <summary>
        /// What KSP did next: <see cref="HeldOnKerbin"/> seconds on that patch, past the edge (KSP's on-rails transition came that
        /// late), then the Sun patch from wherever that left the craft.
        /// </summary>
        private static void RePatchBackOntoTheSun(TransferRig rig)
        {
            var kerbin = rig.System.Find("Kerbin");
            var sun = rig.System.Find("Sun");
            var ut = rig.Ut + HeldOnKerbin;
            Vec3 r, v, kr, kv;
            rig.Orbit.State(ut, out r, out v);
            rig.System.OrbitOf(kerbin).State(ut, out kr, out kv);
            rig.Body = sun;
            rig.Orbit = Conic.FromState(sun.Gm, r + kr, v + kv, ut);
            rig.Ut = ut;
        }

        // ---- Why the re-patch went unnoticed (the pin, written before the fix) ----

        [Fact]
        public void The_step_that_plans_the_long_coast_sees_an_encounter_the_warp_then_loses()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.02;
            rig.CorrectionError = 0.05;
            var ask = Ask("Duna", 50000);

            // Core's only reading of the coast is this one: the craft in physics, on the Sun patch, before the warp packs it.
            var wait = FlyToTheLongCoast(rig, ask);
            Assert.Equal("Sun", rig.Body.Name);
            var setup = Setup(rig);
            Assert.NotNull(TransferFlight.Encounter(setup));

            // KSP re-patches only as the warp begins; nothing in the flight reads the orbit again until wait.Ut, months on.
            RePatchIntoKerbin(rig);
            RePatchBackOntoTheSun(rig);
            Assert.True(wait.Ut > rig.Ut + 100 * 21600);
            Assert.Null(TransferFlight.Encounter(Setup(rig)));
        }

        private static PredictBurn.Setup Setup(TransferRig rig)
        {
            ToolOutcome error;
            return PredictBurn.Setup.Prepare(rig.Snapshot(), "Duna", out error);
        }

        // ---- The post-escape correction, clear of the edge ----

        [Fact]
        public void The_post_escape_correction_is_held_until_the_margin_past_the_sphere_of_influence()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.02;
            var ask = Ask("Duna", 50000);
            var eject = TransferFlight.Next(rig.Snapshot(), ask);
            ask.LastDv = rig.Fly(eject.Kind, eject.Ut, eject.Prograde, eject.Normal, eject.Radial);
            TransferFlight.Carry(ask, eject);

            var wait = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal(TransferFlightStep.Wait, wait.Do);
            Assert.Equal("escaped", wait.Phase);
            rig.MoveTo(wait.Ut);
            Assert.Equal("Sun", rig.Body.Name);

            var kerbin = rig.System.Find("Kerbin");
            var gap = (rig.Orbit.Position(rig.Ut) - rig.System.OrbitOf(kerbin).Position(rig.Ut)).Length;
            Assert.True(gap >= kerbin.SoiRadius * 1.05 - 1000, gap + " m from Kerbin at the wait's end");
        }
    }
}
