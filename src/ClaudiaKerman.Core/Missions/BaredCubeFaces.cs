// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// A part's drag-cube faces with those toward a part the Entry drops bared (#924). Stock's occlusion rewrites a covered face's coefficient as well as its area,
    /// down to 1E-05 when it is fully covered, so a bared face takes back both. Faces not bared keep stock's live values.
    /// </summary>
    public sealed class BaredCubeFaces
    {
        /// <summary>Each face's area, as the live occluded areas with the bared faces whole.</summary>
        public float[] Areas { get; private set; }

        /// <summary>Each face's drag coefficient, as the live ones with the bared faces un-occluded.</summary>
        public float[] Drags { get; private set; }

        /// <summary>
        /// Each face's coefficient before occlusion: the part's cubes' <paramref name="drags"/> averaged by their <paramref name="weights"/>, which stock keeps private.
        /// Null with no weight on any cube, when there is nothing to rebuild it from.
        /// </summary>
        public static float[] UnoccludedDrag(IList<float[]> drags, IList<float> weights)
        {
            float total = 0;
            for (var c = 0; c < drags.Count; c++) total += weights[c];
            if (total <= 0) return null;
            var mean = new float[drags[0].Length];
            for (var f = 0; f < mean.Length; f++)
            {
                for (var c = 0; c < drags.Count; c++) mean[f] += drags[c][f] * weights[c];
                mean[f] /= total;
            }
            return mean;
        }

        /// <summary>
        /// The faces with <paramref name="bared"/> (face indices) restored to <paramref name="wholeAreas"/> and <paramref name="wholeDrags"/>; null when none is bared, so the part reads as stock has it.
        /// With <paramref name="wholeDrags"/> null a bared face keeps its live coefficient.
        /// </summary>
        public static BaredCubeFaces Bare(float[] liveAreas, float[] liveDrags, float[] wholeAreas, float[] wholeDrags, ICollection<int> bared)
        {
            if (bared.Count == 0) return null;
            var areas = (float[])liveAreas.Clone();
            var drags = (float[])liveDrags.Clone();
            foreach (var face in bared)
            {
                areas[face] = wholeAreas[face];
                if (wholeDrags != null) drags[face] = wholeDrags[face];
            }
            return new BaredCubeFaces { Areas = areas, Drags = drags };
        }
    }
}
