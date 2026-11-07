// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #867 in <c>transfer_to</c>: its own warp to a far ejection is watched by the next-patch watch (asked through the Encounter Watch's call, so the
    /// block is unchanged), a drop fails the block with the moon and the time, and its refusal of an ejection that meets a moon names the orbit.
    /// </summary>
    public sealed class MoonCrossingFlightTests
    {
        private static Dictionary<string, object> Input(string phase, bool dropped = false)
        {
            return new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", 30000.0 }, { "correction", true }, { "pe_tolerance", 1000.0 }, { "start_ut", 0.0 },
                { "max_duration", "auto" }, { "auto", true }, { "turn_time", 60.0 }, { "phase", phase }, { "watch_dropped", dropped },
            };
        }

        [Fact]
        public void TheBlocksWaitForAFarEjectionIsWatchedAndEndsALeadBeforeIt()
        {
            var rig = TransferRig.Circular("Duna", 100000, 0);
            var wait = BlockCalls.TransferStep(Input("start"), rig.Snapshot());
            Assert.Equal("", wait["error"]);
            Assert.Equal("wait", wait["do"]);
            var watch = (IDictionary<string, object>)wait["watch"];
            Assert.Equal(true, watch["next_patch"]);
            var carry = (IDictionary<string, object>)wait["carry"];
            Assert.Equal("eject", carry["phase"]);
            Assert.NotEqual("", carry["ejection"]);
            // The report already has the departure, and the wait ends the lead before it.
            var departure = Convert.ToDouble(((IDictionary<string, object>)wait["report"])["departure_ut"]);
            Assert.Equal(departure - TransferFlight.EjectionLead(rig.Orbit, 60), Convert.ToDouble(wait["ut"]), 0);
            // The mod's own read of the first step (BurnPoints) still gets the ejection.
            var first = TransferFlight.Next(rig.Snapshot(), new TransferFlightAsk { Target = "Kerbin", TargetPe = 30000, StartUt = 0, TurnTime = 60 });
            Assert.Equal(TransferFlightStep.Burn, first.Do);
            Assert.Equal(departure, first.Ut, 0);
        }

        [Fact]
        public void AnEjectionWithinTwoLeadsIsFlownWithNoWait()
        {
            var rig = TransferRig.Circular("Duna", 100000, 0);
            var departure = Convert.ToDouble(((IDictionary<string, object>)BlockCalls.TransferStep(Input("start"), rig.Snapshot())["report"])["departure_ut"]);
            rig.MoveTo(departure - 1.5 * TransferFlight.EjectionLead(rig.Orbit, 60));
            Assert.Equal("burn", BlockCalls.TransferStep(Input("start"), rig.Snapshot())["do"]);
        }

        [Fact]
        public void TheEncounterWatchArmedWithTheWaitsPlanIsTheNextPatchWatch()
        {
            var rig = MoonCrossingTests.DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + MoonCrossingTests.Wait908b).Ut.Value;
            var watch = new EncounterWatch(new EncounterWatchPlan { NextPatch = true, SinceUt = rig.Ut }.ToLexicon());
            Assert.True(watch.Due(rig.Ut));
            Assert.Equal("", watch.Check(rig.Snapshot()));
            rig.MoveTo(at - 1000);
            Assert.True(watch.Due(rig.Ut));
            Assert.Equal("Next-patch watch: would meet Ike at UT " + MoonCrossing.Num(at) + "; warp dropped before it", watch.Check(rig.Snapshot()));
        }

        /// <summary>The watch dropped the block's warp: the next step flies nothing and fails with the moon and the time.</summary>
        [Fact]
        public void AfterADropTheBlockFailsWithTheMoonAndTheTime()
        {
            var rig = MoonCrossingTests.DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + MoonCrossingTests.Wait908b).Ut.Value;
            rig.MoveTo(at - 1000);
            var step = BlockCalls.TransferStep(Input("eject", dropped: true), rig.Snapshot());
            Assert.Equal("would meet Ike at UT " + MoonCrossing.Num(at), step["error"]);
        }

        [Fact]
        public void AnEjectionSolvedAfterTheWaitIsRefusedWhenTheCraftMeetsAMoonFirst()
        {
            var rig = MoonCrossingTests.DunaOrbit();
            var at = MoonCrossings.Follow(rig.System, rig.Body, rig.Orbit, rig.Ut, rig.Ut + MoonCrossingTests.Wait908b).Ut.Value;
            rig.MoveTo(at - rig.Orbit.Period / 2);
            var step = BlockCalls.TransferStep(Input("eject"), rig.Snapshot());
            var error = (string)step["error"];
            // Either the ejection itself meets Ike, or the path meets it before the ejection: no burn either way.
            Assert.True(error.Contains("would meet Ike") || error.Contains("meets Ike"), error);
        }

        [Fact]
        public void TheRefusalOfAnEjectionThatMeetsAMoonNamesTheOrbitThatCrossesIt()
        {
            var rig = MoonCrossingTests.DunaOrbit();
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(rig.Snapshot(), null, out error);
            var ike = rig.System.Find("Ike");
            var refusal = SiblingEjection.MeetsMoon(setup, rig.Ut + MoonCrossingTests.Wait908b, ike);
            Assert.StartsWith("the ejection at UT ", refusal);
            Assert.EndsWith(" meets Ike on its way out of Duna's sphere of influence and the orbit you're in crosses Ike's SOI: lower its apoapsis below 1,734 km (about 26 m/s) before waiting", refusal);

            var safe = MoonCrossingTests.DunaOrbit(apAlt: 1520000);
            setup = PredictBurn.Setup.Prepare(safe.Snapshot(), null, out error);
            Assert.EndsWith(" meets Ike on its way out of Duna's sphere of influence", SiblingEjection.MeetsMoon(setup, safe.Ut + MoonCrossingTests.Wait908b, ike));
        }
    }
}
