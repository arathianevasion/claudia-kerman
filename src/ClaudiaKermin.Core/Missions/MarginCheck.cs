// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Roster;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>One finding of the mod's margin check: a Craft Check rule's code (or "" for one the fixed lists don't have), its severity and why.</summary>
    internal sealed class MarginFinding
    {
        public string Code = "";
        public CraftFindingSeverity Severity;
        public string Title = "";
        public string Explanation = "";

        /// <summary>"Blocker: Not enough delta-v. 3,300 m/s left against ...", as the card and the Debrief request show it.</summary>
        public string Describe()
        {
            return Head() + (Explanation.Length > 0 ? ". " + Explanation : "");
        }

        /// <summary>"Blocker: Not enough delta-v".</summary>
        public string Head()
        {
            return (Severity == CraftFindingSeverity.Blocker ? "Blocker: " : "Warning: ") + Title;
        }
    }

    /// <summary>
    /// The mod's own margin check at every Debrief (#574): the craft as it is now, from the Game Snapshot, against the Maneuver
    /// Sequences still to fly in the Mission Plan, with no Claude. Delta-v is <see cref="MarginBar"/>'s; thrust over weight is
    /// <c>stage_performance</c>'s rows for the stages each landing or ascent on an airless body will burn; electric charge and
    /// monopropellant are measured against what is left to fly; and the Craft Check findings the mod can decide in code join them.
    /// The level is the worst of them. Findings the start Craft Check already raised and the player acknowledged (no generation,
    /// a low landing TWR, a margin under 25%, a manual stretch with no other crew) are reported but leave the level as it is, so
    /// that only what has changed since makes the margin tight or short. An unreliable delta-v reading (#234) is not judged.
    /// </summary>
    internal sealed class MarginCheck
    {
        public MarginLevel Level = MarginLevel.Enough;
        /// <summary>The delta-v figure; null when it isn't known or nothing is left to fly.</summary>
        public MarginBar Margin;
        /// <summary>Why delta-v isn't judged ("" when it is, or nothing is left to fly).</summary>
        public string DeltaVUnknown = "";
        public readonly List<MarginFinding> Findings = new List<MarginFinding>();

        private static readonly Regex Surface = new Regex(@"\b(land(s|ed|ing)?|touch ?down|ascen(d|ds|t|ding)|lift ?off|take ?off|launch(es|ing)?)\b", RegexOptions.IgnoreCase);
        private static readonly Regex Landing = new Regex(@"\b(land(s|ed|ing)?|touch ?down)\b", RegexOptions.IgnoreCase);
        private static readonly Regex Reentry = new Regex(@"\b(re-?ent(er|ry|ers|ering)|splash ?down)\b", RegexOptions.IgnoreCase);
        private static readonly Regex ReturnHome = new Regex(@"\breturn(s|ing)?\b.*\bKerbin\b|\bKerbin\b.*\breturn(s|ing)?\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// The check of <paramref name="snapshot"/> against <paramref name="rest"/>, with <paramref name="wantedPercent"/> the Craft
        /// Check's margin and <paramref name="chargePercent"/> the electric charge Telemetry Check's line. <paramref name="monopropellantUsed"/>
        /// is what the run just flown used (null when unknown, or for a manual stretch): each leg still to fly is taken to need as much.
        /// </summary>
        public static MarginCheck Of(GameSnapshot snapshot, IList<PlannedSequence> rest, int wantedPercent, int chargePercent, bool remote, double? monopropellantUsed)
        {
            var check = new MarginCheck();
            if (rest.Count == 0) return check;
            check.DeltaV(snapshot, rest, wantedPercent);
            check.Thrust(snapshot, rest);
            check.Charge(snapshot, chargePercent);
            check.Monopropellant(snapshot, rest, monopropellantUsed);
            check.Rules(snapshot, rest, remote);
            return check;
        }

        private void DeltaV(GameSnapshot snapshot, IList<PlannedSequence> rest, int wantedPercent)
        {
            if (!snapshot.DeltaVSimulator || snapshot.Stages.Count == 0) DeltaVUnknown = "the stock delta-v simulator gives no figures";
            else if (snapshot.DeltaVUnreliable) DeltaVUnknown = "stock reads 0 while an engine is lit at a trickle throttle, so it is unknown, not 0";
            if (DeltaVUnknown.Length > 0) return;
            Margin = MarginBar.Compute(snapshot.Stages.Sum(s => s.DeltaVVac), rest.Sum(s => s.DeltaV), wantedPercent);
            if (Margin == null) return;
            if (Margin.Level != MarginLevel.Enough)
            {
                Add("delta_v_short", Margin.Level, Margin.Describe().TrimEnd('.'));
                return;
            }
            var actual = Margin.ActualPercent;
            if (actual.HasValue && actual.Value < 25) Add("margin_under_25", MarginLevel.Enough, Margin.Describe().TrimEnd('.'));
        }

        /// <summary>
        /// Each landing or ascent on an airless body (a body with air lands under parachute), on the stages it will burn: the
        /// legs before it use the delta-v of the stages in firing order (highest number first), and the leg takes the lowest TWR
        /// of the stages its own budget spans.
        /// </summary>
        private void Thrust(GameSnapshot snapshot, IList<PlannedSequence> rest)
        {
            if (!snapshot.DeltaVSimulator || snapshot.Stages.Count == 0 || snapshot.Bodies.Count == 0) return;
            var below = new List<string>();
            var low = new List<string>();
            var body = Body(snapshot, snapshot.Body);
            double used = 0;
            foreach (var leg in rest)
            {
                var words = leg.Name + " " + leg.Intent;
                body = NamedBody(snapshot, words) ?? body;
                var start = used;
                used += Math.Max(0, leg.DeltaV);
                if (leg.Manual || body == null || body.SeaLevelAtm > 0 || body.SurfaceGravity <= 0 || !Surface.IsMatch(words)) continue;
                var twr = LowestTwr(snapshot, body, start, Math.Max(start + 1, used));
                if (!twr.HasValue) continue;
                var line = TwrText(twr.Value) + " on " + body.Name + " for \"" + leg.Name + "\"";
                if (twr.Value < 1) below.Add(line);
                else if (twr.Value < 1.5 && Landing.IsMatch(words)) low.Add(line);
            }
            if (below.Count > 0) Add("twr_below_one", MarginLevel.Short, "Thrust over weight " + string.Join("; ", below.ToArray()));
            if (low.Count > 0) Add("landing_twr_low", MarginLevel.Enough, "Thrust over weight " + string.Join("; ", low.ToArray()));
        }

        private static double? LowestTwr(GameSnapshot snapshot, SnapshotBody body, double from, double to)
        {
            var rows = StagePerformance.Rows(snapshot, 0, body.SurfaceGravity).Where(r => r.DeltaV > 0 && r.Thrust > 0).OrderByDescending(r => r.Stage).ToList();
            double? lowest = null;
            double at = 0;
            foreach (var row in rows)
            {
                var end = at + row.DeltaV;
                if (end > from && at < to) lowest = Math.Min(lowest ?? double.MaxValue, row.Twr);
                at = end;
            }
            return lowest;
        }

        private static SnapshotBody NamedBody(GameSnapshot snapshot, string words)
        {
            SnapshotBody first = null;
            var firstAt = int.MaxValue;
            foreach (var body in snapshot.Bodies.Where(b => b.Name.Length > 0))
            {
                var match = Regex.Match(words, @"\b" + Regex.Escape(body.Name) + @"\b", RegexOptions.IgnoreCase);
                if (match.Success && match.Index < firstAt)
                {
                    first = body;
                    firstAt = match.Index;
                }
            }
            return first;
        }

        private static SnapshotBody Body(GameSnapshot snapshot, string name)
        {
            return snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>No generation is the start check's Warning; with none, a battery under the Telemetry Check's line is tight and an empty one short.</summary>
        private void Charge(GameSnapshot snapshot, int chargePercent)
        {
            if (snapshot.Craft.Parts.Any(p => p.Tags.Contains("electric_generator"))) return;
            var charge = snapshot.Resources.Where(r => r.Name == "ElectricCharge").ToList();
            var amount = charge.Sum(r => r.Amount);
            var max = charge.Sum(r => r.Max);
            var level = max <= 0 || amount <= 0 ? MarginLevel.Short : 100 * amount / max < chargePercent ? MarginLevel.Tight : MarginLevel.Enough;
            Add("no_electric_generation_or_low_battery", level, "Nothing aboard generates electric charge; " + Whole(amount) + " of " + Whole(max) + " EC left");
        }

        private void Monopropellant(GameSnapshot snapshot, IList<PlannedSequence> rest, double? used)
        {
            if (!used.HasValue || used.Value <= 0) return;
            var left = LiveReadings.MonopropellantOf(snapshot) ?? 0;
            var legs = rest.Count(s => !s.Manual);
            var wanted = used.Value * legs;
            if (legs == 0 || left >= wanted) return;
            Findings.Add(new MarginFinding
            {
                Code = "",
                Severity = CraftFindingSeverity.Warning,
                Title = "Monopropellant low",
                Explanation = Whole(left) + " units left; the last run used " + Whole(used.Value) + ", " + Whole(wanted) + " for the " + legs + (legs == 1 ? " leg" : " legs") + " still to fly",
            });
            Raise(MarginLevel.Tight);
        }

        /// <summary>The fixed-list findings the mod decides in code: the remote Mission's probe core and antenna, a crewed return's parachutes, a manual stretch's crew.</summary>
        private void Rules(GameSnapshot snapshot, IList<PlannedSequence> rest, bool remote)
        {
            if (remote)
            {
                var missing = RemoteBlocker.Missing(snapshot);
                if (missing.Length > 0) Add(RemoteBlocker.Code, MarginLevel.Short, missing.TrimEnd('.'));
            }
            else if (rest.Any(s => ReturnsHome(s.Name + " " + s.Intent)) && !snapshot.Craft.Parts.Any(p => p.Tags.Contains("parachute")))
            {
                Add("no_parachutes_for_return", MarginLevel.Short, "A return is still to fly and no part is a parachute");
            }
            if (rest.Any(s => s.Manual) && !snapshot.HasOtherCrew)
                Add("manual_stretches_without_crew", MarginLevel.Enough, "A manual stretch is still to fly and no Kerbal aboard but Claudia can fly it");
        }

        /// <summary>A leg that comes down through Kerbin's air: a reentry or splashdown, or a return that names Kerbin.</summary>
        private static bool ReturnsHome(string words)
        {
            return Reentry.IsMatch(words) || ReturnHome.IsMatch(words);
        }

        private void Add(string code, MarginLevel level, string explanation)
        {
            var rule = CraftCheckRules.Find(code);
            Findings.Add(new MarginFinding { Code = code, Severity = rule.Severity, Title = rule.Title, Explanation = explanation });
            Raise(level);
        }

        private void Raise(MarginLevel level)
        {
            if (level > Level) Level = level;
        }

        /// <summary>The margin line: <see cref="MarginBar.Describe"/>, or why delta-v isn't judged, with the level when something else set it.</summary>
        public string Line()
        {
            if (Margin != null) return Level == Margin.Level ? Margin.Describe() : Margin.Describe().TrimEnd('.') + "; " + Word(Level) + " overall.";
            if (DeltaVUnknown.Length > 0) return "Delta-v left unknown (" + DeltaVUnknown + "): " + Word(Level) + " on the rest.";
            return "Nothing with a delta-v budget is left to fly: " + Word(Level) + ".";
        }

        /// <summary>The finding lines, as the card shows them.</summary>
        public IList<string> FindingLines()
        {
            return Findings.Select(f => f.Describe()).ToList();
        }

        /// <summary>The Debrief request's line, where the quoted margin recheck went before #574.</summary>
        public string Text()
        {
            return "The mod's margin check (the craft as it is now against the Maneuver Sequences still to fly): " + Line()
                + (Findings.Count > 0 ? " Findings: " + string.Join(" ", Findings.Select(f => f.Describe() + ".").ToArray()) : " No findings.");
        }

        public static string Word(MarginLevel level)
        {
            return level == MarginLevel.Enough ? "enough" : level == MarginLevel.Tight ? "tight" : "short";
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }

        private static string TwrText(double twr)
        {
            return twr.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
