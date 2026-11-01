// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The body interface as one search sees it (#536): the orbit's frame on the body (<see cref="IPlanningBody.SurfaceUnder"/>)
    /// read once, not once per pass search. Every <see cref="LandingPass.Find"/> reads it, and the plane-change search makes
    /// hundreds of those; through the game each read is a frame. Everything else goes straight through.
    /// </summary>
    internal sealed class FrameReadOnce : IPlanningBody
    {
        private readonly IPlanningBody _api;
        private readonly Dictionary<string, double[]> _under = new Dictionary<string, double[]>();

        public FrameReadOnce(IPlanningBody api)
        {
            _api = api;
        }

        public double TerrainAltitude(string body, double latitude, double longitude)
        {
            return _api.TerrainAltitude(body, latitude, longitude);
        }

        public string Biome(string body, double latitude, double longitude)
        {
            return _api.Biome(body, latitude, longitude);
        }

        public IList<string> BiomeNames(string body)
        {
            return _api.BiomeNames(body);
        }

        public double SunElevation(string body, double latitude, double longitude, double ut)
        {
            return _api.SunElevation(body, latitude, longitude, ut);
        }

        public void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
        {
            _api.Survey(body, samples, facts);
        }

        public bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
        {
            var key = string.Format(CultureInfo.InvariantCulture, "{0}|{1:R}|{2:R}|{3:R}", body, x, y, z);
            double[] under;
            if (!_under.TryGetValue(key, out under))
            {
                double lat, lng;
                under = _api.SurfaceUnder(body, x, y, z, out lat, out lng) ? new[] { lat, lng } : null;
                _under[key] = under;
            }
            latitude = under != null ? under[0] : double.NaN;
            longitude = under != null ? under[1] : double.NaN;
            return under != null;
        }
    }
}
