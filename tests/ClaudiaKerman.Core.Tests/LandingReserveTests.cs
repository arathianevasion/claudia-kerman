// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

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
