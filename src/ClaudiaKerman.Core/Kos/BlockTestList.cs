// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Kos
{
    /// <summary>
    /// #1361: what the block test runner window lists. The tab a test is on follows its latest result in block-tests.log (0 Unvalidated,
    /// 1 Failed, 2 Passed); the filter box keeps the tests whose names contain the typed text; the test run last is listed first, and the
    /// window opens on its tab.
    /// </summary>
    public static class BlockTestList
    {
        public const int Unvalidated = 0, Failed = 1, Passed = 2;

        public static int TabOf(string test, IDictionary<string, BlockTestLogEntry> results)
        {
            BlockTestLogEntry entry;
            if (!results.TryGetValue(test, out entry)) return Unvalidated;
            return entry.Passed ? Passed : Failed;
        }

        /// <summary>The tests whose names contain <paramref name="filter"/>, ignoring case and surrounding spaces; all of them for no text.</summary>
        public static IList<string> Filter(IEnumerable<string> tests, string filter)
        {
            var text = (filter ?? "").Trim();
            return tests.Where(t => text.Length == 0 || t.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        /// <summary>The test run last (the latest headline in the log) among <paramref name="tests"/>; null when none has a result.</summary>
        public static string LastRun(IEnumerable<string> tests, IDictionary<string, BlockTestLogEntry> results)
        {
            string last = null;
            var best = 0;
            foreach (var test in tests)
            {
                BlockTestLogEntry entry;
                if (!results.TryGetValue(test, out entry) || entry.Sequence <= best) continue;
                best = entry.Sequence;
                last = test;
            }
            return last;
        }

        /// <summary>The tests on <paramref name="tab"/> that pass the filter: the one run last first, the rest in their given order.</summary>
        public static IList<string> Shown(IEnumerable<string> tests, IDictionary<string, BlockTestLogEntry> results, int tab, string filter)
        {
            var all = tests.ToList();
            var last = LastRun(all, results);
            var onTab = Filter(all, filter).Where(t => TabOf(t, results) == tab).ToList();
            if (last != null && onTab.Remove(last)) onTab.Insert(0, last);
            return onTab;
        }

        /// <summary>The tab the window opens on: that of the test run last, Unvalidated when none has been.</summary>
        public static int OpeningTab(IEnumerable<string> tests, IDictionary<string, BlockTestLogEntry> results)
        {
            var all = tests.ToList();
            var last = LastRun(all, results);
            return last == null ? Unvalidated : TabOf(last, results);
        }
    }
}
