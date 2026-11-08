// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Kos
{
    /// <summary>The latest logged result of one block test: pass or fail, the UTC date of the run and, for a FAIL, the headline's reason.</summary>
    public sealed class BlockTestLogEntry
    {
        public BlockTestLogEntry(bool passed, string date, string reason)
        {
            Passed = passed;
            Date = date;
            Reason = reason;
        }

        public bool Passed { get; private set; }
        /// <summary>The run's UTC date, <c>yyyy-MM-dd</c>.</summary>
        public string Date { get; private set; }
        /// <summary>The text after <c>FAIL &lt;test&gt;:</c> in the headline; empty for a PASS or a FAIL with none.</summary>
        public string Reason { get; private set; }
    }

    /// <summary>
    /// Reads <c>saves/claudia_block_tests/block-tests.log</c>, which the debug block test runner appends to: one headline line
    /// per run, <c>yyyy-MM-dd HH:mm:ssZ PASS &lt;test&gt;</c> or <c>yyyy-MM-dd HH:mm:ssZ FAIL &lt;test&gt;: &lt;reason&gt;</c>, followed by
    /// the run's detail lines indented four spaces. The last headline for a test wins; a line that is not a headline is skipped.
    /// </summary>
    public static class BlockTestLog
    {
        private const string StampFormat = "yyyy-MM-dd HH:mm:ss'Z'";

        public static Dictionary<string, BlockTestLogEntry> Latest(string logText)
        {
            var latest = new Dictionary<string, BlockTestLogEntry>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(logText)) return latest;
            foreach (var raw in logText.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                // "yyyy-MM-dd HH:mm:ssZ " is 21 characters, then the verdict word.
                if (line.Length < 26 || line[20] != ' ') continue;
                DateTime stamp;
                if (!DateTime.TryParseExact(line.Substring(0, 20), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp)) continue;
                var rest = line.Substring(21);
                bool passed;
                if (rest.StartsWith("PASS ", StringComparison.Ordinal)) passed = true;
                else if (rest.StartsWith("FAIL ", StringComparison.Ordinal)) passed = false;
                else continue;
                var body = rest.Substring(5);
                string name = body, reason = "";
                var colon = body.IndexOf(": ", StringComparison.Ordinal);
                if (colon >= 0)
                {
                    name = body.Substring(0, colon);
                    reason = body.Substring(colon + 2).Trim();
                }
                name = name.Trim();
                if (name.Length == 0 || name.IndexOf(' ') >= 0) continue;
                latest[name] = new BlockTestLogEntry(passed, stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), passed ? "" : reason);
            }
            return latest;
        }
    }
}
