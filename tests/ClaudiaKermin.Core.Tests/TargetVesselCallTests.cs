// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>ADDONS:CLAUDIA:TARGETVESSEL (#455): rendezvous learns the Mission's Target Vessel from the Game Snapshot read at the call.</summary>
    public class TargetVesselCallTests
    {
        private static GameSnapshot Snapshot(SnapshotTarget target)
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin", Target = target };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000 });
            return snapshot;
        }

        private static SnapshotTarget Station(string situation = "ORBITING")
        {
            return new SnapshotTarget
            {
                Name = "Station", Body = "Kerbin", Situation = situation,
                Orbit = new SnapshotOrbit { SemiMajorAxis = 750000, Body = "Kerbin" },
            };
        }

        [Fact]
        public void It_gives_the_target_vessel_s_name_and_the_body_it_orbits()
        {
            var got = BlockCalls.TargetVessel(Snapshot(Station()));
            Assert.Equal("", got["error"]);
            Assert.Equal("Station", got["name"]);
            Assert.Equal("Kerbin", got["body"]);
        }

        [Fact]
        public void With_no_target_vessel_or_a_landed_one_it_says_why()
        {
            Assert.Contains("no Target Vessel", (string)BlockCalls.TargetVessel(Snapshot(null))["error"]);
            Assert.Contains("landed, not in orbit", (string)BlockCalls.TargetVessel(Snapshot(Station("LANDED")))["error"]);
            Assert.False(BlockCalls.TargetVessel(Snapshot(null)).ContainsKey("name"));
        }

        [Fact]
        public void A_landed_target_keeps_its_error_for_rendezvous_and_gives_land_where_it_is()
        {
            var landed = Station("LANDED");
            landed.Body = "Mun";
            landed.Position = new SnapshotPosition { Latitude = 0.5, Longitude = 22, Altitude = 1500 };

            var got = BlockCalls.TargetVessel(Snapshot(landed));

            Assert.Contains("landed, not in orbit", (string)got["error"]);
            Assert.Equal("Station", got["name"]);
            Assert.Equal("Mun", got["body"]);
            Assert.Equal(0.5, got["lat"]);
            Assert.Equal(22.0, got["lng"]);
            Assert.Equal(1500.0, got["alt"]);
        }

        [Fact]
        public void An_orbiting_target_gives_no_position()
        {
            var orbiting = Station();
            orbiting.Position = new SnapshotPosition { Latitude = 1, Longitude = 2, Altitude = 3 };
            Assert.False(BlockCalls.TargetVessel(Snapshot(orbiting)).ContainsKey("lat"));
            Assert.False(BlockCalls.TargetVessel(Snapshot(Station("LANDED"))).ContainsKey("lat"));
        }
    }
}
