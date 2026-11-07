// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #825: where the Mission Plan guard sits in the three callers of claudia_fire_drop. The comparison is Core's (DropGuardTests); a block
    /// needs a ship, so these read the order of the source, as the other drop tests do.
    /// </summary>
    public class DropGuardScriptTests
    {
        private static string Source(string file)
        {
            return CoreLibrary.Files.Single(f => f.Name == file).Source;
        }

        private static string Function(string file, string header)
        {
            var source = Source(file);
            var start = source.IndexOf(header, StringComparison.Ordinal);
            Assert.True(start >= 0, header);
            return source.Substring(start, source.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
        }

        [Fact]
        public void The_comparison_is_cores_and_the_estimate_is_kerboscripts()
        {
            var body = Function("claudia_lib", "function claudia_drop_refusal {");
            Assert.Contains("addons:claudia:dropcheck(", body);
            Assert.Contains("drop_parts_dv_left(plan)", body);
            Assert.Contains(":reason", body);
        }

        [Fact]
        public void A_drop_is_refused_before_any_decoupler_fires_and_the_reason_goes_back_as_the_failure()
        {
            var body = Function("claudia_lib", "function claudia_fire_drop {");
            var refusal = body.IndexOf("claudia_drop_refusal(plan)", StringComparison.Ordinal);
            Assert.True(refusal > 0);
            Assert.True(refusal < body.IndexOf("claudia_fire_decoupler(", StringComparison.Ordinal));
            Assert.Contains("return refused.", body);
        }

        [Fact]
        public void Drop_parts_fails_with_the_reason_whatever_allow_no_engine_says()
        {
            var source = Source("drop_parts");
            // The block's only way to fire is claudia_fire_drop, whose failure is the block's, and nothing there looks at allow_no_engine.
            Assert.Contains("claudia_finish(ctx, not_fired = \"\", not_fired, extra)", source);
            Assert.Equal(1, source.Split(new[] { "claudia_fire_drop(" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("allow_no_engine", Function("claudia_lib", "function claudia_drop_refusal {"));
            Assert.DoesNotContain("allow_no_engine", Function("claudia_lib", "function claudia_fire_drop {"));
        }

        [Fact]
        public void Deorbit_refuses_a_separate_before_the_search_and_the_burn()
        {
            var body = Function("deorbit", "function deorbit {");
            var refusal = body.IndexOf("claudia_drop_refusal(separation)", StringComparison.Ordinal);
            Assert.True(refusal > 0);
            Assert.True(refusal < body.IndexOf("addons:claudia:deorbitburn(", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("claudia_burn(ctx, nd)", StringComparison.Ordinal));
            Assert.True(body.IndexOf("claudia_burn(ctx, nd)", StringComparison.Ordinal) < body.IndexOf("claudia_fire_drop(ctx, separation)", StringComparison.Ordinal));
            Assert.Contains("\"separate: \" + refused", body);
        }

        [Fact]
        public void Deorbit_checks_the_separate_again_with_the_burns_delta_v_after_the_search_and_before_the_node()
        {
            var body = Function("deorbit", "function deorbit {");
            var late = body.IndexOf("claudia_drop_refusal(separation, sqrt(", StringComparison.Ordinal);
            Assert.True(late > body.IndexOf("addons:claudia:deorbitburn(", StringComparison.Ordinal));
            Assert.True(late > body.IndexOf("return claudia_finish(ctx, false, found:error, extra)", StringComparison.Ordinal));
            Assert.True(late < body.IndexOf("add nd.", StringComparison.Ordinal));
            // A refusal returns before the node and before burn_ut is set, which stays -1; the check after the burn stays.
            Assert.True(late < body.IndexOf("set extra:burn_ut", StringComparison.Ordinal));
            Assert.Contains("\"separate: \" + late", body);
            Assert.Contains("claudia_fire_drop(ctx, separation)", body);
            Assert.Contains("parameter plan, spent is 0.", Function("claudia_lib", "function claudia_drop_refusal {"));
        }

        [Fact]
        public void A_drop_the_plan_does_not_need_flies_as_before()
        {
            // The early check keeps its one argument and the late one only returns on a reason, so a drop the plan can spare goes on to the node.
            var body = Function("deorbit", "function deorbit {");
            Assert.Contains("local refused is claudia_drop_refusal(separation).", body);
            Assert.Contains("if late <> \"\" {", body);
            Assert.Contains("\"spent\", spent", Function("claudia_lib", "function claudia_drop_refusal {"));
        }

        [Fact]
        public void Reentry_takes_a_refusal_as_a_failed_drop_and_flies_on()
        {
            var body = Function("reentry", "function reentry {");
            var fire = body.IndexOf("set drop_error to claudia_fire_drop(ctx, drop)", StringComparison.Ordinal);
            Assert.True(fire > 0);
            // The refusal is drop_error, which ends the block only above the atmosphere (reentry_stack_refusal); the fall otherwise goes on.
            Assert.True(fire < body.IndexOf("local refused is reentry_stack_refusal(", StringComparison.Ordinal));
            Assert.DoesNotContain("claudia_drop_refusal(", body);
        }
    }
}
