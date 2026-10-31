// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Threading;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The token of the Planning Tool call running on this thread (#544), set by <see cref="ToolStep"/> around the call. A body
    /// interface that blocks a worker on the main thread (the adapter's survey queue) reads it to stop waiting and drop its
    /// queued job when the call is abandoned. <see cref="CancellationToken.None"/> off a tool call.
    /// </summary>
    public static class PlanningCancellation
    {
        [ThreadStatic] private static CancellationToken _current;

        public static CancellationToken Current
        {
            get { return _current; }
        }

        internal static void Set(CancellationToken token)
        {
            _current = token;
        }
    }

    /// <summary>
    /// The body interface as one abandoned-able call sees it (#544): once <see cref="ToolStep"/> gives up on the call at its
    /// timeout, every read throws <see cref="OperationCanceledException"/>, so the call's task queues no more surveys and ends
    /// (the module turns the throw into an error result nobody reads).
    /// </summary>
    internal class CancellableBody : IPlanningBody
    {
        private readonly IPlanningBody _api;
        private readonly CancellationToken _token;

        /// <summary>Wraps <paramref name="api"/>, keeping its own clock (<see cref="IPlanningClock"/>) visible to <see cref="SearchDeadline"/>.</summary>
        public static IPlanningBody Wrap(IPlanningBody api, CancellationToken token)
        {
            var clock = api as IPlanningClock;
            return clock != null ? new Clocked(api, token, clock) : new CancellableBody(api, token);
        }

        private sealed class Clocked : CancellableBody, IPlanningClock
        {
            private readonly IPlanningClock _clock;

            public Clocked(IPlanningBody api, CancellationToken token, IPlanningClock clock) : base(api, token)
            {
                _clock = clock;
            }

            public double ElapsedMs
            {
                get { return _clock.ElapsedMs; }
            }
        }

        private CancellableBody(IPlanningBody api, CancellationToken token)
        {
            _api = api;
            _token = token;
        }

        public double TerrainAltitude(string body, double latitude, double longitude)
        {
            _token.ThrowIfCancellationRequested();
            return _api.TerrainAltitude(body, latitude, longitude);
        }

        public string Biome(string body, double latitude, double longitude)
        {
            _token.ThrowIfCancellationRequested();
            return _api.Biome(body, latitude, longitude);
        }

        public double SunElevation(string body, double latitude, double longitude, double ut)
        {
            _token.ThrowIfCancellationRequested();
            return _api.SunElevation(body, latitude, longitude, ut);
        }

        public void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
        {
            _token.ThrowIfCancellationRequested();
            _api.Survey(body, samples, facts);
            _token.ThrowIfCancellationRequested();
        }

        public bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
        {
            _token.ThrowIfCancellationRequested();
            return _api.SurfaceUnder(body, x, y, z, out latitude, out longitude);
        }
    }
}
