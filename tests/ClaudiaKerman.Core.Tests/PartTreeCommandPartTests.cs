// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>The home part (#927): among the command parts on the Mission's side, the first with crew aboard, else the first with seats, else the first.</summary>
    public sealed class PartTreeCommandPartTests
    {
        private static SnapshotPart Part(uint id, uint? parent, int seats, int crew, params string[] tags)
        {
            return new SnapshotPart { Id = id, ParentId = parent, Name = "p" + id, CrewCapacity = seats, Crew = crew, Tags = tags.ToList() };
        }

        [Fact]
        public void An_empty_first_pod_gives_way_to_a_crewed_later_pod()
        {
            var parts = new List<SnapshotPart>
            {
                Part(1, null, 1, 0, "command", "crew_seat"),
                Part(2, 1, 1, 1, "command", "crew_seat"),
            };
            Assert.Equal(2u, PartTree.CommandPart(parts, null));
        }

        [Fact]
        public void A_crewed_stock_hitchhiker_is_not_home_because_it_is_not_a_command_part()
        {
            var parts = new List<SnapshotPart>
            {
                Part(1, null, 1, 0, "command", "crew_seat"),
                Part(2, 1, 4, 2, "crew_seat"),
            };
            Assert.Equal(1u, PartTree.CommandPart(parts, null));
        }

        [Fact]
        public void A_crewed_pod_stays_home_when_a_probe_core_comes_later()
        {
            var parts = new List<SnapshotPart>
            {
                Part(1, null, 1, 1, "command", "crew_seat"),
                Part(2, 1, 0, 0, "command", "probe_core"),
            };
            Assert.Equal(1u, PartTree.CommandPart(parts, null));
        }

        [Fact]
        public void An_uncrewed_craft_keeps_the_first_with_seats_then_the_first()
        {
            var parts = new List<SnapshotPart>
            {
                Part(1, null, 0, 0, "command", "probe_core"),
                Part(2, 1, 1, 0, "command", "crew_seat"),
                Part(3, 2, 1, 0, "command", "crew_seat"),
            };
            Assert.Equal(2u, PartTree.CommandPart(parts, null));
            parts.RemoveRange(1, 2);
            Assert.Equal(1u, PartTree.CommandPart(parts, null));
        }

        [Fact]
        public void An_old_snapshot_with_no_crew_count_falls_back_to_seats()
        {
            var parts = new List<SnapshotPart>
            {
                Part(1, null, 0, -1, "command", "probe_core"),
                Part(2, 1, 1, -1, "command", "crew_seat"),
                Part(3, 2, 1, -1, "command", "crew_seat"),
            };
            Assert.Equal(2u, PartTree.CommandPart(parts, null));
        }

        [Fact]
        public void A_crewed_pod_across_a_docking_is_not_the_missions()
        {
            var parts = new List<SnapshotPart>
            {
                Part(1, null, 1, 0, "command", "crew_seat"),
                Part(2, 1, 0, 0, "docking_port"),
                Part(3, 2, 0, 0, "docking_port"),
                Part(4, 3, 1, 1, "command", "crew_seat"),
            };
            Assert.Equal(1u, PartTree.CommandPart(parts, 1));
            Assert.Equal(4u, PartTree.CommandPart(parts, 4));
        }
    }
}
