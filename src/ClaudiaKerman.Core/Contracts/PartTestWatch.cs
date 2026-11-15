// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1029: the tested part as the craft has it now, read by the KOS addon.</summary>
    public sealed class TestPart
    {
        /// <summary>kOS's PART:UID of the part.</summary>
        public string Uid = "";
        /// <summary>Its Run Test event is offered now.</summary>
        public bool CanRun;
        /// <summary>It fires with the next stage.</summary>
        public bool CanStage;
        /// <summary>A parachute that is no longer stowed: reentry has opened it.</summary>
        public bool ChuteOpen;
    }

    /// <summary>#1029: the craft at one tick of a block, for <see cref="PartTestWatch.Tick"/>.</summary>
    public sealed class TestCraft
    {
        public string Body = "";
        /// <summary>Height above sea level, m.</summary>
        public double Altitude;
        /// <summary>Surface speed, m/s.</summary>
        public double Speed;
        /// <summary>The game's situation: FLYING, SUB_ORBITAL, LANDED, SPLASHED, ...</summary>
        public string Situation = "";
        /// <summary>The parts tested that are aboard, by part name (the first of each name).</summary>
        public Dictionary<string, TestPart> Parts = new Dictionary<string, TestPart>();
    }

    /// <summary>#1029: what the block does this tick: "run" the part's Run Test (<see cref="Uid"/>), "stage", or "" nothing.</summary>
    public sealed class TestSignal
    {
        public string How = "";
        public string Uid = "";
        /// <summary>The Requirement's key; "" when nothing is signalled.</summary>
        public string Key = "";
        /// <summary>For the run's events: "ran the test of X at 9,000 m, 250 m/s", and each window passed unrun this tick; empty when nothing happened.</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// #1029: Core's timing of the Mission's planned part tests (ADR 0021, ADR 0014: Core decides, the block stages or runs the test). Each
    /// tick of <c>launch</c> or <c>reentry</c> it is told the craft; a test whose window that block flies is signalled at the first tick every
    /// condition holds at once: the body, the situation, the altitude band and the speed band, the part aboard. A chute's test (ADR 0019)
    /// waits for reentry to open the chute at its own timing and is then run on it; it is never staged. A pass through the altitude band
    /// that leaves it unrun is the test's gap, with why: "speed 460 m/s, above the band, through 8–12 km".
    /// </summary>
    public sealed class PartTestWatch
    {
        private sealed class Pass
        {
            public double Lowest = double.PositiveInfinity;
            public double Highest = double.NegativeInfinity;
            public string Situation = "";
            public bool Missing;
            public bool ChuteShut;
            public bool CantAct;
        }

        private readonly object _gate = new object();
        private readonly string _home;
        private List<ChosenRequirement> _tests = new List<ChosenRequirement>();
        private readonly HashSet<string> _ran = new HashSet<string>();
        private readonly Dictionary<string, string> _gaps = new Dictionary<string, string>();
        private readonly Dictionary<string, Pass> _passes = new Dictionary<string, Pass>();

        public PartTestWatch(IEnumerable<ChosenRequirement> chosen, string home)
        {
            _home = home;
            Use(chosen);
        }

        /// <summary>The Mission's chosen Requirements changed (a revised plan): what was run and missed is kept.</summary>
        public void Use(IEnumerable<ChosenRequirement> chosen)
        {
            lock (_gate) _tests = chosen.Where(r => r.Test != null && !r.Test.Haul && r.Test.Window != TestWindow.None).ToList();
        }

        /// <summary>There is a test left to run in <paramref name="block"/>: the block need not read the craft otherwise.</summary>
        public bool Pending(string block)
        {
            lock (_gate) return _tests.Any(t => !_ran.Contains(t.Key) && BlockOf(t.Test.Window) == block);
        }

        /// <summary>The block's one action this tick, if any.</summary>
        public TestSignal Tick(string block, TestCraft craft)
        {
            lock (_gate) return TickLocked(block, craft);
        }

        private TestSignal TickLocked(string block, TestCraft craft)
        {
            var signal = new TestSignal();
            foreach (var requirement in _tests.Where(t => !_ran.Contains(t.Key) && BlockOf(t.Test.Window) == block))
            {
                var test = requirement.Test;
                TestPart part;
                craft.Parts.TryGetValue(test.Part, out part);
                if (craft.Body != _home) continue;
                var inBand = test.Band == null ? craft.Situation == test.Situation : test.Band.HoldsAltitude(craft.Altitude);
                if (!inBand)
                {
                    var gap = Left(requirement);
                    if (gap != null) signal.Notes.Add("the window for \"" + requirement.Words + "\" passed unrun: " + gap);
                    continue;
                }
                var holds = craft.Situation == test.Situation && (test.Band == null || test.Band.HoldsSpeed(craft.Speed)) && part != null;
                var how = part == null ? "" : test.Chute ? (part.ChuteOpen && part.CanRun ? "run" : "") : part.CanRun ? "run" : part.CanStage ? "stage" : "";
                if (holds && how.Length > 0 && signal.How.Length == 0)
                {
                    _ran.Add(requirement.Key);
                    _gaps.Remove(requirement.Key);
                    _passes.Remove(requirement.Key);
                    signal.How = how;
                    signal.Uid = part.Uid;
                    signal.Key = requirement.Key;
                    signal.Notes.Add("ran the test of " + test.Part + " (" + how + ") at " + Whole(craft.Altitude) + " m, " + Whole(craft.Speed) + " m/s");
                    continue;
                }
                Pass pass;
                if (!_passes.TryGetValue(requirement.Key, out pass)) _passes[requirement.Key] = pass = new Pass();
                if (part == null) pass.Missing = true;
                else if (craft.Situation != test.Situation) pass.Situation = craft.Situation;
                else
                {
                    pass.Lowest = Math.Min(pass.Lowest, craft.Speed);
                    pass.Highest = Math.Max(pass.Highest, craft.Speed);
                    if (holds && test.Chute) pass.ChuteShut = true;
                    else if (holds) pass.CantAct = true;
                }
            }
            return signal;
        }

        /// <summary>The craft left the band (or the situation) with the test unrun: the pass's gap.</summary>
        private string Left(ChosenRequirement requirement)
        {
            Pass pass;
            if (!_passes.TryGetValue(requirement.Key, out pass)) return null;
            _passes.Remove(requirement.Key);
            var test = requirement.Test;
            var through = test.Band == null ? test.Situation.ToLowerInvariant() : "through " + test.Band.Through();
            var gap = pass.Missing ? "the part " + test.Part + " wasn't aboard " + through
                : pass.Situation.Length > 0 && double.IsInfinity(pass.Lowest) ? "situation " + pass.Situation + ", not " + test.Situation + ", " + through
                : test.Band != null && test.Band.SpeedMiss(pass.Lowest, pass.Highest).Length > 0 ? test.Band.SpeedMiss(pass.Lowest, pass.Highest)
                : pass.ChuteShut ? "the test chute wasn't open " + through
                : pass.CantAct ? "the part had no Run Test and wasn't the next stage " + through
                : "the conditions never held at once " + through;
            _gaps[requirement.Key] = gap;
            return gap;
        }

        public bool Ran(string key)
        {
            lock (_gate) return _ran.Contains(key);
        }

        /// <summary>Why the test's window passed unrun, from the last pass; null when none has.</summary>
        public string Gap(string key)
        {
            string gap;
            lock (_gate) return _gaps.TryGetValue(key, out gap) ? gap : null;
        }

        private static string BlockOf(TestWindow window)
        {
            return window == TestWindow.Ascent ? "launch" : window == TestWindow.Descent ? "reentry" : "";
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
