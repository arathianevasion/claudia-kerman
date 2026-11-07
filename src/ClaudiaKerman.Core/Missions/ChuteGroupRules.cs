// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// What the Margin Check and the Craft Check say of a craft's Chute Groups beyond the touchdown (ADR 0019): an Entry left with no group,
    /// a group likely to leave before its Entry, and a group in a stage automatic staging can't fire past. All are the mod's own findings.
    /// </summary>
    internal static class ChuteGroupRules
    {
        public static IList<MarginFinding> Of(GameSnapshot snapshot, IList<PlannedSequence> rest, ChuteGroups groups, bool repack, int firstStep)
        {
            var found = new List<MarginFinding>();
            NoneLeft(groups, repack, found);
            if (!snapshot.DeltaVSimulator || snapshot.Stages.Count == 0 || snapshot.DeltaVUnreliable) return found;
            var legs = PlanLegs.Judge(snapshot, rest, "");
            LeavingEarly(snapshot, groups, legs, firstStep, found);
            AboveEngine(snapshot, groups, PlanLegs.Need(legs), found);
            return found;
        }

        /// <summary>The fix each code's finding gives on the Craft Check.</summary>
        public static string Fix(string code)
        {
            if (code == CraftCheckRules.ChuteGroupLeaves) return "Stage those chutes on parts that stay until their Entry, or give them their own stage below the drop.";
            if (code == CraftCheckRules.ChuteGroupAboveEngine) return "Move the chutes to a stage below the last engine the plan needs, or turn off their staging.";
            if (code == CraftCheckRules.ChuteGroupTooFast) return "Add parachutes to that stage.";
            return "Give each Entry its own stage of parachutes, or delta-v for the engines to finish the landing.";
        }

        /// <summary>An Entry with no group, while the craft has chutes: a Blocker, or a Warning with a repack planned. With none at all the return's own rules speak.</summary>
        private static void NoneLeft(ChuteGroups groups, bool repack, List<MarginFinding> found)
        {
            if (groups.Chutes.Count == 0) return;
            for (var i = 0; i < groups.Entries.Count; i++)
            {
                var entry = groups.Entries[i];
                if (entry.Chutes.Count > 0) continue;
                var stages = groups.Chutes.Select(c => c.Stage).Distinct().Count();
                var lead = "The " + entry.Body + " entry for \"" + entry.Sequence + "\" has no Chute Group left: chutes in " + stages + (stages == 1 ? " stage" : " stages")
                    + " for " + groups.Entries.Count + " entries";
                if (repack) found.Add(Finding("chutes_thin_for_landing", lead + "; relies on a repack at " + groups.Entries[Math.Max(0, i - 1)].Body));
                else found.Add(Finding("chutes_short_for_landing", lead));
            }
        }

        /// <summary>A group some of whose chutes sit on parts that drop in a stage the delta-v before its Entry burns through: the rule would move on.</summary>
        private static void LeavingEarly(GameSnapshot snapshot, ChuteGroups groups, IList<JudgedLeg> legs, int firstStep, List<MarginFinding> found)
        {
            var flown = Math.Max(0, firstStep);
            for (var i = 0; i < groups.Entries.Count; i++)
            {
                var entry = groups.Entries[i];
                var spent = legs.Take(Math.Max(0, entry.Step - flown + 1)).Sum(l => l.Need);
                var leaving = entry.Chutes.Where(c => c.LeavesAtStage.HasValue && BurnedTo(snapshot, c.LeavesAtStage.Value, spent)).ToList();
                if (leaving.Count == 0) continue;
                found.Add(Finding(CraftCheckRules.ChuteGroupLeaves, "Stage " + Number(leaving[0].Stage) + "'s chutes sit on parts dropped in stage " + Number(leaving.Max(c => c.LeavesAtStage.Value))
                    + ", and the plan's delta-v burns through it before the " + entry.Body + " entry for \"" + entry.Sequence + "\"" + MovesOn(groups, i)));
            }
        }

        /// <summary>The stage <paramref name="separates"/> fires once <paramref name="spent"/> m/s have used up every stage firing before it.</summary>
        private static bool BurnedTo(GameSnapshot snapshot, int separates, double spent)
        {
            return spent > snapshot.Stages.Where(s => s.Number > separates).Sum(s => s.DeltaVVac);
        }

        /// <summary>What the rule does instead: the next stage opens at this Entry, perhaps leaving the last Entry none.</summary>
        private static string MovesOn(ChuteGroups groups, int index)
        {
            var later = groups.Entries.Skip(index + 1).ToList();
            var stages = later.SelectMany(e => e.Chutes).Select(c => c.Stage).Distinct().OrderByDescending(s => s).ToList();
            if (stages.Count == 0) return ": the rule has no other group for it";
            var text = ": stage " + Number(stages[0]) + " would open at " + groups.Entries[index].Body;
            return stages.Count - 1 < later.Count ? text + ", leaving " + later[later.Count - 1].Body + " none" : text;
        }

        /// <summary>
        /// A group's stage, not yet fired, at or above an unlit engine the plan still needs: <c>claudia_stage_unsafe</c> won't fire a stage holding
        /// chutes, so the delta-v from the stages before it, and from engines already lit, is all the craft gets. Stage -1 is never fired.
        /// </summary>
        private static void AboveEngine(GameSnapshot snapshot, ChuteGroups groups, double need, List<MarginFinding> found)
        {
            var parts = snapshot.Craft.Parts;
            foreach (var stage in groups.Chutes.Select(c => c.Stage).Where(s => s >= 0 && s < snapshot.Craft.Stage).Distinct().OrderByDescending(s => s))
            {
                var engine = parts.Where(p => p.Tags.Contains("engine") && !p.EngineLit && p.Stage >= 0 && p.Stage <= stage).OrderByDescending(p => p.Stage).FirstOrDefault();
                if (engine == null) continue;
                var reach = snapshot.Stages.Where(s => s.Number > stage || parts.Any(p => p.EngineLit && p.Stage == s.Number)).Sum(s => s.DeltaVVac);
                if (need <= reach) continue;
                found.Add(Finding(CraftCheckRules.ChuteGroupAboveEngine, "Stage " + Number(stage) + " holds chutes and " + (engine.Title.Length > 0 ? engine.Title : engine.Name) + " in stage " + Number(engine.Stage)
                    + " is still needed: the plan needs " + Whole(need) + " m/s and only " + Whole(reach) + " m/s burns before that stage, which automatic staging won't fire"));
            }
        }

        private static MarginFinding Finding(string code, string explanation)
        {
            var rule = CraftCheckRules.Find(code);
            return new MarginFinding { Code = code, Severity = rule.Severity, Title = rule.Title, Explanation = explanation };
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
