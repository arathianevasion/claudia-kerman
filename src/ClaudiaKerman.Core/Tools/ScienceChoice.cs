// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>One experiment part as the <c>science</c> block sees it before it acts.</summary>
    public sealed class SciencePart
    {
        /// <summary>The part is in the block's <c>experiments</c> (always, under "all_useful").</summary>
        public bool Named = true;
        public bool HasData;
        public bool Inoperable;
        public bool Rerunnable = true;
        /// <summary>Sending its data would be worth less than the data in hand.</summary>
        public bool LosesValue;
    }

    /// <summary>One experiment, every part of it on the craft, and where the craft is.</summary>
    public sealed class ScienceAsk
    {
        /// <summary>"keep", "transmit" or "auto".</summary>
        public string Mode = "auto";
        /// <summary>The block was given a list of experiments, not "all_useful".</summary>
        public bool Explicit;
        /// <summary>The experiment can run in this situation.</summary>
        public bool Available = true;
        /// <summary>The game tracks science (not Sandbox).</summary>
        public bool Tracked = true;
        /// <summary>Science still to gain here.</summary>
        public double Left;
        public bool Returning;
        /// <summary>An antenna with a link home.</summary>
        public bool CanSend;
        public double EcPct = 100;
        public double MinEcPct;
        public IList<SciencePart> Parts = new List<SciencePart>();
    }

    /// <summary>What the block does with one part: <c>Do</c> is "skip", "hold" (keep the data it holds), "send" (send held data, no new run), "run" or "free" (send held data, then run).</summary>
    public sealed class ScienceStep
    {
        public string Do = "skip";
        /// <summary>For "skip" and "hold": why.</summary>
        public string Why = "";
        /// <summary>For "run" and "free": "transmit" or "keep: &lt;why&gt;" for the fresh data.</summary>
        public string Then = "";
    }

    /// <summary>The <c>science</c> block's part choice and transmit decision (#1297, ADR 0014). Pure.</summary>
    public static class ScienceChoice
    {
        /// <summary>"run" or "skip: &lt;why&gt;" for one part (science_decision).</summary>
        public static string Decision(bool isExplicit, bool rerunnable, bool hasData, bool inoperable, bool available, bool tracked, double left)
        {
            if (inoperable) return "skip: inoperable";
            if (hasData)
                return rerunnable ? "skip: holds data, so it was not overwritten" : "skip: holds data and is not rerunnable, so it was not overwritten";
            if (!available) return "skip: nothing to measure in this situation";
            if (!isExplicit && tracked && left <= 0.05) return "skip: no science left here";
            return "run";
        }

        /// <summary>"transmit" or "keep: &lt;why&gt;" for fresh data (science_transmit_choice).</summary>
        public static string TransmitChoice(string mode, bool returning, bool losesValue, bool canSend, double ecPct, double minEcPct)
        {
            if (mode == "keep") return "keep: transmit is 'keep'";
            if (mode == "auto" && returning && losesValue) return "keep: the craft is returning and transmitting would lose value";
            if (!canSend) return "keep: no antenna or no link";
            if (ecPct < minEcPct) return "keep: electric charge is under min_ec_pct";
            return "transmit";
        }

        /// <summary>"" or the choice for data a part already holds (science_held_choice).</summary>
        public static string HeldChoice(bool hasData, bool inoperable, string mode, bool returning, bool losesValue, bool canSend, double ecPct, double minEcPct)
        {
            if (!hasData || inoperable || mode == "keep") return "";
            return TransmitChoice(mode, returning, losesValue, canSend, ecPct, minEcPct);
        }

        /// <summary>"" or why the experiment got no reading for want of a free part.</summary>
        public static string Missed(ScienceAsk ask)
        {
            return "";
        }

        /// <summary>One step per part of the experiment, in the order given.</summary>
        public static IList<ScienceStep> Choose(ScienceAsk ask)
        {
            var steps = new List<ScienceStep>();
            foreach (var p in ask.Parts)
            {
                if (!p.Named)
                {
                    steps.Add(new ScienceStep { Why = "not in experiments" });
                    continue;
                }
                var held = HeldChoice(p.HasData, p.Inoperable, ask.Mode, ask.Returning, p.LosesValue, ask.CanSend, ask.EcPct, ask.MinEcPct);
                if (held == "transmit") steps.Add(new ScienceStep { Do = "send" });
                else if (held != "") steps.Add(new ScienceStep { Do = "hold", Why = held });
                else
                {
                    var decision = Decision(ask.Explicit, p.Rerunnable, p.HasData, p.Inoperable, ask.Available, ask.Tracked, ask.Left);
                    if (decision == "run")
                        steps.Add(new ScienceStep { Do = "run", Then = TransmitChoice(ask.Mode, ask.Returning, p.LosesValue, ask.CanSend, ask.EcPct, ask.MinEcPct) });
                    else steps.Add(new ScienceStep { Why = decision.Substring(6) });
                }
            }
            return steps;
        }
    }
}
