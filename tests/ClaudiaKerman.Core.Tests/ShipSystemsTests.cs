// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#756: the Ship Systems rule, one test per transition the player agreed.</summary>
    public sealed class ShipSystemsTests
    {
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRadius = 600000;

        private static ShipSystemsReading Orbit(string body = "Kerbin", double top = 70000, double pe = 80000, bool engines = false, double ut = 100)
        {
            return new ShipSystemsReading
            {
                Ut = ut, Situation = ShipSystemsSituation.Orbiting, Body = body, AtmosphereTop = top, Periapsis = pe, Altitude = pe + 10000,
                Radar = pe + 10000, VerticalSpeed = 1, EnginesOn = engines,
            };
        }

        private static ShipSystemsReading Coast(double altitude, double? secondsToAir, double vs = -100, string body = "Duna", double top = 50000, double pe = 20000, ShipSystemsSituation situation = ShipSystemsSituation.SubOrbital)
        {
            return new ShipSystemsReading
            {
                Ut = 200, Situation = situation, Body = body, AtmosphereTop = top, Periapsis = pe, Altitude = altitude, Radar = altitude,
                VerticalSpeed = vs, SurfaceSpeed = 500, SecondsToAir = secondsToAir,
            };
        }

        private static ShipSystemsReading Ground(double ut, string body = "Duna", double top = 50000, double speed = 0, ShipSystemsSituation situation = ShipSystemsSituation.Landed)
        {
            return new ShipSystemsReading { Ut = ut, Situation = situation, Body = body, AtmosphereTop = top, Periapsis = -500000, SurfaceSpeed = speed };
        }

        /// <summary>A rule that has read a climb, so the next orbit is one it saw the craft reach.</summary>
        private static ShipSystemsRule Flown()
        {
            var rule = new ShipSystemsRule();
            Assert.Null(rule.Step(Coast(60000, null, 100, "Kerbin", 70000, -100000)));
            return rule;
        }

        // ---- Extend in orbit ----

        [Fact]
        public void An_orbit_clear_of_the_air_with_the_engines_off_extends_once()
        {
            var rule = Flown();
            var transition = rule.Step(Orbit());
            Assert.Equal(ShipSystemsDeployables.Extend, transition.Deployables);
            Assert.Equal("in orbit of Kerbin", transition.Why);
            Assert.Null(rule.Step(Orbit()));
        }

        [Fact]
        public void Nothing_extends_while_burning_and_it_extends_when_the_burn_ends()
        {
            var rule = Flown();
            Assert.Null(rule.Step(Orbit(engines: true)));
            Assert.Equal(ShipSystemsDeployables.Extend, rule.Step(Orbit()).Deployables);
        }

        [Fact]
        public void An_orbiting_periapsis_inside_the_air_extends_nothing()
        {
            var rule = Flown();
            var low = Orbit(pe: 60000);
            low.Altitude = 200000;
            Assert.Null(rule.Step(low));
        }

        [Fact]
        public void An_airless_body_needs_the_periapsis_above_its_terrain()
        {
            var rule = Flown();
            Assert.Null(rule.Step(Orbit("Mun", 0, 5000)));
            Assert.Equal(ShipSystemsDeployables.Extend, rule.Step(Orbit("Mun", 0, 15000)).Deployables);
        }

        [Fact]
        public void A_vacuum_burn_after_the_orbit_is_reached_extends_nothing_again()
        {
            var rule = Flown();
            rule.Step(Orbit());
            Assert.Null(rule.Step(Orbit(engines: true)));
            Assert.Null(rule.Step(Orbit()));
        }

        [Fact]
        public void An_orbit_the_craft_was_already_in_at_the_first_reading_is_no_transition()
        {
            var rule = new ShipSystemsRule();
            Assert.Null(rule.Step(Orbit()));
            Assert.Null(rule.Step(Orbit()));
        }

        [Fact]
        public void An_orbit_first_read_during_a_burn_is_no_transition_either()
        {
            var rule = new ShipSystemsRule();
            Assert.Null(rule.Step(Orbit(engines: true)));
            Assert.Null(rule.Step(Orbit()));
        }

        // ---- Retract before an atmosphere ----

        [Theory]
        [InlineData(300000, 58.0, true)]
        [InlineData(300000, 60.0, true)]
        [InlineData(300000, 61.0, false)]
        public void The_60_second_trigger(double altitude, double seconds, bool fires)
        {
            var transition = Flown().Step(Coast(altitude, seconds));
            if (!fires)
            {
                Assert.Null(transition);
                return;
            }
            Assert.Equal(ShipSystemsDeployables.Retract, transition.Deployables);
            Assert.StartsWith("entering Duna's atmosphere in ", transition.Why);
        }

        [Fact]
        public void The_10_km_trigger_fires_coming_down_and_not_climbing()
        {
            Assert.Null(Flown().Step(Coast(59000, 300, 100)));
            Assert.Null(Flown().Step(Coast(61000, 300)));
            Assert.Equal(ShipSystemsDeployables.Retract, Flown().Step(Coast(59000, 300)).Deployables);
        }

        [Fact]
        public void A_trajectory_that_misses_the_air_retracts_nothing()
        {
            Assert.Null(Flown().Step(Coast(55000, null, -100, "Duna", 50000, 51000)));
        }

        [Fact]
        public void It_fires_once_per_entry()
        {
            var rule = Flown();
            Assert.NotNull(rule.Step(Coast(59000, 10)));
            Assert.Null(rule.Step(Coast(58000, 9)));
            Assert.Null(rule.Step(Coast(40000, null)));
        }

        [Fact]
        public void The_deorbit_or_reentry_cue_retracts_and_the_orbit_it_starts_from_does_not_extend_them_again()
        {
            var rule = Flown();
            rule.Step(Orbit("Duna", 50000, 60000));
            rule.Cue(ShipSystemsCue.EntryStart);
            var transition = rule.Step(Orbit("Duna", 50000, 60000));
            Assert.Equal(ShipSystemsDeployables.Retract, transition.Deployables);
            Assert.Equal("starting down to Duna's atmosphere", transition.Why);
            Assert.Null(rule.Step(Orbit("Duna", 50000, 60000, engines: true)));
            Assert.Null(rule.Step(Orbit("Duna", 50000, 60000)));
        }

        [Fact]
        public void An_aerobrake_retracts_on_its_trajectory_and_the_orbit_after_it_extends()
        {
            var rule = Flown();
            Assert.Equal(ShipSystemsDeployables.Retract, rule.Step(Coast(500000, 50, -100, "Kerbin", 70000, 40000, ShipSystemsSituation.Orbiting)).Deployables);
            Assert.Equal(ShipSystemsDeployables.Extend, rule.Step(Orbit()).Deployables);
        }

        [Fact]
        public void A_bounce_after_landing_is_no_entry_and_no_new_landing()
        {
            var rule = Flown();
            rule.Step(Coast(500, null));
            rule.Step(Ground(300));
            Assert.NotNull(rule.Step(Ground(303)));
            var bounce = Coast(2000, null, -1, situation: ShipSystemsSituation.Flying, pe: -300000);
            bounce.Radar = 2;
            Assert.Null(rule.Step(bounce));
            Assert.Null(rule.Step(Ground(310)));
            Assert.Null(rule.Step(Ground(320)));
        }

        [Fact]
        public void A_hop_above_the_air_retracts_on_its_way_back_and_a_low_one_does_not()
        {
            var rule = new ShipSystemsRule();
            rule.Step(Ground(300));
            Assert.Null(rule.Step(Coast(40000, null, 100, pe: -300000)));
            Assert.Null(rule.Step(Coast(40000, null, -100, pe: -300000)));
            Assert.Null(rule.Step(Coast(55000, 100, 100, pe: -300000)));
            Assert.Equal(ShipSystemsDeployables.Retract, rule.Step(Coast(55000, 30, -100, pe: -300000)).Deployables);
        }

        // ---- Airless bodies ----

        [Fact]
        public void An_airless_body_keeps_them_out_through_descent_landing_and_ascent()
        {
            var rule = Flown();
            rule.Step(Orbit("Mun", 0, 15000));
            rule.Cue(ShipSystemsCue.EntryStart);
            Assert.Null(rule.Step(Orbit("Mun", 0, 15000)));
            Assert.Null(rule.Step(Coast(5000, null, -50, "Mun", 0, -100000)));
            Assert.Null(rule.Step(Ground(300, "Mun", 0)));
            Assert.Null(rule.Step(Ground(310, "Mun", 0)));
            rule.Cue(ShipSystemsCue.LiftOff);
            var lift = rule.Step(Ground(320, "Mun", 0));
            Assert.Equal(ShipSystemsDeployables.None, lift.Deployables);
            Assert.Equal(ShipSystemsLights.Off, lift.Lights);
        }

        // ---- Lights ----

        [Fact]
        public void Lights_come_on_at_the_final_descent_cue_and_stay_on_at_the_lower_mark()
        {
            var rule = Flown();
            rule.Cue(ShipSystemsCue.FinalDescent);
            var transition = rule.Step(Coast(5000, null, -50, "Mun", 0, -100000));
            Assert.Equal(ShipSystemsLights.On, transition.Lights);
            Assert.Equal("final descent to Mun", transition.Why);
            Assert.Null(rule.Step(Coast(900, null, -10, "Mun", 0, -100000)));
        }

        [Fact]
        public void Lights_come_on_below_gear_alt_coming_down_only()
        {
            var rule = Flown();
            Assert.Null(rule.Step(Coast(1100, null, -10, "Mun", 0, -100000)));
            Assert.Null(rule.Step(Coast(900, null, 10, "Mun", 0, -100000)));
            Assert.Equal(ShipSystemsLights.On, rule.Step(Coast(900, null, -10, "Mun", 0, -100000)).Lights);
        }

        [Fact]
        public void A_low_orbital_pass_does_not_turn_the_lights_on()
        {
            var rule = Flown();
            var pass = Orbit("Mun", 0, 15000);
            pass.Radar = 500;
            pass.VerticalSpeed = -5;
            Assert.Equal(ShipSystemsLights.None, rule.Step(pass).Lights);
        }

        [Fact]
        public void Lights_stay_on_after_touchdown_switch_off_at_lift_off_and_come_on_again_for_the_next_landing()
        {
            var rule = Flown();
            Assert.Equal(ShipSystemsLights.On, rule.Step(Coast(900, null, -10, "Mun", 0, -100000)).Lights);
            Assert.Null(rule.Step(Ground(300, "Mun", 0)));
            rule.Cue(ShipSystemsCue.LiftOff);
            Assert.Equal(ShipSystemsLights.Off, rule.Step(Ground(310, "Mun", 0)).Lights);
            Assert.Equal(ShipSystemsLights.On, rule.Step(Coast(900, null, -10, "Mun", 0, -100000)).Lights);
        }

        // ---- Atmospheric landing and lift-off ----

        [Fact]
        public void A_landing_on_a_body_with_air_redeploys_after_3_s_stationary()
        {
            var rule = Flown();
            rule.Step(Coast(500, null));
            Assert.Null(rule.Step(Ground(300)));
            Assert.Null(rule.Step(Ground(302.9)));
            var transition = rule.Step(Ground(303));
            Assert.Equal(ShipSystemsDeployables.Extend, transition.Deployables);
            Assert.Equal("landed on Duna", transition.Why);
            Assert.Null(rule.Step(Ground(310)));
        }

        [Fact]
        public void Moving_on_the_ground_restarts_the_3_s()
        {
            var rule = Flown();
            rule.Step(Coast(500, null));
            rule.Step(Ground(300));
            Assert.Null(rule.Step(Ground(302, speed: 1)));
            Assert.Null(rule.Step(Ground(303)));
            Assert.Null(rule.Step(Ground(305.9)));
            Assert.Equal(ShipSystemsDeployables.Extend, rule.Step(Ground(306)).Deployables);
        }

        [Fact]
        public void A_splashdown_redeploys_and_the_pad_does_not()
        {
            var rule = Flown();
            rule.Step(Coast(500, null));
            rule.Step(Ground(300, situation: ShipSystemsSituation.Splashed));
            Assert.NotNull(rule.Step(Ground(303, situation: ShipSystemsSituation.Splashed)));

            var pad = new ShipSystemsRule();
            pad.Step(Ground(300, "Kerbin", 70000, 0, ShipSystemsSituation.Prelaunch));
            Assert.Null(pad.Step(Ground(310, "Kerbin", 70000, 0, ShipSystemsSituation.Prelaunch)));
        }

        [Fact]
        public void A_craft_already_on_the_ground_at_the_first_reading_is_no_landing()
        {
            var rule = new ShipSystemsRule();
            Assert.Null(rule.Step(Ground(300)));
            Assert.Null(rule.Step(Ground(310)));
        }

        [Fact]
        public void The_lift_off_cue_stows_from_the_ground_and_the_pad()
        {
            var rule = new ShipSystemsRule();
            rule.Step(Ground(300));
            rule.Cue(ShipSystemsCue.LiftOff);
            var transition = rule.Step(Ground(305));
            Assert.Equal(ShipSystemsDeployables.Retract, transition.Deployables);
            Assert.Equal(ShipSystemsLights.Off, transition.Lights);
            Assert.Equal("lifting off from Duna", transition.Why);

            var pad = new ShipSystemsRule();
            pad.Cue(ShipSystemsCue.LiftOff);
            Assert.Equal(ShipSystemsDeployables.Retract, pad.Step(Ground(0, "Kerbin", 70000, 0, ShipSystemsSituation.Prelaunch)).Deployables);
        }

        [Fact]
        public void A_lift_off_cue_in_flight_stows_nothing()
        {
            var rule = Flown();
            rule.Cue(ShipSystemsCue.LiftOff);
            Assert.Equal(ShipSystemsDeployables.None, rule.Step(Coast(20000, null, 50, "Duna", 50000, -100000)).Deployables);
        }

        [Fact]
        public void The_lift_off_cue_ends_a_landing_still_counting_its_3_s()
        {
            var rule = Flown();
            rule.Step(Coast(500, null));
            rule.Step(Ground(300));
            rule.Cue(ShipSystemsCue.LiftOff);
            rule.Step(Ground(301));
            Assert.Null(rule.Step(Ground(310)));
        }

        // ---- The stow wait ----

        [Fact]
        public void The_stow_wait_ends_when_the_parts_are_in_or_at_the_cap_saying_so()
        {
            Assert.True(ShipSystemsStowWait.Step(100, 100.5, 0).Done);
            Assert.False(ShipSystemsStowWait.Step(100, 100.5, 0).Capped);
            Assert.False(ShipSystemsStowWait.Step(100, 109.9, 2).Done);
            var capped = ShipSystemsStowWait.Step(100, 110, 2);
            Assert.True(capped.Done);
            Assert.True(capped.Capped);
            Assert.Equal("Ship Systems: 2 still not retracted after 10 s; lifting off anyway", capped.Note);
        }

        [Fact]
        public void The_stow_wait_counts_what_can_come_in_and_not_lights_or_what_can_never_retract()
        {
            Assert.Equal(2, ShipSystemsStowWait.StillOut(new[]
            {
                Part(1, ShipSystemKind.Panel, ShipSystemState.Out),
                Part(2, ShipSystemKind.Antenna, ShipSystemState.Moving),
                Part(3, ShipSystemKind.Radiator, ShipSystemState.Stowed),
                Part(4, ShipSystemKind.Light, ShipSystemState.Out),
                Part(5, ShipSystemKind.Panel, ShipSystemState.Out, false),
                Part(6, ShipSystemKind.Panel, ShipSystemState.Broken),
            }));
        }

        // ---- The setting ----

        [Fact]
        public void The_setting_off_means_no_action()
        {
            var rule = Flown();
            Assert.Null(rule.Step(Orbit(), false));
            rule.Cue(ShipSystemsCue.EntryStart);
            Assert.Null(rule.Step(Orbit(), false));
            Assert.Null(rule.Step(Coast(59000, 10), false));
        }

        [Fact]
        public void Switching_the_setting_on_again_does_not_replay_what_was_missed()
        {
            var rule = Flown();
            Assert.Null(rule.Step(Orbit(), false));
            Assert.Null(rule.Step(Orbit()));
        }

        // ---- Player overrides ----

        [Fact]
        public void A_manual_change_between_transitions_stands_until_the_next_one()
        {
            var rule = Flown();
            var panel = Part(1, ShipSystemKind.Panel, ShipSystemState.Stowed);
            var parts = new[] { panel };
            var extended = ShipSystemsMoves.Of(rule.Step(Orbit()), parts, ShipSystemsHold.None);
            Assert.Single(extended.Extend);
            // The player retracts it in orbit: no later reading in the same orbit asks for anything, burns or not.
            panel.State = ShipSystemState.Stowed;
            for (var ut = 110; ut < 200; ut += 10)
            {
                Assert.Null(rule.Step(Orbit(ut: ut)));
                Assert.Null(rule.Step(Orbit(engines: true, ut: ut + 5)));
            }
            // The next transition does.
            panel.State = ShipSystemState.Out;
            Assert.Single(ShipSystemsMoves.Of(rule.Step(Coast(59000, 10)), parts, ShipSystemsHold.None).Retract);
        }

        [Fact]
        public void A_manual_extension_during_an_entry_stands_too()
        {
            var rule = Flown();
            Assert.NotNull(rule.Step(Coast(59000, 10)));
            Assert.Null(rule.Step(Coast(55000, 5)));
        }

        // ---- The moves and their line ----

        private static ShipSystemPart Part(uint id, ShipSystemKind kind, ShipSystemState state, bool canRetract = true)
        {
            return new ShipSystemPart { Id = id, Kind = kind, State = state, CanRetract = canRetract };
        }

        [Fact]
        public void The_line_counts_what_moved_and_says_why()
        {
            var parts = new[]
            {
                Part(1, ShipSystemKind.Panel, ShipSystemState.Out), Part(2, ShipSystemKind.Panel, ShipSystemState.Out),
                Part(3, ShipSystemKind.Panel, ShipSystemState.Out), Part(4, ShipSystemKind.Panel, ShipSystemState.Out),
                Part(5, ShipSystemKind.Antenna, ShipSystemState.Out), Part(6, ShipSystemKind.Radiator, ShipSystemState.Stowed),
            };
            var moves = ShipSystemsMoves.Of(new ShipSystemsTransition { Deployables = ShipSystemsDeployables.Retract, Why = "entering Duna's atmosphere in 58 s" }, parts, ShipSystemsHold.None);
            Assert.Equal(5, moves.Retract.Count);
            Assert.Equal("Ship Systems: retracted 4 panels, 1 antenna; entering Duna's atmosphere in 58 s", moves.Text);
        }

        [Fact]
        public void Lights_and_deployables_share_a_line_and_nothing_moved_is_no_event()
        {
            var transition = new ShipSystemsTransition { Deployables = ShipSystemsDeployables.Retract, Lights = ShipSystemsLights.Off, Why = "lifting off from Duna" };
            var both = ShipSystemsMoves.Of(transition, new[] { Part(1, ShipSystemKind.Panel, ShipSystemState.Out), Part(2, ShipSystemKind.Light, ShipSystemState.Out) }, ShipSystemsHold.None);
            Assert.Equal("Ship Systems: retracted 1 panel, lights off; lifting off from Duna", both.Text);
            Assert.Equal("", ShipSystemsMoves.Of(transition, new[] { Part(1, ShipSystemKind.Panel, ShipSystemState.Stowed), Part(2, ShipSystemKind.Light, ShipSystemState.Stowed) }, ShipSystemsHold.None).Text);
        }

        [Fact]
        public void A_part_that_cannot_retract_is_never_asked_to_and_a_broken_one_is_left()
        {
            var moves = ShipSystemsMoves.Of(new ShipSystemsTransition { Deployables = ShipSystemsDeployables.Retract, Why = "x" },
                new[] { Part(1, ShipSystemKind.Panel, ShipSystemState.Out, false), Part(2, ShipSystemKind.Panel, ShipSystemState.Broken) }, ShipSystemsHold.None);
            Assert.Empty(moves.Retract);
        }

        // ---- Parts that can't retract ----

        private static GameSnapshot Craft()
        {
            // The pod (root) over a decoupler and a service module, a fixed panel on each; Kerbin and Duna have air, the Mun none.
            var snapshot = new GameSnapshot { Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit { Body = "Kerbin" };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Kerbin", AtmosphereHeight = 70000, SeaLevelAtm = 1 });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Duna", AtmosphereHeight = 50000, SeaLevelAtm = 0.2 });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Mun" });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1, Title = "Pod", Tags = new List<string> { "command", "crew_seat" }, CrewCapacity = 1, Stage = 0 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 2, ParentId = 1, Title = "OX-Fixed", CannotRetract = true, Stage = 0 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 3, ParentId = 1, Title = "Decoupler", Tags = new List<string> { "decoupler" }, Stage = 1 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 4, ParentId = 3, Title = "Service", Stage = 1 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 5, ParentId = 4, Title = "OX-Fixed", CannotRetract = true, Stage = 1 });
            return snapshot;
        }

        private static List<PlannedSequence> Plan(params string[] legs)
        {
            return legs.Select(l => new PlannedSequence { Name = l }).ToList();
        }

        private static readonly ShipSystemsTransition Extend = new ShipSystemsTransition { Deployables = ShipSystemsDeployables.Extend, Why = "in orbit of Kerbin" };

        [Fact]
        public void A_part_still_aboard_at_an_entry_ahead_stays_stowed_and_the_line_says_why()
        {
            var hold = ShipSystemsHold.Find(Craft(), Plan("Reach orbit", "Fly to the Mun", "Return to Kerbin"), 0, false, null);
            var moves = ShipSystemsMoves.Of(Extend, new[] { Part(2, ShipSystemKind.Panel, ShipSystemState.Stowed, false), Part(9, ShipSystemKind.Panel, ShipSystemState.Stowed) }, hold);
            Assert.Single(moves.Held);
            Assert.Single(moves.Extend);
            Assert.Equal("Ship Systems: extended 1 panel; in orbit of Kerbin; left 1 panel stowed: can't retract, and the Mission Plan enters Kerbin's atmosphere (Return to Kerbin)", moves.Text);
        }

        [Fact]
        public void A_part_dropped_before_the_entry_counts_as_off_the_craft()
        {
            var hold = ShipSystemsHold.Find(Craft(), Plan("Return to Kerbin"), 0, false, null);
            Assert.True(hold.Holds(2));
            Assert.False(hold.Holds(5));
        }

        [Fact]
        public void With_no_entry_ahead_it_extends()
        {
            var hold = ShipSystemsHold.Find(Craft(), Plan("Reach orbit", "Land on the Mun"), 0, false, null);
            Assert.False(hold.Holds(2));
            Assert.Single(ShipSystemsMoves.Of(Extend, new[] { Part(2, ShipSystemKind.Panel, ShipSystemState.Stowed, false) }, hold).Extend);
        }

        [Fact]
        public void Only_the_plan_from_the_leg_in_hand_on_counts()
        {
            var plan = Plan("Land on Duna", "Reach orbit", "Land on the Mun");
            Assert.True(ShipSystemsHold.Find(Craft(), plan, 0, false, null).Holds(2));
            Assert.False(ShipSystemsHold.Find(Craft(), plan, 1, false, null).Holds(2));
        }

        [Fact]
        public void A_landing_just_flown_is_behind_the_craft()
        {
            var plan = Plan("Land on Duna", "Reach orbit", "Land on the Mun");
            var duna = Craft();
            duna.Orbit = new SnapshotOrbit { Body = "Duna" };
            Assert.True(ShipSystemsHold.Find(duna, plan, 0, false, null).Holds(2));
            Assert.False(ShipSystemsHold.Find(duna, plan, 0, false, null, true).Holds(2));
            Assert.True(ShipSystemsHold.Find(duna, Plan("Land on Duna", "Return to Kerbin"), 0, false, null, true).Holds(2));
            Assert.True(ShipSystemsHold.Find(duna, Plan("Land on Duna and return to Kerbin"), 0, false, null, true).Holds(2));
            Assert.False(ShipSystemsHold.Find(Craft(), Plan("Return to Kerbin"), 0, false, null, true).Holds(2));
        }

        [Fact]
        public void The_touchdown_says_it_landed()
        {
            var rule = Flown();
            rule.Step(Coast(500, null));
            rule.Step(Ground(300));
            Assert.True(rule.Step(Ground(303)).Landed);
            Assert.False(Flown().Step(Orbit()).Landed);
        }

        [Fact]
        public void A_part_caught_moving_is_asked_again_once_it_has_stopped()
        {
            var transition = new ShipSystemsTransition { Deployables = ShipSystemsDeployables.Retract, Why = "x" };
            var moving = Part(1, ShipSystemKind.Panel, ShipSystemState.Moving);
            var first = ShipSystemsMoves.Of(transition, new[] { moving }, ShipSystemsHold.None);
            Assert.True(first.Unsettled);
            Assert.Empty(first.Retract);
            moving.State = ShipSystemState.Out;
            var second = ShipSystemsMoves.Of(transition, new[] { moving }, ShipSystemsHold.None);
            Assert.False(second.Unsettled);
            Assert.Single(second.Retract);
        }

        [Fact]
        public void An_aerobrake_ahead_is_an_entry()
        {
            Assert.True(ShipSystemsHold.Find(Craft(), Plan("Aerobrake at Duna"), 0, false, null).Holds(2));
        }

        [Fact]
        public void No_mission_plan_yet_holds_them_back()
        {
            var hold = ShipSystemsHold.Find(Craft(), null, 0, false, null);
            Assert.True(hold.Holds(2));
            Assert.Equal("there is no Mission Plan yet", hold.Why);
        }

        [Fact]
        public void The_craft_check_line_names_the_parts_that_cannot_retract()
        {
            Assert.Equal("Ship Systems that can't retract: OX-Fixed x2. Claudia leaves them stowed if the Mission Plan enters an atmosphere with them aboard.", ShipSystemsHold.CraftCheckLine(Craft()));
            Assert.Equal("", ShipSystemsHold.CraftCheckLine(new GameSnapshot()));
        }

        [Fact]
        public void The_cannot_retract_flag_stays_out_of_the_prompts_snapshot()
        {
            Assert.DoesNotContain("retract", Craft().ToJson(false), System.StringComparison.OrdinalIgnoreCase);
        }

        // ---- Time to the air ----

        [Fact]
        public void Seconds_to_the_air_on_an_ellipse_coming_down()
        {
            // 90 x 40 km over Kerbin, at 80 km coming down: the top of the air at 70 km is a short fall away.
            var top = KerbinRadius + 70000;
            var sma = KerbinRadius + 65000;
            var ecc = 25000.0 / sma;
            var seconds = ShipSystemsRule.SecondsToAir(KerbinMu, sma, ecc, KerbinRadius + 80000, -100, top);
            Assert.InRange(seconds.Value, 30, 200);
            var from85 = ShipSystemsRule.SecondsToAir(KerbinMu, sma, ecc, KerbinRadius + 85000, -100, top);
            Assert.True(from85.Value > seconds.Value);
            // Climbing past the same height, it goes over the apoapsis first, but not round an orbit (about 1,815 s).
            Assert.InRange(ShipSystemsRule.SecondsToAir(KerbinMu, sma, ecc, KerbinRadius + 80000, 100, top).Value, seconds.Value + 100, 1815);
        }

        [Fact]
        public void Seconds_to_the_air_on_an_arrival_hyperbola_and_leaving_on_one()
        {
            const double mu = 3.0136e11; // Duna
            const double radius = 320000;
            const double top = radius + 50000;
            const double sma = -200000;
            var ecc = 1 + (radius + 20000) / 200000.0;
            var coming = ShipSystemsRule.SecondsToAir(mu, sma, ecc, radius + 600000, -800, top);
            Assert.InRange(coming.Value, 100, 2000);
            Assert.Null(ShipSystemsRule.SecondsToAir(mu, sma, ecc, radius + 600000, 800, top));
        }

        [Fact]
        public void No_seconds_to_the_air_in_it_or_clear_of_it()
        {
            var sma = KerbinRadius + 90000;
            Assert.Null(ShipSystemsRule.SecondsToAir(KerbinMu, sma, 0.02, KerbinRadius + 60000, -100, KerbinRadius + 70000));
            Assert.Null(ShipSystemsRule.SecondsToAir(KerbinMu, sma, 0.001, KerbinRadius + 90000, -1, KerbinRadius + 70000));
        }
    }
}
