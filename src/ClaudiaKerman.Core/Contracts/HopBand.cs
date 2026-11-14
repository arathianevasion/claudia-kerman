// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
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
            var speed = MinSpeed.HasValue || MaxSpeed.HasValue ? " at " + Range(MinSpeed, MaxSpeed) + " m/s" : "";
            return "hop through " + Range(MinAltitude, MaxAltitude) + " m" + speed + " over " + home;
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
