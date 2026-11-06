// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// What the checks before Approval found in an offered Sequence Plan: the Script Check's problems (ADR 0003), and the
    /// plan's own figures that are wrong by the Game Snapshot (#674's predicted crossings, #696's duration short of its first
    /// burn, #699's landing in another biome than the goal names). Any of them sends the plan back in the one automatic correction; a check of the plan's figures adds a field here,
    /// its line in <see cref="MissionPrompts.PlanCheckFailed"/> and <see cref="MissionPrompts.PlanFixNotice"/>, and its warning.
    /// </summary>
    public sealed class PlanFindings
    {
        /// <summary>The Script Check's problems; empty when the script passed.</summary>
        public IList<string> ScriptProblems = new List<string>();
        /// <summary>Delta-v left checks the plan's own expected_delta_v is certain to cross (#674), from <see cref="StartDeltaV"/>.</summary>
        public IList<PredictedCrossing> Crossings = new List<PredictedCrossing>();
        /// <summary>The delta-v left the plan starts from (0 when unreadable) and its expected_delta_v, for the crossings' text.</summary>
        public double StartDeltaV;
        public double ExpectedDeltaV;
        /// <summary>#830: what plans.jsonl logs when the mod raised a too-short expected_duration_s (#696) to cover the first burn itself; null when it didn't.</summary>
        public string DurationRaised;
        /// <summary>A landing aimed at another biome than the Mission Goal names (#699); null when none is.</summary>
        public GoalBiomeMiss BiomeMiss;

        /// <summary>The Script Check refused the script.</summary>
        public bool ScriptFailed
        {
            get { return ScriptProblems.Count > 0; }
        }

        /// <summary>Some figure of the plan's own is wrong by the snapshot, or its landing misses the goal's biome.</summary>
        public bool FiguresWrong
        {
            get { return Crossings.Count > 0 || BiomeMiss != null; }
        }

        /// <summary>The figures found wrong, without the Script Check's problems: what a correction for them alone would carry.</summary>
        public PlanFindings FiguresOnly()
        {
            var copy = (PlanFindings)MemberwiseClone();
            copy.ScriptProblems = new List<string>();
            return copy;
        }

        /// <summary>What plans.jsonl logs for a plan offered with its figures still wrong (after the one correction).</summary>
        public IList<string> Warnings()
        {
            var warnings = Crossings.Select(c => MissionPrompts.PredictedCrossingWarning(c, StartDeltaV, ExpectedDeltaV)).ToList();
            if (DurationRaised != null) warnings.Add(DurationRaised);
            if (BiomeMiss != null) warnings.Add("landing biome: " + MissionPrompts.GoalBiomeMissed(BiomeMiss));
            return warnings;
        }
    }
}
