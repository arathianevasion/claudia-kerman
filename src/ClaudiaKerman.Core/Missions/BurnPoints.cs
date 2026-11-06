// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>The wait to a Mission Script's first burn point (<see cref="BurnPoints.First"/>) and what covers it.</summary>
    public sealed class BurnWait
    {
        /// <summary>The block that burns there, e.g. "tune_pe".</summary>
        public string Block = "";
        /// <summary>Where, e.g. "the apoapsis" or "its maneuver node".</summary>
        public string Point = "";
        /// <summary>Game seconds from now to the burn point.</summary>
        public double WaitSeconds;
        /// <summary>The burn's seconds: the stages' estimate, or <see cref="BurnPoints.BurnAllowanceSeconds"/> without one.</summary>
        public double BurnSeconds;
        /// <summary>For a run the hard stop cut short (<see cref="BurnPoints.WarpCutShort"/>): game seconds still left to the point.</summary>
        public double LeftSeconds;

        /// <summary>The least <c>expected_duration_s</c> that covers the wait, the burn and the turn to it, rounded up to 10 s.</summary>
        public double MinimumDurationSeconds
        {
            get { return Math.Ceiling((WaitSeconds + BurnSeconds + BurnPoints.TurnMarginSeconds) / 10) * 10; }
        }
    }

    /// <summary>
    /// #696: the game-time wait to the first burn point a Mission Script shows, worked out from the Game Snapshot before
    /// Approval, so a Sequence Plan whose <c>expected_duration_s</c> leaves it out goes back before the runaway hard stop
    /// (ADR 0004: waits count in the planned duration) would kill a run doing what it should. Block calls and their
    /// options, defaults included, are read through <see cref="BlockOptionCheck"/>. It answers only for a burn point
    /// fixed before the run (an apsis of the current orbit, a literal node UT, a node already planned); anything else,
    /// or another block first, gives null, and so no check.
    /// </summary>
    public static class BurnPoints
    {
        /// <summary>Allowed for turning to the burn and settling: seconds over the wait and the burn.</summary>
        public const double TurnMarginSeconds = 60;
        /// <summary>A burn's seconds when the stages can't estimate it.</summary>
        public const double BurnAllowanceSeconds = 120;
        /// <summary>tune_pe takes the next apoapsis when this one is under this many seconds off (tune_pe.ks).</summary>
        private const double TunePeLeadSeconds = 120;

        private static readonly Regex AddNode = new Regex(@"\badd\s+node\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Remove = new Regex(@"\bremove\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);
        private static readonly Regex FromNow = new Regex(@"^time\s*:\s*seconds\s*\+\s*(?<s>\d+(\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The plan's first burn when <c>expected_duration_s</c> doesn't cover it; null when it does or none can be read.
        /// <paramref name="snapshot"/> is the craft the plan flies (null: no check).
        /// </summary>
        public static BurnWait ShortOf(SequencePlan plan, GameSnapshot snapshot, double now)
        {
            if (plan == null || plan.Manual) return null;
            var wait = First(plan.Script, snapshot, now, plan.ExpectedDeltaV);
            return wait != null && plan.ExpectedDurationSeconds < wait.MinimumDurationSeconds ? wait : null;
        }

        /// <summary>
        /// The wait to the script's first burn point at <paramref name="now"/> (UT), with its burn time from
        /// <paramref name="expectedDeltaV"/> (or the existing node's delta-v); null when the script doesn't show one.
        /// </summary>
        public static BurnWait First(string script, GameSnapshot snapshot, double now, double expectedDeltaV)
        {
            if (string.IsNullOrWhiteSpace(script) || snapshot == null) return null;
            var masked = CoreLibrary.MissionScript(script, true);
            var code = CoreLibrary.MissionScript(script);
            double? nodeUt = null;
            var nodes = AddNode.Matches(masked).Cast<Match>().ToList();
            foreach (var call in BlockOptionCheck.Calls(script))
            {
                // An "add node" before this block call sets the node it flies; one whose UT can't be read leaves nothing to check.
                var added = nodes.LastOrDefault(n => n.Index < call.Index);
                if (added != null)
                {
                    nodeUt = NodeUt(masked, code, added, now);
                    if (!nodeUt.HasValue) return null;
                }
                var wait = Wait(call, snapshot, now, nodeUt, Remove.IsMatch(masked.Substring(0, call.Index)));
                if (wait == null) return null;
                var dv = expectedDeltaV > 0 ? expectedDeltaV : call.Block == "exec_node" && !nodeUt.HasValue && snapshot.Nodes.Count > 0 ? snapshot.Nodes[0].DeltaV : 0;
                wait.BurnSeconds = (dv > 0 ? Tools.PredictBurn.BurnSeconds(snapshot, dv) : null) ?? BurnAllowanceSeconds;
                return wait;
            }
            return null;
        }

        /// <summary>
        /// For a run the hard stop ended at <paramref name="stoppedUt"/>: its last warp request (from the warp trace) when that
        /// request's target was still ahead, as the wait from the run's start to it; null when it wasn't warping to one.
        /// </summary>
        public static BurnWait WarpCutShort(WarpTrace trace, double runStartUt, double stoppedUt)
        {
            if (trace == null) return null;
            var last = trace.Entries.LastOrDefault(e => e.Kind == WarpTraceKind.Request);
            if (last == null || !last.TargetUt.HasValue || last.TargetUt.Value <= stoppedUt) return null;
            return new BurnWait
            {
                Block = last.Block.Length > 0 ? last.Block : "the script",
                Point = "its warp target",
                WaitSeconds = last.TargetUt.Value - runStartUt,
                BurnSeconds = BurnAllowanceSeconds,
                LeftSeconds = last.TargetUt.Value - stoppedUt,
            };
        }

        /// <summary>The wait for this call's burn point, or null when it has none fixed before the run (or isn't a burn).</summary>
        private static BurnWait Wait(BlockCall call, GameSnapshot snapshot, double now, double? nodeUt, bool removesNodes)
        {
            switch (call.Block)
            {
                case "exec_node":
                    if (nodeUt.HasValue) return At(call, "its maneuver node", nodeUt.Value - now);
                    if (removesNodes || snapshot.Nodes.Count == 0) return null;
                    return At(call, "the maneuver node already planned", snapshot.Nodes[0].Ut - now);
                case "circularize":
                case "change_orbit":
                    return Apsis(call, snapshot, now, call.Option("at"));
                case "tune_pe":
                    return TunePe(call, snapshot, now);
                default:
                    return null;
            }
        }

        /// <summary>tune_pe's patch "current" burns at the apoapsis (the next one if this is under 2 minutes off).</summary>
        private static BurnWait TunePe(BlockCall call, GameSnapshot snapshot, double now)
        {
            if (call.Option("patch") != "\"current\"") return null;
            var orbit = Closed(snapshot);
            if (orbit == null) return null;
            // It needs no burn when the periapsis is already within tolerance.
            var value = Literal(call.Option("value"));
            var tolerance = Literal(call.Option("tolerance"));
            if (value.HasValue && tolerance.HasValue && Math.Abs(orbit.Periapsis - value.Value) <= tolerance.Value) return null;
            // A periapsis in the air or the ground reached first makes it burn sooner: no fixed point to check.
            var body = snapshot.Bodies.FirstOrDefault(b => b.Name == (orbit.Body.Length > 0 ? orbit.Body : snapshot.Body));
            var toAp = TimeTo(orbit, now, Math.PI);
            if (toAp < TunePeLeadSeconds) toAp += orbit.Period;
            if ((orbit.Periapsis < 0 || (body != null && orbit.Periapsis < body.AtmosphereHeight)) && TimeTo(orbit, now, 0) < toAp) return null;
            return At(call, "the apoapsis", toAp);
        }

        private static BurnWait Apsis(BlockCall call, GameSnapshot snapshot, double now, string at)
        {
            var orbit = Closed(snapshot);
            if (orbit == null) return null;
            if (at == "\"ap\"") return At(call, "the apoapsis", TimeTo(orbit, now, Math.PI));
            if (at == "\"pe\"") return At(call, "the periapsis", TimeTo(orbit, now, 0));
            return null;
        }

        private static BurnWait At(BlockCall call, string point, double wait)
        {
            return wait < 0 ? null : new BurnWait { Block = call.Block, Point = point, WaitSeconds = wait };
        }

        /// <summary>The craft's orbit when it is a closed one it flies now (not on the ground); null otherwise.</summary>
        private static SnapshotOrbit Closed(GameSnapshot snapshot)
        {
            var orbit = snapshot.Orbit;
            if (orbit == null || snapshot.IsGrounded) return null;
            return orbit.Eccentricity < 1 && orbit.Period > 0 && !double.IsInfinity(orbit.Period) ? orbit : null;
        }

        /// <summary>Seconds from <paramref name="now"/> until the mean anomaly is <paramref name="mean"/> (0 periapsis, π apoapsis).</summary>
        private static double TimeTo(SnapshotOrbit orbit, double now, double mean)
        {
            var n = 2 * Math.PI / orbit.Period;
            var at = orbit.MeanAnomalyAtEpoch + n * (now - orbit.Epoch);
            var left = (mean - at) % (2 * Math.PI);
            if (left < 0) left += 2 * Math.PI;
            return left / n;
        }

        /// <summary>The UT of an <c>add node(ut, ...)</c> whose first argument is a number or <c>time:seconds + n</c>; null otherwise.</summary>
        private static double? NodeUt(string masked, string code, Match added, double now)
        {
            var end = masked.IndexOf(',', added.Index + added.Length);
            var close = masked.IndexOf(')', added.Index + added.Length);
            if (end < 0 || (close >= 0 && close < end)) return null;
            var first = code.Substring(added.Index + added.Length, end - added.Index - added.Length).Trim();
            if (Number.IsMatch(first)) return double.Parse(first, CultureInfo.InvariantCulture);
            var from = FromNow.Match(first);
            return from.Success ? now + double.Parse(from.Groups["s"].Value, CultureInfo.InvariantCulture) : (double?)null;
        }

        private static double? Literal(string text)
        {
            double value;
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (double?)null;
        }
    }
}
