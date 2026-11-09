// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>launch</c> and <c>ascend</c> from the ground clear what is still under an engine that gives thrust (#945): the decoupler nearest it, found
    /// by walking the part tree away from home. A normal launch craft names nothing.
    /// </summary>
    public class LiftoffClearTests
    {
        private sealed class Craft
        {
            public readonly List<SnapshotPart> Parts = new List<SnapshotPart>();

            public uint Add(string name, uint? parent, params string[] tags)
            {
                var id = (uint)(Parts.Count + 1);
                Parts.Add(new SnapshotPart { Id = id, ParentId = parent, Name = name, Tags = tags.ToList() });
                return id;
            }

            public uint Lit(string name, uint? parent)
            {
                var id = Add(name, parent, "engine");
                Parts[Parts.Count - 1].EngineAvailableThrustKn = 100;
                return id;
            }

            public uint Shield(uint parent)
            {
                return Add("HeatShield1", parent, "heat_shield", "decoupler");
            }

            public uint BareShield(uint parent)
            {
                return Add("HeatShield1", parent, "heat_shield");
            }
        }

        private static List<string> Names(IEnumerable<SnapshotPart> parts)
        {
            return parts.Select(p => p.Name).ToList();
        }

        [Fact]
        public void An_engine_over_a_decoupler_and_a_shield_names_that_decoupler()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("toroidalAerospike", pod);
            var dec = c.Add("Decoupler.1", engine, "decoupler");
            var shield = c.Shield(dec);
            var below = c.Add("Decoupler.1", shield, "decoupler");
            c.Add("fuelTank", below);

            var clear = LiftoffClear.Of(c.Parts, null, null);

            Assert.Equal(new[] { dec }, clear.Decouplers.Select(p => p.Id));
            Assert.Equal(new[] { "Decoupler.1", "HeatShield1", "Decoupler.1", "fuelTank" }, Names(clear.Gone));
        }

        [Fact]
        public void Nothing_under_the_engine_names_nothing()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var tank = c.Add("fuelTank", pod);
            c.Lit("liquidEngine", tank);

            var clear = LiftoffClear.Of(c.Parts, null, null);

            Assert.Empty(clear.Decouplers);
            Assert.Empty(clear.Gone);
        }

        [Fact]
        public void An_engine_on_a_shield_names_the_shield()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var shield = c.Shield(engine);
            c.Add("fuelTank", shield);

            var clear = LiftoffClear.Of(c.Parts, null, null);

            Assert.Equal(new[] { shield }, clear.Decouplers.Select(p => p.Id));
            Assert.Equal(new[] { "HeatShield1", "fuelTank" }, Names(clear.Gone));
            Assert.Empty(clear.Stuck);
        }

        [Fact]
        public void An_engine_over_a_shield_over_a_decoupler_names_the_shield()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var shield = c.Shield(engine);
            var dec = c.Add("Decoupler.1", shield, "decoupler");
            c.Add("fuelTank", dec);

            var clear = LiftoffClear.Of(c.Parts, null, null);

            Assert.Equal(new[] { shield }, clear.Decouplers.Select(p => p.Id));
            Assert.Equal(new[] { "HeatShield1", "Decoupler.1", "fuelTank" }, Names(clear.Gone));
            Assert.Empty(clear.Stuck);
        }

        [Fact]
        public void A_shield_with_no_decoupler_of_its_own_stays_stuck()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var shield = c.BareShield(engine);
            c.Add("fuelTank", shield);

            var clear = LiftoffClear.Of(c.Parts, null, null);

            Assert.Empty(clear.Decouplers);
            Assert.Empty(clear.Gone);
            Assert.Equal(new[] { "HeatShield1", "fuelTank" }, Names(clear.Stuck));
        }

        [Fact]
        public void The_nearest_decoupler_is_named_not_one_further_down()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var tank = c.Add("fuelTank", engine);
            var near = c.Add("near", tank, "decoupler");
            var far = c.Add("far", c.Add("fuelTank", near), "decoupler");
            c.Add("fuelTank", far);

            Assert.Equal(new[] { near }, LiftoffClear.Of(c.Parts, null, null).Decouplers.Select(p => p.Id));
        }

        [Fact]
        public void An_engine_that_is_not_firing_is_never_considered()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Add("liquidEngine", pod, "engine");
            c.Add("fuelTank", c.Add("Decoupler.1", engine, "decoupler"));

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Decouplers);
        }

        [Fact]
        public void A_decoupler_over_the_part_running_the_script_is_left()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var core = c.Add("kOSMachine1m", c.Add("Decoupler.1", engine, "decoupler"), "kos_processor");

            Assert.Empty(LiftoffClear.Of(c.Parts, null, core).Decouplers);
        }

        [Fact]
        public void A_decoupler_over_another_firing_engine_is_left()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("upper", pod);
            c.Lit("lifter", c.Add("Decoupler.1", engine, "decoupler"));

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Decouplers);
        }

        [Fact]
        public void A_launch_clamp_is_not_a_decoupler()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var tank = c.Add("fuelTank", pod);
            var engine = c.Lit("liquidEngine", tank);
            c.Add("launchClamp1", tank);
            c.Add("launchClamp1", engine);

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Decouplers);
        }

        /// <summary>The Mun Shot as it stands on the pad once staging has lit the lifter: its upper-stage engine has a decoupler and the lifter's stage under it, and gives no thrust yet.</summary>
        [Fact]
        public void The_Mun_Shot_on_the_pad_names_nothing()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            var parts = snapshot.Craft.Parts;
            parts.Single(p => p.Name == "liquidEngine2.v2").EngineAvailableThrustKn = 120;
            var upper = parts.Single(p => p.Name == "liquidEngine3.v2");
            Assert.Contains(parts, p => p.Tags.Contains("decoupler") && p.ParentId == upper.Id);

            Assert.Empty(LiftoffClear.Of(parts, null, null).Decouplers);
        }

        /// <summary>The Duna ship on the pad: the lander's aerospike unlit over the stack, the Mainsail lit under shield 2.</summary>
        [Fact]
        public void The_Duna_ship_on_the_pad_names_nothing()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var lander = c.Add("fuelTank", pod);
            var aerospike = c.Add("toroidalAerospike", lander, "engine");
            var shield = c.Shield(c.Add("Decoupler.1", aerospike, "decoupler"));
            var stack = c.Add("fuelTank.long", c.Add("Decoupler.1", shield, "decoupler"));
            c.Lit("liquidEngine1-2", stack);

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Decouplers);
        }

        [Fact]
        public void Home_not_the_root_decides_which_side_is_under()
        {
            var c = new Craft();
            var root = c.Add("fuelTank", null);
            var dec = c.Add("Decoupler.1", root, "decoupler");
            var engine = c.Lit("liquidEngine", dec);
            var pod = c.Add("mk1pod.v2", engine, "command");

            var clear = LiftoffClear.Of(c.Parts, pod, null);

            Assert.Equal(new[] { dec }, clear.Decouplers.Select(p => p.Id));
            Assert.Equal(new[] { "Decoupler.1", "fuelTank" }, Names(clear.Gone));
        }

        [Fact]
        public void A_lit_booster_radially_under_the_decoupler_leaves_it_alone()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("upper", pod);
            var dec = c.Add("Decoupler.1", engine, "decoupler");
            var tank = c.Add("fuelTank", dec);
            var booster = c.Lit("solidBooster", tank);
            c.Parts[(int)booster - 1].SurfaceAttached = true;

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Decouplers);
        }

        [Fact]
        public void A_core_radially_under_the_decoupler_leaves_it_alone_and_radial_parts_that_go_are_in_gone()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("upper", pod);
            var dec = c.Add("Decoupler.1", engine, "decoupler");
            var tank = c.Add("fuelTank", dec);
            var probe = c.Add("probeCoreOcto", tank);
            c.Parts[(int)probe - 1].SurfaceAttached = true;

            Assert.Empty(LiftoffClear.Of(c.Parts, null, probe).Decouplers);
            var clear = LiftoffClear.Of(c.Parts, null, null);
            Assert.Equal(new[] { "Decoupler.1", "fuelTank", "probeCoreOcto" }, Names(clear.Gone));
        }

        [Fact]
        public void A_part_under_the_engine_with_no_decoupler_is_reported_stuck()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            c.Add("fuelTank", engine);

            var clear = LiftoffClear.Of(c.Parts, null, null);

            Assert.Empty(clear.Decouplers);
            Assert.Equal(new[] { "fuelTank" }, Names(clear.Stuck));
        }

        [Fact]
        public void A_decoupler_left_alone_leaves_what_is_under_it_stuck_and_clamps_never_are()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var core = c.Add("kOSMachine1m", c.Add("Decoupler.1", engine, "decoupler"), "kos_processor");
            c.Add("launchClamp1", engine);

            var clear = LiftoffClear.Of(c.Parts, null, core);

            Assert.Equal(new[] { "Decoupler.1", "kOSMachine1m" }, Names(clear.Stuck));
        }

        [Fact]
        public void A_normal_launch_craft_reports_nothing_stuck()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var tank = c.Add("fuelTank", pod);
            var engine = c.Lit("liquidEngine", tank);
            c.Add("launchClamp1", tank);
            c.Add("launchClamp1", engine);
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Craft.Parts.Single(p => p.Name == "liquidEngine2.v2").EngineAvailableThrustKn = 120;

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Stuck);
            Assert.Empty(LiftoffClear.Of(snapshot.Craft.Parts, null, null).Stuck);
        }

        [Fact]
        public void Launch_keeps_full_throttle_for_a_craft_on_clamps_and_stages_at_zero_otherwise()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "kerboscript", "library", "launch.ks"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var launch = File.ReadAllText(Path.Combine(dir.FullName, "kerboscript", "library", "launch.ks"));
            Assert.Contains("local tset is choose 1 if ship:modulesnamed(\"LaunchClamp\"):length > 0 else 0.", launch);
        }

        [Fact]
        public void Ground_false_gives_empty_lists_and_no_error()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            var got = ClaudiaKerman.Core.Tools.BlockCalls.LiftoffClearCall(new Dictionary<string, object> { { "ground", false } }, snapshot, id => id.ToString());

            Assert.Equal("", got["error"]);
            Assert.Empty((List<object>)got["decouplers"]);
            Assert.Empty((List<object>)got["gone"]);
            Assert.Empty((List<object>)got["stuck"]);
        }

        [Fact]
        public void A_failed_liftoffclear_read_does_nothing_and_carries_on()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "kerboscript", "library", "claudia_lib.ks"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var lib = File.ReadAllText(Path.Combine(dir.FullName, "kerboscript", "library", "claudia_lib.ks"));
            var call = lib.IndexOf("addons:claudia:liftoffclear(", StringComparison.Ordinal);
            Assert.True(call > 0);
            var guard = lib.IndexOf("if under:error <> \"\" { return \"\". }", call, StringComparison.Ordinal);
            var use = lib.IndexOf("claudia_drop_of(under", call, StringComparison.Ordinal);
            Assert.True(guard > call && guard < use, "a failed read must return before its result is used");
        }
    }
}
