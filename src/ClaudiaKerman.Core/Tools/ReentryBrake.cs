// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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

        /// <summary>Thrust goes to zero while the craft turns faster than this, deg/s (#954): the wheels give only about 7 deg/s², so a spin has to be prevented, not corrected, and thrust mustn't pulse each time a spinning craft swings past its aim. To tune from the log.</summary>
        public const double MaxSpinDeg = 20;

        /// <summary>A packed chute counts as failed this long after it was first called, s (#954): measured from the call, not from a height, as a block starting low is already under the last-call heights.</summary>
        public const double PackedFailSeconds = 1;

        /// <summary>Why the thrust is zero (<c>zero_why</c>, for the log): a chute called and not yet fully open and settled.</summary>
        public const string WhyChuteOpening = "chute opening";

        /// <summary>Why the thrust is zero: over <see cref="MaxSpinDeg"/>.</summary>
        public const string WhySpinning = "spinning";

        /// <summary>Why the thrust is zero: over <see cref="MaxOffAimDeg"/>.</summary>
        public const string WhyOffAim = "off aim";

        /// <summary>A full-state chute reads this, the adapter's name for stock's fully deployed.</summary>
        public const string Deployed = "deployed";

        /// <summary>A chute the adapter reads as cut: not waited for (#954).</summary>
        public const string Cut = "cut";

        /// <summary>A chute still packed, its canopy not out: not asked to open yet.</summary>
        public const string Stowed = "stowed";

        /// <summary>A chute still packed, its canopy not out: asked to open, and waiting for the air.</summary>
        public const string Armed = "armed";

        /// <summary>A chute reading deployed is still filling: the wait after which braking goes on the radar rule whatever the speed is doing, s (a stock deploymentSpeed of 0.12 is 8.3 s to fill).</summary>
        public const double SettleSeconds = 8;

        /// <summary>Sooner than <see cref="SettleSeconds"/>, the speed counts as settled after this long fully open, s: at first the slowing reads low only because the canopy has barely begun to fill.</summary>
        public const double SettleMinSeconds = 4;

        /// <summary>The speed has settled once it changes by no more than this, m/s².</summary>
        public const double SettledDecel = 1;

        /// <summary>The slowing is worked out over at least this long, s, so frame noise doesn't read as slowing.</summary>
        public const double DecelSampleSeconds = 0.25;

        /// <summary>Seconds of the fall added to the last-resort height, for the 0.25 s between the samples it is read from.</summary>
        public const double SampleLagSeconds = 0.5;

        /// <summary>
        /// Metres of fall a braking burn takes to bring the downward speed <paramref name="vDown"/> (m/s) to <paramref name="soft"/>, with <paramref name="net"/> m/s²
        /// of thrust over gravity (it may be negative) and drag k v² (k in 1/m) for each <paramref name="step"/> m of height below the craft, <paramref name="ks"/>
        /// listing k from the top step down. -1 if it doesn't get there within the steps, so not before the ground; with <paramref name="pastGround"/>, it goes on at the last k
        /// instead, giving the distance it would take however far that is (-1 only if it would never stop). Each step is solved exactly for its k.
        /// </summary>
        public static double StopDistance(double vDown, double net, IList<double> ks, double step, double soft, bool pastGround = false)
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
            if (!pastGround) return -1;
            var last = ks.Count > 0 ? ks[ks.Count - 1] : 0;
            if (last <= 1e-9) return net > 0 ? x + (v2 - goal) / (2 * net) : -1;
            var drag = net / last;
            return goal + drag > 0 && v2 + drag > 0 ? x + Math.Log((v2 + drag) / (goal + drag)) / (2 * last) : -1;
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

        /// <summary>The last-resort height (m): where full thrust, with no margin, can only just stop the fall, plus <see cref="SampleLagSeconds"/> of the fall. It may be above the craft: a fall already past it. -1 if full thrust would never stop it.</summary>
        public static double LastResortHeight(double vDown, double aUp, double g, IList<double> ks, double radar, double soft)
        {
            var s = StopDistance(vDown, aUp - g, ks, radar / Math.Max(1, ks.Count), soft, true);
            return s < 0 ? -1 : s + SampleLagSeconds * Math.Max(0, vDown);
        }

        /// <summary>True once chutes fully open for <paramref name="openSeconds"/> have done their slowing: the speed has stopped changing (<paramref name="decel"/>, m/s²), or they have had the time to fill.</summary>
        public static bool ChutesSettled(double openSeconds, double decel)
        {
            return openSeconds >= SettleSeconds || (openSeconds >= SettleMinSeconds && Math.Abs(decel) <= SettledDecel);
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
        /// True if chutes not yet fully open must give way: at or past the last-resort height, or for a fall full thrust would never stop, once the air has done its work, on the shield's own
        /// speed and height, as <see cref="DueByRadar"/> waits: high on a shallow path the fuel would go at orbital speed.
        /// </summary>
        public static bool LastResortDue(double radar, double lastH, double speed, double shieldSpeed, double shieldAgl)
        {
            return (lastH < 0 || radar <= lastH) && (speed <= shieldSpeed || radar <= shieldAgl);
        }

        /// <summary>
        /// Why the thrust must be zero now, "" if it needn't: a chute opening, the craft spinning (over <see cref="MaxSpinDeg"/>) or off the aim (over <see cref="MaxOffAimDeg"/>), the first that holds.
        /// A rate that couldn't be read reaches here as -1 and doesn't hold the engine: no reading must not stop the landing, and the off-aim guard still catches a tipped craft.
        /// </summary>
        public static string ZeroThrustWhy(bool chuteOpening, double rateDeg, double offDeg)
        {
            if (chuteOpening) return WhyChuteOpening;
            if (rateDeg > MaxSpinDeg) return WhySpinning;
            return offDeg > MaxOffAimDeg ? WhyOffAim : "";
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
            var vDown = Number(ask, "v_down", 0);
            var aUp = Number(ask, "a_up", 0);
            var g = Number(ask, "g", 0);
            var radar = Number(ask, "radar", 0);
            var soft = Number(ask, "soft", 0);
            return Heights(ReentryBrake.BrakeHeight(vDown, aUp, g, ks, radar, soft, Number(ask, "end", 0)), ReentryBrake.LastResortHeight(vDown, aUp, g, ks, radar, soft));
        }

        private static IDictionary<string, object> Heights(double brakeH, double lastH)
        {
            return new Dictionary<string, object> { { "error", "" }, { "brake_h", brakeH }, { "last_h", lastH } };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:BRAKETICK</c>, for <c>reentry</c> (#954), every tick of a powered finish. <c>chutes</c>, the addon's reading of the Chute Group aboard (each <c>title</c>,
        /// <c>state</c>: stowed, armed, semi, deployed or cut, and <c>drogue</c>), decides the rule. A set (drogues or mains) is called once the block has called it (<c>drogues_called</c>,
        /// <c>mains_called</c>) or one of it reads past stowed; its time is <c>drogue_call_ut</c> or <c>main_call_ut</c>, handed back as <c>open_ut</c> is. A called chute is opening until all
        /// called chutes read fully open and settled (<see cref="ReentryBrake.ChutesSettled"/>, from <c>open_ut</c>, <c>now</c> and the slowing, sampled here from <c>speed</c> so it stays live during a burn, through <c>sample_ut</c>, <c>sample_speed</c> and <c>decel</c>, handed back the same way): no burn starts and the thrust is zero.
        /// A cut chute, one of <c>uids</c> no longer aboard, or one still packed <see cref="ReentryBrake.PackedFailSeconds"/> after its call has failed and holds nothing back.
        /// None to wait for is <c>why</c> "radar" (<see cref="ReentryBrake.DueByRadar"/>, as before #954). All fully open and settled is "chutes open", the same rule. With nothing opening and a chute
        /// failed it is "last resort" (<see cref="ReentryBrake.LastResortDue"/>); with only chutes not called yet, it waits for their call. Takes also <c>radar</c>, <c>speed</c>, <c>brake_h</c>, <c>last_h</c>,
        /// <c>shield_speed</c>, <c>shield_agl</c>, and <c>throttle</c>, <c>off_deg</c> (the angle off the steering aim) and <c>rate_deg</c> (deg/s). Gives <c>error</c> "", <c>start</c>, <c>why</c>,
        /// <c>open</c> (all fully open with chutes to wait for), <c>open_ut</c> (when the called chutes all first read so, -1 if they don't), <c>drogue_call_ut</c>, <c>main_call_ut</c>,
        /// <c>chutes</c> (each title and state, for the note), <c>throttle</c> (0 for any <see cref="ReentryBrake.ZeroThrustWhy"/>), <c>zero_why</c> and <c>rate_deg</c> (-1 if unread, for the log).
        /// With no reading (the addon's answer to a failed call) nothing starts and the throttle is 0. Pure.
        /// </summary>
        public static IDictionary<string, object> BrakeTick(IDictionary<string, object> ask)
        {
            ask = ask ?? new Dictionary<string, object>();
            var radar = Number(ask, "radar", double.PositiveInfinity);
            var now = Number(ask, "now", 0);
            var read = new List<string>();
            var aboard = new List<KeyValuePair<string, bool>>();
            object chutes;
            if (ask.TryGetValue("chutes", out chutes) && chutes is IEnumerable)
            {
                foreach (var entry in (IEnumerable)chutes)
                {
                    var chute = entry as IDictionary<string, object>;
                    if (chute == null) continue;
                    var state = chute.ContainsKey("state") ? chute["state"] as string : null;
                    read.Add(chute.ContainsKey("title") ? chute["title"] + " " + state : "" + state);
                    aboard.Add(new KeyValuePair<string, bool>(state, chute.ContainsKey("drogue") && chute["drogue"] is bool && (bool)chute["drogue"]));
                }
            }
            var drogueCallUt = CallTime(ask, "drogues_called", "drogue_call_ut", aboard, true, now);
            var mainCallUt = CallTime(ask, "mains_called", "main_call_ut", aboard, false, now);
            var waitedFor = 0;
            var deployed = 0;
            var healthy = 0;
            var filling = 0;
            var failed = false;
            foreach (var chute in aboard)
            {
                if (chute.Key == ReentryBrake.Cut)
                {
                    failed = true;
                    continue;
                }
                waitedFor++;
                if (chute.Key == ReentryBrake.Deployed) deployed++;
                var callUt = chute.Value ? drogueCallUt : mainCallUt;
                if (callUt < 0) continue;
                if ((chute.Key == ReentryBrake.Stowed || chute.Key == ReentryBrake.Armed) && now - callUt >= ReentryBrake.PackedFailSeconds)
                {
                    failed = true;
                    continue;
                }
                healthy++;
                if (chute.Key != ReentryBrake.Deployed) filling++;
            }
            object uids;
            if (ask.TryGetValue("uids", out uids) && uids is IEnumerable && !(uids is string) && ((IEnumerable)uids).Cast<object>().Count() > aboard.Count) failed = true;
            var open = waitedFor > 0 && deployed == waitedFor;
            var heldUt = Number(ask, "open_ut", -1);
            var openUt = healthy == 0 || filling > 0 ? -1 : heldUt >= 0 ? heldUt : now;
            var speed = Number(ask, "speed", double.PositiveInfinity);
            var sampleUt = Number(ask, "sample_ut", -1);
            var sampleSpeed = Number(ask, "sample_speed", speed);
            var decel = Number(ask, "decel", 0);
            if (sampleUt < 0)
            {
                sampleUt = now;
                sampleSpeed = speed;
            }
            else if (now - sampleUt >= ReentryBrake.DecelSampleSeconds)
            {
                decel = (sampleSpeed - speed) / (now - sampleUt);
                sampleUt = now;
                sampleSpeed = speed;
            }
            var settled = openUt >= 0 && ReentryBrake.ChutesSettled(now - openUt, decel);
            var opening = healthy > 0 && !settled;
            var shieldSpeed = Number(ask, "shield_speed", 0);
            var shieldAgl = Number(ask, "shield_agl", 0);
            var byRadar = waitedFor == 0 || (open && settled);
            var start = byRadar
                ? ReentryBrake.DueByRadar(radar, Number(ask, "brake_h", -1), speed, shieldSpeed, shieldAgl)
                : !opening && failed && ReentryBrake.LastResortDue(radar, Number(ask, "last_h", -1), speed, shieldSpeed, shieldAgl);
            var rateRead = Number(ask, "rate_deg", -1);
            var rate = double.IsNaN(rateRead) || double.IsInfinity(rateRead) ? -1 : rateRead;
            var zeroWhy = ReentryBrake.ZeroThrustWhy(opening, rate, Number(ask, "off_deg", 0));
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "start", start },
                { "why", waitedFor == 0 ? "radar" : byRadar ? "chutes open" : "last resort" },
                { "open", open },
                { "open_ut", openUt },
                { "drogue_call_ut", drogueCallUt },
                { "main_call_ut", mainCallUt },
                { "sample_ut", sampleUt },
                { "sample_speed", sampleSpeed },
                { "decel", decel },
                { "chutes", string.Join(", ", read.ToArray()) },
                { "throttle", zeroWhy == "" ? Number(ask, "throttle", 0) : 0.0 },
                { "zero_why", zeroWhy },
                { "rate_deg", rate },
            };
        }

        /// <summary>When the set was first called, UT: the time held in <paramref name="heldKey"/>, or now on the first tick it is called; -1 while it isn't, so no flight inherits it from the last.</summary>
        private static double CallTime(IDictionary<string, object> ask, string calledKey, string heldKey, IEnumerable<KeyValuePair<string, bool>> aboard, bool drogues, double now)
        {
            var flagged = ask.ContainsKey(calledKey) && ask[calledKey] is bool && (bool)ask[calledKey];
            if (!flagged && !aboard.Any(c => c.Value == drogues && c.Key != ReentryBrake.Stowed && c.Key != ReentryBrake.Cut)) return -1;
            var held = Number(ask, heldKey, -1);
            return held >= 0 ? held : now;
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
