// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #1029: the block side of a part test. <c>claudia_tick</c> asks Core's PARTTEST each tick of <c>launch</c> and <c>reentry</c> only;
    /// at its signal <c>claudia_part_test</c> runs the part's own Run Test on the part Core names (a chute's included, never a stage), or
    /// stages. The real library's function, lifted and run on kOS.Safe with the game faked.
    /// </summary>
    public class PartTestActionTests
    {
        private static string Lifted(string name)
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source.Replace("\r\n", "\n");
            var start = source.IndexOf("function " + name + " {", StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            return source.Substring(start, source.IndexOf("\n}\n", start, StringComparison.Ordinal) + 3 - start);
        }

        private const string Rig =
            "local ran is list().\n"
            + "local asked is list().\n"
            + "function part {\n parameter uid.\n"
            + "  local m is lexicon(\"doevent\", { parameter e. ran:add(uid + \":\" + e). }).\n"
            + "  return lexicon(\"uid\", uid, \"getmodule\", { parameter n. return m. }).\n}\n"
            + "global ship is lexicon(\"parts\", list(part(\"7\"), part(\"42\"))).\n"
            + "local answer is lexicon(\"how\", \"\", \"uid\", \"\").\n"
            + "global addons is lexicon(\"claudia\", lexicon(\"parttest\", { parameter b. asked:add(b). return answer. })).\n"
            + "function claudia_restart_clock { parameter ctx. }\n"
            + "local ctx is lexicon(\"block\", \"launch\", \"parts\", 2).\n";

        /// <summary>The harness has no game clock, so the half-second settle (parts leaving, as autostage waits) is lifted out; the rest runs as written.</summary>
        private static string[] Run(string script)
        {
            var body = Lifted("claudia_part_test");
            Assert.Contains("wait 0.5. ", body);
            return KosHarness.Run(CoreLibrary.Bundle(Rig + body.Replace("wait 0.5. ", "") + script)).ToArray();
        }

        [Fact]
        public void At_Cores_run_signal_the_named_part_gets_its_Run_Test_event()
        {
            Assert.Equal(new[] { "42:run test", "launch", "2" },
                Run("set answer to lexicon(\"how\", \"run\", \"uid\", \"42\").\nclaudia_part_test(ctx).\nprint ran:join(\",\"). print asked:join(\",\"). print ctx:parts.\n"));
        }

        [Fact]
        public void With_nothing_due_nothing_is_done()
        {
            Assert.Equal(new[] { "", "2" }, Run("claudia_part_test(ctx).\nprint ran:join(\",\"). print ctx:parts.\n"));
        }

        [Fact]
        public void The_tick_asks_only_in_launch_and_reentry()
        {
            var tick = CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source;
            Assert.Contains("if ctx:block = \"launch\" or ctx:block = \"reentry\" { claudia_part_test(ctx). }", tick);
        }
    }
}
