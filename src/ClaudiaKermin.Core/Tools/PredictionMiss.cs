// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>reentry</c>'s log of how far its predicted landing was from the actual one (#478), through the block-internal
    /// <c>ADDONS:CLAUDIA:PREDICTIONMISS</c> (<see cref="Call"/>). Log-only: nothing flies on it. Pure.
    /// </summary>
    public static class PredictionMiss
    {
        /// <summary>
        /// The great-circle distance in km on a body of <c>radius</c> m between the predicted <c>lat</c>, <c>lng</c> and the
        /// actual <c>actual_lat</c>, <c>actual_lng</c> (degrees). Gives <c>km</c>.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var radius = Math.Max(1, Number(input, "radius", 1));
            var arc = LandingSiteSearch.ArcDegrees(Number(input, "lat", 0), Number(input, "lng", 0), Number(input, "actual_lat", 0), Number(input, "actual_lng", 0));
            return new Dictionary<string, object> { { "km", arc * Math.PI / 180 * radius / 1000 } };
        }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
