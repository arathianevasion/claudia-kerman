// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Whether a burn is worth its turn (#715, ADR 0014), through the block-internal <c>ADDONS:CLAUDIA:BURNWORTH</c>
    /// (<see cref="Call"/>), called once by <c>circularize</c> and by <c>tune_pe</c>. When Claudia gave no <c>tolerance</c> the two
    /// blocks skip a burn too small to be worth the turn before it; a tolerance she gave is honoured exactly. Also the safe
    /// periapsis height (<c>tune_pe_safe_height</c> before it). Pure; kOS still reads the orbit and flies the burn.
    /// </summary>
    public static class BurnWorth
    {
        /// <summary><c>circularize</c> skips a burn under this many m/s ...</summary>
        public const double CircularizeMaxDv = 5;

        /// <summary>... on an orbit under this eccentricity.</summary>
        public const double CircularizeMaxEcc = 0.03;

        /// <summary>A Course Correction (#817) under this many m/s is small: flown with the throttle capped, and stopped within <see cref="DoneShare"/> of its node.</summary>
        public const double SmallCorrectionDv = 2;

        /// <summary>A small correction's throttle is capped so one physics tick (<see cref="PhysicsTick"/>, s) at the cap is at most this share of it.</summary>
        public const double TickShare = 0.05;

        /// <summary>KSP's physics tick, s.</summary>
        public const double PhysicsTick = 0.02;

        /// <summary>
        /// A small correction's done_dv, m/s: exec_node stops the burn once what is left is under this share of it, from <see cref="LeastDone"/> to <see cref="MostDone"/>, so it ends within
        /// 0.05 m/s of its node and within a tenth of the burn. Its max_residual stays the block's (exec_node's 0.5 m/s): a residual of a few tenths fails no return.
        /// </summary>
        public const double DoneShare = 0.1;

        /// <summary>The most a small correction's done_dv is, m/s.</summary>
        public const double MostDone = 0.05;

        /// <summary>The least a small correction's done_dv is, m/s.</summary>
        public const double LeastDone = 0.005;

        /// <summary>exec_node's own done_dv, m/s, for a correction that isn't small.</summary>
        public const double ExecNodeDone = 0.1;

        /// <summary>A correction under this many m/s isn't flown once its landing is within <see cref="ThereKm"/> km of the target, or it has no landing: its own error is that size.</summary>
        public const double CorrectionSkipDv = 0.1;

        /// <summary>km from the target within which a return's landing counts as there.</summary>
        public const double ThereKm = 2;

        /// <summary>A correction under this many m/s is none (#805: an aimed correction that can't be solved flies none).</summary>
        private const double NoBurnDv = 0.001;

        /// <summary><c>tune_pe</c>'s default tolerance is at least this, m, and ...</summary>
        public const double TunePeMinTolerance = 1000;

        /// <summary>... this share of the periapsis wanted.</summary>
        public const double TunePeShare = 0.05;

        private static readonly Dictionary<string, double> Peaks = new Dictionary<string, double>
        {
            { "Moho", 6817 }, { "Gilly", 6401 }, { "Mun", 7061 }, { "Minmus", 5725 }, { "Ike", 12738 }, { "Dres", 5670 },
            { "Vall", 7985 }, { "Tylo", 11290 }, { "Bop", 21757 }, { "Pol", 5585 }, { "Eeloo", 3874 },
        };

        /// <summary>
        /// The lowest periapsis (m) that is safe at the body named <paramref name="name"/>: above its atmosphere
        /// (<paramref name="atmHeight"/> m, <paramref name="hasAtm"/>), or 2 km above its highest peak for the stock airless
        /// bodies; -1 for a body whose peaks aren't known.
        /// </summary>
        public static double SafeHeight(string name, bool hasAtm, double atmHeight)
        {
            if (hasAtm) return atmHeight;
            double peak;
            return name != null && Peaks.TryGetValue(name, out peak) ? peak + 2000 : -1;
        }

        /// <summary>Whether a periapsis of <paramref name="pe"/> m is safe above <paramref name="safe"/> m (<see cref="SafeHeight"/>; -1, unknown, is never safe).</summary>
        public static bool Safe(double pe, double safe)
        {
            return safe >= 0 && pe > safe;
        }

        /// <summary>The tolerance (m) <c>tune_pe</c> uses when it was given none: the larger of 1 km and 5% of <paramref name="value"/>.</summary>
        public static double TunePeTolerance(double value)
        {
            return Math.Max(TunePeMinTolerance, TunePeShare * value);
        }

        /// <summary>
        /// What a miss on <c>tune_pe</c>'s tolerance adds to the reason (#610): "" or, when the craft is on an encounter
        /// (<paramref name="toMoon"/>) whose periapsis <paramref name="pe"/> m at the body named <paramref name="name"/> is
        /// <see cref="Safe"/> above <paramref name="safe"/> m (<see cref="SafeHeight"/>), that a capture can still follow (20ac: a
        /// script's <c>if res:success</c> skipped one at 35.3 km against 30 +- 5).
        /// </summary>
        public static string SafeHint(bool toMoon, double pe, string name, double safe)
        {
            if (!toMoon || !Safe(pe, safe)) return "";
            return "; encounter safe: pe " + Math.Round(pe / 1000, 1).ToString("0.#", CultureInfo.InvariantCulture) + " km at " + name + "; a capture can follow";
        }

        /// <summary>
        /// The answer to a block's ask. <c>block</c> <c>"hint"</c> gives <c>note</c> as <see cref="SafeHint"/> (<c>to_moon</c>,
        /// <c>pe</c> and <c>body</c>, <c>has_atm</c>, <c>atm_height</c>). Otherwise <c>given</c> says Claudia set the tolerance, and the answer is <c>tolerance</c> (m for
        /// <c>tune_pe</c>; 0 for <c>circularize</c>, which keeps its own), <c>skip</c> and <c>note</c> (why, when it skips).
        /// <c>circularize</c> (<c>dv</c> m/s of the burn, <c>ecc</c>, <c>pe</c> m) skips a burn under 5 m/s on an orbit under 0.03 with a
        /// safe periapsis. <c>tune_pe</c> (<c>value</c> and <c>pe</c> m, <c>patch</c>) skips on patch "current" with a safe periapsis within
        /// the wider tolerance of value; on "next" (a small burn far out moves an encounter a lot) and with an unsafe periapsis the
        /// tolerance stays 1 km and it never skips.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var safeHeight = SafeHeight(Text(input, "body"), Bool(input, "has_atm"), Number(input, "atm_height", 0));
            var answer = new Dictionary<string, object> { { "safe_height", safeHeight }, { "tolerance", 0.0 }, { "skip", false }, { "note", "" } };
            var block = Text(input, "block");
            if (block == "hint")
            {
                answer["note"] = SafeHint(Bool(input, "to_moon"), Number(input, "pe", double.NegativeInfinity), Text(input, "body"), safeHeight);
                return answer;
            }
            if (block == "correction") return Correction(input, answer);
            if (Bool(input, "given")) return answer;
            var pe = Number(input, "pe", double.NegativeInfinity);
            var safe = Safe(pe, safeHeight);
            if (block == "circularize")
            {
                var dv = Math.Abs(Number(input, "dv", double.PositiveInfinity));
                if (!(dv < CircularizeMaxDv) || !(Number(input, "ecc", double.PositiveInfinity) < CircularizeMaxEcc) || !safe) return answer;
                answer["skip"] = true;
                answer["note"] = "within its default tolerance: a " + Format(dv) + " m/s burn isn't worth the turn";
                return answer;
            }
            var value = Number(input, "value", 0);
            answer["tolerance"] = TunePeMinTolerance;
            if (Text(input, "patch") != "current" || !safe) return answer;
            var tolerance = TunePeTolerance(value);
            answer["tolerance"] = tolerance;
            var off = Math.Abs(pe - value);
            if (!(off <= tolerance)) return answer;
            answer["skip"] = true;
            answer["note"] = "within its default tolerance: the periapsis is " + Format(off) + " m from " + Format(value) + " m, inside "
                + Format(tolerance) + " m, so a burn isn't worth the turn";
            return answer;
        }

        /// <summary>
        /// A Course Correction's flight (#817): <c>fix</c> is CORRECTIONBURN's answer, <c>accel</c> the craft's full thrust over its mass (m/s²), <c>max_residual</c> the block's (m/s;
        /// -1 for exec_node's own). Skips one with an error (its <c>note</c>), none, or one under <see cref="CorrectionSkipDv"/> whose landing is already there; one under
        /// <see cref="SmallCorrectionDv"/> gets <c>max_throttle</c> (0 to 1) and a <c>done_dv</c> by <see cref="DoneShare"/>; <c>max_residual</c> is the block's. Kerboscript caps the throttle.
        /// </summary>
        private static IDictionary<string, object> Correction(IDictionary<string, object> input, Dictionary<string, object> answer)
        {
            object found;
            var fix = input.TryGetValue("fix", out found) ? found as IDictionary<string, object> : null;
            var given = Number(input, "max_residual", -1);
            answer["max_throttle"] = 1.0;
            answer["max_residual"] = given;
            answer["done_dv"] = ExecNodeDone;
            var error = fix == null ? "no correction" : Text(fix, "error");
            var dv = Number(fix, "dv", 0);
            var km = Number(fix, "error_km", -1);
            if (error != "" || dv < NoBurnDv || (dv < CorrectionSkipDv && km <= ThereKm))
            {
                answer["skip"] = true;
                answer["note"] = error != "" ? error : "a " + Hundredths(dv) + " m/s correction isn't worth the turn" + (km >= 0 ? ", " + Hundredths(km) + " km from the target" : "");
                return answer;
            }
            if (!(dv < SmallCorrectionDv)) return answer;
            var accel = Number(input, "accel", 0);
            if (accel > 0) answer["max_throttle"] = Math.Min(1, TickShare * dv / (accel * PhysicsTick));
            answer["done_dv"] = Math.Max(LeastDone, Math.Min(MostDone, DoneShare * dv));
            return answer;
        }

        private static string Hundredths(double x)
        {
            return Math.Round(x, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Format(double x)
        {
            return Math.Round(x, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Text(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static bool Bool(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null || value is string) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
