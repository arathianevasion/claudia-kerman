// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// What the checks before Approval found in an offered Sequence Plan: the Script Check's problems (ADR 0003), and the
    /// plan's own figures that are wrong by the Game Snapshot (#674's predicted crossings, #696's duration short of its first
    /// burn). Any of them sends the plan back in the one automatic correction; a check of the plan's figures adds a field here,
    /// its line in <see cref="MissionPrompts.PlanCheckFailed"/> and <see cref="MissionPrompts.PlanFixNotice"/>, and its warning.
    /// </summary>
    public sealed class PlanFindings
    {
        public IList<string> ScriptProblems = new List<string>();
        /// <summary>Delta-v left checks the plan's own expected_delta_v is certain to cross (#674), from <see cref="StartDeltaV"/>.</summary>
        public IList<PredictedCrossing> Crossings = new List<PredictedCrossing>();
        public double StartDeltaV;
        public double ExpectedDeltaV;
        /// <summary>The first burn expected_duration_s doesn't cover (#696); null when it does or none is known.</summary>
        public BurnWait ShortDuration;
        public double ExpectedDurationSeconds;

        public bool ScriptFailed
        {
            get { return ScriptProblems.Count > 0; }
        }

        /// <summary>Some figure of the plan's own is wrong by the snapshot.</summary>
        public bool FiguresWrong
        {
            get { return Crossings.Count > 0 || ShortDuration != null; }
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
            if (ShortDuration != null) warnings.Add(MissionPrompts.ShortDurationWarning(ShortDuration, ExpectedDurationSeconds));
            return warnings;
        }
    }
}
