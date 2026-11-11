// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The landing reserve counts only the delta-v the landing craft keeps (#893, #800's cases). The fixture is a three-stage serial stack whose masses
    /// are stages 4, 3 and 2 of Fixtures/snapshots/mun-shot-pad.json (the Mun flight's pad craft): Terriers (60 kN, 345 s), each stage burning 4 t
    /// (the top one 3 t) of LiquidFuel, start masses 15.77, 11.00 and 6.23 t.
    /// </summary>
    public sealed class LandingReserveTests
    {
        private const uint Pod = 1, TankU = 2, EngU = 3, D1 = 4, TankM = 5, EngM = 6, D2 = 7, TankL = 8, EngL = 9;

        // Stock's own figures for those stages: "dv_vac" of stages 4, 3 and 2 in mun-shot-pad.json (the stock delta-v simulator's readout, recorded in game).
        private const double StockStage4 = 989.8015747070312, StockStage3 = 1529.1993408203125, StockStage2 = 2222.46630859375;

        [Fact]
        public void Per_stage_delta_v_matches_stocks_within_2_percent_on_a_three_stage_stack()
        {
            var per = StageDeltaV.PerStage(Stack(), 4);
            Assert.InRange(per[4], StockStage4 * 0.98, StockStage4 * 1.02);
            Assert.InRange(per[3], StockStage3 * 0.98, StockStage3 * 1.02);
            Assert.InRange(per[2], StockStage2 * 0.98, StockStage2 * 1.02);
            Assert.Equal(0, per[1]);
            Assert.Equal(0, per[0]);
        }

        [Fact]
        public void A_stage_dropped_before_the_landing_carries_no_reserve()
        {
            var snapshot = Snapshot(Stack());
            var reserve = LandingReserve.Of(snapshot, snapshot.Craft.Parts.Where(p => p.Id == D2 || p.Id == TankL || p.Id == EngL), 0);
            var share = (StockStage3 + StockStage2) / (StockStage4 + StockStage3 + StockStage2);
            Assert.Equal(share, reserve.KeptShare, 2);
            Assert.True(reserve.KeepsEngine);
            Assert.True(DeorbitBurn.AutoMaxDv(4000, 0, 0, reserve.KeptShare) > DeorbitBurn.AutoMaxDv(4000, 0));
        }

        [Fact]
        public void A_craft_that_drops_its_only_engines_keeps_no_engine()
        {
            var snapshot = Snapshot(Stack());
            var reserve = LandingReserve.Of(snapshot, snapshot.Craft.Parts.Where(p => p.Id != Pod), 0);
            Assert.False(reserve.KeepsEngine);
            Assert.Equal(0, reserve.KeptShare);
        }

        [Fact]
        public void Stages_below_stage_limit_are_held_back_and_a_craft_with_none_above_it_keeps_no_engine()
        {
            var snapshot = Snapshot(Stack());
            var held = LandingReserve.Of(snapshot, new SnapshotPart[0], 3);
            Assert.InRange(held.HeldDv, StockStage2 * 0.98, StockStage2 * 1.02);
            Assert.Equal(1, held.KeptShare, 9);
            Assert.False(LandingReserve.Of(snapshot, new SnapshotPart[0], 5).KeepsEngine);
        }

        [Fact]
        public void A_single_stage_lander_s_reserve_is_unchanged()
        {
            var parts = Stack().Where(p => p.Id == Pod || p.Id == TankU || p.Id == EngU).ToList();
            parts.Single(p => p.Id == EngU).Stage = 0;
            var snapshot = Snapshot(parts, 0);
            var reserve = LandingReserve.Of(snapshot, snapshot.Craft.Parts.Where(p => p.Id == Pod && false), 0);
            Assert.Equal(1, reserve.KeptShare, 12);
            Assert.True(reserve.KeepsEngine);
            Assert.Equal(0, reserve.HeldDv);
            Assert.Equal(DeorbitBurn.AutoMaxDv(500, 120, 30), DeorbitBurn.AutoMaxDv(500, 120, 30, reserve.KeptShare), 12);
        }

        [Fact]
        public void A_craft_re_rooted_at_its_last_engine_has_the_same_reserve_as_the_pod_rooted_one()
        {
            var rooted = Stack();
            var flipped = Stack();
            var chain = new[] { Pod, TankU, EngU, D1, TankM, EngM, D2, TankL, EngL };
            for (var i = 0; i < chain.Length; i++) flipped.Single(p => p.Id == chain[i]).ParentId = i + 1 < chain.Length ? chain[i + 1] : (uint?)null;
            Func<List<SnapshotPart>, uint?, LandingReserve> reserve = (parts, home) =>
                LandingReserve.Of(Snapshot(parts), parts.Where(p => p.Id == D2 || p.Id == TankL || p.Id == EngL), 0, home);
            var expected = reserve(rooted, Pod);
            var actual = reserve(flipped, Pod);
            Assert.Equal(expected.KeptShare, actual.KeptShare, 9);
            Assert.True(actual.KeepsEngine);
            Assert.Equal(expected.HeldDv, actual.HeldDv, 9);
            Assert.Equal(expected.KeptShare, reserve(rooted, null).KeptShare, 9);
        }

        [Fact]
        public void A_docked_pair_counts_only_the_Mission_craft_s_side()
        {
            // The Mission's pod has no engine of its own; the vessel docked to its port has one. Only the Mission's side counts, whichever pod is the root.
            const uint Port = 20, PartnerPort = 21, PartnerPod = 22;
            Func<uint?, LandingReserve> reserve = root =>
            {
                var parts = new List<SnapshotPart>
                {
                    Part(Pod, null, -1, 1.0, tags: "command"),
                    Part(Port, Pod, -1, 0.05, tags: "docking_port"),
                    Part(PartnerPort, Port, -1, 0.05, tags: "docking_port"),
                    Part(PartnerPod, PartnerPort, -1, 1.0, tags: "command"),
                    Tank(TankU, PartnerPod, 0, 3.0, 0.5),
                    Engine(EngU, TankU, 0),
                };
                return LandingReserve.Of(Snapshot(parts, 0), new SnapshotPart[0], 0, root);
            };
            Assert.False(reserve(Pod).KeepsEngine);
            Assert.True(reserve(null).KeepsEngine);
            Assert.True(reserve(PartnerPod).KeepsEngine);
            Assert.True(reserve(PartnerPod).KeptShare > 0.99);
        }

        [Fact]
        public void A_port_used_as_a_structural_joint_in_the_craft_s_own_stack_keeps_the_engine_and_reserve()
        {
            // Pod, a docking port as a VAB joint, tank, engine: the same craft with or without the port (a lone port is not a docking).
            const uint Port = 20;
            Func<bool, LandingReserve> reserve = withPort =>
            {
                var parts = new List<SnapshotPart> { Part(Pod, null, -1, 1.0, tags: "command") };
                if (withPort) parts.Add(Part(Port, Pod, -1, 0.05, tags: "docking_port"));
                parts.Add(Tank(TankU, withPort ? Port : Pod, 0, 3.0, 0.5));
                parts.Add(Engine(EngU, TankU, 0));
                return LandingReserve.Of(Snapshot(parts, 0), new SnapshotPart[0], 0, Pod);
            };
            Assert.True(reserve(true).KeepsEngine);
            Assert.Equal(reserve(false).KeptShare, reserve(true).KeptShare, 3);
            Assert.True(reserve(true).KeptShare > 0.9);
        }

        // A pod, a pair of ports, then a tank and engine beyond them; each port's state is as the adapter read it ("" = none read).
        private static LandingReserve ThroughPair(string stateA, string stateB)
        {
            const uint PortA = 20, PortB = 21;
            var a = Part(PortA, Pod, -1, 0.05, tags: "docking_port");
            var b = Part(PortB, PortA, -1, 0.05, tags: "docking_port");
            a.PortState = stateA;
            b.PortState = stateB;
            var parts = new List<SnapshotPart> { Part(Pod, null, -1, 1.0, tags: "command"), a, b, Tank(TankU, PortB, 0, 3.0, 0.5), Engine(EngU, TankU, 0) };
            return LandingReserve.Of(Snapshot(parts, 0), new SnapshotPart[0], 0, Pod);
        }

        [Fact]
        public void A_PreAttached_port_pair_used_as_a_decoupler_keeps_the_engine_and_reserve_beyond_it()
        {
            var reserve = ThroughPair("PreAttached", "PreAttached");
            Assert.True(reserve.KeepsEngine);
            Assert.True(reserve.KeptShare > 0.9);
        }

        [Fact]
        public void A_docked_port_pair_ends_the_own_side_through_the_state_tag()
        {
            Assert.False(ThroughPair("Docked (docker)", "Docked (dockee)").KeepsEngine);
            Assert.False(ThroughPair("Docked (docker)", "").KeepsEngine);
        }

        [Fact]
        public void A_port_docked_to_its_own_vessel_does_not_end_the_own_side()
        {
            Assert.True(ThroughPair("Docked (same vessel)", "Docked (same vessel)").KeepsEngine);
        }

        [Fact]
        public void An_old_snapshot_with_no_port_state_still_counts_a_joined_port_pair_as_a_docking()
        {
            Assert.False(ThroughPair("", "").KeepsEngine);
        }

        [Fact]
        public void A_docked_inline_port_keeps_its_own_structural_neighbours()
        {
            // Pod, inline port (docked), tank, engine below it; the partner port hangs off the side. Only the edge to the partner is cut.
            const uint Inline = 20, Partner = 21, PartnerTank = 23;
            var inline = Part(Inline, Pod, -1, 0.05, tags: "docking_port");
            inline.PortState = "Docked (docker)";
            var partner = Part(Partner, Inline, -1, 0.05, tags: "docking_port");
            partner.PortState = "Docked (dockee)";
            var parts = new List<SnapshotPart>
            {
                Part(Pod, null, -1, 1.0, tags: "command"), inline, Tank(TankU, Inline, 0, 3.0, 0.5), Engine(EngU, TankU, 0),
                partner, Tank(PartnerTank, Partner, 0, 3.0, 0.5),
            };
            var reserve = LandingReserve.Of(Snapshot(parts, 0), new SnapshotPart[0], 0, Pod);
            Assert.True(reserve.KeepsEngine);
            Assert.True(PartTree.OwnSide(parts, Pod).All(x => x.Id != Partner && x.Id != PartnerTank));
            Assert.Contains(PartTree.OwnSide(parts, Pod), x => x.Id == EngU);
        }

        /// <summary>#907: ENTRYCRAFT reads stage_limit, so the library's passing it changes held_dv and whether an engine stays.</summary>
        [Fact]
        public void Stage_limit_reaches_ENTRYCRAFT_and_changes_the_reserve()
        {
            Func<double, IDictionary<string, object>> call = limit => BlockCalls.EntryCraftCall(new Dictionary<string, object> { { "stage_limit", limit } }, Snapshot(Stack()), null, 0, "", id => id.ToString());
            var none = call(0);
            var held = call(3);
            Assert.Equal("", none["error"]);
            Assert.Equal(0.0, Convert.ToDouble(none["held_dv"]));
            Assert.InRange(Convert.ToDouble(held["held_dv"]), StockStage2 * 0.98, StockStage2 * 1.02);
            Assert.Equal(true, none["keeps_engine"]);
            Assert.Equal(false, call(5)["keeps_engine"]);
        }

        private static GameSnapshot Snapshot(List<SnapshotPart> parts, int stage = 4)
        {
            return new GameSnapshot { Ut = 0, Craft = new SnapshotCraft { Stage = stage, Parts = parts, MassTonnes = parts.Sum(p => p.MassT) } };
        }

        private static List<SnapshotPart> Stack()
        {
            return new List<SnapshotPart>
            {
                Part(Pod, null, -1, 2.23, tags: "command"),
                Tank(TankU, Pod, 2, 3.0, 0.5),
                Engine(EngU, TankU, 2),
                Part(D1, EngU, 2, 0.04, tags: "decoupler"),
                Tank(TankM, D1, 3, 4.0, 0.23),
                Engine(EngM, TankM, 3),
                Part(D2, EngM, 3, 0.04, tags: "decoupler"),
                Tank(TankL, D2, 4, 4.0, 0.23),
                Engine(EngL, TankL, 4),
            };
        }

        private static SnapshotPart Part(uint id, uint? parent, int stage, double massT, string tags = "")
        {
            return new SnapshotPart { Id = id, ParentId = parent, Stage = stage, MassT = massT, Tags = tags == "" ? new List<string>() : new List<string> { tags } };
        }

        private static SnapshotPart Tank(uint id, uint parent, int stage, double fuelT, double dryT)
        {
            var tank = Part(id, parent, stage, fuelT + dryT);
            tank.DryMassT = dryT;
            tank.Resources.Add(new SnapshotPartResource { Name = "LiquidFuel", Amount = fuelT / 0.005, Density = 0.005 });
            return tank;
        }

        private static SnapshotPart Engine(uint id, uint parent, int stage)
        {
            var engine = Part(id, parent, stage, 0.5, tags: "engine");
            engine.ThrustVacKn = 60;
            engine.IspVac = 345;
            engine.EnginePropellants.Add("LiquidFuel");
            return engine;
        }
    }
}
