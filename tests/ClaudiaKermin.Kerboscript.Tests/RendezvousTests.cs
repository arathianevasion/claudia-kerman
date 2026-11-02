// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// The rendezvous Building Block (#455) flown in the kOS harness: rendezvous_run, the block's own flight, with the
    /// block-internal calls answered by the real <see cref="BlockCalls"/> (ORBITBURN, EJECTIONBURN, CORRECTIONBURN) on a Game
    /// Snapshot of the orbits "now" that carries the Target Vessel, each node flown as an impulse on a <see cref="Conic"/>
    /// (<see cref="RendezvousRig"/>), and the target placed as the block test runner places it (<see cref="BlockTestTarget"/>).
    /// Flying it in KSP is tests/blocks/rendezvous (#457).
    /// </summary>
    public class RendezvousTests
    {
        static RendezvousTests()
        {
            RendezvousRigFunctions.Register();
        }

        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        private static double Number(string text)
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        /// <summary>rendezvous_run on the rig with options: the reason, the burns, the distance, relative speed and loaded at the end.</summary>
        private static string[] Fly(string options = "")
        {
            return Run("local opt is claudia_options(lexicon(" + options + "), rendezvous_defaults(), \"auto\"):opt.\n"
                + "local done is rendezvous_run(opt, \"Station\", { return test_rv_state(). }, { parameter kind, input. return test_rv_ask(kind, input). }, "
                + "{ parameter plan. return test_rv_fly(plan). }, 120).\n"
                + "print done:reason. print done:burns:join(\",\"). print done:state:distance. print done:state:rel_speed. print done:state:loaded.");
        }

        // ---- Options and the pure helpers ----

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("auto|0.1|5|none|auto", One("rendezvous_defaults():values:join(\"|\") + \"|\" + claudia_options(lexicon(), rendezvous_defaults(), \"auto\"):opt:max_duration"));
            Assert.Equal("unknown option approach_distance", One("claudia_options(lexicon(\"approach_distance\", 100), rendezvous_defaults(), \"auto\"):error"));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("\"target\", \"Station\", \"max_dv\", 500, \"max_phasing_orbits\", 2", "")]
        [InlineData("\"target\", 3", "target must be the Target Vessel or its name")]
        [InlineData("\"max_rel_inc\", 0", "max_rel_inc must be degrees above 0, not 0")]
        [InlineData("\"max_phasing_orbits\", 0", "max_phasing_orbits must be a number of orbits, 1 or more, not 0")]
        [InlineData("\"max_dv\", \"lots\"", "max_dv must be m/s above 0 or 'none', not lots")]
        [InlineData("\"max_dv\", -1", "max_dv must be m/s above 0 or 'none', not -1")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("rendezvous_unreadable(claudia_options(lexicon(" + options + "), rendezvous_defaults(), \"auto\"):opt)"));
        }

        private const string Far = "lexicon(\"loaded\", false, \"rel_speed\", 50, \"rel_inc\", 0.01)";
        private const string NoApproach = "lexicon(\"error\", \"none\")";

        [Theory]
        // MechJeb's rule order: the hand-off, a loaded target's speed, the plane, a close approach, the correction, the transfer.
        [InlineData("lexicon(\"loaded\", true, \"rel_speed\", 1.5, \"rel_inc\", 3)", NoApproach, false, "handoff")]
        [InlineData("lexicon(\"loaded\", true, \"rel_speed\", 4, \"rel_inc\", 3)", NoApproach, true, "kill_speed")]
        [InlineData("lexicon(\"loaded\", false, \"rel_speed\", 50, \"rel_inc\", 0.5)", "lexicon(\"error\", \"\", \"distance_predicted\", 300)", false, "match_plane")]
        [InlineData("lexicon(\"loaded\", false, \"rel_speed\", 50, \"rel_inc\", 0.5)", "lexicon(\"error\", \"\", \"distance_predicted\", 300)", true, "match_velocities")]
        [InlineData(Far, "lexicon(\"error\", \"\", \"distance_predicted\", 300)", false, "match_velocities")]
        [InlineData(Far, "lexicon(\"error\", \"\", \"distance_predicted\", 5)", true, "correction")]
        [InlineData(Far, "lexicon(\"error\", \"\", \"distance_predicted\", 9000)", true, "correction")]
        [InlineData(Far, "lexicon(\"error\", \"\", \"distance_predicted\", 9000)", false, "transfer")]
        [InlineData(Far, NoApproach, false, "transfer")]
        public void The_next_step_follows_the_rule_order(string state, string approach, bool transferred, string step)
        {
            Assert.Equal(step, One("rendezvous_next(" + state + ", " + approach + ", " + (transferred ? "true" : "false") + ", 0.1)"));
        }

        [Fact]
        public void The_correction_burns_halfway_to_the_approach_or_not_at_all()
        {
            Assert.Equal(1060, Number(One("rendezvous_correction_ut(0, 120, 2000)")), 9);
            Assert.Equal("-1", One("rendezvous_correction_ut(0, 120, 200)"));
        }

        [Theory]
        [InlineData(344)]
        [InlineData(90)]
        [InlineData(181)]
        public void The_phasing_orbit_leaves_the_target_at_the_transfer_s_lead_after_its_laps(double phase)
        {
            const double Gm = OrbitRig.KerbinGm;
            const double R = 700000, Rt = 719000;
            double period = 2 * Math.PI * Math.Sqrt(R * R * R / Gm), targetPeriod = 2 * Math.PI * Math.Sqrt(Rt * Rt * Rt / Gm);
            var minPeriod = 2 * Math.PI * Math.Sqrt(Math.Pow((R + 680000) / 2, 3) / Gm);
            var printed = Run("local p is rendezvous_phasing(" + string.Join(", ", new[] { phase, period, targetPeriod, R, Rt, Gm, minPeriod, 5 }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)))
                + "). print p:value. print p[\"of\"].");
            Assert.Equal("5", printed[0]);
            var of = Number(printed[1]);
            // After 5 laps of the phasing orbit the target has made "of": its lead is the Hohmann transfer's.
            var hohmann = Math.PI * Math.Sqrt(Math.Pow((R + Rt) / 2, 3) / Gm);
            var lead = 180 - 360 * hohmann / targetPeriod;
            var after = phase + 360 * of;
            Assert.InRange(((after - lead) % 360 + 360 + 180) % 360 - 180, -1e-6, 1e-6);
            // The cheapest: within one target lap of the craft's own period, and clear of Kerbin's air.
            var phasingPeriod = of * targetPeriod / 5;
            Assert.True(Math.Abs(phasingPeriod - period) <= targetPeriod / 5 + 1e-6);
            Assert.True(phasingPeriod >= minPeriod);
        }

        // ---- Flown on the rig ----

        [Fact]
        public void A_target_off_plane_and_far_behind_takes_all_five_steps_and_ends_loaded_under_2_m_s()
        {
            // 200 km behind on an orbit 30 m/s faster and 6 m/s off plane (0.15 degrees): the window is about 24 orbits away.
            RendezvousRig.Start(-200000, 30, 6);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.Equal("match_plane,phasing,transfer,correction,match_velocities", printed[1]);
            Assert.InRange(Number(printed[2]), 100, 2000);
            Assert.InRange(Number(printed[3]), 0, 2);
            Assert.Equal("True", printed[4]);
        }

        [Fact]
        public void A_target_already_in_the_plane_skips_the_plane_match()
        {
            RendezvousRig.Start(-200000, 30, 0);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.Equal("phasing,transfer,correction,match_velocities", printed[1]);
            Assert.Equal("True", printed[4]);
        }

        [Fact]
        public void A_target_at_the_right_phase_skips_the_phasing_orbit()
        {
            // 200 km ahead: the transfer window is under an orbit away.
            RendezvousRig.Start(200000, 30, 6);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.DoesNotContain("phasing", printed[1]);
            Assert.EndsWith("transfer,correction,match_velocities", printed[1]);
            Assert.Equal("True", printed[4]);
        }

        [Fact]
        public void A_burn_past_max_dv_fails_before_it_flies()
        {
            RendezvousRig.Start(-200000, 30, 6);
            var printed = Fly("\"max_dv\", 1");
            Assert.StartsWith("needs ", printed[0]);
            Assert.EndsWith(" m/s, over max_dv 1", printed[0]);
            Assert.Equal("", printed[1]);
            Assert.Equal(0, RendezvousRig.Flights);
        }

        [Fact]
        public void A_loaded_target_drifting_apart_is_stopped_where_it_is()
        {
            // 1 km ahead and 5 m/s down in the craft's frame there (about 8 m/s apart, the two velocities being 0.08 degrees round
            // the circle from each other): loaded, so the relative speed is killed at once.
            RendezvousRig.Start(1000, 0, 0, radial: -5);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.Equal("kill_speed", printed[1]);
            Assert.InRange(Number(printed[3]), 0, 2);
        }
    }

    /// <summary>
    /// The craft (circular at 100 km round Kerbin, equatorial) and the Target Vessel "Station" placed relative to it at UT 0, as
    /// two conics; "now" moves to just after each burn. The target is loaded within 2,250 m, KSP's on-rails unpack range.
    /// </summary>
    internal static class RendezvousRig
    {
        private const double R = OrbitRig.KerbinR + 100000;
        public const double LoadRange = 2250;

        public static Conic Craft;
        public static Conic Target;
        public static double Ut;
        public static int Flights;

        public static void Start(double along, double prograde, double normal, double radial = 0)
        {
            var craft = new SnapshotOrbit { SemiMajorAxis = R, Body = "Kerbin" };
            Craft = Conic.FromElements(OrbitRig.KerbinGm, R, 0, 0, 0, 0, 0, 0);
            var placed = BlockTestTarget.Place(OrbitRig.KerbinGm, craft, 0, along, prograde, normal, radial);
            Target = Conic.FromElements(OrbitRig.KerbinGm, placed.SemiMajorAxis, placed.Eccentricity, placed.Inclination, placed.Lan,
                placed.ArgumentOfPeriapsis, placed.MeanAnomalyAtEpoch, placed.Epoch);
            Ut = 0;
            Flights = 0;
        }

        public static GameSnapshot Snapshot()
        {
            var snapshot = new GameSnapshot { Ut = Ut, Situation = "ORBITING", Body = "Kerbin", Orbit = OrbitRig.Elements(Craft) };
            OrbitRig.AddBodies(snapshot);
            snapshot.Target = new SnapshotTarget { Name = "Station", Body = "Kerbin", Situation = "ORBITING", Orbit = OrbitRig.Elements(Target) };
            return snapshot;
        }

        private static double Degrees(Vec3 a, Vec3 b)
        {
            return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(a, b) / (a.Length * b.Length)))) * 180 / Math.PI;
        }

        /// <summary>rendezvous_state's lexicon for the two orbits now.</summary>
        public static Dictionary<string, object> State()
        {
            Vec3 rc, vc, rt, vt;
            Craft.State(Ut, out rc, out vc);
            Target.State(Ut, out rt, out vt);
            var own = Vec3.Cross(rc, vc);
            var phase = Degrees(rc, rt);
            if (Vec3.Dot(Vec3.Cross(rc, rt), own) < 0) phase = 360 - phase;
            var low = OrbitRig.KerbinR + 70000 + 10000;
            var a = (rc.Length + low) / 2;
            var elements = OrbitRig.Elements(Target);
            return new Dictionary<string, object>
            {
                { "ut", Ut }, { "loaded", (rt - rc).Length < LoadRange }, { "distance", (rt - rc).Length }, { "rel_speed", (vt - vc).Length },
                { "rel_inc", Degrees(own, Vec3.Cross(rt, vt)) }, { "phase", phase }, { "period", Craft.Period }, { "target_period", Target.Period },
                { "r", Craft.A }, { "target_r", Target.A }, { "mu", OrbitRig.KerbinGm }, { "min_period", 2 * Math.PI * Math.Sqrt(a * a * a / OrbitRig.KerbinGm) },
                { "body", "Kerbin" },
                { "orbit", new Dictionary<string, object>
                    {
                        { "target_sma", elements.SemiMajorAxis }, { "target_ecc", elements.Eccentricity }, { "target_inc", elements.Inclination },
                        { "target_lan", elements.Lan }, { "target_argpe", elements.ArgumentOfPeriapsis },
                        { "target_mna", elements.MeanAnomalyAtEpoch * 180 / Math.PI }, { "target_epoch", elements.Epoch },
                    }
                },
            };
        }

        public static IDictionary<string, object> Ask(string kind, IDictionary<string, object> input)
        {
            var snapshot = Snapshot();
            if (kind == "orbit") return BlockCalls.OrbitBurn(input, snapshot);
            if (kind == "transfer") return BlockCalls.EjectionBurn(input, snapshot);
            return BlockCalls.CorrectionBurn(input, snapshot);
        }

        /// <summary>Flies plan's node as an impulse at its UT; "now" is just after it.</summary>
        public static Dictionary<string, object> Fly(IDictionary<string, object> plan)
        {
            Flights++;
            var ut = (double)plan["ut"];
            Vec3 r, v;
            Craft.State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var burn = along * (double)plan["prograde"] + up * (double)plan["normal"] + Vec3.Cross(along, up) * (double)plan["radial"];
            Craft = Conic.FromState(OrbitRig.KerbinGm, r, v + burn, ut);
            Ut = ut + 1;
            return new Dictionary<string, object> { { "success", true }, { "reason", "" }, { "dv_used", burn.Length } };
        }
    }

    /// <summary>The rig's kOS functions: the state now, a block-internal call, and a flight.</summary>
    internal static class RendezvousRigFunctions
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            foreach (var type in typeof(RendezvousRigFunctions).GetNestedTypes())
                foreach (FunctionAttribute attribute in type.GetCustomAttributes(typeof(FunctionAttribute), false))
                    FunctionManager.RegisterMethod(attribute, type);
            _registered = true;
        }

        [Function("test_rv_state")]
        public class FunctionState : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(RendezvousRig.State());
            }
        }

        [Function("test_rv_ask")]
        public class FunctionAsk : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var input = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                var kind = PopStructureAssertEncapsulated(shared, true).ToString();
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(RendezvousRig.Ask(kind, input));
            }
        }

        [Function("test_rv_fly")]
        public class FunctionFly : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var plan = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(RendezvousRig.Fly(plan));
            }
        }
    }
}
