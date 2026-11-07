// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Landing predictions fly the craft <c>reentry</c> will fly (#814): <c>ENTRYCRAFT</c>'s answer, read once at block start, carried as <c>craft</c>
    /// into <c>RETURNBURN</c>, <c>CORRECTIONBURN</c>, <c>REENTRYIMPACT</c> and <c>DEORBITBURN</c>. The craft is the Mun flight's shape
    /// (m20261106-132839-970f: about 3.2 t aiming, about 1.4 t entering): a pod with its chute and shield, Decoupler.1, a tank and an engine.
    /// The adapter builds the predictor's craft from <see cref="BlockCalls.LeftOut"/> and <see cref="BlockCalls.ShieldDrop"/>; <see cref="PartsCraft"/> does the same here.
    /// </summary>
    public sealed class EntryCraftTests
    {
        private const uint Pod = 1, PodChute = 2, Shield = 3, Dec = 4, Tank = 5, Engine = 6, PodEngine = 7;
        private const double KscLat = -0.0972;
        private const double KscLng = -74.5577;

        private readonly ITestOutputHelper _out;

        public EntryCraftTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>The rows of #814's table, and a stack below the shield in each but the one with no shield.</summary>
        public enum Shape
        {
            /// <summary>Its chute lands the capsule softly, so reentry drops the stack.</summary>
            Drops,
            NoShield,
            /// <summary>No chute aboard: reentry keeps everything and lands on the stack's engine.</summary>
            Engines,
            /// <summary>No decoupler between the shield and the tank.</summary>
            Stuck,
        }

        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
            RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };
        private static readonly SnapshotBody Mun = new SnapshotBody
        {
            Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = 1.7,
        };

        private static string Uid(uint id)
        {
            return "uid" + id.ToString(CultureInfo.InvariantCulture);
        }

        private static SnapshotPart Part(uint id, uint? parent, string name, double massT, params string[] tags)
        {
            return new SnapshotPart { Id = id, ParentId = parent, Name = name, Title = name, MassT = massT, Tags = tags.ToList() };
        }

        /// <summary>The craft's parts in <paramref name="shape"/>: a 1.45 t capsule (pod, chute, shield, Decoupler.1, which stays with the shield) over a 1.75 t stack.</summary>
        private static List<SnapshotPart> Parts(Shape shape, bool stack = true, bool stackLit = true, bool podEngine = false)
        {
            var parts = new List<SnapshotPart> { Part(Pod, null, "mk1pod.v2", 0.9, "command", "crew_seat") };
            if (shape != Shape.Engines)
            {
                parts.Add(Part(PodChute, Pod, "parachuteSingle", 0.1, "parachute"));
                parts.Last().Chute = ReentrySim.Mk16();
            }
            else parts[0].MassT += 0.1;
            if (shape != Shape.NoShield) parts.Add(Part(Shield, Pod, "HeatShield1", 0.4, "heat_shield", "decoupler"));
            else parts[0].MassT += 0.4;
            var top = shape == Shape.NoShield ? Pod : Shield;
            if (shape != Shape.Stuck) parts.Add(Part(Dec, top, "Decoupler.1", 0.05, "decoupler"));
            else parts[0].MassT += 0.05;
            if (podEngine)
            {
                parts.Add(Part(PodEngine, Pod, "microEngine", 0.1, "engine"));
                parts.Last().DryMassT = 0.1;
                parts.Last().EngineLit = parts.Last().EngineFuelled = true;
            }
            if (!stack) return parts;
            parts.Add(Part(Tank, shape == Shape.Stuck ? Shield : Dec, "fuelTank", 1.25));
            parts.Last().DryMassT = 0.15;
            parts.Add(Part(Engine, Tank, "liquidEngine3.v2", 0.5, "engine"));
            parts.Last().DryMassT = 0.5;
            parts.Last().EngineLit = stackLit;
            parts.Last().EngineFuelled = true;
            return parts;
        }

        private static GameSnapshot With(GameSnapshot snapshot, List<SnapshotPart> parts)
        {
            snapshot.Craft = new SnapshotCraft { Name = "Mun Shot", Parts = parts, MassTonnes = parts.Sum(p => p.MassT) };
            return snapshot;
        }

        /// <summary>In a 30 km orbit of the Mun, as <c>return</c> starts.</summary>
        private static GameSnapshot AtMun(List<SnapshotPart> parts)
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = Mun.Radius + 30000, Eccentricity = 0, Body = "Mun", Periapsis = 30000, Apoapsis = 30000, Epoch = 1000 };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, Mun });
            return With(snapshot, parts);
        }

        /// <summary>In an 80 km circular equatorial orbit of Kerbin, crossing longitude 0 at UT 0, as <c>deorbit</c> starts.</summary>
        private static GameSnapshot AtKerbin(List<SnapshotPart> parts)
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = Kerbin.Radius + 80000, Eccentricity = 0, Body = "Kerbin", Periapsis = 80000, Apoapsis = 80000, Epoch = 0 };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin });
            return With(snapshot, parts);
        }

        /// <summary><c>ENTRYCRAFT</c> as the block asks it: <c>return</c> names its planet, <c>deorbit</c> its own drop.</summary>
        private static IDictionary<string, object> Read(GameSnapshot snapshot, string body = null, params uint[] ownDrop)
        {
            var ask = new Dictionary<string, object> { { "home", Uid(Pod) } };
            if (body != null) ask["body"] = body;
            if (ownDrop.Length > 0) ask["leave_out"] = ownDrop.Select(id => (object)Uid(id)).ToList();
            return BlockCalls.EntryCraftCall(ask, snapshot, null, 0, "", Uid);
        }

        private static List<string> Uids(object list)
        {
            return ((IEnumerable<object>)list).Select(u => (string)u).OrderBy(u => u, StringComparer.Ordinal).ToList();
        }

        private static List<string> Uids(params uint[] ids)
        {
            return ids.Select(Uid).OrderBy(u => u, StringComparer.Ordinal).ToList();
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The predictor's craft as the adapter builds it from an ask: the parts aboard less those left out, the research's capsule drag, and
        /// half as much again for each tank and engine of the stack still on; with a shield drop, the craft after it.
        /// </summary>
        private sealed class PartsCraft : IReentryCraft
        {
            private readonly ReentrySim.Capsule _capsule = new ReentrySim.Capsule();
            private readonly double _mass;
            private readonly double _scale;
            private readonly IReentryCraft _after;

            public PartsCraft(GameSnapshot snapshot, IEnumerable<string> leaveOut, IEnumerable<string> shieldDrop)
            {
                var gone = new HashSet<string>(leaveOut);
                var aboard = snapshot.Craft.Parts.Where(p => !gone.Contains(Uid(p.Id))).ToList();
                _mass = aboard.Sum(p => p.MassT) * 1000;
                _scale = 1 + 0.5 * aboard.Count(p => p.Id == Tank || p.Id == Engine);
                var drop = shieldDrop.ToList();
                if (drop.Count > 0) _after = new PartsCraft(snapshot, gone.Concat(drop), new string[0]);
            }

            public static PartsCraft For(GameSnapshot snapshot, IDictionary<string, object> ask)
            {
                return new PartsCraft(snapshot, BlockCalls.LeftOut(ask), BlockCalls.ShieldDrop(ask));
            }

            public double MassKg { get { return _mass; } }
            public double DragArea(double mach, double densitySpeed) { return _capsule.DragArea(mach, densitySpeed) * _scale; }
            public double LiftArea(double mach, double angleOfAttack) { return 0; }
            public IList<ReentryChute> Chutes { get { return new ReentryChute[0]; } }
            public IReentryCraft AfterShield { get { return _after; } }
        }

        // ---- The table: the craft each Entry flies ----

        [Theory]
        [InlineData(Shape.Drops, new uint[] { Tank, Engine }, true)]
        [InlineData(Shape.NoShield, new uint[0], false)]
        [InlineData(Shape.Engines, new uint[0], false)]
        [InlineData(Shape.Stuck, new uint[0], false)]
        public void Each_row_of_the_table_is_the_craft_return_its_correction_and_deorbit_predict(Shape shape, uint[] leftOut, bool shieldGoes)
        {
            var atMun = Read(AtMun(Parts(shape)), "Kerbin");
            var atKerbin = Read(AtKerbin(Parts(shape)));
            foreach (var answer in new[] { atMun, atKerbin })
            {
                Assert.Equal("", answer["error"]);
                Assert.Equal(Uids(leftOut), Uids(answer["leave_out"]));
                Assert.Equal(shieldGoes ? Uids(Shield) : Uids(), Uids(answer["shield_drop"]));
                // Each block's ask carries it as it is (return's burn_ask and its correction's, deorbit_query, the checks after): the adapter's craft reads these.
                var ask = new Dictionary<string, object> { { "lat", 0.0 }, { "craft", answer } };
                Assert.Equal(Uids(leftOut), BlockCalls.LeftOut(ask).OrderBy(u => u, StringComparer.Ordinal).ToList());
                Assert.Equal(shieldGoes ? Uids(Shield) : Uids(), BlockCalls.ShieldDrop(ask).OrderBy(u => u, StringComparer.Ordinal).ToList());
            }
            _out.WriteLine(shape + ": " + atMun["why"]);
        }

        [Fact]
        public void A_return_read_at_the_moon_counts_the_planets_Entry_not_the_moons_lack_of_air()
        {
            // At the Mun the Entry ahead is Kerbin's: its chutes are judged in Kerbin's air, so the capsule's chute lands it and the stack drops.
            var answer = Read(AtMun(Parts(Shape.Drops)), "Kerbin");
            Assert.Equal(Uids(Tank, Engine), Uids(answer["leave_out"]));
            Assert.StartsWith("without its stack (fuelTank, liquidEngine3.v2), shield jettisoned, 1.45 t", (string)answer["why"]);
        }

        [Fact]
        public void Deorbits_own_separate_is_left_out_and_the_Entry_is_read_on_what_stays()
        {
            // separate names the stack: what stays has nothing below its shield, so the shield still jettisons.
            var answer = Read(AtKerbin(Parts(Shape.Drops)), null, Tank, Engine);
            Assert.Equal(Uids(Tank, Engine), Uids(answer["leave_out"]));
            Assert.Equal(Uids(Shield), Uids(answer["shield_drop"]));
            Assert.Equal(1.45, D(answer, "kept"), 6);
            Assert.Equal(3.2, D(answer, "whole"), 6);
        }

        /// <summary>A shield with no decoupler of its own (reentry_shield_modules' rule): with a decoupler beside it, it jettisons; with none on or beside it, it stays on.</summary>
        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void The_shield_jettisons_only_with_a_decoupler_on_or_beside_it(bool decouplerBeside, bool shieldGoes)
        {
            var parts = Parts(Shape.Drops, stack: false);
            parts.Single(p => p.Id == Shield).Tags.Remove("decoupler");
            if (!decouplerBeside) parts.RemoveAll(p => p.Id == Dec);
            foreach (var answer in new[] { Read(AtKerbin(parts)), Read(AtMun(parts), "Kerbin") })
            {
                Assert.Empty(Uids(answer["leave_out"]));
                Assert.Equal(shieldGoes ? Uids(Shield) : Uids(), Uids(answer["shield_drop"]));
                Assert.Contains(shieldGoes ? "shield jettisoned" : "shield kept", (string)answer["why"]);
            }
        }

        [Fact]
        public void An_Entry_on_its_engines_with_a_stack_below_its_shield_keeps_both_in_deorbits_prediction()
        {
            var snapshot = AtKerbin(Parts(Shape.Engines));
            var answer = Read(snapshot);
            Assert.Empty(Uids(answer["leave_out"]));
            Assert.Empty(Uids(answer["shield_drop"]));
            Assert.Contains("on its engines", (string)answer["why"]);
            var flown = Deorbit(snapshot, answer, 0);
            var asIs = BlockCalls.DeorbitBurn(DeorbitAsk(0, null), snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(),
                new PartsCraft(snapshot, new string[0], new string[0]));
            var dropped = BlockCalls.DeorbitBurn(DeorbitAsk(0, null), snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(),
                new PartsCraft(snapshot, Uids(Tank, Engine), Uids(Shield)));
            Assert.Equal("", flown["error"]);
            Assert.Equal(D(asIs, "lat"), D(flown, "lat"), 9);
            Assert.Equal(D(asIs, "lng"), D(flown, "lng"), 9);
            Assert.Equal(D(asIs, "burn_ut"), D(flown, "burn_ut"), 9);
            Assert.NotEqual(D(dropped, "burn_ut"), D(flown, "burn_ut"));
        }

        // ---- The same landing as the craft with the stack already gone ----

        private static IDictionary<string, object> Return(GameSnapshot snapshot, IDictionary<string, object> craft, double isp)
        {
            var ask = new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut }, { "lat", KscLat }, { "lng", KscLng }, { "isp", isp } };
            if (craft != null) ask["craft"] = craft;
            return BlockCalls.ReturnBurn(ask, snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), PartsCraft.For(snapshot, ask));
        }

        private static IDictionary<string, object> Correction(GameSnapshot snapshot, IDictionary<string, object> craft, double isp, double lat, double lng)
        {
            var ask = new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut + 300 }, { "latest_ut", snapshot.Ut + 300 }, { "lat", lat }, { "lng", lng }, { "dv_left", -1.0 }, { "isp", isp },
            };
            if (craft != null) ask["craft"] = craft;
            return BlockCalls.CorrectionBurn(ask, snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), PartsCraft.For(snapshot, ask));
        }

        private static Dictionary<string, object> DeorbitAsk(double isp, IDictionary<string, object> craft)
        {
            var ask = new Dictionary<string, object>
            {
                { "lat", KscLat }, { "lng", KscLng }, { "earliest_ut", 120.0 }, { "max_wait", Kerbin.RotationPeriod }, { "accuracy_km", 5.0 }, { "isp", isp }, { "dv_left", 5000.0 },
            };
            if (craft != null) ask["craft"] = craft;
            return ask;
        }

        private static IDictionary<string, object> Deorbit(GameSnapshot snapshot, IDictionary<string, object> craft, double isp)
        {
            var ask = DeorbitAsk(isp, craft);
            return BlockCalls.DeorbitBurn(ask, snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), PartsCraft.For(snapshot, ask));
        }

        private static void AssertSameLanding(IDictionary<string, object> expected, IDictionary<string, object> actual, string timeKey)
        {
            Assert.Equal("", expected["error"]);
            Assert.Equal("", actual["error"]);
            Assert.Equal(D(expected, timeKey), D(actual, timeKey), 9);
            Assert.Equal(D(expected, "lat"), D(actual, "lat"), 9);
            Assert.Equal(D(expected, "lng"), D(actual, "lng"), 9);
            Assert.Equal(D(expected, "error_km"), D(actual, "error_km"), 9);
        }

        /// <summary>
        /// A heavy stack the Entry drops: return, its correction and deorbit predict the landing of the same craft with the stack already gone. The stack's
        /// engine burns, so the stack's tanks pay (DropGuard's rule, #854): the capsule weighs the same after the burn, as the stackless craft with no isp.
        /// </summary>
        [Fact]
        public void A_heavy_stack_reentry_drops_predicts_as_the_craft_with_the_stack_gone()
        {
            var stacked = AtMun(Parts(Shape.Drops));
            var bare = AtMun(Parts(Shape.Drops, stack: false));
            var aimed = Return(stacked, Read(stacked, "Kerbin"), 320);
            AssertSameLanding(Return(bare, Read(bare, "Kerbin"), 0), aimed, "ut");
            var stackOn = Return(stacked, null, 0);
            _out.WriteLine("return: " + D(aimed, "lat") + ", " + D(aimed, "lng") + " against the stack on " + D(stackOn, "lat") + ", " + D(stackOn, "lng"));
            Assert.NotEqual(D(stackOn, "ut"), D(aimed, "ut"));

            var falling = With(AimedCorrectionTests.Falling(), Parts(Shape.Drops));
            var fallingBare = With(AimedCorrectionTests.Falling(), Parts(Shape.Drops, stack: false));
            double lat, lng;
            AimedCorrectionTests.Natural(falling, out lat, out lng);
            var target = lng + 0.3;
            AssertSameLanding(Correction(fallingBare, Read(fallingBare), 0, lat, target), Correction(falling, Read(falling), 320, lat, target), "ut");

            var orbiting = AtKerbin(Parts(Shape.Drops));
            var orbitingBare = AtKerbin(Parts(Shape.Drops, stack: false));
            AssertSameLanding(Deorbit(orbitingBare, Read(orbitingBare), 0), Deorbit(orbiting, Read(orbiting), 320), "burn_ut");
        }

        [Fact]
        public void Its_aim_spread_is_near_the_capsules()
        {
            var stacked = AtMun(Parts(Shape.Drops));
            var aimed = D(Return(stacked, Read(stacked, "Kerbin"), 320), "spread_km");
            var capsule = D(BlockCalls.ReturnBurn(new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", stacked.Ut }, { "lat", KscLat }, { "lng", KscLng } },
                stacked, new AimedCorrectionTests.FrameBody(stacked.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule()), "spread_km");
            var stackOn = D(Return(stacked, null, 0), "spread_km");
            _out.WriteLine("aim_spread_km: " + aimed + " for the craft that enters, " + capsule + " for the research's capsule, " + stackOn + " with the stack on");
            Assert.InRange(aimed, 0.75 * capsule, 1.25 * capsule);
        }

        // ---- The burn search predicts the carried answer ----

        /// <summary>
        /// The aimed burn predicts the craft its ask carries, not the snapshot's parts: a craft changed since the read (here, its parts gone from the
        /// snapshot) is aimed the same. That each block reads ENTRYCRAFT once is checked on the Kerboscript source (TransferAndReturnTests, DeorbitTests), not here.
        /// </summary>
        [Fact]
        public void The_burn_search_predicts_the_carried_answer_not_the_snapshots_parts()
        {
            var stacked = AtMun(Parts(Shape.Drops));
            var answer = Read(stacked, "Kerbin");
            var craft = PartsCraft.For(stacked, new Dictionary<string, object> { { "craft", answer } });
            var ask = new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", stacked.Ut }, { "lat", KscLat }, { "lng", KscLng }, { "isp", 320.0 }, { "craft", answer } };
            var read = BlockCalls.ReturnBurn(ask, stacked, new AimedCorrectionTests.FrameBody(stacked.Ut), new ReentrySim.Atmosphere(), craft);
            var emptied = AtMun(new List<SnapshotPart>());
            var again = BlockCalls.ReturnBurn(ask, emptied, new AimedCorrectionTests.FrameBody(emptied.Ut), new ReentrySim.Atmosphere(), craft);
            AssertSameLanding(read, again, "ut");
        }

        [Fact]
        public void A_craft_that_is_not_the_answer_or_carries_its_error_fails_the_call()
        {
            var snapshot = AtKerbin(Parts(Shape.Drops));
            Assert.Equal("the Entry's craft: there is no Game Snapshot of the craft", Deorbit(snapshot, new Dictionary<string, object> { { "error", "there is no Game Snapshot of the craft" } }, 0)["error"]);
            Assert.Equal("craft must be ENTRYCRAFT's answer", Deorbit(snapshot, new Dictionary<string, object> { { "error", "" } }, 0)["error"]);
            var ask = new Dictionary<string, object> { { "target_pe", 30000.0 }, { "lat", KscLat }, { "lng", KscLng }, { "craft", "capsule" } };
            Assert.Equal("found no return: craft must be ENTRYCRAFT's answer",
                BlockCalls.ReturnBurn(ask, AtMun(Parts(Shape.Drops)), new AimedCorrectionTests.FrameBody(1000), new ReentrySim.Atmosphere(), new ReentrySim.Capsule())["error"]);
        }

        // ---- Who pays the burn (#854) ----

        /// <summary>With separate set and a kept engine burning, deorbit predicts the kept craft at its mass after the burn: the kept tanks pay, at the whole craft's rate.</summary>
        [Fact]
        public void With_a_kept_burner_and_separate_set_deorbit_predicts_the_kept_craft_at_its_mass_after_the_burn()
        {
            var snapshot = AtKerbin(Parts(Shape.Drops, stackLit: false, podEngine: true));
            var answer = Read(snapshot, null, Tank, Engine);
            Assert.Equal(1.0, D(answer, "burners_kept"));
            var craft = PartsCraft.For(snapshot, new Dictionary<string, object> { { "craft", answer } });
            var payer = new DropBurn { Whole = D(answer, "whole"), GonePropellant = D(answer, "gone_prop"), BurnersKept = D(answer, "burners_kept") };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), Circular(), new DeorbitAsk
            {
                TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, AccuracyKm = 5, DeltaVLeft = 5000, Isp = 300, Payer = payer,
            }, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Null(plan.Error);
            var after = craft.MassKg - 1000 * D(answer, "whole") * (1 - Math.Exp(-plan.DeltaV / (300 * 9.80665)));
            Assert.InRange(craft.MassKg - after, 10, 1000);
            var again = ReentryPredictor.Predict(ReentrySim.Kerbin(), plan.After, plan.BurnUt, 0, after, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Equal(again.Latitude, plan.Landing.Latitude, 9);
            Assert.Equal(again.Longitude, plan.Landing.Longitude, 9);
        }

        [Fact]
        public void With_the_burner_in_the_separate_its_tanks_pay_and_deorbit_predicts_the_kept_craft_as_it_is()
        {
            var snapshot = AtKerbin(Parts(Shape.Drops));
            var answer = Read(snapshot, null, Tank, Engine);
            Assert.Equal(0.0, D(answer, "burners_kept"));
            Assert.Equal(1.1, D(answer, "gone_prop"), 6);
            var craft = PartsCraft.For(snapshot, new Dictionary<string, object> { { "craft", answer } });
            var payer = new DropBurn { Whole = D(answer, "whole"), GonePropellant = D(answer, "gone_prop"), BurnersKept = D(answer, "burners_kept") };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), Circular(), new DeorbitAsk
            {
                TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, AccuracyKm = 5, DeltaVLeft = 5000, Isp = 300, Payer = payer,
            }, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Null(plan.Error);
            var again = ReentryPredictor.Predict(ReentrySim.Kerbin(), plan.After, plan.BurnUt, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Equal(again.Latitude, plan.Landing.Latitude, 9);
            Assert.Equal(again.Longitude, plan.Landing.Longitude, 9);
        }

        private static Conic Circular()
        {
            var r = ReentrySim.R + 80000;
            return Conic.FromState(ReentrySim.Mu, new Vec3(r, 0, 0), new Vec3(0, Math.Sqrt(ReentrySim.Mu / r), 0), 0);
        }

        [Fact]
        public void The_payer_rule_is_DropGuards()
        {
            var dv = 100.0;
            var used = 3.2 * (1 - Math.Exp(-dv / (300 * 9.80665)));
            Assert.Equal(used, DropGuard.KeptPays(new DropBurn { Spent = dv, Whole = 3.2, Isp = 300, GonePropellant = 1.1, BurnersKept = 1 }), 9);
            Assert.Equal(0, DropGuard.KeptPays(new DropBurn { Spent = dv, Whole = 3.2, Isp = 300, GonePropellant = 1.1 }), 9);
            Assert.Equal(used - 0.05, DropGuard.KeptPays(new DropBurn { Spent = dv, Whole = 3.2, Isp = 300, GonePropellant = 0.05 }), 9);
            Assert.Equal(0, DropGuard.KeptPays(new DropBurn { Spent = dv, Whole = 3.2, BurnersKept = 1 }), 9);
            // No craft read: the rocket equation on the craft alone, as before #814.
            Assert.Equal(1450 * Math.Exp(-dv / (300 * 9.80665)), DropGuard.MassAfterBurn(1450, dv, 300, null), 9);
            Assert.Equal(1450, DropGuard.MassAfterBurn(1450, dv, 0, null), 9);
            Assert.Equal(1450 - 1000 * used, DropGuard.MassAfterBurn(1450, dv, 300, new DropBurn { Whole = 3.2, GonePropellant = 1.1, BurnersKept = 1 }), 9);
        }

        // ---- reentry names both crafts when they differ ----

        [Fact]
        public void Reentry_names_the_craft_aimed_for_and_the_craft_it_flies_when_they_differ()
        {
            var aimed = Read(AtMun(Parts(Shape.Drops)), "Kerbin");
            var key = (string)aimed["key"];
            var why = (string)aimed["why"];
            Func<GameSnapshot, string> finish = s => (string)BlockCalls.EntryFinishCall(new Dictionary<string, object> { { "home", Uid(Pod) }, { "chutes", true }, { "drop", true } },
                s, null, 0, "", Uid, key, why)["why"];

            // Home with the craft aimed for: nothing to say.
            var same = finish(AtKerbin(Parts(Shape.Drops)));
            Assert.DoesNotContain("aimed for", same);

            // Its chute gone since, the Entry keeps the stack and lands on its engine: both crafts are named.
            var other = finish(AtKerbin(Parts(Shape.Engines)));
            _out.WriteLine(other);
            Assert.Contains("; the landing was aimed for the craft without its stack (fuelTank, liquidEngine3.v2), shield jettisoned, 1.45 t, it flies as it is, its stack kept, on its engines, shield kept, 3.20 t", other);

            // No aim recorded (a reentry after a load, or with no return or deorbit before it): its line is as before.
            Assert.DoesNotContain("aimed for", (string)BlockCalls.EntryFinishCall(new Dictionary<string, object> { { "home", Uid(Pod) } }, AtKerbin(Parts(Shape.Engines)), null, 0, "", Uid)["why"]);
        }

        [Fact]
        public void A_part_staged_away_since_the_aim_is_no_difference()
        {
            // An empty stage on the capsule's side, dropped by drop_empty between return and reentry: the parts that enter are the same.
            var parts = Parts(Shape.Drops);
            parts.Add(Part(99, Pod, "spentBooster", 0.2));
            var aimed = Read(AtMun(parts), "Kerbin");
            var flown = BlockCalls.EntryFinishCall(new Dictionary<string, object> { { "home", Uid(Pod) } }, AtKerbin(Parts(Shape.Drops)), null, 0, "", Uid, (string)aimed["key"], (string)aimed["why"]);
            Assert.DoesNotContain("aimed for", (string)flown["why"]);
        }

        [Fact]
        public void A_stack_already_dropped_before_reentry_is_the_craft_aimed_for()
        {
            // return aimed for the capsule; the stack went before the air (staged away on the way home): the same parts enter.
            var aimed = Read(AtMun(Parts(Shape.Drops)), "Kerbin");
            var flown = BlockCalls.EntryFinishCall(new Dictionary<string, object> { { "home", Uid(Pod) } }, AtKerbin(Parts(Shape.Drops, stack: false)), null, 0, "", Uid, (string)aimed["key"], (string)aimed["why"]);
            Assert.DoesNotContain("aimed for", (string)flown["why"]);
        }

        // ---- The aim reentry compares with (EntryAim, held by the adapter) ----

        private static IDictionary<string, object> Result(bool success)
        {
            return new Dictionary<string, object> { { "success", success } };
        }

        [Fact]
        public void An_aim_that_stands_is_read_once_by_the_vessel_that_aimed()
        {
            var aim = new EntryAim();
            aim.Block("return", null);
            aim.Aimed(42, "1,2|shield kept", "the capsule");
            aim.Block("return", Result(true));
            aim.Block("reentry", null);
            aim.Block("reentry", Result(false));
            string why;
            Assert.Equal("1,2|shield kept", aim.Take(42, out why));
            Assert.Equal("the capsule", why);
            Assert.Equal("", aim.Take(42, out why));
        }

        [Theory]
        [InlineData("return")]
        [InlineData("deorbit")]
        public void An_aiming_block_that_gives_up_leaves_no_aim(string block)
        {
            var aim = new EntryAim();
            aim.Block(block, null);
            aim.Aimed(42, "1,2|shield kept", "the capsule");
            aim.Block(block, Result(false));
            string why;
            Assert.Equal("", aim.Take(42, out why));
            Assert.Equal("", why);
        }

        [Fact]
        public void Another_vessels_Entry_or_a_new_aiming_block_has_no_aim_to_compare()
        {
            var aim = new EntryAim();
            aim.Aimed(42, "1,2|shield kept", "the capsule");
            aim.Block("return", Result(true));
            string why;
            Assert.Equal("", aim.Take(7, out why));

            aim.Aimed(42, "1,2|shield kept", "the capsule");
            aim.Block("deorbit", null);
            Assert.Equal("", aim.Take(42, out why));
        }
    }
}
