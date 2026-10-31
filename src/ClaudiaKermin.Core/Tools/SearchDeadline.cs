// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>A body interface that keeps its own time, ms since it was made: the tests' model of what the game's would take.</summary>
    internal interface IPlanningClock
    {
        double ElapsedMs { get; }
    }

    /// <summary>
    /// The time a Planning Tool's search may take (#536), so it answers with what it found by then instead of running into the
    /// tool timeout (<see cref="ToolStep.CallTimeout"/>). On a worker thread the body interface costs a frame a round trip and
    /// a few ms of samples a frame, so the clock is the wall clock; a body interface that keeps its own time
    /// (<see cref="IPlanningClock"/>) is timed by that instead. <see cref="None"/> never passes, for a search that must run to
    /// the end (land's block call, on the main thread).
    /// </summary>
    internal sealed class SearchDeadline
    {
        public static readonly SearchDeadline None = new SearchDeadline(() => 0, double.PositiveInfinity);

        private readonly Func<double> _clockMs;
        private readonly double _endMs;

        private SearchDeadline(Func<double> clockMs, double endMs)
        {
            _clockMs = clockMs;
            _endMs = endMs;
        }

        /// <summary>A deadline <paramref name="budgetMs"/> from now, by <paramref name="api"/>'s clock if it keeps one, else the wall clock.</summary>
        public static SearchDeadline After(double budgetMs, IPlanningBody api)
        {
            var clock = api as IPlanningClock;
            if (clock != null)
            {
                var start = clock.ElapsedMs;
                return new SearchDeadline(() => clock.ElapsedMs - start, budgetMs);
            }
            var watch = Stopwatch.StartNew();
            return new SearchDeadline(() => watch.Elapsed.TotalMilliseconds, budgetMs);
        }

        public bool Passed
        {
            get { return _clockMs() >= _endMs; }
        }
    }

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
