// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>The kOS behaviour the Core Library's bundling relies on, checked on kOS.Safe itself.</summary>
    public class HarnessTests
    {
        [Fact]
        public void Runs_a_script_and_captures_what_it_prints()
        {
            var printed = KosHarness.Run("print 1 + 2. local l is lexicon(\"a\", 1). print l:haskey(\"a\").");
            Assert.Equal(new[] { "3", "True" }, printed);
        }

        [Fact]
        public void A_function_declared_further_down_the_file_can_be_called_from_the_top()
        {
            // The bundle puts the Mission Script first and the Core Library after it.
            var printed = KosHarness.Run("@lazyglobal off.\nprint later(2).\nfunction later { parameter x. return x * 10. }");
            Assert.Equal(new[] { "20" }, printed);
        }

        [Fact]
        public void A_lazyglobal_directive_further_down_the_file_is_rejected()
        {
            // So the bundle drops the Core Library files' own @lazyglobal lines.
            Assert.Contains("LAZYGLOBAL", KosHarness.CompileError("@lazyglobal off.\nprint 1.\n@lazyglobal off.\nprint 2."));
        }

        [Fact]
        public void A_bundle_compiles_only_its_Mission_Script_once_the_Core_Library_is_compiled()
        {
            // Test classes run in parallel, and a rig registering its functions meanwhile rightly makes the next bundle recompile it.
            var registrations = KosHarness.Registrations;
            KosHarness.Run(CoreLibrary.Bundle("print claudia_warp_end(5400, 30)."));
            var compiles = KosHarness.LibraryCompiles;
            Assert.Equal(new[] { "5370", "7" }, KosHarness.Run(CoreLibrary.Bundle("local x is 7.\nprint claudia_warp_end(5400, 30).\nprint x.")));
            Assert.True(KosHarness.LibraryCompiles == compiles || KosHarness.Registrations > registrations);
        }

        [Fact]
        public void The_Core_Library_runs_under_the_Mission_Scripts_own_directives()
        {
            // As in a bundle, where the Mission Script's @lazyglobal line is the only one and covers the library too.
            Assert.Equal(new[] { "5370" }, KosHarness.Run(CoreLibrary.Bundle("@lazyglobal off.\nprint claudia_warp_end(5400, 30).")));
            Assert.Equal(new[] { "5370" }, KosHarness.Run(CoreLibrary.Bundle("// a comment first\n@lazyglobal off. @clobberbuiltins on.\nprint claudia_warp_end(5400, 30).")));
        }

        [Fact]
        public void A_Mission_Script_function_with_a_library_blocks_name_wins_as_it_does_in_a_bundle()
        {
            Assert.Equal(new[] { "1" }, KosHarness.Run(CoreLibrary.Bundle(
                "function claudia_warp_end { parameter a, b. return 1. }\nprint claudia_warp_end(5400, 30).")));
        }

        [Fact]
        public void A_compile_error_is_reported()
        {
            Assert.NotNull(KosHarness.CompileError("print (1 + ."));
        }
    }
}
