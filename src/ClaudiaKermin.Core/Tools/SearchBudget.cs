// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Threading;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// A switch for tests (#636): where set, the searches that time themselves by the wall clock (<see cref="SearchDeadline.After"/>
    /// without a body clock, <c>BrakePath.Search</c>) never run out of time, so a loaded machine can't cut a search short and change
    /// the answer. Off by default and per async flow, so production budgets are untouched and parallel tests don't see each other's.
    /// </summary>
    internal static class SearchBudget
    {
        private static readonly AsyncLocal<bool> NoWallClock = new AsyncLocal<bool>();

        public static bool Unlimited
        {
            get { return NoWallClock.Value; }
            set { NoWallClock.Value = value; }
        }
    }
}
