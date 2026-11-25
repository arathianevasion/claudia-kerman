// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

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

        /// <summary>Why a landed start at this spot is refused, with its slope (read as <c>land</c> reads it, <see cref="Terrain.Slope"/>); null if the ground is flat enough.</summary>
        public static string RefuseSite(Terrain.HeightAt height, double radius, double lat, double lng)
        {
            var slope = Terrain.Slope(height, radius, lat, lng);
            if (slope <= MaxSlopeDeg) return null;
            return "the landed start at " + lat.ToString("0.###", CultureInfo.InvariantCulture) + ", " + lng.ToString("0.###", CultureInfo.InvariantCulture)
                + " slopes " + slope.ToString("0.0", CultureInfo.InvariantCulture) + " degrees, more than " + MaxSlopeDeg.ToString("0", CultureInfo.InvariantCulture)
                + ": move it to flatter ground";
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
