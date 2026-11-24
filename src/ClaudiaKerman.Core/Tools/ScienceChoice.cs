// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

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
        /// <summary>Under this, data is left as it is: too little science left here to count.</summary>
        public const double LeftFloor = 0.05;

        /// <summary>
        /// One step per part of the experiment, in the order given. At most one part runs: a free one (holding no data) first, its data
        /// kept aboard unless <c>Mode</c> is "transmit". With none free and a reading needed here, "auto" and "transmit" send one
        /// rerunnable part's held data to free it, given a link and the charge, then run it ("free"); "transmit" also sends every other
        /// part's held data ("send"). A reading is needed when the experiment was named, when science is left here, or in Sandbox
        /// when no part of it holds data. When none can be freed every part holding data says "no free part: &lt;why&gt;" (<see cref="Missed"/>).
        /// </summary>
        public static IList<ScienceStep> Choose(ScienceAsk ask)
        {
            var parts = ask.Parts ?? new List<SciencePart>();
            var steps = new List<ScienceStep>();
            var cantSend = !ask.CanSend ? "no antenna or no link" : ask.EcPct < ask.MinEcPct ? "electric charge is under min_ec_pct" : "";
            foreach (var p in parts)
            {
                if (!p.Named) steps.Add(new ScienceStep { Why = "not in experiments" });
                else if (p.Inoperable) steps.Add(new ScienceStep { Why = "inoperable" });
                else if (p.HasData && ask.Mode == "transmit") steps.Add(cantSend == "" ? new ScienceStep { Do = "send" } : new ScienceStep { Do = "hold", Why = "keep: " + cantSend });
                else if (p.HasData) steps.Add(new ScienceStep { Why = p.Rerunnable ? "holds data, so it was not overwritten" : "holds data and is not rerunnable, so it was not overwritten" });
                else steps.Add(new ScienceStep { Why = !ask.Available ? "nothing to measure in this situation" : Needed(ask) ? "one reading per experiment; another part took it" : "no science left here" });
            }
            if (!ask.Available || !Needed(ask)) return steps;
            var then = ask.Mode == "keep" ? "keep: transmit is 'keep'" : ask.Mode == "transmit" ? (cantSend == "" ? "transmit" : "keep: " + cantSend) : "keep: kept aboard; sent only to free a part";
            for (var i = 0; i < parts.Count; i++)
                if (Usable(parts[i]) && !parts[i].HasData)
                {
                    steps[i] = new ScienceStep { Do = "run", Then = then };
                    return steps;
                }
            var missed = Missed(ask);
            for (var i = 0; i < parts.Count; i++)
                if (Usable(parts[i]) && parts[i].Rerunnable && missed == "")
                {
                    steps[i] = new ScienceStep { Do = "free", Then = then };
                    return steps;
                }
            for (var i = 0; i < parts.Count; i++)
                if (Usable(parts[i]) && steps[i].Do == "skip") steps[i].Why = "no free part: " + missed;
            return steps;
        }

        /// <summary>
        /// "" or why the experiment gets no reading for want of a free part: every part it may use holds data and none can be freed
        /// ("transmit is 'keep'", "no antenna or no link", "electric charge is under min_ec_pct" or "every part holds data and none is
        /// rerunnable"). "" too when no reading is needed here or it can't run in this situation.
        /// </summary>
        public static string Missed(ScienceAsk ask)
        {
            var parts = ask.Parts ?? new List<SciencePart>();
            if (!ask.Available || !Needed(ask) || !parts.Any(Usable) || parts.Any(p => Usable(p) && !p.HasData)) return "";
            if (ask.Mode == "keep") return "transmit is 'keep'";
            if (!parts.Any(p => Usable(p) && p.Rerunnable)) return "every part holds data and none is rerunnable";
            if (!ask.CanSend) return "no antenna or no link";
            if (ask.EcPct < ask.MinEcPct) return "electric charge is under min_ec_pct";
            return "";
        }

        private static bool Usable(SciencePart p)
        {
            return p.Named && !p.Inoperable;
        }

        private static bool Needed(ScienceAsk ask)
        {
            if (ask.Explicit) return true;
            if (ask.Tracked) return ask.Left > LeftFloor;
            return !(ask.Parts ?? new List<SciencePart>()).Any(p => Usable(p) && p.HasData);
        }
    }
}
