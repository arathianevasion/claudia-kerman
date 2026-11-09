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
    /// <summary>The Craft Check Warning for a decoupler that can't leave with the Entry Shield from under an engine (#948). Parts run from home outward: the shield's parent is toward the pod.</summary>
    public class DecouplerUnderEngineTests
    {
        private static readonly PlannedSequence[] Landing = { new PlannedSequence { Name = "Return to Kerbin", DeltaV = 600 } };

        private static SnapshotPart Part(List<SnapshotPart> parts, string name, uint? parent, params string[] tags)
        {
            var part = new SnapshotPart { Id = (uint)(parts.Count + 1), ParentId = parent, Name = name, Tags = tags.ToList() };
            parts.Add(part);
            return part;
        }

        private static GameSnapshot Snapshot(List<SnapshotPart> parts)
        {
            return new GameSnapshot
            {
                Body = "Kerbin",
                Orbit = new SnapshotOrbit { Body = "Kerbin" },
                Craft = new SnapshotCraft { Name = "Ship", Parts = parts },
                Bodies = new List<SnapshotBody> { new SnapshotBody { Name = "Kerbin", AtmosphereHeight = 70000, SeaLevelAtm = 1 } },
            };
        }

        /// <summary>Pod, engine, then <paramref name="decouplers"/> in a row, then the shield. Each decoupler lets go of the part above it (toward the pod) unless numbered in <paramref name="wrongWay"/>.</summary>
        private static List<SnapshotPart> Stack(int decouplers, params int[] wrongWay)
        {
            var parts = new List<SnapshotPart>();
            var pod = Part(parts, "pod", null, "command");
            var above = Part(parts, "engine", pod.Id, "engine");
            for (var i = 0; i < decouplers; i++)
            {
                var dec = Part(parts, "dec" + i, above.Id, "decoupler");
                dec.Releases.Add(wrongWay.Contains(i) ? 0u : above.Id);
                above = dec;
            }
            Part(parts, "shield", above.Id, "heat_shield", "decoupler");
            return parts;
        }

        private static IList<MarginFinding> Run(List<SnapshotPart> parts, params PlannedSequence[] legs)
        {
            return Entries.DecouplerUnderEngine(Snapshot(parts), legs.Length == 0 ? Landing : legs);
        }

        [Fact]
        public void Engine_right_way_decoupler_shield_has_no_finding()
        {
            Assert.Empty(Run(Stack(1)));
        }

        [Fact]
        public void A_wrong_way_decoupler_is_named_with_the_engine()
        {
            var finding = Assert.Single(Run(Stack(1, 0)));
            Assert.Equal(CraftCheckRules.DecouplerUnderEngine, finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("dec0", finding.Explanation);
            Assert.Contains("engine", finding.Explanation);
        }

        [Fact]
        public void Two_decouplers_in_the_chain_are_a_finding_even_when_both_are_right_way_up()
        {
            var finding = Assert.Single(Run(Stack(2)));
            Assert.Contains("dec0", finding.Explanation);
            Assert.Contains("dec1", finding.Explanation);
        }

        [Fact]
        public void A_tank_between_the_engine_and_the_decoupler_is_not_this_case()
        {
            var parts = new List<SnapshotPart>();
            var pod = Part(parts, "pod", null, "command");
            var engine = Part(parts, "engine", pod.Id, "engine");
            var tank = Part(parts, "tank", engine.Id);
            var dec = Part(parts, "dec0", tank.Id, "decoupler");
            Part(parts, "shield", dec.Id, "heat_shield", "decoupler");
            Assert.Empty(Run(parts));
        }

        [Fact]
        public void No_Entry_in_the_goal_is_no_finding()
        {
            Assert.Empty(Run(Stack(1, 0), new PlannedSequence { Name = "Circularize in orbit" }));
        }

        private static GameSnapshot DunaShip(bool ringReleasesAerospike, bool ringReadable = true)
        {
            var parts = new List<SnapshotPart>();
            var pod = Part(parts, "mk1pod.v2", null, "command");
            var tank = Part(parts, "fuelTank", pod.Id);
            var aerospike = Part(parts, "toroidalAerospike", tank.Id, "engine");
            var ring = Part(parts, "Decoupler.1", aerospike.Id, "decoupler");
            if (ringReadable) ring.Releases.Add(ringReleasesAerospike ? aerospike.Id : 0u);
            Part(parts, "HeatShield1", ring.Id, "heat_shield", "decoupler");
            return Snapshot(parts);
        }

        [Fact]
        public void The_Duna_ships_layout_with_its_ring_letting_go_of_the_aerospike_says_nothing()
        {
            Assert.Empty(Entries.DecouplerUnderEngine(DunaShip(true), Landing));
        }

        [Fact]
        public void The_Duna_ships_layout_with_its_ring_flipped_names_the_ring_and_the_aerospike()
        {
            var finding = Assert.Single(Entries.DecouplerUnderEngine(DunaShip(false), Landing));
            Assert.Contains("Decoupler.1", finding.Explanation);
            Assert.Contains("toroidalAerospike", finding.Explanation);
        }

        [Fact]
        public void A_decoupler_whose_Releases_cannot_be_read_is_not_called_wrong_way()
        {
            Assert.Empty(Entries.DecouplerUnderEngine(DunaShip(true, ringReadable: false), Landing));
        }

        [Fact]
        public void Two_stacked_decouplers_still_warn_when_neither_Releases_can_be_read()
        {
            var parts = Stack(2);
            foreach (var p in parts) p.Releases.Clear();
            Assert.Single(Run(parts));
        }

        [Fact]
        public void The_rule_is_the_mods_own_Warning()
        {
            var rule = CraftCheckRules.Find(CraftCheckRules.DecouplerUnderEngine);
            Assert.True(rule.ModOnly);
            Assert.Equal(CraftFindingSeverity.Warning, rule.Severity);
        }
    }
}
