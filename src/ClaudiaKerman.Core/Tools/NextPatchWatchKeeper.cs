// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Which <see cref="NextPatchWatch"/> the adapter holds, and what it carries across a rebuild (#878). The watch is rebuilt when the planned set
    /// changes, and the view names no craft while a run is going, so the meeting answered is kept through that stretch: it goes only with a different
    /// craft or <see cref="Reset"/> (a loaded save). The adapter only passes in what the view says.
    /// </summary>
    public sealed class NextPatchWatchKeeper
    {
        private string _craft = "";
        private string _planned;
        private NextPatchWatch _watch;
        /// <summary>The last watch of <see cref="_craft"/>, kept while no craft is named (a run is going).</summary>
        private NextPatchWatch _remembered;

        /// <summary>The watch for this craft ("" for none, so null) and planned set; rebuilt only when either changed, carrying the answered meeting for the same craft.</summary>
        public NextPatchWatch Watch(string craft, string planned)
        {
            craft = craft ?? "";
            planned = planned ?? "";
            if (craft.Length == 0)
            {
                if (_watch != null) _remembered = _watch;
                _watch = null;
                _planned = null;
                return null;
            }
            if (_watch != null && craft == _craft && planned == _planned) return _watch;
            var kept = craft == _craft ? (_watch ?? _remembered) : null;
            _craft = craft;
            _planned = planned;
            _remembered = null;
            _watch = new NextPatchWatch(planned, kept);
            return _watch;
        }

        /// <summary>A save was loaded: nothing carries over.</summary>
        public void Reset()
        {
            _craft = "";
            _planned = null;
            _watch = null;
            _remembered = null;
        }
    }
}
