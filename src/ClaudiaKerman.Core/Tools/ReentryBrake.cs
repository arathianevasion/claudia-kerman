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

        /// <summary>A full-state chute reads this, the adapter's name for stock's fully deployed.</summary>
        public const string Deployed = "deployed";

        /// <summary>A chute the adapter reads as cut: not waited for (#954).</summary>
        public const string Cut = "cut";

        /// <summary>A chute reading deployed is still filling: the wait after which braking goes on the radar rule whatever the speed is doing, s (a stock deploymentSpeed of 0.12 is 8.3 s to fill).</summary>
        public const double SettleSeconds = 8;

        /// <summary>Sooner than <see cref="SettleSeconds"/>, the speed counts as settled after this long fully open, s: at first the slowing reads low only because the canopy has barely begun to fill.</summary>
        public const double SettleMinSeconds = 2;

        /// <summary>The speed has settled once it changes by no more than this, m/s².</summary>
        public const double SettledDecel = 1;

        /// <summary>The share of a chute's deploy height at which it should be fully open: the last resort waits for it (#954, player's option b), as the canopy fills while the craft falls below that height.</summary>
        public const double OpenByFraction = 0.8;

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
        /// <c>state</c>: stowed, armed, semi, deployed or cut, <c>deploy_m</c> and <c>drogue</c>), decides the rule. A cut chute, or one of <c>uids</c> no longer aboard, isn't waited for.
        /// None to wait for is <c>why</c> "radar" (<see cref="ReentryBrake.DueByRadar"/>, as before #954). All fully open and settled (<see cref="ReentryBrake.ChutesSettled"/>, from
        /// <c>open_ut</c>, <c>now</c> and <c>decel</c>) is "chutes open", the same rule. Any other is "last resort" (<see cref="ReentryBrake.LastResortDue"/>), and that can't fire above
        /// <see cref="ReentryBrake.OpenByFraction"/> of the lowest <c>deploy_m</c> unless a chute is cut, missing, or still packed by its last-call height (<c>chute_last_agl</c>, or
        /// <c>drogue_last_agl</c> for a drogue): the player's option (b). Takes also <c>radar</c>, <c>speed</c>, <c>brake_h</c>, <c>last_h</c>, <c>shield_speed</c>, <c>shield_agl</c>,
        /// and <c>throttle</c>, <c>off_deg</c> (the angle off the steering aim). Gives <c>error</c> "", <c>start</c>, <c>why</c>, <c>open</c> (fully open with chutes to wait for),
        /// <c>open_ut</c> (when they all first read so, -1 if they don't: the caller hands it back next tick), <c>chutes</c> (each title and state, for the note) and <c>throttle</c>,
        /// guarded (<see cref="ReentryBrake.GuardThrottle"/>). With no reading (the addon's answer to a failed call) nothing starts and the throttle is 0. Pure.
        /// </summary>
        public static IDictionary<string, object> BrakeTick(IDictionary<string, object> ask)
        {
            ask = ask ?? new Dictionary<string, object>();
            var radar = Number(ask, "radar", double.PositiveInfinity);
            var chuteLast = Number(ask, "chute_last_agl", -1);
            var drogueLast = Number(ask, "drogue_last_agl", -1);
            var aboard = 0;
            var total = 0;
            var deployed = 0;
            var lost = false;
            var lowestDeploy = double.PositiveInfinity;
            var read = new List<string>();
            object chutes;
            if (ask.TryGetValue("chutes", out chutes) && chutes is IEnumerable)
            {
                foreach (var entry in (IEnumerable)chutes)
                {
                    var chute = entry as IDictionary<string, object>;
                    if (chute == null) continue;
                    aboard++;
                    var state = chute.ContainsKey("state") ? chute["state"] as string : null;
                    read.Add(chute.ContainsKey("title") ? chute["title"] + " " + state : "" + state);
                    if (state == ReentryBrake.Cut)
                    {
                        lost = true;
                        continue;
                    }
                    total++;
                    if (state == ReentryBrake.Deployed) deployed++;
                    var drogue = chute.ContainsKey("drogue") && chute["drogue"] is bool && (bool)chute["drogue"];
                    if ((state == "stowed" || state == "armed") && radar <= (drogue ? drogueLast : chuteLast)) lost = true;
                    var deployM = Number(chute, "deploy_m", 0);
                    if (deployM > 0) lowestDeploy = Math.Min(lowestDeploy, deployM);
                }
            }
            object uids;
            if (ask.TryGetValue("uids", out uids) && uids is IEnumerable && !(uids is string) && ((IEnumerable)uids).Cast<object>().Count() > aboard) lost = true;
            var open = total > 0 && deployed == total;
            var now = Number(ask, "now", 0);
            var heldUt = Number(ask, "open_ut", -1);
            var openUt = !open ? -1 : heldUt >= 0 ? heldUt : now;
            var settled = open && ReentryBrake.ChutesSettled(now - openUt, Number(ask, "decel", 0));
            var speed = Number(ask, "speed", double.PositiveInfinity);
            var shieldSpeed = Number(ask, "shield_speed", 0);
            var shieldAgl = Number(ask, "shield_agl", 0);
            var start = total == 0 || settled
                ? ReentryBrake.DueByRadar(radar, Number(ask, "brake_h", -1), speed, shieldSpeed, shieldAgl)
                : (lost || radar <= ReentryBrake.OpenByFraction * lowestDeploy) && ReentryBrake.LastResortDue(radar, Number(ask, "last_h", -1), speed, shieldSpeed, shieldAgl);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "start", start },
                { "why", total == 0 ? "radar" : settled ? "chutes open" : "last resort" },
                { "open", open },
                { "open_ut", openUt },
                { "chutes", string.Join(", ", read.ToArray()) },
                { "throttle", ReentryBrake.GuardThrottle(Number(ask, "throttle", 0), Number(ask, "off_deg", 0)) },
            };
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
