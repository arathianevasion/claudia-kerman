// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;

namespace ClaudiaKerman.Core.Tools
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
            if (SearchBudget.Unlimited) return None;
            var watch = Stopwatch.StartNew();
            return new SearchDeadline(() => watch.Elapsed.TotalMilliseconds, budgetMs);
        }

        public bool Passed
        {
            get { return _clockMs() >= _endMs; }
        }
    }
}
