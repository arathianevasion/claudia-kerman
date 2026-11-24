// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Tools
{
    public static partial class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:SCIENCECHOICE</c>, for <c>science</c> (#1297, ADR 0014; was the library's <c>science_decision</c> and its transmit choices):
        /// <see cref="ScienceChoice.Choose"/> for one experiment, from <c>mode</c>, <c>explicit</c>, <c>available</c>, <c>tracked</c>, <c>left</c>,
        /// <c>can_send</c>, <c>ec_pct</c>, <c>min_ec_pct</c> and <c>parts</c>, a list of lexicons <c>named</c>, <c>has_data</c>, <c>inoperable</c>,
        /// <c>rerunnable</c>. Gives <c>error</c> "", <c>missed</c> ("" or why no part was free) and <c>steps</c>, one lexicon per part in order:
        /// <c>do</c>, <c>why</c>, <c>then</c>. <paramref name="failed"/>: the addon's answer to a call that threw, every part skipped. Pure.
        /// </summary>
        public static IDictionary<string, object> ScienceChoiceOf(IDictionary<string, object> input, bool failed = false)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            object mode, partsValue;
            ask.TryGetValue("mode", out mode);
            ask.TryGetValue("parts", out partsValue);
            var parts = (partsValue as IList<object> ?? new List<object>()).Select(p =>
            {
                var part = new Dictionary<string, object>(p as IDictionary<string, object> ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                return new SciencePart { Named = Flag(part, "named", true), HasData = Flag(part, "has_data", false), Inoperable = Flag(part, "inoperable", false), Rerunnable = Flag(part, "rerunnable", true) };
            }).ToList();
            var science = new ScienceAsk
            {
                Mode = mode as string ?? "auto",
                Explicit = Flag(ask, "explicit", false),
                Available = Flag(ask, "available", true),
                Tracked = Flag(ask, "tracked", true),
                Left = Number(ask, "left", 0),
                CanSend = Flag(ask, "can_send", false),
                EcPct = Number(ask, "ec_pct", 100),
                MinEcPct = Number(ask, "min_ec_pct", 0),
                Parts = parts,
            };
            var steps = failed ? parts.Select(p => new ScienceStep { Why = "the choice failed" }).ToList() : ScienceChoice.Choose(science);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "missed", failed ? "" : ScienceChoice.Missed(science) },
                { "steps", steps.Select(s => (object)new Dictionary<string, object> { { "do", s.Do }, { "why", s.Why }, { "then", s.Then } }).ToList() },
            };
        }

        private static bool Flag(IDictionary<string, object> ask, string key, bool fallback)
        {
            object value;
            return ask.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }
    }
}
