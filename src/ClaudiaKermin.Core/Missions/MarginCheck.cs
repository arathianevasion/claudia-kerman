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

        /// <summary>
        /// The check of <paramref name="snapshot"/> against <paramref name="rest"/>, with <paramref name="wantedPercent"/> the Craft
        /// Check's margin and <paramref name="chargePercent"/> the electric charge Telemetry Check's line. <paramref name="monopropellantUsed"/>
        /// is what the run just flown used (null when unknown, or for a manual stretch): each leg still to fly is taken to need as much.
        /// </summary>
        public static MarginCheck Of(GameSnapshot snapshot, IList<PlannedSequence> rest, int wantedPercent, int chargePercent, bool remote, double? monopropellantUsed,
            ChuteAllocation chutes = null, string goal = "", int firstStep = 0)
        {
            var check = new MarginCheck();
            if (rest.Count == 0) return check;
            var repack = Repack.IsMatch(goal ?? "");
            var finishes = chutes == null ? new List<EntryChutes>() : chutes.Entries.Where(e => !e.Return && e.PoweredFinish && e.TouchdownSpeed >= 0).ToList();
            // A goal that has an Engineer repack the chutes reuses them: the finish it would need is a Warning, not delta-v to carry.
            check.DeltaV(snapshot, rest, wantedPercent, repack ? new List<EntryChutes>() : finishes);
            check.Chutes(snapshot, rest, chutes, finishes, repack, firstStep);
            check.Thrust(snapshot, rest);
            check.Charge(snapshot, chargePercent);
            check.Monopropellant(snapshot, rest, monopropellantUsed);
            check.Rules(snapshot, rest, remote);
            return check;
        }

        /// <summary>The Mission Goal has an Engineer repack the chutes (a Kerbal on EVA does it, in stock).</summary>
        private static readonly Regex Repack = new Regex(@"(?=.*\bengineer)(?=.*\brepack(s|ed|ing)?\b)(?=.*\bchutes?\b|.*\bparachutes?\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private void DeltaV(GameSnapshot snapshot, IList<PlannedSequence> rest, int wantedPercent, IList<EntryChutes> finishes)
        {
            if (!snapshot.DeltaVSimulator || snapshot.Stages.Count == 0) DeltaVUnknown = "the stock delta-v simulator gives no figures";
            else if (snapshot.DeltaVUnreliable) DeltaVUnknown = "stock reads 0 while an engine is lit at a trickle throttle, so it is unknown, not 0";
            if (DeltaVUnknown.Length > 0) return;
            Margin = MarginBar.Compute(snapshot.Stages.Sum(s => s.DeltaVVac), rest.Sum(s => s.DeltaV) + finishes.Sum(e => e.TouchdownSpeed), wantedPercent);
            if (Margin == null) return;
            if (Margin.Level != MarginLevel.Enough)
            {
                // A shortfall that only the powered finishes cause is the Chute finding's (it names the finish): one finding, not two.
                var legs = MarginBar.Compute(snapshot.Stages.Sum(s => s.DeltaVVac), rest.Sum(s => s.DeltaV), wantedPercent);
                if (finishes.Count > 0 && legs != null && legs.Level == MarginLevel.Enough) return;
                Add("delta_v_short", Margin.Level, Margin.Describe().TrimEnd('.'));
                return;
            }
            var actual = Margin.ActualPercent;
            if (actual.HasValue && actual.Value < 25) Add("margin_under_25", MarginLevel.Enough, Margin.Describe().TrimEnd('.'));
        }

        /// <summary>
        /// The Chute Allocation's verdicts (#629): a powered finish whose stages (in firing order, after the legs before it and the
        /// finishes before it) lack the delta-v for it, or a Kerbin return that can't be covered, is a Blocker; a finish with under
        /// 25% spare is a Warning. With an Engineer repacking the chutes the shortfalls are Warnings that say so. The delta-v for a
        /// finish is its touchdown speed, from the allocation's predictor; an entry that couldn't be predicted is not judged.
        /// </summary>
        private void Chutes(GameSnapshot snapshot, IList<PlannedSequence> rest, ChuteAllocation chutes, IList<EntryChutes> finishes, bool repack, int firstStep)
        {
            if (chutes == null) return;
            var severity = repack ? CraftCheckRules.Find("chutes_thin_for_landing") : CraftCheckRules.Find("chutes_short_for_landing");
            var level = repack ? MarginLevel.Enough : MarginLevel.Short;
            var have = DeltaVUnknown.Length == 0 && snapshot.DeltaVSimulator ? snapshot.Stages.Sum(s => s.DeltaVVac) : (double?)null;
            double carried = 0;
            foreach (var entry in finishes)
            {
                var need = entry.TouchdownSpeed;
                var through = Math.Max(0, Math.Min(rest.Count, entry.Step - firstStep + 1));
                var left = have - rest.Take(through).Sum(s => Math.Max(0, s.DeltaV)) - carried;
                carried += need;
                if (!left.HasValue) continue;
                var where = "The powered finish at " + entry.Body + " for \"" + entry.Sequence + "\" needs " + Whole(need) + " m/s (its chutes leave it falling that fast), "
                    + Whole(Math.Max(0, left.Value)) + " m/s left";
                if (left.Value < need) AddChute(severity, level, where + (repack ? "; relies on a repack at " + entry.Body : ""));
                else if (left.Value < 1.25 * need) AddChute(CraftCheckRules.Find("chutes_thin_for_landing"), MarginLevel.Enough, where + ", under 25% spare");
            }
            var home = chutes.Return;
            if (chutes.ReturnUncovered && home != null)
                AddChute(severity, level, "The Kerbin return's chutes leave it at " + Whole(home.TouchdownSpeed) + " m/s, over " + Whole(ChuteAllocation.TouchdownLimit)
                    + (repack ? "; relies on a repack at Kerbin" : ""));
        }

        private void AddChute(CraftCheckRule rule, MarginLevel level, string explanation)
        {
            Findings.Add(new MarginFinding { Code = rule.Code, Severity = rule.Severity, Title = rule.Title, Explanation = explanation });
            Raise(level);
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
            var body = SequenceWords.Body(snapshot, snapshot.Body);
            double used = 0;
            foreach (var leg in rest)
            {
                var words = SequenceWords.Of(leg);
                body = SequenceWords.NamedBody(snapshot, words) ?? body;
                var start = used;
                used += Math.Max(0, leg.DeltaV);
                if (leg.Manual || body == null || body.SeaLevelAtm > 0 || body.SurfaceGravity <= 0 || !SequenceWords.Surface.IsMatch(words)) continue;
                var twr = LowestTwr(snapshot, body, start, Math.Max(start + 1, used));
                if (!twr.HasValue) continue;
                var line = TwrText(twr.Value) + " on " + body.Name + " for \"" + leg.Name + "\"";
                if (twr.Value < 1) below.Add(line);
                else if (twr.Value < 1.5 && SequenceWords.Landing.IsMatch(words)) low.Add(line);
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
            else if (rest.Any(s => SequenceWords.ReturnsHome(SequenceWords.Of(s))) && !snapshot.Craft.Parts.Any(p => p.Tags.Contains("parachute")))
            {
                Add("no_parachutes_for_return", MarginLevel.Short, "A return is still to fly and no part is a parachute");
            }
            if (rest.Any(s => s.Manual) && !snapshot.HasOtherCrew)
                Add("manual_stretches_without_crew", MarginLevel.Enough, "A manual stretch is still to fly and no Kerbal aboard but Claudia can fly it");
            if (rest.Any(s => !s.Manual && SequenceWords.Docking.IsMatch(SequenceWords.Of(s))))
                foreach (var finding in Docking(snapshot, "A docking is still to fly"))
                {
                    Findings.Add(finding);
                    Raise(finding.Severity == CraftFindingSeverity.Blocker ? MarginLevel.Short : MarginLevel.Enough);
                }
        }

        /// <summary>
        /// What the dock block needs of the craft (#456), for a docking still to fly (<paramref name="lead"/> says which): a docking
        /// port, RCS and monopropellant (a Blocker), and a port of a size the Target Vessel has free (a Warning; not judged when the
        /// snapshot has no target, or one with no free ports read). The Craft Check raises the same for a goal that docks.
        /// </summary>
        public static IList<MarginFinding> Docking(GameSnapshot snapshot, string lead)
        {
            return Docking(snapshot, lead, snapshot.Target ?? snapshot.KspTarget);
        }

        /// <summary>The same, judged against <paramref name="target"/>'s free ports: the Craft Check's chosen Target Vessel (#643); null judges no port size.</summary>
        public static IList<MarginFinding> Docking(GameSnapshot snapshot, string lead, SnapshotTarget target)
        {
            var found = new List<MarginFinding>();
            var sizes = snapshot.Craft.Parts.Where(p => p.Tags.Contains("docking_port")).Select(p => p.PortSize).Distinct().ToList();
            var lacks = new List<string>();
            if (sizes.Count == 0) lacks.Add("docking port");
            if (!snapshot.Craft.Parts.Any(p => p.Tags.Contains("rcs"))) lacks.Add("RCS");
            if ((LiveReadings.MonopropellantOf(snapshot) ?? 0) <= 0) lacks.Add("monopropellant");
            if (lacks.Count > 0)
                found.Add(Finding(CraftCheckRules.DockingKit, lead + " and the craft has no " + string.Join(", no ", lacks.ToArray())));
            else if (target != null && target.FreePorts.Count > 0 && !sizes.Any(target.FreePorts.ContainsKey))
                found.Add(Finding(CraftCheckRules.DockingPortSize, "The craft's ports (" + string.Join(", ", sizes.ToArray()) + ") match none of "
                    + target.Name + "'s free ports (" + string.Join(", ", target.FreePorts.Select(f => f.Value + " " + f.Key).ToArray()) + ")"));
            return found;
        }

        private static MarginFinding Finding(string code, string explanation)
        {
            var rule = CraftCheckRules.Find(code);
            return new MarginFinding { Code = code, Severity = rule.Severity, Title = rule.Title, Explanation = explanation };
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
