// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Kos
{
    /// <summary>
    /// #1358: the rules for a <c>start = landed</c> block test. The runner refuses a site steeper than
    /// <see cref="MaxSlopeDeg"/> before it spawns, sets the craft down <see cref="SpawnClearance"/> m above the ground, and
    /// starts the test script only once <see cref="Settle"/> says the craft has come to rest on it.
    /// </summary>
    public static class BlockTestLandedStart
    {
        /// <summary>Degrees: steeper ground than this is refused before the spawn.</summary>
        public const double MaxSlopeDeg = 5;

        /// <summary>m from the craft's lowest point down to the ground when it is spawned.</summary>
        public const double SpawnClearance = 0.2;

        /// <summary>m/s: slower than this, LANDED, counts as at rest.</summary>
        public const double SettleSpeed = 0.1;

        /// <summary>s at rest in a row before the script starts.</summary>
        public const double SettleSeconds = 3;

        /// <summary>s after the craft is unpacked before the test fails as never settled.</summary>
        public const double GiveUpSeconds = 30;

        /// <summary>#1363: m out from the site asked for that the search for flat ground goes.</summary>
        public const double SearchReach = 10000;

        /// <summary>m between the rings of spots the search reads.</summary>
        public const double SearchStep = 100;

        /// <summary>m the hop's distance may change by when its target moves to flat ground.</summary>
        public const double HopTolerance = 2000;

        /// <summary>
        /// #1363: where a landed test starts and, when it names one, where its hop goes. Each is the nearest spot within
        /// <see cref="SearchReach"/> of the one asked for whose slope (read as <c>land</c> reads it, <see cref="Terrain.Slope"/>)
        /// is under <see cref="MaxSlopeDeg"/>. The target's spot must also be within <see cref="HopTolerance"/> of the hop's
        /// distance asked for, measured from the start's spot. <see cref="Placement.Failure"/> says why there is none.
        /// </summary>
        public static Placement Place(Terrain.HeightAt height, double radius, double lat, double lng, double? hopLat, double? hopLng)
        {
            var start = Terrain.Flattest(height, radius, lat, lng, SearchReach, MaxSlopeDeg, SearchStep, null);
            if (!start.Flat) return Placement.Failed(NoFlatGround("landed start", lat, lng, start));
            var notes = new List<string> { Moved("landed start", lat, lng, start) };
            if (!hopLat.HasValue || !hopLng.HasValue) return new Placement(start, null, notes, null);

            var asked = Terrain.Distance(lat, lng, hopLat.Value, hopLng.Value, radius);
            Func<double, double, bool> keepsTheHop = (la, ln) => Math.Abs(Terrain.Distance(start.Lat, start.Lng, la, ln, radius) - asked) <= HopTolerance;
            var target = Terrain.Flattest(height, radius, hopLat.Value, hopLng.Value, SearchReach, MaxSlopeDeg, SearchStep, keepsTheHop);
            if (target == null || !target.Flat) return Placement.Failed(NoFlatGround("hop target", hopLat.Value, hopLng.Value, target));
            notes.Add(Moved("hop target", hopLat.Value, hopLng.Value, target));
            var hop = Terrain.Distance(start.Lat, start.Lng, target.Lat, target.Lng, radius);
            notes.Add("the hop is " + Km(hop) + " km, " + Km(asked) + " km asked for");
            return new Placement(start, target, notes, null);
        }

        /// <summary>
        /// #1363: the test script with the moved hop target declared as the global <c>test_hop_target</c>, just after its
        /// <c>@lazyglobal</c> directive (which must come first), or at the top if it has none.
        /// </summary>
        public static string WithHopTarget(string source, Spot target)
        {
            var line = "global test_hop_target is latlng(" + target.Lat.ToString("R", CultureInfo.InvariantCulture) + ", "
                + target.Lng.ToString("R", CultureInfo.InvariantCulture) + ").\n";
            var lines = (source ?? "").Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("@lazyglobal", StringComparison.OrdinalIgnoreCase)) continue;
                return string.Join("\n", lines, 0, i + 1) + "\n" + line + string.Join("\n", lines, i + 1, lines.Length - i - 1);
            }
            return line + source;
        }

        private static string Moved(string what, double lat, double lng, Spot spot)
        {
            if (spot.Distance == 0) return "the " + what + " at " + At(lat, lng) + " slopes " + Degrees(spot.Slope) + " degrees: it stays there";
            return "moved the " + what + " from " + At(lat, lng) + " to " + At(spot.Lat, spot.Lng) + ", " + Km(spot.Distance) + " km away, where the ground slopes "
                + Degrees(spot.Slope) + " degrees";
        }

        private static string NoFlatGround(string what, double lat, double lng, Spot flattest)
        {
            return "no ground under " + MaxSlopeDeg.ToString("0", CultureInfo.InvariantCulture) + " degrees within " + Km(SearchReach) + " km of the " + what + " at "
                + At(lat, lng) + (flattest == null ? " that keeps the hop's distance" : ": the flattest found slopes " + Degrees(flattest.Slope) + " degrees");
        }

        private static string At(double lat, double lng)
        {
            return lat.ToString("0.###", CultureInfo.InvariantCulture) + ", " + lng.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Km(double metres)
        {
            return (metres / 1000).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Degrees(double slope)
        {
            return slope.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>#1363: where a landed test spawns and hops to, and the lines to log about it; or why it can't start.</summary>
        public sealed class Placement
        {
            public Placement(Spot start, Spot target, IList<string> notes, string failure)
            {
                Start = start;
                Target = target;
                Notes = notes;
                Failure = failure;
            }

            internal static Placement Failed(string why)
            {
                return new Placement(null, null, new List<string>(), why);
            }

            /// <summary>Where the craft spawns; null when <see cref="Failure"/> is set.</summary>
            public Spot Start { get; private set; }

            /// <summary>Where the hop goes; null when the test names no hop target, or on a failure.</summary>
            public Spot Target { get; private set; }

            /// <summary>Lines for the log: where each spot moved to, how far and its slope, and the hop's distance.</summary>
            public IList<string> Notes { get; private set; }

            /// <summary>Why the test fails before spawning (no flat ground in reach, naming the flattest slope found); null if it can start.</summary>
            public string Failure { get; private set; }
        }

        /// <summary>The reason a test fails when <see cref="Settle"/> gives up: the craft's lean from upright and the ground's slope, in degrees.</summary>
        public static string NeverSettled(double leanDeg, double slopeDeg)
        {
            return "never settled: " + GiveUpSeconds.ToString("0", CultureInfo.InvariantCulture) + " s without "
                + SettleSeconds.ToString("0", CultureInfo.InvariantCulture) + " s LANDED under " + SettleSpeed.ToString("0.0", CultureInfo.InvariantCulture)
                + " m/s; the craft leans " + leanDeg.ToString("0.0", CultureInfo.InvariantCulture) + " degrees on ground sloping "
                + slopeDeg.ToString("0.0", CultureInfo.InvariantCulture) + " degrees";
        }

        public enum SettleState
        {
            Waiting,
            Settled,
            NeverSettled,
        }

        /// <summary>
        /// Watches the craft from when it is unpacked: Settled once it has been LANDED and under <see cref="SettleSpeed"/> for
        /// <see cref="SettleSeconds"/> in a row, NeverSettled if that hasn't happened <see cref="GiveUpSeconds"/> after the first step.
        /// </summary>
        public sealed class Settle
        {
            private double? _first;
            private double? _restingSince;

            /// <summary>One look at the craft at <paramref name="now"/> s (any clock that runs on with the game).</summary>
            public SettleState Step(double now, bool landed, double speed)
            {
                if (!_first.HasValue) _first = now;
                if (landed && speed < SettleSpeed)
                {
                    if (!_restingSince.HasValue) _restingSince = now;
                    if (now - _restingSince.Value >= SettleSeconds) return SettleState.Settled;
                }
                else
                {
                    _restingSince = null;
                }
                return now - _first.Value >= GiveUpSeconds ? SettleState.NeverSettled : SettleState.Waiting;
            }
        }
    }
}
