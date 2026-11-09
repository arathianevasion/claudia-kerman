// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// When a powered finish in <c>reentry</c> brakes (#628, #824, #954; ADR 0014): the heights braking is judged by, whether it may start this tick, and the
    /// throttle guard. Chutes first, engines last: with chutes in the Chute Group it waits for all of them to be fully open, unless the fall is at the height
    /// where full thrust can only just stop it. Pure; the block still does the flying.
    /// </summary>
    public static class ReentryBrake
    {
        /// <summary>Thrust goes to zero while the craft points more than this many degrees off the steering aim (#954): a tipped craft must not be driven by its engine.</summary>
        public const double MaxOffAimDeg = 30;

        /// <summary>A full-state chute reads this, the adapter's name for stock's fully deployed.</summary>
        public const string Deployed = "deployed";

        /// <summary>
        /// Metres of fall a braking burn takes to bring the downward speed <paramref name="vDown"/> (m/s) to <paramref name="soft"/>, with <paramref name="net"/> m/s²
        /// of thrust over gravity (it may be negative) and drag k v² (k in 1/m) for each <paramref name="step"/> m of height below the craft, <paramref name="ks"/>
        /// listing k from the top step down. -1 if it doesn't get there within the steps, so not before the ground. Each step is solved exactly for its k.
        /// </summary>
        public static double StopDistance(double vDown, double net, IList<double> ks, double step, double soft)
        {
            var v2 = vDown * vDown;
            var goal = soft * soft;
            if (v2 <= goal) return 0;
            var x = 0.0;
            foreach (var k in ks)
            {
                var next = v2 - 2 * net * step;
                if (k > 1e-9)
                {
                    var c = net / k;
                    next = (v2 + c) * Math.Exp(-2 * k * step) - c;
                    if (next <= goal && goal + c > 0) return x + Math.Log((v2 + c) / (goal + c)) / (2 * k);
                }
                else if (next <= goal)
                {
                    return x + (v2 - goal) / (2 * net);
                }
                v2 = next;
                x += step;
            }
            return -1;
        }

        /// <summary>
        /// The radar altitude (m) at which the braking must start: the stopping distance to <paramref name="soft"/> with a sixth of the thrust held back, against gravity
        /// and the drag in <paramref name="ks"/> over <paramref name="radar"/> m, plus <paramref name="end"/> and 1 s of the fall. A craft that only stops in time on all
        /// its thrust gets the figure for all of it; -1 if not even that stops it before the ground.
        /// </summary>
        public static double BrakeHeight(double vDown, double aUp, double g, IList<double> ks, double radar, double soft, double end)
        {
            var step = radar / Math.Max(1, ks.Count);
            var s = StopDistance(vDown, aUp / 1.2 - g, ks, step, soft);
            if (s < 0) s = StopDistance(vDown, aUp - g, ks, step, soft);
            return s < 0 ? -1 : s + end + Math.Max(0, vDown);
        }

        /// <summary>The last-resort height (m): where full thrust, with no margin, can only just stop the fall. -1 if not even that stops it before the ground.</summary>
        public static double LastResortHeight(double vDown, double aUp, double g, IList<double> ks, double radar, double soft)
        {
            return StopDistance(vDown, aUp - g, ks, radar / Math.Max(1, ks.Count), soft);
        }

        /// <summary>
        /// True if the braking is due by radar (the rule with no chutes to wait for): within <paramref name="brakeH"/>, or for a fall the engines can't stop, once the
        /// air has done its work, on the shield's own speed and height: high on a shallow path thrust barely points up, and the fuel would go at orbital speed.
        /// </summary>
        public static bool DueByRadar(double radar, double brakeH, double speed, double shieldSpeed, double shieldAgl)
        {
            return radar <= brakeH || (brakeH < 0 && (speed <= shieldSpeed || radar <= shieldAgl));
        }

        /// <summary>
        /// True if chutes not yet fully open must give way: at the last-resort height, or for a fall the engines can't stop, by the height the mains open by at the latest
        /// (<paramref name="chuteLastAgl"/>). Sooner, the chutes are still the craft's best hope, and a burn under chutes opening is what tumbled flight m20261109-140705-ae3a.
        /// </summary>
        public static bool LastResortDue(double radar, double lastH, double chuteLastAgl)
        {
            return lastH >= 0 ? radar <= lastH : radar <= chuteLastAgl;
        }

        /// <summary>The throttle to fly: 0 while the craft is more than <see cref="MaxOffAimDeg"/> off the aim, else <paramref name="throttle"/>.</summary>
        public static double GuardThrottle(double throttle, double offDeg)
        {
            return offDeg > MaxOffAimDeg ? 0 : throttle;
        }
    }

    public static partial class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:BRAKEHEIGHTS</c>, for <c>reentry</c> (#954), sampled every 0.25 s of the fall: <c>brake_h</c> (<see cref="ReentryBrake.BrakeHeight"/>) and <c>last_h</c>
        /// (<see cref="ReentryBrake.LastResortHeight"/>), m, from <c>v_down</c>, <c>a_up</c>, <c>g</c>, <c>ks</c>, <c>radar</c>, <c>soft</c> and <c>end</c>. Gives <c>error</c> "" and -1
        /// for a height the engines can't reach; with no reading, both -1, which starts no burn by itself. Pure.
        /// </summary>
        public static IDictionary<string, object> BrakeHeights(IDictionary<string, object> ask)
        {
            if (ask == null) return Heights(-1, -1);
            var ks = BrakeNumbers(ask, "ks");
            var vDown = BrakeNumber(ask, "v_down", 0);
            var aUp = BrakeNumber(ask, "a_up", 0);
            var g = BrakeNumber(ask, "g", 0);
            var radar = BrakeNumber(ask, "radar", 0);
            var soft = BrakeNumber(ask, "soft", 0);
            return Heights(ReentryBrake.BrakeHeight(vDown, aUp, g, ks, radar, soft, BrakeNumber(ask, "end", 0)), ReentryBrake.LastResortHeight(vDown, aUp, g, ks, radar, soft));
        }

        private static IDictionary<string, object> Heights(double brakeH, double lastH)
        {
            return new Dictionary<string, object> { { "error", "" }, { "brake_h", brakeH }, { "last_h", lastH } };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BRAKETICK</c>, for <c>reentry</c> (#954), every tick of a powered finish. <c>chutes</c>, the addon's reading of the Chute Group aboard (each <c>title</c> and
        /// <c>state</c>: stowed, armed, semi, deployed or cut), decides the rule: none aboard is <c>why</c> "radar" (<see cref="ReentryBrake.DueByRadar"/>, as before #954); all deployed
        /// is "chutes open", the same rule; any other is "last resort" (<see cref="ReentryBrake.LastResortDue"/>). Takes <c>radar</c>, <c>speed</c>, <c>brake_h</c>, <c>last_h</c>,
        /// <c>shield_speed</c>, <c>shield_agl</c>, <c>chute_last_agl</c>, and <c>throttle</c>, <c>off_deg</c> (the angle off the steering aim). Gives <c>error</c> "", <c>start</c>, <c>why</c>,
        /// <c>open</c> (fully open with chutes in the group), <c>chutes</c> (each title and state, for the note) and <c>throttle</c>, guarded (<see cref="ReentryBrake.GuardThrottle"/>).
        /// With no reading (the addon's answer to a failed call) nothing starts and the throttle is 0. Pure.
        /// </summary>
        public static IDictionary<string, object> BrakeTick(IDictionary<string, object> ask)
        {
            ask = ask ?? new Dictionary<string, object>();
            var total = 0;
            var deployed = 0;
            var read = new List<string>();
            object chutes;
            if (ask.TryGetValue("chutes", out chutes) && chutes is IEnumerable)
            {
                foreach (var entry in (IEnumerable)chutes)
                {
                    var chute = entry as IDictionary<string, object>;
                    if (chute == null) continue;
                    total++;
                    var state = chute.ContainsKey("state") ? chute["state"] as string : null;
                    if (state == ReentryBrake.Deployed) deployed++;
                    read.Add(chute.ContainsKey("title") ? chute["title"] + " " + state : "" + state);
                }
            }
            var open = total > 0 && deployed == total;
            var radar = BrakeNumber(ask, "radar", double.PositiveInfinity);
            var byRadar = ReentryBrake.DueByRadar(radar, BrakeNumber(ask, "brake_h", -1), BrakeNumber(ask, "speed", double.PositiveInfinity), BrakeNumber(ask, "shield_speed", 0), BrakeNumber(ask, "shield_agl", 0));
            var start = total == 0 || open ? byRadar : ReentryBrake.LastResortDue(radar, BrakeNumber(ask, "last_h", -1), BrakeNumber(ask, "chute_last_agl", 0));
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "start", start },
                { "why", total == 0 ? "radar" : open ? "chutes open" : "last resort" },
                { "open", open },
                { "chutes", string.Join(", ", read.ToArray()) },
                { "throttle", ReentryBrake.GuardThrottle(BrakeNumber(ask, "throttle", 0), BrakeNumber(ask, "off_deg", 0)) },
            };
        }

        private static double BrakeNumber(IDictionary<string, object> ask, string key, double fallback)
        {
            object value;
            if (!ask.TryGetValue(key, out value) || value == null || value is string || value is bool || !(value is IConvertible)) return fallback;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static IList<double> BrakeNumbers(IDictionary<string, object> ask, string key)
        {
            var numbers = new List<double>();
            object value;
            if (ask.TryGetValue(key, out value) && value is IEnumerable && !(value is string))
                foreach (var item in (IEnumerable)value) numbers.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
            return numbers;
        }
    }
}
