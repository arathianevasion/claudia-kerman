// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1160: the launch options Claudia wrote into a leg's words ("launch with target_ap 55000 and turn_start_speed 100000"), as the
    /// plan check reads them: the same names launch and plan_ascent take. A leg that writes none has no <see cref="WrittenLaunch"/>
    /// (null), and is judged on plan_ascent's default shape as before. No schema carries them, so they are read from the words.
    /// </summary>
    internal sealed class WrittenLaunch
    {
        public double? TargetAp;
        public double? TurnStartSpeed;
        public double? TurnShape;
        public double? MaxQ;
        public int? StageLimit;

        /// <summary>The launch options in <paramref name="words"/>, or null when it names none.</summary>
        public static WrittenLaunch Read(string words)
        {
            words = words ?? "";
            var read = new WrittenLaunch
            {
                TargetAp = Number(words, "target_ap"),
                TurnStartSpeed = Number(words, "turn_start_speed"),
                TurnShape = Number(words, "turn_shape"),
                MaxQ = Number(words, "throttle_max_q"),
            };
            var limit = Number(words, "stage_limit");
            if (limit.HasValue) read.StageLimit = (int)limit.Value;
            return read.TargetAp.HasValue || read.TurnStartSpeed.HasValue || read.TurnShape.HasValue || read.MaxQ.HasValue || read.StageLimit.HasValue ? read : null;
        }

        private static double? Number(string words, string name)
        {
            var m = Regex.Match(words, @"\b" + name + @"\s*(?:=|:|of|to)?\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            double value;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (double?)null;
        }

        /// <summary>The target apoapsis is below orbit over <paramref name="body"/>: under the top of its air.</summary>
        public bool Suborbital(SnapshotBody body)
        {
            return body != null && TargetAp.HasValue && TargetAp.Value <= body.AtmosphereHeight;
        }

        /// <summary>A hop: a suborbital target apoapsis and a turn speed no climb reaches, so it flies straight up and never turns.</summary>
        public bool IsHop(SnapshotBody body)
        {
            return Suborbital(body) && TurnStartSpeed.HasValue && body.Radius > 0 && TurnStartSpeed.Value >= System.Math.Sqrt(body.Gm / body.Radius);
        }

        /// <summary>One string for the options, to key a flight kept by the judge.</summary>
        public string Key
        {
            get { return string.Join("|", new[] { TargetAp, TurnStartSpeed, TurnShape, MaxQ, (double?)StageLimit }.Select(v => v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : "")); }
        }
    }
}
