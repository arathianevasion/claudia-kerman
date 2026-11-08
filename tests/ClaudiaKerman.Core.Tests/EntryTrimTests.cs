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
    /// <c>reentry</c>'s one late landing trim (#892), through <see cref="BlockCalls.EntryTrim"/>: more than 1,200 s to the air and a predicted miss over 5 km, the corridor and the spare delta-v of #817,
    /// and a trim it can't solve that flies nothing, says why and doesn't fail the block.
    /// </summary>
    public sealed class EntryTrimTests
    {
        private const double Pe = 30000;
        private const double KmPerDeg = 600 * Math.PI / 180;
        /// <summary>Halfway home from the Mun: <c>reentry</c> starts right after <c>return</c>'s correction.</summary>
        private const double ToPeriapsis = 15000;
        private readonly ITestOutputHelper _out;

        public EntryTrimTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>A capsule with one chute: it needn't land on its engines, so nothing is kept back for that.</summary>
        private sealed class ChutedCapsule : IReentryCraft
        {
            private readonly ReentrySim.Capsule _capsule = new ReentrySim.Capsule();
            public double MassKg { get { return _capsule.MassKg; } }
            public double DragArea(double mach, double densitySpeed) { return _capsule.DragArea(mach, densitySpeed); }
            public double LiftArea(double mach, double angleOfAttack) { return _capsule.LiftArea(mach, angleOfAttack); }
            public IList<ReentryChute> Chutes { get { return new[] { new ReentryChute() }; } }
            public IReentryCraft AfterShield { get { return null; } }
        }

        private static IDictionary<string, object> Trim(GameSnapshot snapshot, double toAir, double dvLeft = 80, bool hasNode = false)
        {
            return BlockCalls.EntryTrim(new Dictionary<string, object> { { "to_air", toAir }, { "dv_left", dvLeft }, { "isp", 0.0 }, { "has_node", hasNode } },
                snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ChutedCapsule());
        }

        private static double D(IDictionary<string, object> node, string key)
        {
            return Convert.ToDouble(node[key]);
        }

        /// <summary>A craft on its way home whose natural landing is <paramref name="eastKm"/> km from the aimed target, its aim's periapsis <paramref name="aimedPe"/>.</summary>
        private static GameSnapshot Aimed(double eastKm, double pe = Pe, double? aimedPe = Pe, double toPeriapsis = ToPeriapsis)
        {
            var snapshot = AimedCorrectionTests.Falling(pe, 0, toPeriapsis);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            snapshot.LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = lat, Longitude = lng + eastKm / KmPerDeg, TargetPe = aimedPe };
            return snapshot;
        }

        private static void NoTrim(IDictionary<string, object> fix, string why)
        {
            Assert.Equal("", fix["error"]);
            Assert.Equal(0.0, D(fix, "dv"));
            Assert.Contains(why, (string)fix["aim_note"]);
            var worth = BurnWorth.Call(new Dictionary<string, object> { { "block", "correction" }, { "fix", fix }, { "accel", 10.0 } });
            Assert.Equal(true, worth["skip"]);
        }

        [Theory]
        [InlineData(20)]
        [InlineData(-20)]
        public void More_than_1200_s_out_with_a_20_km_miss_flies_one_trim_inside_the_corridor_and_the_spend_limit(double eastKm)
        {
            var snapshot = Aimed(eastKm);
            var fix = Trim(snapshot, ToPeriapsis - 500);
            _out.WriteLine("{0} km: {1:0.00} m/s, Pe {2:0} m, {3:0.00} km left; {4}", eastKm, D(fix, "dv"), D(fix, "pe_predicted"), D(fix, "error_km"), fix["aim_note"]);
            Assert.Equal("", fix["error"]);
            Assert.True(D(fix, "dv") > 0);
            Assert.InRange(D(fix, "dv"), 0, Math.Min(CorrectionBurn.MostFixDv, 30));
            Assert.InRange(D(fix, "pe_predicted"), Pe - CorrectionBurn.PeCorridor - 100, Pe + CorrectionBurn.PeCorridor + 100);
            Assert.True(D(fix, "error_km") < Math.Abs(eastKm));
            Assert.Equal(false, BurnWorth.Call(new Dictionary<string, object> { { "block", "correction" }, { "fix", fix }, { "accel", 10.0 } })["skip"]);
        }

        /// <summary>The spend limit is #817's: 100 m/s left with later legs needing 40 keeps those back too.</summary>
        [Fact]
        public void The_trim_keeps_back_the_reserve_and_the_later_legs()
        {
            var snapshot = Aimed(400);
            snapshot.LaterLegsDv = 40;
            var fix = Trim(snapshot, ToPeriapsis - 500, 100);
            _out.WriteLine("{0:0.00} m/s; {1}", D(fix, "dv"), fix["aim_note"]);
            Assert.InRange(D(fix, "dv"), 0, 10.5);
        }

        /// <summary>The corridor is the aim's own (#817): a correction left the path at 34 km of an aimed 30, and the trim still ends inside 25 to 35 km.</summary>
        [Theory]
        [InlineData(100)]
        [InlineData(-100)]
        public void The_corridor_is_the_aims_own_not_the_paths(double eastKm)
        {
            var snapshot = Aimed(eastKm, 34000);
            var fix = Trim(snapshot, ToPeriapsis - 500);
            _out.WriteLine("{0} km: {1:0.00} m/s, Pe {2:0} m; {3}", eastKm, D(fix, "dv"), D(fix, "pe_predicted"), fix["aim_note"]);
            Assert.Equal("", fix["error"]);
            Assert.InRange(D(fix, "pe_predicted"), 25000 - 100, 35000 + 100);
        }

        [Fact]
        public void At_1200_s_to_the_air_or_less_there_is_no_trim()
        {
            NoTrim(Trim(Aimed(20), 1200), "s to the air");
            NoTrim(Trim(Aimed(20), 600), "s to the air");
            NoTrim(Trim(Aimed(20), 0), "s to the air");
        }

        [Fact]
        public void A_miss_of_5_km_or_less_gives_no_trim()
        {
            NoTrim(Trim(Aimed(4.9), ToPeriapsis - 500), "km from the target");
            NoTrim(Trim(Aimed(0), ToPeriapsis - 500), "km from the target");
        }

        /// <summary>About 3,000 s before periapsis the along-track lever vanishes, which stops a correction; the trim still has the corridor.</summary>
        [Theory]
        [InlineData(2000)]
        [InlineData(3000)]
        public void A_late_trim_still_has_the_corridor_when_the_along_track_lever_is_gone(double toPeriapsis)
        {
            var fix = Trim(Aimed(20, Pe, Pe, toPeriapsis), toPeriapsis - 500);
            Assert.Equal("", fix["error"]);
            Assert.InRange(D(fix, "dv"), 0.01, 5);
            Assert.InRange(D(fix, "pe_predicted"), Pe - CorrectionBurn.PeCorridor - 100, Pe + CorrectionBurn.PeCorridor + 100);
            Assert.InRange(D(fix, "error_km"), 0, 5);
        }

        [Fact]
        public void No_target_gives_no_trim()
        {
            var snapshot = Aimed(20);
            snapshot.LandingTarget = null;
            NoTrim(Trim(snapshot, ToPeriapsis - 500), "no landing target");
        }

        [Fact]
        public void A_target_on_another_body_gives_no_trim()
        {
            var snapshot = Aimed(20);
            snapshot.LandingTarget.Body = "Duna";
            NoTrim(Trim(snapshot, ToPeriapsis - 500), "the landing target is on Duna");
        }

        [Fact]
        public void A_maneuver_node_already_planned_gives_no_trim()
        {
            NoTrim(Trim(Aimed(20), ToPeriapsis - 500, 80, true), "a maneuver node is already planned");
        }

        /// <summary>Unsolved (#805): a periapsis under the ground can't be held, so the solve refuses it; the trim flies nothing, says why and the block goes on.</summary>
        [Fact]
        public void Unsolved_flies_nothing_logs_a_reason_and_is_no_failure()
        {
            NoTrim(Trim(Aimed(50, -10000, null), ToPeriapsis - 500), "found no correction");
        }

        [Fact]
        public void An_aiming_call_gives_the_target_and_one_with_no_landing_gives_none()
        {
            var target = BlockCalls.LandingTargetOf(new Dictionary<string, object> { { "LAT", -0.1 }, { "lng", -74.5 }, { "target_pe", 30000.0 } }, "Kerbin");
            Assert.Equal("Kerbin", target.Body);
            Assert.Equal(-0.1, target.Latitude);
            Assert.Equal(-74.5, target.Longitude);
            Assert.Equal(30000.0, target.TargetPe);
            Assert.Null(BlockCalls.LandingTargetOf(new Dictionary<string, object> { { "lat", 1.0 } }, "Kerbin"));
            Assert.Null(BlockCalls.LandingTargetOf(new Dictionary<string, object> { { "target_pe", 30000.0 } }, "Kerbin"));
            Assert.Null(BlockCalls.LandingTargetOf(new Dictionary<string, object> { { "lat", 0.0 }, { "lng", 0.0 } }, ""));
        }

        [Fact]
        public void A_reentry_clears_the_recorded_target_so_the_next_one_has_none()
        {
            BlockTestAim.Record(new LandingTarget { Body = "Kerbin", Latitude = 1, Longitude = 2 });
            Assert.NotNull(BlockTestAim.Landing);
            BlockTestAim.Clear();
            Assert.Null(BlockTestAim.Landing);
        }

        [Fact]
        public void The_Mission_save_round_trips_the_target()
        {
            var saved = new MissionSaveState { Id = "m1", LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = -0.1025, Longitude = -74.575, TargetPe = 30000 } };
            var read = MissionSaveState.FromValues(saved.ToValues()).LandingTarget;
            Assert.Equal("Kerbin", read.Body);
            Assert.Equal(-0.1025, read.Latitude);
            Assert.Equal(-74.575, read.Longitude);
            Assert.Equal(30000.0, read.TargetPe);
            var noPe = MissionSaveState.FromValues(new MissionSaveState { Id = "m2", LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = 1, Longitude = 2 } }.ToValues()).LandingTarget;
            Assert.Null(noPe.TargetPe);
            Assert.Null(MissionSaveState.FromValues(new MissionSaveState { Id = "m3" }.ToValues()).LandingTarget);
        }
    }
}
