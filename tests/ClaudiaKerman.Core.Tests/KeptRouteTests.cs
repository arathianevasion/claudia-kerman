// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #866: arriving at Duna with an Ike leg next, <c>capture</c> keeps a high ellipse timed to meet Ike and <c>transfer_to</c> leaves from it at a periapsis.
    /// Flight m20261107-055424-908b burned 253.6 m/s at a 60 km Duna periapsis into a 60 × 24,434 km orbit (so it arrived at about 821 m/s clear of Duna), then
    /// 6.1 for the plane, 359.3 to a 60 km circle, 289.0 out to Ike and 120.7 into low Ike orbit: 1,029 m/s. The triage model kept the ellipse for about 440.
    /// </summary>
    public sealed class KeptRouteTests
    {
        /// <summary>908b's speed clear of Duna, m/s: 253.6 m/s at a 60 km periapsis made a 60 × 24,434 km orbit.</summary>
        internal const double VInf908b = 821;
        /// <summary>Ike's low orbit on the Delta-v Map, m: the periapsis the windows are judged at.</summary>
        private const double IkePe = 9100;

        internal static IList<PlannedSequence> Legs(params string[] names)
        {
            return names.Select(n => new PlannedSequence { Name = n }).ToList();
        }

        /// <summary>908b's arrival at Duna, its periapsis <paramref name="argPe"/> degrees round, with the Mission Plan's legs from the one in hand on.</summary>
        internal static TransferRig Arrival(double argPe, params string[] legs)
        {
            var rig = TransferRig.Arriving("Duna", 60000, VInf908b, 0, 0, argPe, 3000);
            var ahead = Legs(legs);
            rig.Stamp = s => s.LegsAhead = ahead;
            return rig;
        }

        internal static TransferRig IkeNext(double argPe = 180)
        {
            return Arrival(argPe, "Transfer to Duna", "Transfer to Ike", "Land on Ike");
        }

        private static CaptureAsk Ask(double? alt = 60000)
        {
            return new CaptureAsk { Alt = alt, TurnTime = 60 };
        }

        private static string Flown(TransferRig rig)
        {
            return string.Join(", ", rig.Burns.Select(b => b.Key + " " + b.Value.ToString("0.#") + " at " + rig.BurnBodies[rig.Burns.IndexOf(b)]));
        }

        private static string Kept(TransferFlightStep step)
        {
            object kept;
            return step.Report.TryGetValue(CaptureFlight.KeptKey, out kept) ? (string)kept : null;
        }

        [Fact]
        public void From908bsArrivalTheKeptRouteToLowIkeOrbitIsWithinTenPercentOf440()
        {
            var rig = IkeNext();
            var capture = rig.RunCapture(Ask());
            Assert.True(capture.Error == "", capture.Error + " | " + Flown(rig));
            Assert.StartsWith("kept 60 x ", Kept(capture));
            Assert.Equal("Duna", rig.Body.Name);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 59000, 61000);

            var flight = rig.Run(new TransferFlightAsk { Target = "Ike", TargetPe = IkePe, StartUt = rig.Ut, TurnTime = 60 }, 30);
            Assert.True(flight.Error == "", flight.Error + " | " + Flown(rig));
            // On to Ike's periapsis, and its capture.
            rig.MoveTo(Convert.ToDouble(flight.Report["arrival_ut"]) - 600);
            Assert.Equal("Ike", rig.Body.Name);
            var ike = rig.RunCapture(new CaptureAsk { TurnTime = 60 });
            Assert.True(ike.Error == "", ike.Error + " | " + Flown(rig));

            var total = rig.Burns.Sum(b => b.Value);
            Assert.True(Math.Abs(total - 440) <= 44, total.ToString("0") + " m/s: " + Flown(rig));
        }

        [Fact]
        public void TheTimedApoapsisLinesAWindowUpOnTheNextPeriapsisAndTheWaitMeetsNothing()
        {
            var rig = IkeNext();
            Vec3 r, v;
            var peUt = rig.Orbit.NextPeriapsis(0).Value;
            rig.Orbit.State(peUt, out r, out v);
            string why;
            var plan = KeptRoute.Plan(rig.Snapshot(), rig.System, rig.Body, rig.System.Find("Ike"), r, rig.Orbit.W, peUt, r.Length, 0.5 * rig.Body.SoiRadius, IkePe, null, out why);
            Assert.NotNull(plan);
            Assert.True(plan.Timed);
            Assert.Equal(1, plan.Loop);
            var ellipse = Conic.FromState(rig.Body.Gm, r, Vec3.Cross(rig.Orbit.W, r.Normalized) * KeptRoute.Speed(rig.Body.Gm, r.Length, plan.HighR), peUt);
            // One loop: the departure is at the next periapsis.
            Assert.InRange(plan.Departure.Ut, peUt + ellipse.Period * 0.9, peUt + ellipse.Period * 1.1);
            Assert.Null(MoonCrossings.Follow(rig.System, rig.Body, ellipse, peUt, plan.Departure.Ut));
        }

        [Fact]
        public void WithNoApoapsisTimedItKeepsTheHighestSafeOneAndWaitsUpToThreeLoops()
        {
            // A ceiling just over Ike's orbit leaves no apoapsis to time: the highest, with a window a loop or more on, or none.
            var found = 0;
            foreach (var argPe in new[] { 90.0, 180, 270 })
            {
                var rig = IkeNext(argPe);
                Vec3 r, v;
                var peUt = rig.Orbit.NextPeriapsis(0).Value;
                rig.Orbit.State(peUt, out r, out v);
                var ike = rig.System.Find("Ike");
                var top = ike.Sma * (1 + ike.Eccentricity) * 1.01;
                string why;
                var plan = KeptRoute.Plan(rig.Snapshot(), rig.System, rig.Body, ike, r, rig.Orbit.W, peUt, r.Length, top, IkePe, null, out why);
                if (plan == null)
                {
                    Assert.StartsWith("no Ike window within 3 loops", why);
                    continue;
                }
                found++;
                Assert.False(plan.Timed);
                Assert.Equal(top, plan.HighR);
                Assert.InRange(plan.Loop, 1, KeptRoute.MaxLoops);
            }
            Assert.True(found > 0, "no arrival kept the highest apoapsis");
        }

        [Fact]
        public void AWaitPastTheNextFixedDateIsntTakenSoItCircularizesSayingWhy()
        {
            var rig = Arrival(180, "Transfer to Duna", "Transfer to Ike", "Land on Ike", "Return to Kerbin");
            var legs = rig.Snapshot().LegsAhead;
            legs[3].DueTimeUt = 20000;
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error);
            Assert.Equal("circle: no Ike window within 3 loops of a safe high orbit before the next fixed date", Kept(step));
            Assert.InRange(rig.Orbit.E, 0, 0.011);
        }

        [Fact]
        public void APathThatMeetsIkeBeforeTheWindowIsNeverKeptAndWithNoneSafeItCircularizes()
        {
            // Here Ike sits on the way out from periapsis: every ellipse meets it in its first half loop.
            var rig = IkeNext(0);
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error);
            Assert.StartsWith("circle: no Ike window", Kept(step));
            Assert.InRange(rig.Orbit.E, 0, 0.011);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 59000, 61000);

            // Wherever it is kept, the path to the window meets nothing.
            foreach (var argPe in new[] { 90.0, 180, 270 })
            {
                var kept = IkeNext(argPe);
                var done = kept.RunCapture(Ask());
                Assert.StartsWith("kept", Kept(done));
                var at = Convert.ToDouble(Kept(done).Substring(Kept(done).IndexOf("UT ", StringComparison.Ordinal) + 3).Replace(",", ""));
                Assert.Null(MoonCrossings.Follow(kept.System, kept.Body, kept.Orbit, kept.Ut, at - 60));
            }
        }

        [Theory]
        [InlineData("Land on Duna")]
        [InlineData("Return to Kerbin")]
        public void WithNoMoonNextItIsTheCircleAsTodayAtAlt(string next)
        {
            var rig = Arrival(180, "Transfer to Duna", next);
            var step = rig.RunCapture(Ask(100000));
            Assert.True(step.Error == "", step.Error);
            Assert.Equal("circle: the next leg isn't to a moon of Duna", Kept(step));
            Assert.InRange(rig.Orbit.E, 0, 0.011);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 98000, 102000);
        }

        [Fact]
        public void AKerbinMoonFromKerbinAndNoMissionPlanAreTheCircleAsToday()
        {
            var home = TransferRig.Arriving("Kerbin", 100000, 900, 0, 0, 0, 3000);
            var legs = Legs("Return to Kerbin", "Transfer to the Mun");
            home.Stamp = s => s.LegsAhead = legs;
            var step = home.RunCapture(Ask(100000));
            Assert.True(step.Error == "", step.Error);
            Assert.Equal("circle: the next leg isn't to a moon of Kerbin", Kept(step));
            Assert.InRange(home.Orbit.E, 0, 0.011);

            var alone = TransferRig.Arriving("Duna", 60000, VInf908b, 0, 0, 180, 3000);
            var plain = alone.RunCapture(Ask());
            Assert.Null(Kept(plain));
            Assert.InRange(alone.Orbit.E, 0, 0.011);
        }

        [Fact]
        public void TransferToFromTheKeptEllipseLeavesAtThePlannedPeriapsisAndMeetsIke()
        {
            var rig = IkeNext();
            var capture = rig.RunCapture(Ask());
            var line = Kept(capture);
            Assert.StartsWith("kept", line);
            var planned = Convert.ToDouble(line.Substring(line.IndexOf("UT ", StringComparison.Ordinal) + 3).Replace(",", ""));

            var first = TransferFlight.Next(rig.Snapshot(), new TransferFlightAsk { Target = "Ike", TargetPe = IkePe, StartUt = rig.Ut, TurnTime = 60 });
            Assert.True(first.Error == "", first.Error);
            Assert.InRange(Convert.ToDouble(first.Report["departure_ut"]), planned - 2000, planned + 2000);
            // The capture burn was at a periapsis: the one meant is the next.
            var pe = rig.Orbit.NextPeriapsis(rig.Ut + 600).Value;
            Assert.InRange(Convert.ToDouble(first.Report["departure_ut"]), pe - 2000, pe + 2000);

            var flight = rig.Run(new TransferFlightAsk { Target = "Ike", TargetPe = IkePe, StartUt = rig.Ut, TurnTime = 60 }, 30);
            Assert.True(flight.Error == "", flight.Error + " | " + Flown(rig));
            var path = rig.System.Follow(rig.Body, rig.Orbit, rig.Ut, 3);
            Assert.True(path[0].End == PatchEnd.Encounter && path[0].Next.Name == "Ike", Flown(rig));
            var arrivedPe = path[1].Orbit.PeR - path[1].Body.Radius;
            Assert.True(Math.Abs(arrivedPe - IkePe) <= 1000, arrivedPe.ToString("0") + " m; " + Flown(rig) + "; " + string.Join(", ", flight.Report.Select(kv => kv.Key + "=" + kv.Value)));
        }
    }
}
