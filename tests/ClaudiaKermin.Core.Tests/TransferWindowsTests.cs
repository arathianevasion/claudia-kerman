// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Transfer Windows to a planet (#458): the heliocentric Lambert grid's Soonest and Optimal, against the known Kerbin to
    /// Duna and Kerbin to Eve windows, on the stock body table of a recorded Game Snapshot with the craft in a 100 km circular
    /// orbit at UT 0 (the start of a new game); and plan_transfer's answer for a planet.
    /// </summary>
    public sealed class TransferWindowsTests
    {
        /// <summary>A Kerbin day, s: the game's calendar counts 6 h days.</summary>
        private const double Day = 21600;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>The stock body table, with the craft in a 100 km circular orbit of Kerbin at <paramref name="ut"/>.</summary>
        private static GameSnapshot Parked(double ut = 0)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Ut = ut;
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = 700000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000, Epoch = ut,
            };
            return snapshot;
        }

        private static WindowSearch Find(string target, GameSnapshot snapshot = null, bool capture = true, int margin = 15)
        {
            var found = TransferWindows.Find(snapshot ?? Parked(), target, 100000, capture, margin);
            Assert.True(found.Error == null, found.Error);
            return found;
        }

        private JsonElement Run(string input, GameSnapshot snapshot)
        {
            var outcome = _tools.Run("plan_transfer", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            Assert.InRange(outcome.Json.Length, 1, PlanningTools.MaxResultChars);
            return JsonDocument.Parse(outcome.Json).RootElement.Clone();
        }

        // ---- Accuracy ----

        // The first Duna window of a new game is Year 1, Day 236 (235 days in), at about 1,030 m/s of ejection from low Kerbin
        // orbit (the community's launch-window planners and the KSP wiki's tables). The planners date the window by the total
        // with the insertion burn, so the date is checked with the capture costed; the ejection with it and without.
        [Fact]
        public void Kerbin_to_Duna_is_the_known_window_with_the_known_ejection()
        {
            foreach (var capture in new[] { true, false })
            {
                var optimal = Find("Duna", capture: capture).Optimal;
                Assert.InRange(optimal.EjectionDv, 1030 * 0.95, 1030 * 1.05);
                Assert.InRange((optimal.ArrivalUt - optimal.DepartureUt) / Day, 200, 350);
                if (capture) Assert.InRange(optimal.DepartureUt / Day, 235 - 5, 235 + 5);
            }
        }

        // Kerbin to Eve: the departure-only window's ejection is about 1,030 m/s too. Eve's heavy capture at 100 km (about
        // 1,500 m/s) moves the cheapest total to a slower arrival bought with more ejection, so the ejection check is on the
        // departure-only figure, as the planners quote it.
        [Fact]
        public void Kerbin_to_Eve_departing_alone_has_the_known_ejection()
        {
            var optimal = Find("Eve", capture: false).Optimal;
            Assert.InRange(optimal.EjectionDv, 1030 * 0.95, 1030 * 1.05);
            Assert.Equal(optimal.EjectionDv, optimal.TotalDv);
        }

        // The date check needs an answer known independently of any planner: with Eve's 2.1° tilt and 0.01 eccentricity taken
        // out, the cheapest departure is the Hohmann transfer, at the date the phase angle comes round and at the Hohmann ejection.
        [Fact]
        public void Kerbin_to_a_flat_circular_Eve_is_the_Hohmann_window()
        {
            var snapshot = Parked();
            var eve = snapshot.Bodies.First(b => b.Name == "Eve");
            var kerbin = snapshot.Bodies.First(b => b.Name == "Kerbin");
            var sun = snapshot.Bodies.First(b => b.Name == "Sun");
            eve.Inclination = 0;
            eve.Eccentricity = 0;

            double r1 = kerbin.Sma, r2 = eve.Sma, mu = sun.Gm;
            var a = (r1 + r2) / 2;
            var hohmann = Math.PI * Math.Sqrt(a * a * a / mu);
            double nKerbin = Math.Sqrt(mu / (r1 * r1 * r1)), nEve = Math.Sqrt(mu / (r2 * r2 * r2));
            // Eve must lead Kerbin by pi - nEve * hohmann at departure; it gains nEve - nKerbin rad/s from its lead at UT 0.
            var lead0 = (eve.Lan + eve.ArgumentOfPeriapsis) * Math.PI / 180 + eve.MeanAnomalyAtEpoch - kerbin.MeanAnomalyAtEpoch;
            var wanted = Math.PI - nEve * hohmann;
            var gain = ((wanted - lead0) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            var date = gain / (nEve - nKerbin);
            var vInf = Math.Sqrt(mu / r1) * (1 - Math.Sqrt(2 * r2 / (r1 + r2)));
            var ejection = Math.Sqrt(vInf * vInf + 2 * kerbin.Gm / 700000) - Math.Sqrt(kerbin.Gm / 700000);

            var optimal = Find("Eve", snapshot, capture: false).Optimal;
            Assert.InRange(optimal.DepartureUt / Day, date / Day - 3, date / Day + 3);
            Assert.InRange(optimal.EjectionDv, ejection * 0.99, ejection * 1.01);
            Assert.InRange(optimal.ArrivalUt - optimal.DepartureUt, hohmann * 0.95, hohmann * 1.05);
        }

        // ---- Soonest, Optimal and the setting ----

        [Fact]
        public void Soonest_is_the_first_departure_within_the_margin_of_optimal()
        {
            var snapshot = Parked();
            string error;
            var problem = TransferWindows.Problem.Of(snapshot, "Duna", 100000, true, out error);
            var found = TransferWindows.Search(problem, 15);
            var budget = found.Optimal.TotalDv * 1.15;
            Assert.True(found.Soonest.DepartureUt < found.Optimal.DepartureUt - 10 * Day, "soonest " + found.Soonest.DepartureUt / Day + " d");
            Assert.InRange(found.Soonest.TotalDv, found.Optimal.TotalDv, budget + 0.01);
            // A day earlier, no flight time fits the budget.
            var earlier = found.Soonest.DepartureUt - Day;
            for (var tof = 100 * Day; tof <= 450 * Day; tof += Day / 4)
                Assert.True(problem.Cost(earlier, tof).TotalDv > budget, "a day before soonest fits at a flight of " + tof / Day + " d");
        }

        [Fact]
        public void A_window_already_open_is_soonest_now()
        {
            var optimal = Find("Duna").Optimal;
            var found = Find("Duna", Parked(optimal.DepartureUt - 2 * Day));
            Assert.Equal(optimal.DepartureUt - 2 * Day, found.Soonest.DepartureUt, 3);
        }

        [Fact]
        public void The_setting_picks_the_window()
        {
            var cheaper = new WindowSearch { Soonest = new TransferWindow { TotalDv = 1000 }, Optimal = new TransferWindow { TotalDv = 900 } };
            Assert.Same(cheaper.Soonest, TransferWindows.Pick(cheaper, TimeVersusDeltaV.Fastest));
            Assert.Same(cheaper.Optimal, TransferWindows.Pick(cheaper, TimeVersusDeltaV.MostEfficient));
            // Balanced: Optimal saving 10% of Soonest's total is enough; 9.9% is not.
            Assert.Same(cheaper.Optimal, TransferWindows.Pick(cheaper, TimeVersusDeltaV.Balanced));
            var close = new WindowSearch { Soonest = new TransferWindow { TotalDv = 1000 }, Optimal = new TransferWindow { TotalDv = 901 } };
            Assert.Same(close.Soonest, TransferWindows.Pick(close, TimeVersusDeltaV.Balanced));
        }

        [Fact]
        public void Without_capture_the_figure_is_the_departure_alone()
        {
            var found = Find("Duna", capture: false);
            foreach (var w in new[] { found.Soonest, found.Optimal })
            {
                Assert.Equal(0, w.CaptureDv);
                Assert.Equal(w.EjectionDv, w.TotalDv);
                Assert.True(w.ArrivalVInf > 0);
            }
            var captured = Find("Duna").Optimal;
            Assert.True(captured.CaptureDv > 0);
            Assert.Equal(captured.EjectionDv + captured.CaptureDv, captured.TotalDv, 6);
            // capture_dv is the burn into a circular orbit at target_pe, not the arrival excess speed.
            var duna = Parked().Bodies.First(b => b.Name == "Duna");
            var r = duna.Radius + 100000;
            Assert.Equal(Math.Sqrt(captured.ArrivalVInf * captured.ArrivalVInf + 2 * duna.Gm / r) - Math.Sqrt(duna.Gm / r), captured.CaptureDv, 6);
        }

        // ---- The source ----

        // The search works on what Problem.Of copied: the Game Snapshot can change under it (a tool thread, ToolStep.cs) and the answer doesn't.
        [Fact]
        public void The_search_never_touches_the_snapshot_after_it_starts()
        {
            var expected = Find("Duna");
            var snapshot = Parked();
            string error;
            var problem = TransferWindows.Problem.Of(snapshot, "Duna", 100000, true, out error);
            Assert.Null(error);
            foreach (var b in snapshot.Bodies)
            {
                b.Sma = double.NaN;
                b.Gm = double.NaN;
                b.MeanAnomalyAtEpoch = double.NaN;
            }
            snapshot.Bodies.Clear();
            snapshot.Orbit = null;
            snapshot.Ut = double.NaN;
            var found = TransferWindows.Search(problem, 15);
            Assert.Null(found.Error);
            Assert.Equal(expected.Optimal.DepartureUt, found.Optimal.DepartureUt);
            Assert.Equal(expected.Optimal.TotalDv, found.Optimal.TotalDv);
            Assert.Equal(expected.Soonest.DepartureUt, found.Soonest.DepartureUt);
        }

        // ---- Speed ----

        [Fact]
        [Trait("Category", "Timing")]
        public void A_search_is_well_inside_the_tool_cap()
        {
            Assert.True(TransferWindows.BudgetMs * 10 <= ToolStep.CallTimeout.TotalMilliseconds);
            var snapshot = Parked();
            Timing.Within(TransferWindows.BudgetMs, () => Assert.Null(TransferWindows.Find(snapshot, "Duna", 100000, true, 15).Error));
        }

        // ---- plan_transfer ----

        [Fact]
        public void Plan_transfer_to_Duna_gives_soonest_optimal_and_the_setting_s_pick()
        {
            var snapshot = Parked();
            var found = Find("Duna");
            var result = Run("{\"target\":\"Duna\",\"target_pe\":100000}", snapshot);
            Assert.Equal("Duna", result.GetProperty("target").GetString());
            Assert.True(result.GetProperty("capture").GetBoolean());
            Assert.Equal(100000, result.GetProperty("parking_pe").GetDouble());
            Assert.False(result.TryGetProperty("parking_note", out _));
            foreach (var name in new[] { "soonest", "optimal" })
            {
                var w = result.GetProperty(name);
                foreach (var field in new[] { "depart_ut", "depart_in", "arrive_ut", "ejection_dv", "arrival_v_inf", "capture_dv", "total_dv" })
                    Assert.True(w.TryGetProperty(field, out _), name + " has no " + field);
            }
            Assert.Equal(Math.Round(found.Optimal.TotalDv, 1), result.GetProperty("optimal").GetProperty("total_dv").GetDouble());
            Assert.Equal(Math.Round(found.Soonest.DepartureUt, 1), result.GetProperty("soonest").GetProperty("depart_ut").GetDouble());
            // Balanced (the default): Optimal saves over 10% of Soonest here.
            Assert.Equal("Balanced", result.GetProperty("setting").GetString());
            Assert.Equal("optimal", result.GetProperty("pick").GetString());

            snapshot.TimeVersusDeltaV = TimeVersusDeltaV.Fastest;
            Assert.Equal("soonest", Run("{\"target\":\"Duna\",\"target_pe\":100000}", snapshot).GetProperty("pick").GetString());

            var alone = Run("{\"target\":\"Duna\",\"target_pe\":100000,\"capture\":false}", Parked());
            Assert.False(alone.GetProperty("optimal").TryGetProperty("capture_dv", out _));
            Assert.Equal(alone.GetProperty("optimal").GetProperty("ejection_dv").GetDouble(), alone.GetProperty("optimal").GetProperty("total_dv").GetDouble());
        }

        [Fact]
        public void From_the_pad_the_parking_orbit_is_the_low_orbit_and_says_so()
        {
            var snapshot = Parked();
            snapshot.Orbit = null;
            snapshot.Situation = "PRELAUNCH";
            var result = Run("{\"target\":\"Duna\",\"target_pe\":100000}", snapshot);
            Assert.Equal(80000, result.GetProperty("parking_pe").GetDouble());
            Assert.Contains("assumed", result.GetProperty("parking_note").GetString());
        }

        [Fact]
        public void The_home_planet_and_another_planet_s_moon_are_not_windows()
        {
            Assert.False(TransferWindows.IsPlanetTransfer(Parked(), "Kerbin"));
            Assert.False(TransferWindows.IsPlanetTransfer(Parked(), "Ike"));
            Assert.False(TransferWindows.IsPlanetTransfer(Parked(), "Sun"));
            Assert.False(TransferWindows.IsPlanetTransfer(Parked(), "Mun"));
            Assert.True(TransferWindows.IsPlanetTransfer(Parked(), "Jool"));
            Assert.True(_tools.Run("plan_transfer", "{\"target\":\"Ike\",\"target_pe\":30000}", Parked(), new NoPlanningBody()).IsError);
            // The Mun is still Transfer's rendezvous.
            Assert.Equal("rendezvous", Run("{\"target\":\"Mun\",\"target_pe\":30000}", Parked()).GetProperty("mode").GetString());
        }

        // ---- A moon of the craft's planet (#647) ----

        private static WindowSearch Moon(string moon, GameSnapshot snapshot = null)
        {
            var found = TransferWindows.Find(snapshot ?? Parked(), moon, 30000, true, 15);
            Assert.True(found.Error == null, found.Error);
            Assert.True(found.Moon);
            return found;
        }

        // Minmus, 6° out of the parking orbit's plane, from a 100 km orbit at the start of a new game: leaving now pays a plane
        // change on the way; Optimal waits about 28 days for Minmus to arrive at a node, where there is none. The figures are
        // the search's own (pinned): the ejection is the known one to Minmus from low Kerbin orbit, about 930 m/s from 80 km.
        [Fact]
        public void Minmus_waits_for_a_node_and_optimal_is_no_dearer_than_soonest()
        {
            var found = Moon("Minmus");
            Assert.Equal(0, found.Soonest.DepartureUt, 0);
            Assert.InRange(found.Soonest.TotalDv, 1081.6 - 1, 1081.6 + 1);
            Assert.InRange(found.Soonest.PlaneDv, 8.5 - 1, 8.5 + 1);
            Assert.InRange(found.Optimal.DepartureUt / Day, 28.45 - 0.5, 28.45 + 0.5);
            Assert.InRange(found.Optimal.TotalDv, 1068.3 - 1, 1068.3 + 1);
            Assert.InRange(found.Optimal.EjectionDv, 900, 930);
            Assert.True(found.Optimal.TotalDv <= found.Soonest.TotalDv);
            Assert.InRange(found.Optimal.PlaneDv, 0, 0.5);
            // At a node: Minmus arrives in the parking orbit's plane.
            var minmus = Parked().Bodies.First(b => b.Name == "Minmus");
            Vec3 r, v;
            new BodySystem(Parked().Bodies).OrbitOf(minmus).State(found.Optimal.ArrivalUt, out r, out v);
            Assert.InRange(Math.Abs(r.Z) / r.Length, 0, Math.Sin(0.2 * Math.PI / 180));
            // The horizon is one orbit of Minmus; Most efficient takes Optimal, and Balanced Soonest (Optimal saves under 10%).
            Assert.Equal(new BodySystem(Parked().Bodies).OrbitOf(minmus).Period, found.Synodic, 0);
            Assert.Same(found.Optimal, TransferWindows.Pick(found, TimeVersusDeltaV.MostEfficient));
            Assert.Same(found.Soonest, TransferWindows.Pick(found, TimeVersusDeltaV.Balanced));
        }

        // Ten days on, Minmus would arrive far from a node: leaving now pays more, and Optimal is the next node, about 18 days off.
        [Fact]
        public void Minmus_ten_days_on_waits_for_the_next_node()
        {
            var found = Moon("Minmus", Parked(10 * Day));
            Assert.Equal(10 * Day, found.Soonest.DepartureUt, 0);
            Assert.InRange(found.Soonest.TotalDv, 1098.3 - 1, 1098.3 + 1);
            Assert.InRange((found.Optimal.DepartureUt - 10 * Day) / Day, 18.45 - 0.5, 18.45 + 0.5);
            Assert.InRange(found.Optimal.TotalDv, 1068.3 - 1, 1068.3 + 1);
        }

        // The Mun is in the parking orbit's plane and circular: every departure costs the same, so Optimal is Soonest, now.
        [Fact]
        public void The_Mun_s_soonest_and_optimal_agree()
        {
            foreach (var ut in new[] { 0, 10 * Day })
            {
                var found = Moon("Mun", Parked(ut));
                Assert.InRange(found.Optimal.TotalDv, found.Soonest.TotalDv - TransferWindows.NoSavingDv, found.Soonest.TotalDv);
                Assert.Equal(ut, found.Soonest.DepartureUt, 0);
                Assert.Same(found.Soonest, found.Optimal);
                Assert.Equal(0, found.Soonest.PlaneDv);
                Assert.InRange(found.Soonest.TotalDv, 1144.7 - 1, 1144.7 + 1);
            }
        }

        [Fact]
        public void A_moon_is_a_moon_transfer_and_a_planet_or_another_planet_s_moon_is_not()
        {
            Assert.True(TransferWindows.IsMoonTransfer(Parked(), "Mun"));
            Assert.True(TransferWindows.IsMoonTransfer(Parked(), "Minmus"));
            Assert.False(TransferWindows.IsMoonTransfer(Parked(), "Kerbin"));
            Assert.False(TransferWindows.IsMoonTransfer(Parked(), "Duna"));
            Assert.False(TransferWindows.IsMoonTransfer(Parked(), "Ike"));
            Assert.Contains("nor a moon of Kerbin", TransferWindows.Find(Parked(), "Ike", 30000, true, 15).Error);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_moon_search_is_well_inside_the_tool_cap()
        {
            var snapshot = Parked();
            Timing.Within(TransferWindows.BudgetMs, () => Assert.Null(TransferWindows.Find(snapshot, "Minmus", 30000, true, 15).Error));
        }

        // plan_transfer to Minmus in orbit: the rendezvous as before, and its windows beside it.
        [Fact]
        public void Plan_transfer_to_Minmus_carries_its_windows_and_to_the_Mun_none()
        {
            var found = Moon("Minmus");
            var result = Run("{\"target\":\"Minmus\",\"target_pe\":30000}", Parked());
            Assert.Equal("rendezvous", result.GetProperty("mode").GetString());
            var windows = result.GetProperty("windows");
            Assert.Equal(Math.Round(found.Optimal.TotalDv, 1), windows.GetProperty("optimal").GetProperty("total_dv").GetDouble());
            Assert.Equal(Math.Round(found.Optimal.DepartureUt, 1), windows.GetProperty("optimal").GetProperty("depart_ut").GetDouble());
            Assert.Equal("in 0 s", windows.GetProperty("soonest").GetProperty("depart_in").GetString());
            Assert.Equal("soonest", windows.GetProperty("pick").GetString());
            var efficient = Parked();
            efficient.TimeVersusDeltaV = TimeVersusDeltaV.MostEfficient;
            Assert.Equal("optimal", Run("{\"target\":\"Minmus\",\"target_pe\":30000}", efficient).GetProperty("windows").GetProperty("pick").GetString());

            Assert.False(Run("{\"target\":\"Mun\",\"target_pe\":30000}", Parked()).TryGetProperty("windows", out _));
        }

        // From the pad there is no orbit to plan the burn from: Minmus's answer is its windows, the Mun's the error it always was.
        [Fact]
        public void From_the_pad_Minmus_gets_its_windows_and_the_Mun_its_old_error()
        {
            var snapshot = Parked();
            snapshot.Orbit = null;
            snapshot.Situation = "PRELAUNCH";
            var result = Run("{\"target\":\"Minmus\",\"target_pe\":30000}", snapshot);
            Assert.Equal(PlanTransfer.MoonWindowModel, result.GetProperty("model").GetString());
            Assert.Equal(80000, result.GetProperty("parking_pe").GetDouble());
            Assert.True(result.TryGetProperty("parking_note", out _));
            Assert.True(result.GetProperty("optimal").GetProperty("total_dv").GetDouble() <= result.GetProperty("soonest").GetProperty("total_dv").GetDouble());
            Assert.True(_tools.Run("plan_transfer", "{\"target\":\"Mun\",\"target_pe\":30000}", snapshot, new NoPlanningBody()).IsError);
        }

        // The Mission request's stage table rows are plan_transfer's answers, so Minmus's carries its windows.
        [Fact]
        public void The_stage_table_row_for_Minmus_carries_its_windows()
        {
            var table = MissionPrompts.StageTable(Parked(), "Land on Minmus and the Mun.", null);
            var minmus = table.Split('\n').Single(l => l.StartsWith("plan_transfer {\"target\":\"Minmus\""));
            Assert.Contains("\"windows\":{\"soonest\":", minmus);
            Assert.DoesNotContain("\"windows\"", table.Split('\n').Single(l => l.StartsWith("plan_transfer {\"target\":\"Mun\"")));
        }

        [Fact]
        public void A_target_pe_outside_the_planet_s_sphere_of_influence_is_refused()
        {
            var found = TransferWindows.Find(Parked(), "Duna", 1e9, true, 15);
            Assert.Contains("sphere of influence", found.Error);
        }
    }
}
