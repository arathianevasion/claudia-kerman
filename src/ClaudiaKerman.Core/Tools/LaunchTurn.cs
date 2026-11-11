// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// <c>launch</c>'s turn law (#978, ADR 0014), flown by the block through <c>ADDONS:CLAUDIA:LAUNCHPITCH</c> every tick of the climb and by
    /// <see cref="AscentSim"/> for <c>plan_ascent</c>: the pitch program, then the thin-air rule. Under 500 Pa (where max_aoa doesn't apply) the
    /// nose is held no more than 3 degrees below the surface prograde (#236), so the upper turn follows its trajectory. Low in the air (over
    /// a tenth of the surface pressure, where q is under 500 Pa only because the craft is still slow) a craft with thrust to spare may go
    /// lower, down to the pitch at which its thrust still holds its vertical speed against gravity: on Duna, where the turn starts under
    /// 500 Pa, that lets the turn's first kick happen at all, and the climb no longer goes up nearly straight. On Kerbin's pad the air is
    /// over 500 Pa from 29 m/s, before the turn starts, so nothing there changes.
    /// </summary>
    public static class LaunchTurn
    {
        /// <summary>Dynamic pressure (Pa) above which the air pushes hard enough on the craft for max_aoa to apply (launch_aero_q).</summary>
        public const double AeroQ = 500;
        /// <summary>Degrees the nose may be below the surface prograde in thin air for a craft with no thrust to spare (#236).</summary>
        public const double ThinLead = 3;
        /// <summary>Under this surface speed (m/s) the prograde means little and the thin-air rule waits.</summary>
        public const double ThinSpeed = 30;
        /// <summary>The pressure, as a fraction of the surface's, over which the air is thick (launch_thick_air).</summary>
        public const double ThickRatio = 0.1;
        /// <summary>The most milliseconds 100 calls of <see cref="Call"/> may take: it runs every tick of the climb (ADR 0014 guardrail 6).</summary>
        public const double BudgetMs = 2;

        /// <summary>
        /// The pitch program (degrees above the horizon) at altitude <paramref name="h"/> m of a turn that starts at <paramref name="startAlt"/>
        /// (negative: not started) and ends flat at <paramref name="endAlt"/>: 90 * (1 - fraction^shape); a smaller shape turns over sooner.
        /// </summary>
        public static double Program(double h, double startAlt, double endAlt, double shape)
        {
            if (startAlt < 0 || h <= startAlt || endAlt <= startAlt) return 90;
            var fraction = Math.Min(1, (h - startAlt) / (endAlt - startAlt));
            return 90 * (1 - Math.Pow(fraction, shape));
        }

        /// <summary>
        /// The pitch (degrees above the horizon) at which thrust <paramref name="accel"/> m/s² holds the vertical speed against
        /// <paramref name="gNet"/> m/s² (gravity less what the orbital speed along the horizon holds up); 90 when the thrust can't.
        /// </summary>
        public static double HoldPitch(double accel, double gNet)
        {
            if (accel <= 0 || gNet >= accel) return 90;
            return Math.Asin(Math.Max(0, gNet) / accel) * 180 / Math.PI;
        }

        /// <summary>
        /// The commanded pitch (degrees above the horizon): the program at <paramref name="program"/>, raised in thin air (q under
        /// <see cref="AeroQ"/> Pa, surface speed over <see cref="ThinSpeed"/>) to no lower than <see cref="ThinLead"/> below
        /// <paramref name="prograde"/> (the surface prograde's pitch), or, in thick air (<paramref name="pressureRatio"/> of the surface
        /// pressure over <see cref="ThickRatio"/>), than the hold pitch if that is lower. <paramref name="thin"/> says whether the rule raised it.
        /// </summary>
        public static double Pitch(double program, double prograde, double speed, double q, double pressureRatio, double accel, double gNet, out bool thin)
        {
            thin = false;
            if (speed <= ThinSpeed || q > AeroQ) return program;
            var floor = prograde - ThinLead;
            if (pressureRatio > ThickRatio) floor = Math.Min(floor, HoldPitch(accel, gNet));
            thin = floor > program;
            return Math.Max(program, floor);
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:LAUNCHPITCH</c>: <c>alt</c>, <c>start_alt</c> (negative before the turn starts), <c>end_alt</c> (m), <c>shape</c>,
        /// <c>speed</c> (surface, m/s), <c>prograde</c> (the surface prograde's pitch, degrees), <c>q</c> (Pa), <c>ratio</c> (the pressure over the surface's), <c>accel</c> (the thrust now,
        /// m/s²) and <c>g_net</c> (m/s²); lexicon <c>error</c>, <c>pitch</c> (degrees above the horizon) and <c>thin</c>. Pure.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, double>();
            foreach (var key in new[] { "alt", "start_alt", "end_alt", "shape", "speed", "prograde", "q", "ratio", "accel", "g_net" })
            {
                object raw;
                if (!ask.TryGetValue(key, out raw) || !(raw is double)) return Error(key + " is required, a number");
                values[key] = (double)raw;
            }
            bool thin;
            var program = Program(values["alt"], values["start_alt"], values["end_alt"], values["shape"]);
            var pitch = Pitch(program, values["prograde"], values["speed"], values["q"], values["ratio"], values["accel"], values["g_net"], out thin);
            return new Dictionary<string, object> { { "error", "" }, { "pitch", pitch }, { "thin", thin } };
        }

        private static IDictionary<string, object> Error(string why)
        {
            return new Dictionary<string, object> { { "error", why }, { "pitch", 90.0 }, { "thin", false } };
        }
    }
}
