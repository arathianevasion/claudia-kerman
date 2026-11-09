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
        public void Parts_under_the_engine_with_no_decoupler_name_nothing()
        {
            var c = new Craft();
            var pod = c.Add("mk1pod.v2", null, "command");
            var engine = c.Lit("liquidEngine", pod);
            var shield = c.Shield(engine);
            c.Add("fuelTank", shield);

            Assert.Empty(LiftoffClear.Of(c.Parts, null, null).Decouplers);
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
    }
}
