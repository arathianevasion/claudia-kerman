// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>plan_transfer</c> (#448) through the Planning Tools entry point: the same search as <c>transfer_to</c>'s block call, a body
    /// met where it is and an orbit reached anywhere on it, on the hand-built 100 km orbit round Kerbin with the Mun.
    /// </summary>
    public sealed class PlanTransferTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;

        private readonly PlanningTools _tools = PlanningTools.Default();

        private static GameSnapshot Circular()
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = KerbinR + 100000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000,
                MeanAnomalyAtEpoch = 1.7,
            });
            return snapshot;
        }

        private JsonElement Run(string input, GameSnapshot snapshot = null)
        {
            var outcome = _tools.Run("plan_transfer", input, snapshot ?? Circular(), new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            Assert.InRange(outcome.Json.Length, 1, PlanningTools.MaxResultChars);
            return JsonDocument.Parse(outcome.Json).RootElement.Clone();
        }

        private string Error(string input)
        {
            var outcome = _tools.Run("plan_transfer", input, Circular(), new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            return JsonDocument.Parse(outcome.Json).RootElement.GetProperty("error").GetString();
        }

        [Fact]
        public void To_the_Mun_it_is_transfer_to_s_ejection_with_the_capture_beside_it()
        {
            var snapshot = Circular();
            var result = Run("{\"target\":\"Mun\",\"target_pe\":30000}", snapshot);
            var block = BlockCalls.EjectionBurn(new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", 30000.0 }, { "earliest_ut", 0.0 } }, snapshot);
            Assert.Equal("rendezvous", result.GetProperty("mode").GetString());
            Assert.Equal(Math.Round((double)block["dv"], 1), result.GetProperty("departure_dv").GetDouble());
            Assert.Equal(Math.Round((double)block["arrival_dv"], 1), result.GetProperty("arrival_dv").GetDouble());
            Assert.Equal(Math.Round((double)block["prograde"], 2), result.GetProperty("dv").GetProperty("prograde").GetDouble());
            Assert.Equal(Math.Round((double)block["ut"], 1), result.GetProperty("depart_ut").GetDouble());
            Assert.InRange(result.GetProperty("target_pe_actual").GetDouble(), 29990, 30010);
            Assert.InRange(result.GetProperty("total_dv").GetDouble(), 1050, 1150);
            Assert.StartsWith("in ", result.GetProperty("depart_in").GetString());
            Assert.True(result.GetProperty("arrive_ut").GetDouble() > result.GetProperty("encounter_ut").GetDouble());
        }

        [Fact]
        public void To_a_200_km_orbit_it_is_the_Hohmann_transfer()
        {
            var result = Run("{\"orbit\":{\"ap\":200000,\"pe\":200000}}");
            Assert.Equal("intercept", result.GetProperty("mode").GetString());
            Assert.InRange(result.GetProperty("departure_dv").GetDouble(), 72.7, 74.7);
            Assert.InRange(result.GetProperty("arrival_dv").GetDouble(), 70.2, 72.2);
            Assert.False(result.TryGetProperty("target_pe_actual", out _));
        }

        [Fact]
        public void Capture_false_finds_the_cheapest_departure_alone()
        {
            var both = Run("{\"orbit\":{\"ap\":300000,\"pe\":300000,\"inc\":10,\"lan\":90}}");
            var alone = Run("{\"orbit\":{\"ap\":300000,\"pe\":300000,\"inc\":10,\"lan\":90},\"capture\":false}");
            Assert.False(alone.GetProperty("capture").GetBoolean());
            Assert.True(alone.GetProperty("departure_dv").GetDouble() < both.GetProperty("departure_dv").GetDouble());
            Assert.True(both.GetProperty("total_dv").GetDouble() < alone.GetProperty("total_dv").GetDouble());
        }

        [Fact]
        public void To_Minmus_from_a107_s_equatorial_orbit_capture_true_and_false_both_find_a_transfer()
        {
            // a107 (#611): with capture plan_transfer found none, without it 1160 m/s out. Minmus is 6° above the craft's plane
            // when it arrives: since #619 the departure stays in plane, about 920 m/s, and a plane change where the arc is slow
            // takes about 40 m/s, in the result alone (plane_ut, plane_in, plane_dv), counted in total_dv.
            var snapshot = TransferTests.A107();
            var both = Run("{\"target\":\"Minmus\",\"target_pe\":30000}", snapshot);
            var alone = Run("{\"target\":\"Minmus\",\"target_pe\":30000,\"capture\":false}", snapshot);
            foreach (var result in new[] { both, alone })
            {
                Assert.InRange(result.GetProperty("target_pe_actual").GetDouble(), 29900, 30100);
                Assert.InRange(result.GetProperty("departure_dv").GetDouble(), 900, 950);
                Assert.InRange(result.GetProperty("plane_dv").GetDouble(), 20, 80);
                Assert.True(result.GetProperty("plane_ut").GetDouble() > result.GetProperty("depart_ut").GetDouble());
                Assert.StartsWith("in ", result.GetProperty("plane_in").GetString());
                Assert.Equal(result.GetProperty("departure_dv").GetDouble() + result.GetProperty("plane_dv").GetDouble() + result.GetProperty("arrival_dv").GetDouble(),
                    result.GetProperty("total_dv").GetDouble(), 1);
                Assert.False(result.TryGetProperty("note", out _));
            }
            Assert.True(alone.GetProperty("departure_dv").GetDouble() + alone.GetProperty("plane_dv").GetDouble()
                <= both.GetProperty("departure_dv").GetDouble() + both.GetProperty("plane_dv").GetDouble() + 1);
            // An in-plane target's result has none of them.
            Assert.False(Run("{\"target\":\"Mun\",\"target_pe\":30000}", snapshot).TryGetProperty("plane_dv", out _));
        }

        [Fact]
        public void When_the_cheapest_one_burn_window_can_t_be_aimed_the_broken_plane_is_given_with_no_note()
        {
            // The Mun across the cheapest one-burn window's arc: before #619 the next window, noted ("not the cheapest window:
            // ..."; EJECTIONBURN with plane_change false still finds it, TransferTests); now the broken plane in plane, cheaper
            // than either, which nothing was skipped for.
            var snapshot = TransferTests.A107MunInTheWay(2429559.1);
            var result = Run("{\"target\":\"Minmus\",\"target_pe\":30000}", snapshot);
            Assert.InRange(result.GetProperty("target_pe_actual").GetDouble(), 29900, 30100);
            Assert.True(result.TryGetProperty("plane_dv", out _));
            Assert.False(result.TryGetProperty("note", out _));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void It_runs_under_100_ms()
        {
            Timing.Within(100, () => Run("{\"target\":\"Mun\",\"target_pe\":30000}"));
            Timing.Within(100, () => Run("{\"orbit\":{\"ap\":300000,\"pe\":300000,\"inc\":10,\"lan\":90}}"));
        }

        [Theory]
        [InlineData("{}", "give target (a body) or orbit, one of them")]
        [InlineData("{\"target\":\"Mun\",\"target_pe\":30000,\"orbit\":{\"ap\":1,\"pe\":1}}", "give target (a body) or orbit, one of them")]
        [InlineData("{\"target\":\"Mun\"}", "target needs target_pe, the periapsis altitude to arrive at in metres")]
        [InlineData("{\"orbit\":{\"ap\":200000,\"pe\":200000},\"target_pe\":5}", "target_pe is for a target body; an orbit is reached at its own altitude")]
        [InlineData("{\"orbit\":{\"ap\":100000,\"pe\":200000}}", "orbit.ap must be at least orbit.pe")]
        [InlineData("{\"orbit\":{\"ap\":200000}}", "input.orbit.pe is required")]
        [InlineData("{\"target\":\"Sun\",\"target_pe\":30000}", "found no transfer: Sun does not orbit Kerbin, the body the craft orbits")]
        public void A_target_it_can_t_reach_is_an_error_with_the_reason(string input, string error)
        {
            Assert.Equal(error, Error(input));
        }

        [Fact]
        public void It_is_offered_on_Mission_turns_only_and_names_its_block()
        {
            Assert.Contains("plan_transfer", _tools.Definitions(ToolOffer.Mission).Select(t => t.Name));
            Assert.DoesNotContain("plan_transfer", _tools.Definitions(ToolOffer.CraftCheck).Select(t => t.Name));
            var description = _tools.Definitions(ToolOffer.Mission).Single(t => t.Name == "plan_transfer").Description;
            Assert.Contains("Its block is transfer_to", description);
            Assert.InRange(description.Length, 1, 900);
        }

        [Fact]
        public void From_a_30_km_Mun_orbit_Minmus_gets_its_windows_with_the_ejection_transfer_to_flies()
        {
            // #734: a body sharing the parent of the body the craft orbits is planned through Transfer Windows, as transfer_to
            // flies it (the transfer_to_minmus_from_mun block test: the block test brackets its ejection 200 to 350 m/s; about 239 here).
            var snapshot = TransferRig.Circular("Mun", 30000, 0).Snapshot();
            var result = Run("{\"target\":\"Minmus\",\"target_pe\":30000}", snapshot);
            Assert.Equal("Minmus", result.GetProperty("target").GetString());
            foreach (var name in new[] { "soonest", "optimal" })
                Assert.InRange(result.GetProperty(name).GetProperty("ejection_dv").GetDouble(), 200, 350);
            Assert.True(result.TryGetProperty("pick", out _));
        }
    }
}
