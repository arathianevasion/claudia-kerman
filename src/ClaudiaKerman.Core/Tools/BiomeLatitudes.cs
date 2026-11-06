// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Where a body's biomes lie (#806), as a landing site's latitude for the arrival plane: the game gives a biome only for a point, so a survey of the
    /// whole body every <see cref="LandingSiteSearch.BiomeGridDeg"/> degrees reads them, one point to about the same area. A biome that lies wholly at high latitude is
    /// polar, 90; one the equator runs through is 0, which sets no plane; any other is its median |latitude|, so the track runs through it rather than along its edge.
    /// Its lowest latitude is the tenth percentile, since the map's blended edges put stray points of a biome far from it. A biome too small for the grid is not in the table.
    /// </summary>
    public static class BiomeLatitudes
    {
        /// <summary>A biome wholly above this |latitude| (degrees) is polar.</summary>
        private const double PolarFrom = 60;
        /// <summary>A biome that reaches below this |latitude| (degrees) has the equator's track through it.</summary>
        private const double EquatorialTo = 5;

        /// <summary>Each biome's latitude, from <paramref name="biomeAt"/> (latitude, longitude; "" for none); empty for a body with no biome map.</summary>
        public static IDictionary<string, double> Survey(Func<double, double, string> biomeAt)
        {
            var seen = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            var step = LandingSiteSearch.BiomeGridDeg;
            for (var lat = -90 + step / 2; lat < 90; lat += step)
            {
                var count = Math.Max(1, (int)Math.Round(360 * Math.Cos(lat * Math.PI / 180) / step));
                for (var k = 0; k < count; k++)
                {
                    var biome = biomeAt(lat, -180 + (k + 0.5) * 360 / count);
                    if (string.IsNullOrEmpty(biome)) continue;
                    List<double> at;
                    if (!seen.TryGetValue(biome, out at)) seen[biome] = at = new List<double>();
                    at.Add(Math.Abs(lat));
                }
            }
            return seen.ToDictionary(s => s.Key, s => LatitudeOf(s.Value), StringComparer.OrdinalIgnoreCase);
        }

        private static double LatitudeOf(List<double> latitudes)
        {
            latitudes.Sort();
            var lowest = latitudes[latitudes.Count / 10];
            if (lowest >= PolarFrom) return 90;
            return lowest < EquatorialTo ? 0 : latitudes[latitudes.Count / 2];
        }
    }
}
