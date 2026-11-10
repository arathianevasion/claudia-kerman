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
    /// throttle guard. Chutes first, engines last: with chutes in the Chute Group there is no thrust from a chute's call until the called chutes are fully open and
    /// settled. Only a failed chute (cut, gone, or still packed a second after its call) can bring the last resort, the height where full thrust can only just stop the fall, and then only with nothing opening. Pure; the block still does the flying.
    /// </summary>
    public static class ReentryBrake
    {
        /// <summary>Thrust goes to zero while the craft points more than this many degrees off the steering aim (#954): a tipped craft must not be driven by its engine.</summary>
        public const double MaxOffAimDeg = 30;

        /// <summary>Thrust goes to zero while the craft tips faster than this, deg/s (#954): the wheels give only about 7 deg/s², so a spin has to be prevented, not corrected, and thrust mustn't pulse each time a spinning craft swings past its aim. Roll about the thrust axis isn't counted: it doesn't move the thrust off the aim. To tune from the log.</summary>
        public const double MaxSpinDeg = 20;

        /// <summary>A packed chute counts as failed this long after it was first called, s (#954): measured from the call, not from a height, as a block starting low is already under the last-call heights.</summary>
        public const double PackedFailSeconds = 1;

        /// <summary>Why the thrust is zero (<c>zero_why</c>, for the log): a chute called and not yet fully open and settled.</summary>
        public const string WhyChuteOpening = "chute opening";

        /// <summary>Why the thrust is zero: over <see cref="MaxSpinDeg"/>.</summary>
        public const string WhySpinning = "spinning";

        /// <summary>Why the thrust is zero: over <see cref="MaxOffAimDeg"/>.</summary>
        public const string WhyOffAim = "off aim";

        /// <summary>Why the thrust is zero: the addon can't read the chute states, and they aren't cut yet.</summary>
        public const string WhyUnread = "chutes unreadable";

        /// <summary>The chute states have to stay unreadable this long, s, before the chutes are cut (#954): one bad tick mustn't cut good chutes.</summary>
        public const double UnreadSeconds = 1;

        /// <summary>The reason a burn began when a failed chute brought it (<c>why</c>, handed back by the block as <c>brake_why</c>).</summary>
        public const string WhyLastResort = "last resort";

        /// <summary>The seconds between the burn's trace lines (#954).</summary>
        public const double TraceSeconds = 1;

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

        /// <summary>Why the thrust is zero (#969): braking under chutes all fully open and settled, the engine waits for the final height, where they are cut.</summary>
        public const string WhyFinalWait = "final burn wait";

        /// <summary>Why the thrust is zero (#969): at the final height but over <see cref="MaxCutTiltDeg"/> off upright or tipping over <see cref="MaxSpinDeg"/>, so the chutes are held and the engine waits.</summary>
        public const string WhyCutHeld = "tipped, cut held";

        /// <summary>The chutes are cut for the final burn only within this many degrees of upright (#969): cut tipped, the burn would start sideways.</summary>
        public const double MaxCutTiltDeg = 30;

        /// <summary><c>cut_why</c> when the chutes are cut for the final burn (#969); "unreadable" when their states can't be read.</summary>
        public const string CutForFinalBurn = "final burn";

        /// <summary><see cref="FinalStep"/>: cut the chutes and burn.</summary>
        public const string StepCut = "cut";

        /// <summary><see cref="FinalStep"/>: burn with the chutes on, on the guards alone.</summary>
        public const string StepBurn = "burn";

        /// <summary>
        /// The most seconds the final height leaves for the craft to stop its turn (#969): the 41 t Duna stack's wheels (about 7 deg/s²) stopping the worst turn the cut allows
        /// (20 deg/s at 30° off, turning away) and coming back inside 30° take about 5.7 s. At a settled canopy's speed that is under a hundred metres, never kilometres.
        /// A craft that would take longer has the cut held (<see cref="WhyCutHeld"/>) until its swing slows, or burns with the chutes on under the last height.
        /// </summary>
        public const double MaxTurnStopSeconds = 6;

        private static readonly double[] NoDrag = { 0 };

        /// <summary>The final brake height (m, #969): <see cref="BrakeHeight"/> with no drag at all, as the chutes will be cut, plus the fall in <paramref name="turnS"/> (<see cref="TurnStopSeconds"/>, no more than <see cref="MaxTurnStopSeconds"/>). -1 (burn with the chutes kept) if the thrust can't hold the craft up against <paramref name="g"/>, whatever the speed, or if not even all of it stops the fall before the ground.</summary>
        public static double FinalHeight(double vDown, double aUp, double g, double radar, double soft, double end, double turnS = 0)
        {
            var h = aUp <= g ? -1 : BrakeHeight(vDown, aUp, g, NoDrag, radar, soft, end);
            return h < 0 ? h : h + Math.Min(Math.Max(0, turnS), MaxTurnStopSeconds) * Math.Max(0, vDown);
        }

        /// <summary>
        /// Seconds for a craft tipping at <paramref name="rateDeg"/> deg/s, <paramref name="upDeg"/>° off upright, to stop its turn on <paramref name="alphaDeg"/> deg/s² (#969): the rate over the
        /// acceleration, and, if the turn (taken as away from upright) would carry it past <see cref="MaxOffAimDeg"/>, where the guard zeroes the thrust, the time to come back inside it from rest.
        /// 0 with no turn (or an unread rate, -1, as the spin rule); infinite with nothing to stop it.
        /// </summary>
        public static double TurnStopSeconds(double rateDeg, double upDeg, double alphaDeg)
        {
            if (!(rateDeg > 0)) return 0;
            if (!(alphaDeg > 0)) return double.PositiveInfinity;
            var past = upDeg + rateDeg * rateDeg / (2 * alphaDeg) - MaxOffAimDeg;
            return rateDeg / alphaDeg + (past > 0 ? Math.Sqrt(2 * past / alphaDeg) : 0);
        }

        /// <summary>The height (m, #969) under which full thrust with no drag can't stop the fall: <see cref="LastResortHeight"/> with none. -1 if full thrust never would, or can't hold the craft up against <paramref name="g"/>.</summary>
        public static double FinalLastHeight(double vDown, double aUp, double g, double radar, double soft)
        {
            return aUp <= g ? -1 : LastResortHeight(vDown, aUp, g, NoDrag, radar, soft);
        }

        /// <summary>
        /// What braking under chutes all fully open and settled does at <paramref name="radar"/> m (#969): <see cref="WhyFinalWait"/> above <paramref name="finalH"/>;
        /// at or under it, <see cref="StepCut"/> within <see cref="MaxCutTiltDeg"/> of upright (<paramref name="upDeg"/>) and tipping no faster than <see cref="MaxSpinDeg"/> (<paramref name="rateDeg"/>, -1 unread: no hold),
        /// and stopping its turn within <see cref="MaxTurnStopSeconds"/> (<paramref name="turnS"/>), else <see cref="WhyCutHeld"/>: a craft swinging through upright would lose its canopy just as the spin rule zeroes the thrust; and
        /// <see cref="StepBurn"/>, the chutes kept, wherever the engine alone couldn't stop the fall (under <paramref name="lastH"/>, or either height -1): waiting longer or cutting would leave it falling.
        /// </summary>
        public static string FinalStep(double radar, double finalH, double lastH, double upDeg, double rateDeg, double turnS = 0)
        {
            if (finalH < 0) return StepBurn;
            if (radar > finalH) return WhyFinalWait;
            if (lastH < 0 || radar <= lastH) return StepBurn;
            return upDeg <= MaxCutTiltDeg && rateDeg <= MaxSpinDeg && turnS <= MaxTurnStopSeconds ? StepCut : WhyCutHeld;
        }

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
        /// Once settled, <c>settled</c> is handed back and holds until a chute reads semi, so a burn's own slowing can't unsettle them. A chute reading semi sets the settled time aside (<c>kept_ut</c>, <c>kept_settled</c>, handed back) and one then cut or gone, with no more chutes reading deployed, takes it back, so a chute that fails while opening doesn't restart the others' time. A chute called and still packed zeroes the thrust for its second, but only one really opening clears <c>open_ut</c>: a packed chute that fails doesn't restart the others' time. One more chute reading deployed than <c>open_count</c> held starts that time again, as a set can go from packed to deployed between two ticks.
        /// A cut chute, one of <c>uids</c> no longer aboard, or one still packed <see cref="ReentryBrake.PackedFailSeconds"/> after its call has failed and holds nothing back.
        /// None to wait for is <c>why</c> "radar" (<see cref="ReentryBrake.DueByRadar"/>, as before #954). All fully open and settled is "chutes open", the same rule. With nothing opening and a chute
        /// failed it is "last resort" (<see cref="ReentryBrake.LastResortDue"/>); with only chutes not called yet, it waits for their call. Takes also <c>radar</c>, <c>speed</c>, <c>brake_h</c>, <c>last_h</c>,
        /// <c>shield_speed</c>, <c>shield_agl</c>, and <c>throttle</c>, <c>off_deg</c> (the angle off the steering aim) and <c>rate_deg</c> (deg/s). Gives <c>error</c> "", <c>start</c>, <c>why</c>,
        /// <c>open</c> (all fully open with chutes to wait for), <c>open_ut</c> (when the called chutes all first read so, -1 if one is really opening or none is open), <c>open_count</c>, <c>settled</c>, <c>drogue_call_ut</c>, <c>main_call_ut</c>,
        /// <c>chutes</c> (each title and state, for the note), <c>throttle</c> (0 for any <see cref="ReentryBrake.ZeroThrustWhy"/>), <c>zero_why</c> and <c>rate_deg</c> (-1 if unread, for the log).
        /// With no reading (the addon's answer to a failed call) nothing starts and the throttle is 0.
        /// <para>A last resort under way (<c>brake_why</c> "last resort", the block's own record of the burn; its first tick is <c>last_resort_ut</c>, handed back): a set called at or after that time is passed over, so a later set can't interrupt the burn. A chute that counted as failed is not: if it opens after all, the thrust is zero until it settles. The spin and off-aim rules hold throughout.</para>
        /// <para>The chute states unreadable (<c>chutes_unread</c>, the addon's flag when its read fails) with a group to read (<c>uids</c>): nothing starts, the thrust is zero and what was held is handed back untouched, so one bad tick costs no settling time. After <see cref="ReentryBrake.UnreadSeconds"/> of failing without a break (<c>unread_ut</c>, handed back; a read from an earlier flight doesn't count, judged by the <c>sample_ut</c> that came in), <c>cut</c> asks the block to cut what is out (<c>cut_why</c> "unreadable"). The block then sends no <c>uids</c>, so <c>cut</c> is asked for once and the fall goes on the radar rule, as with no chutes.</para>
        /// <para>The final burn (#969): braking (a last resort or "chutes open") under chutes all reading fully open and settled (from <c>all_open_ut</c>, handed back, every chute aboard counted, a set passed over too), the engine waits:
        /// <c>final</c>, handed back, holds that until no chute is left, whatever a chute or the slowing then reads. It waits with zero thrust (<see cref="ReentryBrake.WhyFinalWait"/>) for the final height, <c>final_h</c>
        /// (<see cref="ReentryBrake.FinalHeight"/> from <c>v_down</c>, <c>a_up</c>, <c>g</c>, <c>soft</c>, <c>end</c>: no drag, as the chutes will be cut), and there, as <see cref="ReentryBrake.FinalStep"/> says from <c>up_deg</c>,
        /// gives <c>cut</c> (<c>cut_why</c> <see cref="ReentryBrake.CutForFinalBurn"/>, <c>open</c> false so the gimbals are freed) and the guarded throttle, or holds the cut (<see cref="ReentryBrake.WhyCutHeld"/>), or burns with the chutes on.
        /// The first tick of the wait gives <c>note</c> (empty otherwise), which the addon writes to the log for the block. Falling, chutes fully open and settled also start the braking at the final height if it is the higher.
        /// A chute-only landing never asks, and with no thrust there is no final height: neither ever cuts this way.</para>
        /// <para>The turn to stop (#969's follow-up): tipping (<c>rate_deg</c> over 0) and within <see cref="ReentryBrake.MaxTurnStopSeconds"/> of the fall above the final height, <paramref name="craft"/> is read (null, or none: no extra)
        /// and the final height gains the fall while its wheels stop the turn (<see cref="ReentryBrake.TurnStopSeconds"/> on <see cref="Tools.TurnTime.Alpha"/> without RCS, which is off under the chutes; capped).
        /// The tick that cuts gives <c>note</c> <c>final_cut</c> with the height, <c>final_h</c>, <c>rate_deg</c>, <c>up_deg</c> and <c>turn_s</c>, the seconds it added.</para>
        /// <para><c>trace</c>: while braking (<c>brake_why</c> not ""), true once every <see cref="ReentryBrake.TraceSeconds"/>, through <c>trace_ut</c>, handed back. Pure but for <paramref name="craft"/>.</para>
        /// </summary>
        public static IDictionary<string, object> BrakeTick(IDictionary<string, object> ask, Func<TurnCraft> craft = null)
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
                    aboard.Add(new KeyValuePair<string, bool>(state, Flag(chute, "drogue")));
                }
            }
            var speed = Number(ask, "speed", double.PositiveInfinity);
            var heldSampleUt = Number(ask, "sample_ut", -1);
            var sampleUt = heldSampleUt;
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
            object uids;
            var group = ask.TryGetValue("uids", out uids) && uids is IEnumerable && !(uids is string) ? ((IEnumerable)uids).Cast<object>().Count() : 0;
            var brakeWhy = ask.ContainsKey("brake_why") ? ask["brake_why"] as string ?? "" : "";
            var traceUt = Number(ask, "trace_ut", -1);
            var trace = brakeWhy != "" && now >= traceUt;
            if (trace) traceUt = now + ReentryBrake.TraceSeconds;
            var rateRead = Number(ask, "rate_deg", -1);
            var rate = double.IsNaN(rateRead) || double.IsInfinity(rateRead) ? -1 : rateRead;
            var heldUnreadUt = Number(ask, "unread_ut", -1);
            var unbroken = heldUnreadUt >= 0 && heldSampleUt >= 0 && now - heldSampleUt <= ReentryBrake.UnreadSeconds;
            var unreadUt = !Flag(ask, "chutes_unread") || group == 0 ? -1 : unbroken ? heldUnreadUt : now;
            var heldLastResortUt = Number(ask, "last_resort_ut", -1);
            var lastResortUt = brakeWhy != ReentryBrake.WhyLastResort ? -1 : heldLastResortUt >= 0 ? heldLastResortUt : now;
            if (unreadUt >= 0)
            {
                return new Dictionary<string, object>
                {
                    { "error", "" },
                    { "start", false },
                    { "why", "" },
                    { "open", false },
                    { "open_ut", Number(ask, "open_ut", -1) },
                    { "open_count", Number(ask, "open_count", 0) },
                    { "settled", Flag(ask, "settled") },
                    { "kept_ut", Number(ask, "kept_ut", -1) },
                    { "kept_settled", Flag(ask, "kept_settled") },
                    { "kept_drogues", Number(ask, "kept_drogues", 0) },
                    { "kept_mains", Number(ask, "kept_mains", 0) },
                    { "drogue_call_ut", Number(ask, "drogue_call_ut", -1) },
                    { "main_call_ut", Number(ask, "main_call_ut", -1) },
                    { "sample_ut", sampleUt },
                    { "sample_speed", sampleSpeed },
                    { "decel", decel },
                    { "last_resort_ut", lastResortUt },
                    { "unread_ut", unreadUt },
                    { "all_open_ut", Number(ask, "all_open_ut", -1) },
                    { "final", Flag(ask, "final") },
                    { "final_h", -1.0 },
                    { "note", new Dictionary<string, object>() },
                    { "cut", now - unreadUt >= ReentryBrake.UnreadSeconds },
                    { "cut_why", "unreadable" },
                    { "trace", trace },
                    { "trace_ut", traceUt },
                    { "chutes", "unreadable" },
                    { "throttle", 0.0 },
                    { "zero_why", ReentryBrake.WhyUnread },
                    { "rate_deg", rate },
                };
            }
            var drogueCallUt = CallTime(ask, "drogues_called", "drogue_call_ut", aboard, true, now);
            var mainCallUt = CallTime(ask, "mains_called", "main_call_ut", aboard, false, now);
            var waitedFor = 0;
            var deployed = 0;
            var counted = 0;
            var countedDrogues = 0;
            var countedMains = 0;
            var healthy = 0;
            var filling = 0;
            var packing = 0;
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
                if (callUt < 0 || (lastResortUt >= 0 && callUt >= lastResortUt)) continue;
                if (chute.Key == ReentryBrake.Deployed)
                {
                    counted++;
                    if (chute.Value) countedDrogues++;
                    else countedMains++;
                }
                var packed = chute.Key == ReentryBrake.Stowed || chute.Key == ReentryBrake.Armed;
                if (packed && now - callUt >= ReentryBrake.PackedFailSeconds)
                {
                    failed = true;
                    continue;
                }
                healthy++;
                if (packed) packing++;
                else if (chute.Key != ReentryBrake.Deployed) filling++;
            }
            if (group > aboard.Count) failed = true;
            var open = waitedFor > 0 && deployed == waitedFor;
            var heldUt = Number(ask, "open_ut", -1);
            var heldSettled = Flag(ask, "settled");
            var keptUt = Number(ask, "kept_ut", -1);
            var keptSettled = Flag(ask, "kept_settled");
            var openCount = Number(ask, "open_count", counted);
            var keptDrogues = Number(ask, "kept_drogues", 0);
            var keptMains = Number(ask, "kept_mains", 0);
            if (heldUt < 0 && keptUt >= 0 && countedDrogues <= keptDrogues && countedMains <= keptMains)
            {
                heldUt = keptUt;
                heldSettled = keptSettled;
            }
            var held = heldUt >= 0 && counted <= openCount;
            var openUt = healthy == packing || filling > 0 ? -1 : held ? heldUt : now;
            var settled = openUt >= 0 && ((held && heldSettled) || ReentryBrake.ChutesSettled(now - openUt, decel));
            var nextKeptUt = -1.0;
            var nextKeptSettled = false;
            var nextKeptDrogues = 0.0;
            var nextKeptMains = 0.0;
            if (healthy > 0 && (filling > 0 || healthy == packing))
            {
                var fromHeld = Number(ask, "open_ut", -1) >= 0;
                nextKeptUt = fromHeld ? Number(ask, "open_ut", -1) : keptUt;
                nextKeptSettled = fromHeld ? Flag(ask, "settled") : keptSettled;
                nextKeptDrogues = fromHeld ? countedDrogues : keptDrogues;
                nextKeptMains = fromHeld ? countedMains : keptMains;
            }
            var opening = packing > 0 || (healthy > 0 && !settled);
            var shieldSpeed = Number(ask, "shield_speed", 0);
            var shieldAgl = Number(ask, "shield_agl", 0);
            var vDown = Number(ask, "v_down", 0);
            var aUp = Number(ask, "a_up", 0);
            var g = Number(ask, "g", 0);
            var soft = Number(ask, "soft", 0);
            var end = Number(ask, "end", 0);
            var upDeg = Number(ask, "up_deg", 0);
            var turnS = 0.0;
            var finalH = ReentryBrake.FinalHeight(vDown, aUp, g, radar, soft, end);
            if (craft != null && waitedFor > 0 && rate > 0 && finalH >= 0 && radar <= ReentryBrake.FinalHeight(vDown, aUp, g, radar, soft, end, ReentryBrake.MaxTurnStopSeconds))
            {
                var turning = craft();
                if (turning != null)
                {
                    turnS = ReentryBrake.TurnStopSeconds(rate, upDeg, Tools.TurnTime.Alpha(turning, false) * 180 / Math.PI);
                    finalH = ReentryBrake.FinalHeight(vDown, aUp, g, radar, soft, end, turnS);
                }
            }
            var heldAllOpenUt = Number(ask, "all_open_ut", -1);
            var allOpenUt = !open ? -1 : heldAllOpenUt >= 0 ? heldAllOpenUt : now;
            var heldFinal = Flag(ask, "final");
            var final = brakeWhy != "" && waitedFor > 0 && (heldFinal || (open && ReentryBrake.ChutesSettled(now - allOpenUt, decel)));
            var step = final ? ReentryBrake.FinalStep(radar, finalH, ReentryBrake.FinalLastHeight(vDown, aUp, g, radar, soft), upDeg, rate, turnS) : "";
            var waiting = step == ReentryBrake.WhyFinalWait || step == ReentryBrake.WhyCutHeld;
            var note = new Dictionary<string, object>();
            if (waiting && !heldFinal)
            {
                note["phase"] = "thrust_stopped";
                note["why"] = brakeWhy;
                note["wait"] = step;
                note["agl"] = Math.Round(radar);
                note["speed"] = Math.Round(speed, 1);
                note["final_h"] = Math.Round(finalH);
            }
            if (step == ReentryBrake.StepCut)
            {
                note["phase"] = "final_cut";
                note["agl"] = Math.Round(radar);
                note["final_h"] = Math.Round(finalH);
                note["rate_deg"] = Math.Round(rate, 1);
                note["up_deg"] = Math.Round(upDeg, 1);
                note["turn_s"] = Math.Round(turnS, 1);
            }
            var byRadar = waitedFor == 0 || (open && settled);
            var start = byRadar
                ? ReentryBrake.DueByRadar(radar, Number(ask, "brake_h", -1), speed, shieldSpeed, shieldAgl) || (waitedFor > 0 && finalH >= 0 && radar <= finalH)
                : !opening && failed && ReentryBrake.LastResortDue(radar, Number(ask, "last_h", -1), speed, shieldSpeed, shieldAgl);
            var zeroWhy = waiting ? step : ReentryBrake.ZeroThrustWhy(opening && !final, rate, Number(ask, "off_deg", 0));
            var cut = step == ReentryBrake.StepCut;
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "start", start },
                { "why", waitedFor == 0 ? "radar" : byRadar ? "chutes open" : ReentryBrake.WhyLastResort },
                { "open", open && !cut },
                { "open_ut", openUt },
                { "open_count", (double)counted },
                { "settled", settled },
                { "kept_ut", nextKeptUt },
                { "kept_settled", nextKeptSettled },
                { "kept_drogues", nextKeptDrogues },
                { "kept_mains", nextKeptMains },
                { "drogue_call_ut", drogueCallUt },
                { "main_call_ut", mainCallUt },
                { "sample_ut", sampleUt },
                { "sample_speed", sampleSpeed },
                { "decel", decel },
                { "last_resort_ut", lastResortUt },
                { "unread_ut", -1.0 },
                { "all_open_ut", allOpenUt },
                { "final", final },
                { "final_h", finalH },
                { "note", note },
                { "cut", cut },
                { "cut_why", cut ? ReentryBrake.CutForFinalBurn : "" },
                { "trace", trace },
                { "trace_ut", traceUt },
                { "chutes", string.Join(", ", read.ToArray()) },
                { "throttle", zeroWhy == "" ? Number(ask, "throttle", 0) : 0.0 },
                { "zero_why", zeroWhy },
                { "rate_deg", rate },
            };
        }

        /// <summary>
        /// What <see cref="BrakeTick"/> holds between ticks, holding nothing: for the addon when a chute path goes over to its engines in the air (#963, and #958's switch).
        /// A chute path asks <c>BRAKETICK</c> nothing, so what an earlier flight left must not count. A set already out is then called at the switch and its settle time runs from there: never shorter than its own.
        /// </summary>
        public static IDictionary<string, object> EmptyBrakeHold()
        {
            return new Dictionary<string, object>
            {
                { "open_ut", -1.0 }, { "open_count", 0.0 }, { "settled", false }, { "kept_ut", -1.0 }, { "kept_settled", false }, { "kept_drogues", 0.0 }, { "kept_mains", 0.0 },
                { "drogue_call_ut", -1.0 }, { "main_call_ut", -1.0 }, { "sample_ut", -1.0 }, { "sample_speed", 0.0 }, { "decel", 0.0 },
                { "last_resort_ut", -1.0 }, { "unread_ut", -1.0 }, { "trace_ut", -1.0 }, { "all_open_ut", -1.0 }, { "final", false },
            };
        }

        /// <summary>When the set was first called, UT: the time held in <paramref name="heldKey"/>, or now on the first tick it is called; -1 while it isn't, so no flight inherits it from the last.</summary>
        private static double CallTime(IDictionary<string, object> ask, string calledKey, string heldKey, IEnumerable<KeyValuePair<string, bool>> aboard, bool drogues, double now)
        {
            if (!Flag(ask, calledKey) && !aboard.Any(c => c.Value == drogues && c.Key != ReentryBrake.Stowed && c.Key != ReentryBrake.Cut)) return -1;
            var held = Number(ask, heldKey, -1);
            return held >= 0 ? held : now;
        }

        private static bool Flag(IDictionary<string, object> ask, string key)
        {
            object value;
            return ask.TryGetValue(key, out value) && value is bool && (bool)value;
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
