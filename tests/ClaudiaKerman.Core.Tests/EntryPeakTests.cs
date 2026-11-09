// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #921: <c>return</c>'s aimed Course Correction refused its entry at 114.9 g, then flew the plain one at 114.1 g, about 1 km of periapsis away, with no check. A figure that doesn't move with the
    /// periapsis is no entry's: it was the parachute's opening, read off the integrator's trial states (230 g on this model, where the true opening peaks about 17 g and the entry itself 2.3 g). The limit
    /// judges the entry, so its figure is the air's pull on the craft, and every burn a landing's aim leaves to fly meets it, the plain one too.
    /// </summary>
    public sealed class EntryPeakTests
    {
        private const double ApR = 600000 + 10885000;
        private const double Halfway = 15000;
        private const double KmPerDeg = 600 * Math.PI / 180;
        private const double MassKg = 6204;

        /// <summary>The logged craft: 6.204 t, 1.16 m² of drag area at Mach 2 (the research's drag curves, scaled to it), no Entry Shield, its one Mk16 parachute still to open.</summary>
        private sealed class LoggedStack : IReentryCraft
        {
            private readonly ReentrySim.Capsule _shape = new ReentrySim.Capsule();
            public bool Chute = true;
            public double MassKg { get; set; } = EntryPeakTests.MassKg;
            public double DragArea(double mach, double densitySpeed) { return _shape.DragArea(mach, densitySpeed) * 1.16 / _shape.DragArea(2, 1000); }
            public double LiftArea(double mach, double angleOfAttack) { return 0; }
            public IList<ReentryChute> Chutes { get { return Chute ? new[] { ReentrySim.Mk16() } : new ReentryChute[0]; } }
            public IReentryCraft AfterShield { get { return null; } }
        }

        /// <summary>The logged orbit, Ap 10,885 km and the periapsis given, at <paramref name="toPeriapsis"/> s before it.</summary>
        private static GameSnapshot Logged(double pe, double toPeriapsis = Halfway)
        {
            var snapshot = AimedCorrectionTests.Falling(pe, 0, toPeriapsis);
            var peR = 600000 + pe;
            var sma = (ApR + peR) / 2;
            var kerbin = snapshot.Bodies.Find(b => b.Name == "Kerbin");
            snapshot.Orbit.SemiMajorAxis = sma;
            snapshot.Orbit.Eccentricity = (ApR - peR) / (ApR + peR);
            snapshot.Orbit.Apoapsis = ApR - 600000;
            snapshot.Orbit.MeanAnomalyAtEpoch = 2 * Math.PI - toPeriapsis * Math.Sqrt(kerbin.Gm / (sma * sma * sma));
            return snapshot;
        }

        private static Conic OrbitOf(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            return Conic.FromElements(snapshot.Bodies.Find(b => b.Name == "Kerbin").Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        private static ReentryPrediction Fly(GameSnapshot snapshot, IReentryCraft craft)
        {
            string problem;
            var kerbin = snapshot.Bodies.Find(b => b.Name == "Kerbin");
            var frame = BodyFrame.Read(kerbin, new AimedCorrectionTests.FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            var landed = ReentryPredictor.Predict(kerbin, OrbitOf(snapshot), snapshot.Ut, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
            Assert.Null(landed.Error);
            return landed;
        }

        private static IDictionary<string, object> Aim(GameSnapshot snapshot, double lat, double lng, IReentryCraft craft, double targetPe = 30000, double peTolerance = 1000)
        {
            return BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", targetPe }, { "earliest_ut", snapshot.Ut + 300 }, { "latest_ut", snapshot.Ut + 300 }, { "lat", lat }, { "lng", lng }, { "dv_left", 80.0 }, { "pe_tolerance", peTolerance },
            }, snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), craft);
        }

        private static double D(IDictionary<string, object> node, string key)
        {
            return Convert.ToDouble(node[key]);
        }

        /// <summary>The logged case: both periapses (aimed 30,001 m, Late Trim 30,083 m) at the logged 6.204 t and the EntryCraft's 6.75 t pull about 2.3 g, what a fine fixed-step integration of the entry gives at 6.204 t, and the parachute's opening is no part of it.</summary>
        [Theory]
        [InlineData(30001, 6204)]
        [InlineData(30083, 6204)]
        [InlineData(30001, 6750)]
        [InlineData(30083, 6750)]
        public void The_logged_entry_is_judged_by_the_airs_pull_not_the_parachutes_opening(double pe, double massKg)
        {
            var landed = Fly(Logged(pe), new LoggedStack { MassKg = massKg });
            Assert.InRange(landed.PeakG, 2.0, 2.4);
            Assert.Equal(Fly(Logged(pe), new LoggedStack { MassKg = massKg, Chute = false }).PeakG, landed.PeakG, 2);
        }

        /// <summary>With the figure right, the aimed burn the log refused flies: a burn that closes the landing, with no word of the entry's g.</summary>
        [Fact]
        public void The_aimed_burn_the_log_refused_flies()
        {
            var snapshot = Logged(30080);
            var craft = new LoggedStack();
            var natural = Fly(snapshot, craft);
            var node = Aim(snapshot, natural.Latitude, natural.Longitude + 40 / KmPerDeg, craft);
            Assert.Equal("", node["error"]);
            Assert.DoesNotContain("pulls", (string)node["aim_note"]);
            Assert.False(((string)node["aim_note"]).StartsWith("plain burn", StringComparison.Ordinal), (string)node["aim_note"]);
            Assert.InRange(D(node, "error_km"), 0, 5);
        }

        /// <summary>An entry that truly pulls over the limit across the whole corridor (a very draggy craft): the plain burn the aim falls back on is judged too, and <c>return</c> fails with the g figure.</summary>
        [Fact]
        public void A_plain_entry_truly_over_the_limit_fails_with_the_g_figure()
        {
            var snapshot = Logged(30000);
            var craft = new ReentrySim.Capsule { Scale = 400 };
            var natural = Fly(snapshot, craft);
            Assert.True(natural.PeakG > CorrectionBurn.MostEntryG, "the case is an entry over the limit: " + natural.PeakG);
            var node = Aim(snapshot, natural.Latitude, natural.Longitude + 40 / KmPerDeg, craft);
            Assert.Matches(new Regex(@"^found no correction: the burn's entry pulls [\d.]+ g, over 8; without it the entry pulls [\d.]+ g, over 8$"), (string)node["error"]);
            Assert.Equal(0.0, D(node, "dv"));
        }

        /// <summary>A burn over the limit on a path that is under it (a target_pe of 20 km, held, from a path at 30 km): no burn flies, the g figure is said, and <c>return</c> carries on along the path as it is.</summary>
        [Fact]
        public void A_burn_over_the_limit_on_a_path_under_it_flies_nothing_and_goes_on()
        {
            var snapshot = Logged(30000);
            var craft = new ReentrySim.Capsule { Scale = 120 };
            var natural = Fly(snapshot, craft);
            Assert.True(natural.PeakG <= CorrectionBurn.MostEntryG, "the path as it is is under the limit: " + natural.PeakG);
            var node = Aim(snapshot, natural.Latitude, natural.Longitude + 40 / KmPerDeg, craft, 20000, 6000);
            Assert.Equal("", node["error"]);
            Assert.Equal(0.0, D(node, "dv"));
            Assert.Matches(new Regex(@"^no correction flown: the burn's entry pulls [\d.]+ g, over 8; "), (string)node["aim_note"]);
        }

        /// <summary>The same in <c>reentry</c>'s Late Trim (ENTRYTRIM goes through the same solve): no trim, the g figure said, and the block goes on, the entry being already on its way.</summary>
        [Fact]
        public void A_late_trim_on_an_entry_over_the_limit_flies_nothing_and_says_the_g()
        {
            var snapshot = Logged(30000);
            var craft = new ReentrySim.Capsule { Scale = 400 };
            var natural = Fly(snapshot, craft);
            snapshot.LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = natural.Latitude, Longitude = natural.Longitude + 40 / KmPerDeg, TargetPe = 30000 };
            var fix = BlockCalls.EntryTrim(new Dictionary<string, object> { { "to_air", 2000.0 }, { "dv_left", 80.0 }, { "isp", 0.0 }, { "has_node", false } },
                snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), craft);
            Assert.Equal("", fix["error"]);
            Assert.Equal(0.0, D(fix, "dv"));
            Assert.Matches(new Regex(@"^no trim: .*entry pulls [\d.]+ g, over 8"), (string)fix["aim_note"]);
        }
    }
}
