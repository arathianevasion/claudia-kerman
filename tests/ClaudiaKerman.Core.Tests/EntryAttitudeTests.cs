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
        }
    }
}
