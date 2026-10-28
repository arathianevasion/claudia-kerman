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
    /// The change_orbit Building Block (#444) flown in the kOS harness: change_orbit_run, the block's own flight, with
    /// ORBITBURN answered by the real <see cref="BlockCalls.OrbitBurn"/> on a Game Snapshot of the orbit "now", the node
    /// flown as an impulse on a <see cref="Conic"/>, and the elements read back from the orbit it leaves
    /// (<see cref="OrbitRig"/>). The block itself only adds claudia_begin, claudia_finish and those three contacts with the
    /// game; flying it in KSP is tests/blocks/change_orbit_*.
    /// </summary>
    public class ChangeOrbitTests
    {
        static ChangeOrbitTests()
        {
            OrbitRigFunctions.Register();
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

        /// <summary>change_orbit_run on the rig with options, then each expression printed.</summary>
        private static string[] Fly(string options, params string[] prints)
        {
            return Run("local opt is claudia_options(lexicon(" + options + "), change_orbit_defaults(), \"auto\"):opt.\n"
                + "local done is change_orbit_run(opt, " + Solve + ", " + FlyIt + ", " + Read + ").\n"
                + string.Join("\n", prints.Select(p => "print " + p + ".")));
        }

        private const string Solve = "{ parameter ask. return test_orbitburn(ask). }";
        private const string FlyIt = "{ parameter plan. return test_fly(plan). }";
        private const string Read = "{ parameter element. return test_element(element). }";

        // ---- Options ----

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("required|none|none|none|none|none|none|auto|none|auto",
                One("change_orbit_defaults():values:join(\"|\") + \"|\" + claudia_options(lexicon(), change_orbit_defaults(), \"auto\"):opt:max_duration"));
        }

        [Theory]
        [InlineData("\"goal\", \"set_inc\", \"value\", 90", "")]
        [InlineData("\"goal\", \"match_plane\", \"target\", \"Minmus\", \"tolerance\", 0.2, \"max_dv\", 500", "")]
        [InlineData("\"goal\", \"resonant\", \"value\", 2, \"of\", 3, \"at\", \"ap\"", "")]
        [InlineData("\"goal\", \"set_inc\", \"value\", 90, \"at\", 5000", "")]
        [InlineData("", "goal is required")]
        [InlineData("\"goal\", 5", "goal must be a solve_burn goal's name, not 5")]
        [InlineData("\"goal\", \"set_inc\", \"value\", \"high\"", "value must be a number, not high")]
        [InlineData("\"goal\", \"ellipticize\", \"pe\", 80000, \"ap\", \"lots\"", "ap must be a number, not lots")]
        [InlineData("\"goal\", \"set_inc\", \"value\", 90, \"tolerance\", 0", "tolerance must be 'auto' or a number above 0, not 0")]
        [InlineData("\"goal\", \"set_inc\", \"value\", 90, \"tolerance\", \"tight\"", "tolerance must be 'auto' or a number above 0, not tight")]
        [InlineData("\"goal\", \"set_inc\", \"value\", 90, \"max_dv\", -5", "max_dv must be m/s above 0 or 'none', not -5")]
        [InlineData("\"goal\", \"match_plane\", \"target\", 3", "target must be a body or its name")]
        [InlineData("\"goal\", \"set_inc\", \"value\", 90, \"at\", true", "at must be a word or a UT, not True")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("change_orbit_unreadable(claudia_options(lexicon(" + options + "), change_orbit_defaults(), \"auto\"):opt)"));
        }

        [Fact]
        public void An_unknown_option_is_refused_and_the_common_ones_are_taken()
        {
            Assert.Equal("unknown option direction", One("claudia_options(lexicon(\"goal\", \"next_patch_pe\", \"direction\", \"retrograde\"), change_orbit_defaults(), \"auto\"):error"));
            Assert.Equal("", One("claudia_options(lexicon(\"goal\", \"set_inc\", \"value\", 90, \"stage_limit\", 1, \"drop_empty\", list(\"side tanks\"), \"max_duration\", 900), change_orbit_defaults(), \"auto\"):error"));
        }

        [Fact]
        public void Orbitburn_is_asked_the_goal_and_only_the_inputs_given()
        {
            Assert.Equal(new[] { "goal,value,target,of", "resonant", "2", "Mun", "3" },
                Run("local q is change_orbit_ask(claudia_options(lexicon(\"goal\", \"resonant\", \"value\", 2, \"target\", \"Mun\", \"of\", 3), change_orbit_defaults(), \"auto\"):opt).\n"
                    + "print q:keys:join(\",\"). print q:goal. print q:value. print q:target. print q[\"of\"]."));
            Assert.Equal("goal,at", One("change_orbit_ask(claudia_options(lexicon(\"goal\", \"circularize\", \"at\", \"ap\"), change_orbit_defaults(), \"auto\"):opt):keys:join(\",\")"));
        }

        [Theory]
        [InlineData("inc", 90, "\"auto\"", 0.5)]
        [InlineData("rel_inc", 0, "\"auto\"", 0.5)]
        [InlineData("lan", 60, "\"auto\"", 0.5)]
        [InlineData("ecc", 0, "\"auto\"", 0.01)]
        [InlineData("pe", 80000, "\"auto\"", 1000)]
        [InlineData("next_patch_pe", 30000, "\"auto\"", 1000)]
        [InlineData("period", 6000, "\"auto\"", 60)]
        [InlineData("inc", 90, "0.2", 0.2)]
        public void Each_element_has_its_own_default_tolerance(string element, double wanted, string tolerance, double expected)
        {
            Assert.Equal(expected, Number(One("change_orbit_tolerance(\"" + element + "\", " + wanted.ToString(CultureInfo.InvariantCulture) + ", " + tolerance + ")")), 9);
        }

        [Fact]
        public void The_lan_is_judged_the_short_way_round()
        {
            Assert.Equal(2, Number(One("change_orbit_off(\"lan\", 359, 1)")), 9);
            Assert.Equal(358, Number(One("change_orbit_off(\"inc\", 359, 1)")), 9);
        }

        [Fact]
        public void The_judge_names_the_first_element_off_and_how_far()
        {
            Assert.Equal("", One("change_orbit_judge(lexicon(\"pe\", 80000, \"ap\", 300000), lexicon(\"pe\", 80400, \"ap\", 299500), \"auto\")"));
            Assert.Equal("ap 298000 is over tolerance 1000 from 300000",
                One("change_orbit_judge(lexicon(\"pe\", 80000, \"ap\", 300000), lexicon(\"pe\", 80400, \"ap\", 298000), \"auto\")"));
            Assert.Equal("inc 89.7 is over tolerance 0.2 from 90", One("change_orbit_judge(lexicon(\"inc\", 90), lexicon(\"inc\", 89.7), 0.2)"));
        }

        // ---- Flying set_inc, match_plane and resonant ----

        /// <summary>A 100 km by 120 km Kerbin orbit, just past its periapsis at UT 0 (#443's known gap: not exactly on it).</summary>
        private static GameSnapshot Low(double inclination = 0, double lan = 0)
        {
            return OrbitRig.Snapshot(100000, 120000, inclination, lan, 0, 0.01, 0);
        }

        [Fact]
        public void Set_inc_to_90_ends_within_tolerance_with_the_contract_filled()
        {
            OrbitRig.Start(Low());
            var printed = Fly("\"goal\", \"set_inc\", \"value\", 90", "done:reason", "done:extra:inc_actual", "done:extra:burn_ut", "done:extra:keys:join(\",\")");
            Assert.Equal("", printed[0]);
            Assert.InRange(Number(printed[1]), 89.5, 90.5);
            // The equatorial orbit's burn is at the apoapsis, half its 2000 s orbit on, solved from the snapshot at the call.
            Assert.InRange(Number(printed[2]), 950, 1050);
            Assert.Equal("burn_ut,inc_actual", printed[3]);
            Assert.Equal(1, OrbitRig.Flights);
            // 2 v sin(45°) at the apoapsis.
            var v = Math.Sqrt(OrbitRig.KerbinGm * (2 / 720000.0 - 1 / 710000.0));
            Assert.InRange(OrbitRig.DvFlown / (2 * v * Math.Sin(Math.PI / 4)), 0.995, 1.005);
        }

        [Fact]
        public void Match_plane_to_minmus_ends_within_tolerance_with_the_contract_filled()
        {
            OrbitRig.Start(Low());
            var printed = Fly("\"goal\", \"match_plane\", \"target\", \"Minmus\", \"tolerance\", 0.2", "done:reason", "done:extra:rel_inc_actual", "done:extra:burn_ut");
            Assert.Equal("", printed[0]);
            Assert.InRange(Number(printed[1]), 0, 0.2);
            Assert.True(Number(printed[2]) > 0);
            Assert.Equal(1, OrbitRig.Flights);
        }

        [Fact]
        public void A_two_to_three_resonant_orbit_ends_within_tolerance_with_the_contract_filled()
        {
            OrbitRig.Start(Low());
            var before = OrbitRig.Orbit().Period;
            var printed = Fly("\"goal\", \"resonant\", \"value\", 2, \"of\", 3", "done:reason", "done:extra:period_actual", "done:extra:burn_ut");
            Assert.Equal("", printed[0]);
            Assert.InRange(Number(printed[1]) / before, 1.5 * 0.99, 1.5 * 1.01);
            Assert.True(Number(printed[2]) > 0);
        }

        // ---- Solved when the block starts ----

        [Fact]
        public void The_node_is_solved_from_the_orbit_as_it_is_when_the_block_starts_not_when_it_was_planned()
        {
            // Planned on a 10° orbit; by the time the block flies, another burn has tilted it to 30° and an hour has passed.
            // The plan's node (an 80° turn) misses there; the block's own, solved at its start, does not.
            var planned = Low(10, 0);
            var moved = Low(30, 0);
            moved.Ut = 3600;
            OrbitRig.Start(planned, moved);
            var printed = Run(
                "local opt is claudia_options(lexicon(\"goal\", \"set_inc\", \"value\", 90), change_orbit_defaults(), \"auto\"):opt.\n"
                + "local stale is test_orbitburn(change_orbit_ask(opt)).\n"
                + "test_scene(1).\n"
                + "local old is change_orbit_run(opt, { parameter ask. return stale. }, " + FlyIt + ", " + Read + ").\n"
                + "print old:reason.\n"
                + "test_scene(1).\n"
                + "local done is change_orbit_run(opt, " + Solve + ", " + FlyIt + ", " + Read + ").\n"
                + "print done:reason. print done:extra:inc_actual. print done:extra:burn_ut. print stale:ut.");
            Assert.StartsWith("inc ", printed[0]);
            Assert.Contains("is over tolerance 0.5 from 90", printed[0]);
            Assert.Equal("", printed[1]);
            Assert.InRange(Number(printed[2]), 89.5, 90.5);
            // The block's ORBITBURN saw the snapshot at its start (UT 3600), so its burn is after it; the plan's was before.
            Assert.Equal(3600, OrbitRig.SolvedAt.Last());
            Assert.True(Number(printed[3]) > 3600 && Number(printed[4]) < 3600, printed[3] + " / " + printed[4]);
        }

        // ---- Failures ----

        [Fact]
        public void A_goal_orbitburn_cannot_solve_fails_with_its_reason_and_no_burn()
        {
            OrbitRig.Start(Low());
            var printed = Fly("\"goal\", \"set_inc\", \"value\", 200", "done:reason", "done:extra:burn_ut");
            Assert.Equal(new[] { "set_inc's value is an inclination from 0 to 180 degrees", "-1" }, printed);
            Assert.Equal(0, OrbitRig.Flights);
        }

        [Fact]
        public void A_burn_over_max_dv_fails_before_it_flies()
        {
            OrbitRig.Start(Low());
            var printed = Fly("\"goal\", \"set_inc\", \"value\", 90, \"max_dv\", 100", "done:reason", "done:extra:burn_ut");
            Assert.StartsWith("needs ", printed[0]);
            Assert.EndsWith(" m/s, over max_dv 100", printed[0]);
            Assert.Equal("-1", printed[1]);
            Assert.Equal(0, OrbitRig.Flights);
        }

        [Fact]
        public void A_failed_flight_is_the_reason_with_the_element_read_where_it_ended()
        {
            OrbitRig.Start(Low());
            OrbitRig.FailWith = "out of fuel";
            var printed = Fly("\"goal\", \"set_inc\", \"value\", 90", "done:reason", "done:extra:inc_actual");
            Assert.Equal(new[] { "out of fuel", "0" }, printed);
        }

        [Fact]
        public void A_burn_that_ends_short_is_judged_over_tolerance()
        {
            OrbitRig.Start(Low());
            OrbitRig.Scale = 0.9;
            var printed = Fly("\"goal\", \"set_inc\", \"value\", 90", "done:reason");
            Assert.StartsWith("inc ", printed[0]);
            Assert.EndsWith("is over tolerance 0.5 from 90", printed[0]);
        }

        [Fact]
        public void An_orbit_already_there_needs_no_burn()
        {
            OrbitRig.Start(Low(90, 0));
            var printed = Fly("\"goal\", \"set_inc\", \"value\", 90", "done:reason", "done:extra:burn_ut", "done:extra:inc_actual");
            Assert.Equal(new[] { "", "-1", "90" }, printed);
            Assert.Equal(0, OrbitRig.Flights);
        }
    }

    /// <summary>
    /// The harness's stand-in for the game behind change_orbit: Game Snapshots ("scenes") of the craft's orbit, the one "now"
    /// answering ORBITBURN through the real <see cref="BlockCalls.OrbitBurn"/>; a node flown as an impulse at its UT; and
    /// the elements of the orbit "now". Static, as kOS functions are found by name: only <see cref="ChangeOrbitTests"/>
    /// uses it, and xUnit runs one class's tests one at a time.
    /// </summary>
    internal static class OrbitRig
    {
        public const double KerbinGm = 3.5316e12;
        public const double KerbinR = 600000;

        public static List<GameSnapshot> Scenes = new List<GameSnapshot>();
        public static GameSnapshot Now;
        public static int Flights;
        public static double DvFlown;
        public static string FailWith;
        public static double Scale = 1;
        public static List<double> SolvedAt = new List<double>();

        public static void Start(params GameSnapshot[] scenes)
        {
            Scenes = scenes.ToList();
            Now = scenes[0];
            Flights = 0;
            DvFlown = 0;
            FailWith = null;
            Scale = 1;
            SolvedAt = new List<double>();
        }

        /// <summary>A Kerbin orbit between pe and ap (m above sea level) at UT <paramref name="ut"/>, with Mun and Minmus.</summary>
        public static GameSnapshot Snapshot(double pe, double ap, double inclination, double lan, double argPe, double phase, double ut)
        {
            double rp = KerbinR + pe, ra = KerbinR + ap;
            var snapshot = new GameSnapshot { Ut = ut, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = inclination, Lan = lan, ArgumentOfPeriapsis = argPe,
                MeanAnomalyAtEpoch = phase, Epoch = 0, Body = "Kerbin", Periapsis = pe, Apoapsis = ap,
            };
            AddBodies(snapshot);
            return snapshot;
        }

        private static void AddBodies(GameSnapshot snapshot)
        {
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = 1,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
            });
        }

        public static Conic Orbit()
        {
            var o = Now.Orbit;
            return Conic.FromElements(KerbinGm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        /// <summary>Flies plan's node as an impulse (scaled by <see cref="Scale"/>) at its UT; "now" is just after it.</summary>
        public static Lexicon Fly(IDictionary<string, object> plan)
        {
            Flights++;
            var result = new Dictionary<string, object> { { "success", FailWith == null }, { "reason", FailWith ?? "" }, { "dv_used", 0.0 } };
            if (FailWith != null) return (Lexicon)KosPlain.From(result);
            var ut = (double)plan["ut"];
            Vec3 r, v;
            Orbit().State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var burn = (along * (double)plan["prograde"] + up * (double)plan["normal"] + Vec3.Cross(along, up) * (double)plan["radial"]) * Scale;
            var after = Conic.FromState(KerbinGm, r, v + burn, ut);
            DvFlown += burn.Length;
            result["dv_used"] = burn.Length;
            var next = new GameSnapshot { Ut = ut + 1, Situation = "ORBITING", Body = "Kerbin", Orbit = Elements(after) };
            AddBodies(next);
            Now = next;
            return (Lexicon)KosPlain.From(result);
        }

        /// <summary>An element of the orbit now, by change_orbit's names; rel_inc against Minmus.</summary>
        public static double Element(string element)
        {
            var orbit = Orbit();
            switch (element)
            {
                case "inc": return orbit.Inclination;
                case "rel_inc":
                    var w = new BodySystem(Now.Bodies).OrbitOf(Now.Bodies.Single(b => b.Name == "Minmus")).W;
                    return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(orbit.W, w)))) * 180 / Math.PI;
                case "period": return orbit.Period;
                default: throw new ArgumentException("the rig reads no " + element);
            }
        }

        /// <summary>KSP's elements of <paramref name="orbit"/>, as CorrectionBurnTests builds them.</summary>
        private static SnapshotOrbit Elements(Conic orbit)
        {
            var lan = Math.Atan2(orbit.W.X, -orbit.W.Y);
            var node = new Vec3(Math.Cos(lan), Math.Sin(lan), 0);
            var argPe = Math.Atan2(Vec3.Dot(orbit.P, Vec3.Cross(orbit.W, node)), Vec3.Dot(orbit.P, node));
            return new SnapshotOrbit
            {
                SemiMajorAxis = orbit.A, Eccentricity = orbit.E, Inclination = orbit.Inclination, Lan = lan * 180 / Math.PI,
                ArgumentOfPeriapsis = argPe * 180 / Math.PI, MeanAnomalyAtEpoch = orbit.M0, Epoch = orbit.Epoch, Body = "Kerbin",
                Periapsis = orbit.PeR - KerbinR, Apoapsis = orbit.ApR - KerbinR,
            };
        }
    }

    /// <summary>kOS values to plain C# and back, as the mod's KosValues does (it lives in the KSP-bound assembly).</summary>
    internal static class KosPlain
    {
        public static object To(Structure value)
        {
            var lexicon = value as Lexicon;
            if (lexicon != null)
            {
                var result = new Dictionary<string, object>();
                foreach (var entry in lexicon) result[entry.Key.ToString()] = To(entry.Value);
                return result;
            }
            var scalar = value as ScalarValue;
            if (scalar != null) return scalar.GetDoubleValue();
            var boolean = value as BooleanValue;
            if (boolean != null) return boolean.Value;
            return value == null ? null : value.ToString();
        }

        public static Structure From(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var lexicon = new Lexicon();
                foreach (var entry in dictionary) lexicon.Add(new StringValue(entry.Key), From(entry.Value));
                return lexicon;
            }
            if (value is double) return ScalarValue.Create((double)value);
            if (value is bool) return new BooleanValue((bool)value);
            return new StringValue(value == null ? "" : value.ToString());
        }
    }

    /// <summary>The rig's kOS functions: ORBITBURN on the scene now, a flight, an element, and a change of scene.</summary>
    internal static class OrbitRigFunctions
    {
        private static bool _registered;

        /// <summary>
        /// Registers these functions with kOS once. The harness's walk finds only kOS.Safe's own [Function]s, so this
        /// assembly's are added by hand, as the walk would add them.
        /// </summary>
        public static void Register()
        {
            if (_registered) return;
            foreach (var type in typeof(OrbitRigFunctions).GetNestedTypes())
                foreach (FunctionAttribute attribute in type.GetCustomAttributes(typeof(FunctionAttribute), false))
                    FunctionManager.RegisterMethod(attribute, type);
            _registered = true;
        }

        [Function("test_orbitburn")]
        public class FunctionOrbitBurn : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var ask = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                OrbitRig.SolvedAt.Add(OrbitRig.Now.Ut);
                ReturnValue = KosPlain.From(BlockCalls.OrbitBurn(ask, OrbitRig.Now));
            }
        }

        [Function("test_fly")]
        public class FunctionFly : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var plan = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = OrbitRig.Fly(plan);
            }
        }

        [Function("test_element")]
        public class FunctionElement : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var element = PopStructureAssertEncapsulated(shared, true).ToString();
                AssertArgBottomAndConsume(shared);
                ReturnValue = ScalarValue.Create(OrbitRig.Element(element));
            }
        }

        [Function("test_scene")]
        public class FunctionScene : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var index = (int)((ScalarValue)PopStructureAssertEncapsulated(shared, true)).GetDoubleValue();
                AssertArgBottomAndConsume(shared);
                OrbitRig.Now = OrbitRig.Scenes[index];
                ReturnValue = ScalarValue.Create(index);
            }
        }
    }
}
