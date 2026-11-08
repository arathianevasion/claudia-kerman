// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #868: in flight m20261106-203509-34a6 <c>deorbit</c>'s burn search (<c>DEORBITBURN</c>) planned a Duna landing 3.89 km from
    /// the target at Pe 15,000 m, and its check after the burn (<c>REENTRYIMPACT</c>) read 148.83 km at Pe 15,169 m. Between the
    /// two calls the block's entry cue had the Ship Systems retract 4 panels and 5 antennas (the Mission's events), and the
    /// adapter reads each part's drag cubes as they are: the search flew a craft with its panels and dishes out, a craft that
    /// never meets the air. The predictor now reads what the entry will stow as stowed (<see cref="ShipSystemsMoves.StowedForEntry"/>).
    /// </summary>
    public sealed class StowedForEntryTests
    {
        private static ShipSystemPart Part(uint id, ShipSystemKind kind, ShipSystemState state, bool canRetract = true)
        {
            return new ShipSystemPart { Id = id, Kind = kind, State = state, CanRetract = canRetract };
        }

        [Fact]
        public void An_entry_meets_the_air_with_every_panel_antenna_and_radiator_that_can_retract_stowed()
        {
            var parts = new[]
            {
                Part(1, ShipSystemKind.Panel, ShipSystemState.Out), Part(2, ShipSystemKind.Antenna, ShipSystemState.Out),
                Part(3, ShipSystemKind.Radiator, ShipSystemState.Moving), Part(4, ShipSystemKind.Panel, ShipSystemState.Stowed),
                Part(5, ShipSystemKind.Panel, ShipSystemState.Out, canRetract: false), Part(6, ShipSystemKind.Antenna, ShipSystemState.Broken),
                Part(7, ShipSystemKind.Light, ShipSystemState.Out),
            };
            Assert.Equal(new uint[] { 1, 2, 3, 4 }, ShipSystemsMoves.StowedForEntry(parts, true));
        }

        [Fact]
        public void With_the_setting_off_the_craft_meets_the_air_as_it_is()
        {
            Assert.Empty(ShipSystemsMoves.StowedForEntry(new[] { Part(1, ShipSystemKind.Panel, ShipSystemState.Out) }, false));
        }

        // ---- The flight's slope ----

        private const double Gm = 301363211975.09772;
        private const double R = 320000;
        private const double Top = 50000;
        private const double Turn = 65517.859375;

        /// <summary>
        /// Duna's air, near enough for a ratio: its sea-level pressure (the body table's 0.0667 atm) falling with a 5.7 km scale
        /// height to the 50 km top, at 220 K, in Duna's CO2 (0.0429 kg/mol). The repo has no table of stock's own curve.
        /// </summary>
        private sealed class DunaAir : IReentryAir
        {
            public void Air(double altitude, double latitude, out double density, out double speedOfSound)
            {
                const double temperature = 220;
                density = PressureAtm(altitude) * 101325 * 0.0429 / (8.314462 * temperature);
                speedOfSound = Math.Sqrt(1.3 * 8.314462 * temperature / 0.0429);
            }

            public double PressureAtm(double altitude)
            {
                return altitude >= Top ? 0 : 0.0666666677961107 * Math.Exp(-Math.Max(0, altitude) / 5700);
            }
        }

        /// <summary>The flight's kept stack after the burn, 13.1 t on a 1.25 m heat shield (the research capsule's cubes, times <see cref="Out"/>).</summary>
        private sealed class Stack : IReentryCraft
        {
            private readonly ReentrySim.Capsule _cubes = new ReentrySim.Capsule();
            /// <summary>The drag of the deployables the adapter read out, as a multiple of the stowed stack's.</summary>
            public double Out = 1;

            public double MassKg
            {
                get { return 13100; }
            }

            public double DragArea(double mach, double densitySpeed)
            {
                return _cubes.DragArea(mach, densitySpeed) * Out;
            }

            public double LiftArea(double mach, double angleOfAttack)
            {
                return 0;
            }

            public IList<ReentryChute> Chutes { get; } = new List<ReentryChute>();

            public IReentryCraft AfterShield
            {
                get { return null; }
            }
        }

        /// <summary>Where the stack lands from the flight's 60 km Duna orbit after a retrograde burn to <paramref name="pe"/> m.</summary>
        private static ReentryPrediction Land(double pe, Stack craft)
        {
            var r0 = R + 60000;
            var a = (r0 + R + pe) / 2;
            var after = Conic.FromState(Gm, new Vec3(r0, 0, 0), new Vec3(0, Math.Sqrt(Gm * (2 / r0 - 1 / a)), 0), 0);
            var duna = new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = Gm, Radius = R, AtmosphereHeight = Top, SeaLevelAtm = 0.0667, RotationPeriod = Turn };
            var frame = new BodyFrame { LonOffset = 0, Handed = 1, Ut = 0, RotationPeriod = Turn };
            var landing = ReentryPredictor.Predict(duna, after, 0, 0, craft.MassKg, new DunaAir(), craft, frame);
            Assert.Null(landing.Error);
            return landing;
        }

        private static double Km(ReentryPrediction a, ReentryPrediction b)
        {
            return LandingSiteSearch.ArcDegrees(a.Latitude, a.Longitude, b.Latitude, b.Longitude) * Math.PI / 180 * R / 1000;
        }

        [Fact]
        public void The_search_on_the_craft_with_its_deployables_out_misses_the_check_after_the_burn_by_about_the_flights_145_km()
        {
            // Four 1x6 panels (about 1.1 m² a face out, 0.1 to 0.3 stowed) and four DTS-M1 dishes (0.7 m² against 0.02; the fifth antenna, a Communotron 16, is a whip) beside a
            // 1.27 m² shield face: 3.5 times the stowed drag is inside what they can add, as the sun-tracking panels turn.
            var search = Land(15000, new Stack { Out = 3.5 });
            var after = Land(15169, new Stack());
            Assert.InRange(Km(search, after), 100, 200);
        }

        [Fact]
        public void On_the_craft_that_meets_the_air_169_m_of_periapsis_moves_the_landing_a_few_km()
        {
            Assert.InRange(Km(Land(15000, new Stack()), Land(15169, new Stack())), 1, 5);
        }
    }
}
