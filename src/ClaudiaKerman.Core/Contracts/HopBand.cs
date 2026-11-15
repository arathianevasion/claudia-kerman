// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1048: the band a Contract Requirement met below orbit asks the craft through, over the launch body: an altitude band, and a
    /// speed band where the Contract gives one; or, for reaching space, anywhere above the atmosphere top.
    /// </summary>
    public sealed class HopBand
    {
        /// <summary>Above the atmosphere top (reach space): the altitudes come from the body.</summary>
        public bool AboveAir;
        /// <summary>m; null when open.</summary>
        public double? MinAltitude;
        public double? MaxAltitude;
        /// <summary>m/s; null when the Contract gives none.</summary>
        public double? MinSpeed;
        public double? MaxSpeed;

        /// <summary>
        /// The apoapsis a hop straight up aims for, m: the middle of the altitude band (above the atmosphere top, 5% over it), raised
        /// where the band's lowest speed needs it, so the coast enters the band at least that fast. A hop straight up passes every speed
        /// from that down to 0 at its apoapsis, so the band's speeds are met on the way.
        /// </summary>
        public double TargetAp(SnapshotBody body)
        {
            var floor = AboveAir ? body.AtmosphereHeight : MinAltitude ?? 0;
            var ceiling = AboveAir ? (double?)null : MaxAltitude;
            var ap = ceiling.HasValue && ceiling.Value > floor ? (floor + ceiling.Value) / 2 : floor * 1.05;
            if (MinSpeed.HasValue && body.Gm > 0)
            {
                var r = body.Radius + floor;
                ap = Math.Max(ap, floor + MinSpeed.Value * MinSpeed.Value / (2 * body.Gm / (r * r)));
            }
            return ap;
        }

        /// <summary>The hop in words: "reach space over Kerbin", "hop through 51,000–56,000 m at 100–260 m/s over Kerbin".</summary>
        public string Words(string home)
        {
            if (AboveAir) return "reach space over " + home;
            return "hop " + Through(home);
        }

        /// <summary>The band in words, without the hop: "through 51,000–56,000 m at 100–260 m/s over Kerbin".</summary>
        public string Through(string home)
        {
            return "through " + Range(MinAltitude, MaxAltitude) + " m" + AtSpeed() + " over " + home;
        }

        /// <summary>The speed band in words, " at 100–260 m/s"; "" with none.</summary>
        public string AtSpeed()
        {
            return MinSpeed.HasValue || MaxSpeed.HasValue ? " at " + Range(MinSpeed, MaxSpeed) + " m/s" : "";
        }

        /// <summary>#1029: the altitude band in km, "8–12 km", "over 70 km".</summary>
        public string Through()
        {
            var min = MinAltitude ?? 0;
            if (!MaxAltitude.HasValue) return "over " + Km(min) + " km";
            return Km(min) + "–" + Km(MaxAltitude.Value) + " km";
        }

        /// <summary>#1029: whether <paramref name="altitude"/> m is in the altitude band (never for <see cref="AboveAir"/>, whose band is the body's).</summary>
        public bool HoldsAltitude(double altitude)
        {
            return !AboveAir && altitude >= (MinAltitude ?? 0) && (!MaxAltitude.HasValue || altitude <= MaxAltitude.Value);
        }

        /// <summary>#1029: whether <paramref name="speed"/> m/s is in the speed band (always, with none given).</summary>
        public bool HoldsSpeed(double speed)
        {
            return (!MinSpeed.HasValue || speed >= MinSpeed.Value) && (!MaxSpeed.HasValue || speed <= MaxSpeed.Value);
        }

        /// <summary>
        /// #1029: why the speeds seen in the altitude band, <paramref name="lowest"/> to <paramref name="highest"/> m/s, miss the speed band:
        /// "speed 460 m/s, above the band, through 8–12 km"; "" when they meet it.
        /// </summary>
        public string SpeedMiss(double lowest, double highest)
        {
            if (MaxSpeed.HasValue && lowest > MaxSpeed.Value) return "speed " + Whole(lowest) + " m/s, above the band, through " + Through();
            if (MinSpeed.HasValue && highest < MinSpeed.Value) return "speed " + Whole(highest) + " m/s, below the band, through " + Through();
            return "";
        }

        /// <summary>
        /// #1029: whether a path, (altitude m, speed m/s) points in the order flown with speed taken as straight between them, passes through
        /// the band: null when it does, otherwise why not ("speed 460 m/s, above the band, through 8–12 km", or "never reaches 8–12 km").
        /// </summary>
        public string Misses(IList<double[]> path)
        {
            double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
            var bottom = MinAltitude ?? 0;
            var top = MaxAltitude ?? double.PositiveInfinity;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                double a0 = path[i][0], s0 = path[i][1], a1 = path[i + 1][0], s1 = path[i + 1][1];
                if (Math.Max(a0, a1) < bottom || Math.Min(a0, a1) > top) continue;
                Func<double, double> at = a => Math.Abs(a1 - a0) < 1e-9 ? s0 : s0 + (s1 - s0) * (a - a0) / (a1 - a0);
                var from = at(Math.Max(Math.Min(a0, a1), bottom));
                var to = at(Math.Min(Math.Max(a0, a1), top));
                var low = Math.Min(from, to);
                var high = Math.Max(from, to);
                if ((!MaxSpeed.HasValue || low <= MaxSpeed.Value) && (!MinSpeed.HasValue || high >= MinSpeed.Value)) return null;
                lowest = Math.Min(lowest, low);
                highest = Math.Max(highest, high);
            }
            return double.IsInfinity(lowest) ? "never reaches " + Through() : SpeedMiss(lowest, highest);
        }

        private static string Km(double metres)
        {
            return (Math.Round(metres / 100) / 10).ToString("#,0.#", CultureInfo.InvariantCulture);
        }

        private static string Range(double? min, double? max)
        {
            if (!max.HasValue) return "over " + Whole(min ?? 0);
            if (!min.HasValue) return "under " + Whole(max.Value);
            return Whole(min.Value) + "–" + Whole(max.Value);
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
