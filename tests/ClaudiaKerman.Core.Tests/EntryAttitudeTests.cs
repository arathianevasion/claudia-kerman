// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The axis the reentry predictor reads a craft's drag along (#924): the attitude the Entry flies, shield first, from the craft's parts and never from the control point the craft
    /// happens to have when the block asks. The rule takes no control point at all, so a reversed pod or a probe core on the stack can't turn the read tail first.
    /// </summary>
    public sealed class EntryAttitudeTests
    {
        private const uint Pod = 1, Chute = 2, Shield = 3, Dec = 4, Tank = 5, Probe = 6;

        private static SnapshotPart Part(uint id, uint? parent, int seats, params string[] tags)
        {
            return new SnapshotPart { Id = id, ParentId = parent, Name = "p" + id, CrewCapacity = seats, Tags = tags.ToList() };
        }

        private static List<SnapshotPart> Capsule()
        {
            return new List<SnapshotPart>
            {
                Part(Pod, null, 1, "command", "crew_seat"),
                Part(Chute, Pod, 0, "parachute"),
                Part(Shield, Pod, 0, "heat_shield"),
                Part(Dec, Shield, 0, "decoupler"),
                Part(Tank, Dec, 0),
            };
        }

        [Fact]
        public void A_capsule_is_read_along_its_shield_and_pod_with_its_stack_left_out()
        {
            var attitude = EntryAttitude.Of(Capsule(), new uint[] { Dec, Tank });
            Assert.Equal(Pod, attitude.Home.Id);
            Assert.Equal(Shield, attitude.Shield.Id);
        }

        [Fact]
        public void A_probe_core_on_the_stack_is_not_home_while_a_crewed_pod_is_aboard()
        {
            var parts = Capsule();
            parts.Add(Part(Probe, Tank, 0, "command", "probe_core"));
            parts.Reverse();
            var attitude = EntryAttitude.Of(parts, null);
            Assert.Equal(Pod, attitude.Home.Id);
            Assert.Equal(Shield, attitude.Shield.Id);
        }

        [Fact]
        public void The_home_part_stands_alone_when_the_shield_is_gone()
        {
            var attitude = EntryAttitude.Of(Capsule().Where(p => p.Id != Shield && p.Id != Dec && p.Id != Tank).ToList(), null);
            Assert.Equal(Pod, attitude.Home.Id);
            Assert.Null(attitude.Shield);
        }

        [Fact]
        public void A_probe_flies_from_its_probe_core_and_with_no_command_part_from_the_root()
        {
            var probe = new List<SnapshotPart> { Part(Tank, null, 0), Part(Probe, Tank, 0, "command", "probe_core") };
            Assert.Equal(Probe, EntryAttitude.Of(probe, null).Home.Id);
            Assert.Equal(Tank, EntryAttitude.Of(new List<SnapshotPart> { Part(Tank, null, 0), Part(Dec, Tank, 0, "decoupler") }, null).Home.Id);
        }

        [Fact]
        public void With_the_root_in_the_stack_left_out_home_is_the_part_whose_parent_went()
        {
            var parts = new List<SnapshotPart> { Part(Tank, null, 0), Part(Dec, Tank, 0, "decoupler"), Part(Shield, Dec, 0, "heat_shield"), Part(Chute, Shield, 0, "parachute") };
            var attitude = EntryAttitude.Of(parts, new uint[] { Tank, Dec });
            Assert.Equal(Shield, attitude.Home.Id);
        }

        [Fact]
        public void The_nose_points_from_the_shield_toward_home_however_the_shield_was_built()
        {
            var towardHome = new[] { 0.0, 1, 0 };
            Assert.Equal(new[] { 0.0, 1, 0 }, EntryAttitude.Nose(new[] { 0.0, 1, 0 }, towardHome));
            // Built upside down, the shield's own up points away from home, and the nose does not follow it.
            Assert.Equal(new[] { 0.0, 1, 0 }, EntryAttitude.Nose(new[] { 0.0, -1, 0 }, towardHome));
        }

        [Fact]
        public void The_axis_is_the_part_s_own_when_there_is_no_way_to_tell_which_way_home_is()
        {
            Assert.Equal(new[] { 0.0, 1, 0 }, EntryAttitude.Nose(new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 0 }));
        }

        [Fact]
        public void Nothing_at_all_is_no_attitude()
        {
            var attitude = EntryAttitude.Of(new List<SnapshotPart>(), null);
            Assert.Null(attitude.Home);
            Assert.Null(attitude.Shield);
            Assert.Null(attitude.NoseOf(id => new double[3], id => new double[3]));
        }

        [Fact]
        public void The_nose_is_the_shield_s_axis_turned_toward_home_from_where_the_parts_are()
        {
            var attitude = EntryAttitude.Of(Capsule(), new uint[] { Dec, Tank });
            // The shield is built tipped and upside down; home sits above it, so the nose is the shield's axis the other way round.
            var nose = attitude.NoseOf(id => id == Shield ? new[] { 0.0, -0.8, 0.6 } : new[] { 1.0, 0, 0 }, id => id == Pod ? new[] { 0.0, 3, 0 } : new[] { 0.0, 2, 0 });
            Assert.Equal(new[] { 0.0, 0.8, -0.6 }, nose);
        }

        [Fact]
        public void With_no_shield_the_nose_is_home_s_own_up()
        {
            var parts = Capsule().Where(p => p.Id == Pod || p.Id == Chute).ToList();
            var nose = EntryAttitude.Of(parts, null).NoseOf(id => id == Pod ? new[] { 0.0, 0, 1 } : new[] { 1.0, 0, 0 }, id => new double[3]);
            Assert.Equal(new[] { 0.0, 0, 1 }, nose);
        }

        private const uint Mine = 10, Theirs = 11, MyPort = 12, TheirPort = 13, MyProbe = 14;

        private static SnapshotPart Port(uint id, uint? parent, string state)
        {
            var port = Part(id, parent, 0, "docking_port");
            port.PortState = state;
            return port;
        }

        /// <summary>The partner's pod is the root and comes first; ours hangs off the far side of the docking, with its own shield.</summary>
        private static List<SnapshotPart> DockedPair()
        {
            return new List<SnapshotPart>
            {
                Part(Theirs, null, 3, "command", "crew_seat"),
                Port(TheirPort, Theirs, "Docked (docker)"),
                Port(MyPort, TheirPort, "Docked (dockee)"),
                Part(Mine, MyPort, 1, "command", "crew_seat"),
                Part(Shield, Mine, 0, "heat_shield"),
            };
        }

        [Fact]
        public void On_a_docked_pair_the_Mission_s_own_pod_is_home_given_its_core()
        {
            var attitude = EntryAttitude.Of(DockedPair(), null, Mine);
            Assert.Equal(Mine, attitude.Home.Id);
            Assert.Equal(Shield, attitude.Shield.Id);
        }

        [Fact]
        public void A_core_that_is_not_a_command_part_still_reads_its_own_side_of_the_docking()
        {
            var parts = DockedPair();
            parts.Add(Part(MyProbe, Mine, 0, "probe_core"));
            Assert.Equal(Mine, EntryAttitude.Of(parts, null, MyProbe).Home.Id);
        }

        [Fact]
        public void With_no_core_a_docked_pair_reads_as_before()
        {
            Assert.Equal(Theirs, EntryAttitude.Of(DockedPair(), null).Home.Id);
        }

        [Fact]
        public void A_core_left_out_with_the_stack_is_as_no_core()
        {
            var parts = Capsule();
            parts.Add(Part(MyProbe, Tank, 0, "command", "probe_core"));
            var attitude = EntryAttitude.Of(parts, new uint[] { Dec, Tank, MyProbe }, MyProbe);
            Assert.Equal(Pod, attitude.Home.Id);
            Assert.Equal(Shield, attitude.Shield.Id);
        }

        [Fact]
        public void A_single_craft_is_unchanged_with_or_without_its_core()
        {
            var without = EntryAttitude.Of(Capsule(), new uint[] { Dec, Tank });
            var with = EntryAttitude.Of(Capsule(), new uint[] { Dec, Tank }, Pod);
            Assert.Equal(without.Home.Id, with.Home.Id);
            Assert.Equal(without.Shield.Id, with.Shield.Id);
        }
    }
}
