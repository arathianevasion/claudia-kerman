// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #923: a chute's opening load is judged by nothing, but when it comes from <c>reentry</c>'s 1,500 m last resort over 250 m/s (a craft too dense to slow for its chutes) the prediction says so,
    /// with the speed and the g at accepted steps, for the log. <see cref="ReentryPrediction.PeakG"/> and its 8 g limit are untouched.
    /// </summary>
    public sealed class ChuteOpeningLoadTests
    {
        private readonly ITestOutputHelper _out;

        public ChuteOpeningLoadTests(ITestOutputHelper output) { _out = output; }

        private sealed class Stack : IReentryCraft
        {
            private readonly ReentrySim.Capsule _shape = new ReentrySim.Capsule();
            public double Area = 1.16;
            public bool Chute = true;
            public double MassKg { get { return 6204; } }
            public double DragArea(double mach, double densitySpeed) { return _shape.DragArea(mach, densitySpeed) * Area / _shape.DragArea(2, 1000); }
            public double LiftArea(double mach, double angleOfAttack) { return 0; }
            public IList<ReentryChute> Chutes { get { return Chute ? new[] { ReentrySim.Mk16() } : new ReentryChute[0]; } }
            public IReentryCraft AfterShield { get { return null; } }
        }

        private static GameSnapshot Logged()
        {
            const double apR = 600000 + 10885000;
            const double toPeriapsis = 15000;
            var snapshot = AimedCorrectionTests.Falling(30000, 0, toPeriapsis);
            var peR = 600000 + 30000.0;
            var sma = (apR + peR) / 2;
            var kerbin = snapshot.Bodies.Find(b => b.Name == "Kerbin");
            snapshot.Orbit.SemiMajorAxis = sma;
            snapshot.Orbit.Eccentricity = (apR - peR) / (apR + peR);
            snapshot.Orbit.Apoapsis = apR - 600000;
            snapshot.Orbit.MeanAnomalyAtEpoch = 2 * Math.PI - toPeriapsis * Math.Sqrt(kerbin.Gm / (sma * sma * sma));
            return snapshot;
        }

        private static ReentryPrediction Fly(Stack craft)
        {
            var snapshot = Logged();
            string problem;
            var kerbin = snapshot.Bodies.Find(b => b.Name == "Kerbin");
            var frame = BodyFrame.Read(kerbin, new AimedCorrectionTests.FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            var o = snapshot.Orbit;
            var orbit = Conic.FromElements(kerbin.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
            var landed = ReentryPredictor.Predict(kerbin, orbit, snapshot.Ut, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
            Assert.Null(landed.Error);
            return landed;
        }

        [Fact]
        public void An_over_dense_craft_reaching_1500_m_over_250_ms_reports_the_last_resort_opening_and_its_g()
        {
            var landed = Fly(new Stack());
            _out.WriteLine("last resort {0:0.0} m/s, opening {1:0.00} g, peak {2:0.00} g", landed.LastResortSpeed, landed.OpeningPeakG, landed.PeakG);
            Assert.InRange(landed.LastResortSpeed, 250.5, 400);
            Assert.InRange(landed.OpeningPeakG, 10, 30);
            Assert.InRange(landed.PeakG, 2.0, 2.4);
            Assert.Equal(Fly(new Stack { Chute = false }).PeakG, landed.PeakG, 2);
        }

        [Fact]
        public void A_craft_that_slows_below_250_ms_first_reports_no_last_resort()
        {
            var landed = Fly(new Stack { Area = 12 });
            _out.WriteLine("last resort {0:0.0} m/s, opening {1:0.00} g, peak {2:0.00} g", landed.LastResortSpeed, landed.OpeningPeakG, landed.PeakG);
            Assert.Equal(0, landed.LastResortSpeed);
            Assert.True(landed.ChuteUt > 0, "its chute opened");
            Assert.Equal(Fly(new Stack { Area = 12, Chute = false }).PeakG, landed.PeakG, 2);
            Assert.Equal("", BlockCalls.LastResortNote(landed));
        }

        [Fact]
        public void The_log_note_says_the_speed_and_the_g()
        {
            var note = BlockCalls.LastResortNote(Fly(new Stack()));
            Assert.Matches(@"^; chute last resort: armed at \d+ m/s above 250 m/s by the 1500 m rule, opening peak \d+\.\d g \(not in PeakG\)$", note);
        }
    }
}
