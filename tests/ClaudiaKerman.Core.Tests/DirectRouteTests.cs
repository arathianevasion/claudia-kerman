// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #760: from a moon's orbit, plan_transfer to another planet of its star gives two rows, the two-step route (return to the
    /// planet's low orbit, then transfer) and the direct route (the fall to a low periapsis at the planet, timed for the window, and
    /// the ejection at that periapsis). The direct route is flown by return, then transfer_to: flown here on <see cref="TransferRig"/>.
    /// </summary>
    public sealed class DirectRouteTests
    {
        private readonly PlanningTools _tools = PlanningTools.Default();

        private JsonElement Run(string input, GameSnapshot snapshot)
        {
            var outcome = _tools.Run("plan_transfer", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            Assert.InRange(outcome.Json.Length, 1, PlanningTools.MaxResultChars);
            return JsonDocument.Parse(outcome.Json).RootElement.Clone();
        }

        private static double D(JsonElement row, string name)
        {
            return row.GetProperty(name).GetDouble();
        }

        private static GameSnapshot Ike(TimeVersusDeltaV setting = TimeVersusDeltaV.Balanced)
        {
            var snapshot = TransferRig.Circular("Ike", 20000, 0).Snapshot();
            snapshot.TimeVersusDeltaV = setting;
            return snapshot;
        }

        /// <summary>
        /// The acceptance's figure. From Ike's low orbit the fall reaches Duna's low orbit height (60 km) about 283 m/s over a circular
        /// orbit there (278 to 296 as Ike moves on its eccentric orbit). The two-step route pays that to stop and pays it again to get
        /// back to the same speed; the direct route pays neither: the Oberth saving is twice the capture, about 566 m/s, on totals of
        /// about 990 and 434 m/s, less the few m/s the moon's phase leaves its periapsis off the ejection's burn point.
        /// </summary>
        [Fact]
        public void From_Ike_s_low_orbit_to_Kerbin_the_direct_route_saves_twice_the_capture_at_Duna()
        {
            var result = Run("{\"target\":\"Kerbin\"}", Ike());
            var direct = result.GetProperty("direct");
            var twoStep = result.GetProperty("two_step");
            Assert.InRange(D(twoStep, "planet_capture_dv"), 278, 296);
            var saving = D(twoStep, "total_dv") - D(direct, "total_dv");
            Assert.InRange(saving, 2 * D(twoStep, "planet_capture_dv") - 20, 2 * D(twoStep, "planet_capture_dv") + 5);
            Assert.InRange(saving, 540, 590);
            Assert.InRange(D(direct, "ejection_dv"), 280, 340);
            // Each is the sum of its legs, no capture at Kerbin (#758).
            Assert.Equal(D(direct, "return_dv") + D(direct, "ejection_dv"), D(direct, "total_dv"), 0);
            Assert.Equal(D(twoStep, "return_dv") + D(twoStep, "planet_capture_dv") + D(twoStep, "ejection_dv"), D(twoStep, "total_dv"), 0);
            Assert.False(direct.TryGetProperty("capture_dv", out _));
            Assert.False(result.GetProperty("capture").GetBoolean());
            Assert.Contains("no capture burn", result.GetProperty("arrival_note").GetString());
        }

        [Fact]
        public void Each_row_gives_its_timing_and_the_moon_departure_is_set_by_the_window()
        {
            var result = Run("{\"target\":\"Kerbin\"}", Ike());
            var direct = result.GetProperty("direct");
            var twoStep = result.GetProperty("two_step");
            foreach (var row in new[] { direct, twoStep })
            {
                Assert.True(D(row, "depart_ut") < D(row, "eject_ut"));
                Assert.True(D(row, "eject_ut") < D(row, "arrive_ut"));
                Assert.StartsWith("in ", row.GetProperty("eject_in").GetString());
            }
            // The two-step route returns now and waits in low orbit; the direct route waits at the moon and falls just before the window.
            Assert.True(D(twoStep, "depart_ut") < 3000, "two-step departs at " + D(twoStep, "depart_ut"));
            Assert.True(D(direct, "depart_ut") > D(twoStep, "depart_ut"));
            Assert.InRange(D(direct, "eject_ut") - D(direct, "depart_ut"), 10000, 40000);
            // Within half an orbit of Ike of the window, the most the moon's phase can move it.
            Assert.InRange(D(direct, "eject_ut") - D(twoStep, "eject_ut"), -40000, 40000);
        }

        [Theory]
        [InlineData(TimeVersusDeltaV.Balanced, "direct")]
        [InlineData(TimeVersusDeltaV.MostEfficient, "direct")]
        public void The_cheaper_route_is_picked_unless_the_setting_is_Fastest(TimeVersusDeltaV setting, string pick)
        {
            Assert.Equal(pick, Run("{\"target\":\"Kerbin\"}", Ike(setting)).GetProperty("pick").GetString());
        }

        [Fact]
        public void Fastest_takes_the_route_arriving_first()
        {
            var result = Run("{\"target\":\"Kerbin\"}", Ike(TimeVersusDeltaV.Fastest));
            var first = D(result.GetProperty("direct"), "arrive_ut") <= D(result.GetProperty("two_step"), "arrive_ut") ? "direct" : "two_step";
            Assert.Equal(first, result.GetProperty("pick").GetString());
        }

        [Fact]
        public void The_pick_rule_is_the_settings_rule_on_the_two_routes()
        {
            var cheapLate = new RouteRow { TotalDv = 250, ArrivalUt = 2000 };
            var dearEarly = new RouteRow { TotalDv = 850, ArrivalUt = 1000 };
            Assert.Same(dearEarly, DirectRoute.Pick(cheapLate, dearEarly, TimeVersusDeltaV.Fastest));
            Assert.Same(cheapLate, DirectRoute.Pick(cheapLate, dearEarly, TimeVersusDeltaV.MostEfficient));
            Assert.Same(cheapLate, DirectRoute.Pick(cheapLate, dearEarly, TimeVersusDeltaV.Balanced));
            // Balanced keeps the earlier unless the other saves at least 10%.
            var nearlyAsCheap = new RouteRow { TotalDv = 800, ArrivalUt = 2000 };
            Assert.Same(dearEarly, DirectRoute.Pick(nearlyAsCheap, dearEarly, TimeVersusDeltaV.Balanced));
        }

        [Fact]
        public void Price_is_the_one_function_for_the_map_and_gives_the_saving_from_body_data()
        {
            var rig = TransferRig.Circular("Ike", 20000, 0);
            var ike = rig.System.Find("Ike");
            var duna = rig.System.Find("Duna");
            var price = DirectRoute.Price(ike, duna, 400);
            Assert.InRange(price.PlanetCaptureDv, 290, 310);
            Assert.Equal(2 * price.PlanetCaptureDv, price.Saving, 6);
            Assert.Equal(price.TwoStep - price.Direct, price.Saving, 6);
            Assert.Equal(price.ReturnDv + price.EjectionDv, price.Direct, 6);
            Assert.Equal(price.ReturnDv + price.PlanetCaptureDv + price.TwoStepEjectionDv, price.TwoStep, 6);
            // The same as the Delta-v Map's return leg, before its plane change, from Ike's low orbit.
            Assert.InRange(price.ReturnDv, 100, 200);
        }

        [Fact]
        public void Price_from_the_window_s_speed_agrees_with_the_rows_flown_figures()
        {
            var snapshot = Ike();
            var result = Run("{\"target\":\"Kerbin\"}", snapshot);
            var rig = TransferRig.Circular("Ike", 20000, 0);
            var price = DirectRoute.Price(rig.System.Find("Ike"), rig.System.Find("Duna"), D(result, "departure_v_inf"), 150000);
            // Price's return is the Delta-v Map's sum, about 50 m/s over the solve from Ike (ReturnBurnTests), so it budgets a little high.
            Assert.InRange(D(result.GetProperty("direct"), "total_dv"), price.Direct * 0.9, price.Direct * 1.02);
            Assert.InRange(D(result.GetProperty("two_step"), "total_dv"), price.TwoStep * 0.9, price.TwoStep * 1.02);
        }

        /// <summary>The acceptance's flown route: return with the row's window and periapsis, then transfer_to with its window, each as the block asks Core.</summary>
        [Theory]
        [InlineData("Ike", 20000, 0, "Kerbin", null)]
        [InlineData("Ike", 20000, 3000000, "Kerbin", null)]
        [InlineData("Ike", 20000, 9000000, "Kerbin", null)]
        [InlineData("Mun", 30000, 0, "Duna", 60000.0)]
        public void The_direct_route_flies_as_return_then_transfer_to_for_about_its_price(string moon, double altitude, double ut, string target, double? pe)
        {
            var rig = TransferRig.Circular(moon, altitude, ut);
            var input = "{\"target\":\"" + target + "\"" + (pe.HasValue ? ",\"target_pe\":" + pe.Value : "") + "}";
            var result = Run(input, rig.Snapshot());
            var direct = result.GetProperty("direct");

            // return: its solve from the row's window, flown; the craft leaves the moon for the low periapsis at its planet.
            var plan = ReturnBurn.Find(rig.Snapshot(), new ReturnAsk { TargetPe = D(result, "return_pe"), EarliestUt = D(direct, "return_window") });
            Assert.Null(plan.Error);
            Assert.Equal(D(direct, "return_dv"), plan.DeltaV, 0);
            Assert.Equal(D(direct, "depart_ut"), plan.Ut, 0);
            rig.Fly("return", plan.Ut, plan.Prograde, plan.Normal, plan.Radial);
            rig.MoveTo(plan.SoiUt + 30);
            Assert.Equal(plan.Planet, rig.Body.Name);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, D(result, "return_pe") - 1000, D(result, "return_pe") + 1000);
            Assert.True(rig.Orbit.Closed);

            // transfer_to: the window from the row, the ejection at the periapsis the row says and for what it says.
            var ask = new TransferFlightAsk
            {
                Target = target, TargetPe = D(result, "target_pe"), Window = D(direct, "eject_window"), StartUt = rig.Ut, TurnTime = 60, DvLeft = 5000,
            };
            var first = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal("", first.Error);
            Assert.Equal("ejection", first.Kind);
            var off = (first.Ut - D(direct, "eject_ut")) / rig.Orbit.Period;
            Assert.True(Math.Abs(off - Math.Round(off)) < 0.02, "ejects at " + first.Ut + ", the row says " + D(direct, "eject_ut") + ", the craft's orbit is " + rig.Orbit.Period + " s");
            var step = rig.Run(ask);
            Assert.True(step.Error == "", step.Error);
            Assert.InRange(rig.Burned("ejection"), D(direct, "ejection_dv") * 0.85, D(direct, "ejection_dv") * 1.15);
            Assert.True(Convert.ToDouble(step.Report["encounter_ut"]) > 0);
            // The whole route's two burns within 10% of the row's, what capture there is left out.
            var capture = direct.TryGetProperty("capture_dv", out var c) ? c.GetDouble() : 0;
            Assert.InRange(rig.Burned("return") + rig.Burned("ejection"), (D(direct, "total_dv") - capture) * 0.9, (D(direct, "total_dv") - capture) * 1.1);
        }

        [Fact]
        public void Each_row_names_the_blocks_that_fly_it()
        {
            var result = Run("{\"target\":\"Kerbin\"}", Ike());
            var direct = result.GetProperty("direct").GetProperty("fly").GetString();
            Assert.StartsWith("return(", direct);
            Assert.Contains("then transfer_to(", direct);
            Assert.DoesNotContain("circularize", direct);
            Assert.Contains("circularize", result.GetProperty("two_step").GetProperty("fly").GetString());
            // The row rides in the Mission request's stage table when the Mission Goal names the planet (docs/prefix-budget/760.md).
            Assert.InRange(result.GetRawText().Length, 1, 1500);
        }

        [Fact]
        public void A_trip_to_another_planet_prices_its_capture_and_needs_its_target_pe()
        {
            var snapshot = TransferRig.Circular("Mun", 30000, 0).Snapshot();
            var result = Run("{\"target\":\"Duna\",\"target_pe\":60000}", snapshot);
            Assert.True(result.GetProperty("capture").GetBoolean());
            var direct = result.GetProperty("direct");
            Assert.Equal(D(direct, "return_dv") + D(direct, "ejection_dv") + D(direct, "capture_dv"), D(direct, "total_dv"), 0);
            // The Mun's fall reaches Kerbin's low orbit height about 852 m/s over circular, which the two-step route pays twice. This
            // window's asymptote is about 5 degrees out of the Mun's plane, which the small burn at periapsis feels more than the large
            // one from low orbit, so the saving is about 1,590 m/s, not the in-plane 1,704.
            var twoStep = result.GetProperty("two_step");
            Assert.InRange(D(twoStep, "planet_capture_dv"), 830, 870);
            Assert.InRange(D(twoStep, "total_dv") - D(direct, "total_dv"), 1500, 2 * D(twoStep, "planet_capture_dv"));

            var outcome = _tools.Run("plan_transfer", "{\"target\":\"Duna\"}", snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            Assert.Contains("target needs target_pe", outcome.Json);
        }

        [Fact]
        public void The_trip_home_takes_its_target_pe_when_given_and_the_entry_periapsis_when_not()
        {
            Assert.Equal(21000, D(Run("{\"target\":\"Kerbin\"}", Ike()), "target_pe"));
            Assert.Equal(80000, D(Run("{\"target\":\"Kerbin\",\"target_pe\":80000}", Ike()), "target_pe"));
        }

        [Fact]
        public void An_explicit_capture_home_is_priced_and_needs_its_target_pe()
        {
            var result = Run("{\"target\":\"Kerbin\",\"target_pe\":80000,\"capture\":true}", Ike());
            Assert.True(D(result.GetProperty("direct"), "capture_dv") > 0);
            var outcome = _tools.Run("plan_transfer", "{\"target\":\"Kerbin\",\"capture\":true}", Ike(), new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
        }

        [Fact]
        public void Fits_is_judged_on_the_pick_against_the_craft_s_delta_v()
        {
            var snapshot = Ike();
            snapshot.DeltaVSimulator = true;
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 500 });
            var result = Run("{\"target\":\"Kerbin\"}", snapshot);
            Assert.Equal(500, D(result, "craft_dv"));
            Assert.True(result.GetProperty("fits").GetBoolean());
            snapshot = Ike();
            snapshot.DeltaVSimulator = true;
            snapshot.Stages.Add(new SnapshotStage { DeltaVVac = 100 });
            Assert.False(Run("{\"target\":\"Kerbin\"}", snapshot).GetProperty("fits").GetBoolean());
        }

        [Fact]
        public void A_moon_s_own_planet_and_other_starts_are_as_they_were()
        {
            var fromIke = _tools.Run("plan_transfer", "{\"target\":\"Duna\",\"target_pe\":60000}", Ike(), new NoPlanningBody());
            Assert.True(fromIke.IsError, fromIke.Json);
            Assert.Contains("does not orbit Ike", fromIke.Json);
            // From a planet's orbit the windows, with no routes.
            var fromKerbin = Run("{\"target\":\"Duna\",\"target_pe\":60000}", TransferRig.Circular("Kerbin", 100000, 0).Snapshot());
            Assert.True(fromKerbin.TryGetProperty("soonest", out _));
            Assert.False(fromKerbin.TryGetProperty("direct", out _));
            // From a moon's sibling, the sibling windows.
            var fromMun = Run("{\"target\":\"Minmus\",\"target_pe\":30000}", TransferRig.Circular("Mun", 30000, 0).Snapshot());
            Assert.False(fromMun.TryGetProperty("direct", out _));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void It_runs_under_a_second()
        {
            Timing.Within(1000, () => Run("{\"target\":\"Kerbin\"}", Ike()));
        }
    }
}
