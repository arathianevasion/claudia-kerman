// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// <c>reentry</c>'s wait on the ground (#968, ADR 0014), through the block-internal <c>ADDONS:CLAUDIA:GROUNDSETTLE</c> (<see cref="Call"/>): the block reports only once the craft has stayed LANDED or SPLASHED
    /// for <see cref="SettleSeconds"/>, so a bounce doesn't read as FLYING. Pure; the block holds <c>since</c> between ticks.
    /// The block cuts its chutes at first touch, before this wait, and has released the controls: nothing in the wait lights or throttles an engine.
    /// </summary>
    public static class GroundSettle
    {
        /// <summary>How long (s) the craft must stay on the ground without a break.</summary>
        public const double SettleSeconds = 2;

        /// <summary>The longest (s) the wait lasts from first touch, so a craft sliding or bouncing for long still ends the block.</summary>
        public const double CapSeconds = 30;

        /// <summary>
        /// One tick. <c>on_ground</c>: LANDED or SPLASHED now; <c>now</c>: UT (s); <c>since</c>: the UT this stretch on the ground began, -1 if the craft is not on it; <c>first</c>: the UT of first touch.
        /// Gives <c>first</c> back as it came, and <c>since</c> (to pass back next tick; -1 once the craft is up again, so the two seconds start over) and <c>done</c>: two seconds on the ground in a row, or the cap passed.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var now = Number(input, "now", 0);
            var since = -1.0;
            object onGround = null;
            if (input != null && input.TryGetValue("on_ground", out onGround) && onGround is bool && (bool)onGround)
            {
                var before = Number(input, "since", -1);
                since = before < 0 ? now : before;
            }
            var done = (since >= 0 && now - since >= SettleSeconds) || now - Number(input, "first", now) >= CapSeconds;
            return new Dictionary<string, object> { { "since", since }, { "first", Number(input, "first", now) }, { "done", done } };
        }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
