// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// A drag-cube face bared by a part the Entry drops (#924): stock's occlusion rewrites the face's coefficient as well as its area, so a bared face takes both back.
    /// HeatShield1's figures are its two cubes in KSP's PartDatabase.cfg, faces in stock's order (+X, -X, +Y, -Y, +Z, -Z); face 3 is the one toward the stack.
    /// Fairing is the cube weighted once the stack has gone: by Physics.cfg's curves at Mach 2 it gives 2.22 m² with the pod's face covered and Clean 1.87, and flight m20261108-202939-bfd5 logged 2.23.
    /// </summary>
    public sealed class BaredCubeFacesTests
    {
        private static readonly float[] FairingDrag = { 0.7461f, 0.7461f, 0.9658f, 0.9675f, 0.7461f, 0.7461f };
        private static readonly float[] CleanDrag = { 0.468f, 0.468f, 0.9574f, 0.9507f, 0.4664f, 0.4695f };

        [Fact]
        public void The_unoccluded_coefficient_is_the_only_weighted_cubes()
        {
            var drag = BaredCubeFaces.UnoccludedDrag(new[] { FairingDrag, CleanDrag }, new[] { 1f, 0f });
            Assert.Equal(FairingDrag, drag);
        }

        [Fact]
        public void The_unoccluded_coefficient_is_normalised_by_the_total_weight()
        {
            var drag = BaredCubeFaces.UnoccludedDrag(new[] { FairingDrag, CleanDrag }, new[] { 2f, 6f });
            Assert.Equal(0.537525f, drag[0], 5);
            Assert.Equal(0.9549f, drag[FaceTowardStack], 5);
        }

        [Fact]
        public void No_weight_on_any_cube_gives_no_coefficient_to_restore()
        {
            Assert.Null(BaredCubeFaces.UnoccludedDrag(new[] { FairingDrag, CleanDrag }, new[] { 0f, 0f }));
        }

        [Fact]
        public void A_fully_covered_face_takes_back_its_area_and_coefficient_from_1E05()
        {
            var faces = BaredCubeFaces.Bare(LiveAreas(0f), LiveDrags(1E-05f), WholeAreas, FairingDrag, new[] { FaceTowardStack });
            Assert.Equal(1.435f, faces.Areas[FaceTowardStack]);
            Assert.Equal(0.9675f, faces.Drags[FaceTowardStack]);
        }

        [Fact]
        public void A_partly_covered_face_takes_back_its_area_and_coefficient()
        {
            var faces = BaredCubeFaces.Bare(LiveAreas(0.4f), LiveDrags(0.31f), WholeAreas, FairingDrag, new[] { FaceTowardStack });
            Assert.Equal(1.435f, faces.Areas[FaceTowardStack]);
            Assert.Equal(0.9675f, faces.Drags[FaceTowardStack]);
        }

        [Fact]
        public void A_face_not_bared_keeps_stocks_live_values_exactly()
        {
            var faces = BaredCubeFaces.Bare(LiveAreas(0.4f), LiveDrags(0.31f), WholeAreas, FairingDrag, new[] { FaceTowardStack });
            for (var f = 0; f < 6; f++)
            {
                if (f == FaceTowardStack) continue;
                Assert.Equal(LiveAreas(0.4f)[f], faces.Areas[f]);
                Assert.Equal(LiveDrags(0.31f)[f], faces.Drags[f]);
            }
        }

        [Fact]
        public void With_no_face_bared_the_part_reads_as_stock_has_it()
        {
            Assert.Null(BaredCubeFaces.Bare(LiveAreas(0.4f), LiveDrags(0.31f), WholeAreas, FairingDrag, new int[0]));
        }

        [Fact]
        public void With_no_coefficient_to_restore_a_bared_face_keeps_its_live_one_and_takes_back_its_area()
        {
            var faces = BaredCubeFaces.Bare(LiveAreas(0.4f), LiveDrags(0.31f), WholeAreas, null, new[] { FaceTowardStack });
            Assert.Equal(1.435f, faces.Areas[FaceTowardStack]);
            Assert.Equal(0.31f, faces.Drags[FaceTowardStack]);
        }

        [Fact]
        public void Baring_leaves_stocks_own_arrays_alone()
        {
            var areas = LiveAreas(0f);
            var drags = LiveDrags(1E-05f);
            BaredCubeFaces.Bare(areas, drags, WholeAreas, FairingDrag, new[] { FaceTowardStack });
            Assert.Equal(0f, areas[FaceTowardStack]);
            Assert.Equal(1E-05f, drags[FaceTowardStack]);
        }

        private const int FaceTowardStack = 3;
        private static readonly float[] WholeAreas = { 0.3875f, 0.3875f, 1.435f, 1.435f, 0.3875f, 0.3875f };

        /// <summary>Stock's live areas with the stack still on: the face toward it at <paramref name="toward"/>, the others as the cube, the pod's face covered.</summary>
        private static float[] LiveAreas(float toward)
        {
            return new[] { 0.3875f, 0.3875f, 0f, toward, 0.3875f, 0.3875f };
        }

        private static float[] LiveDrags(float toward)
        {
            return new[] { 0.7461f, 0.7461f, 1E-05f, toward, 0.7461f, 0.7461f };
        }
    }
}
