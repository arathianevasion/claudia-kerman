// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tests;
using ClaudiaKerman.Core.Tools;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// transfer_to flown in the kOS harness (#459): the block's own loop, transfer_to_run, with TRANSFERSTEP answered by the
    /// real <see cref="BlockCalls.TransferStep"/> on <see cref="TransferRig"/>'s scripted universe, each node flown as an
    /// impulse and each wait moved through on the patched conics. Flying it in KSP is tests/blocks/transfer_to,
    /// transfer_to_duna and transfer_to_minmus_from_mun.
    /// </summary>
    public class TransferToFlightTests
    {
        static TransferToFlightTests()
        {
            KosHarness.Register(typeof(TransferToRigFunctions));
        }

        private static string[] Fly(string target, double pe, string window = "\"next\"")
        {
            var script = "local extra is lexicon(\"encounter_ut\", -1, \"target_pe_actual\", -1, \"correction_dv\", 0, \"plane_dv\", 0, \"ejection_wait\", -1,"
                + " \"ejection_node_dv\", -1, \"ejection_dv_used\", -1, \"ejection_burn_time\", -1, \"departure_ut\", -1, \"arrival_ut\", -1, \"v_inf\", -1).\n"
                + "local opt is lexicon(\"max_duration\", \"auto\").\n"
                + "local state is lexicon(\"target\", \"" + target + "\", \"target_pe\", " + pe.ToString(CultureInfo.InvariantCulture) + ", \"window\", " + window
                + ", \"correction\", true, \"pe_tolerance\", 1000, \"start_ut\", 0, \"max_duration\", opt:max_duration, \"auto\", true, \"turn_time\", 60, \"phase\", \"start\").\n"
                + "local reason is transfer_to_run(state, opt, extra, { parameter s. return test_tr_ask(s). }, { parameter step. return test_tr_fly(step). },"
                + " { parameter ut. return test_tr_wait(ut). }, { return \"\". }).\n"
                + "print reason. print extra:target_pe_actual. print extra:correction_dv. print extra:ejection_dv_used. print extra:departure_ut."
                + " print extra:encounter_ut. print extra:v_inf. if extra:haskey(\"next\") { print extra:next. } else { print \"\". } print opt:max_duration > 0. print extra:ejection_wait.";
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        private static double N(string text)
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Kerbin_to_Duna_flies_end_to_end_to_the_asked_periapsis()
        {
            TransferToRig.Rig = TransferRig.Circular("Kerbin", 100000, 0);
            TransferToRig.Rig.Residual = 0.05;
            var printed = Fly("Duna", 50000);
            Assert.Equal("", printed[0]);
            Assert.InRange(N(printed[1]), 49000, 51000);
            // The heliocentric correction for an ejection flown to its node, every burn 0.05 m/s short.
            Assert.InRange(N(printed[2]), 0, 20);
            Assert.InRange(N(printed[3]), 1030 * 0.95, 1030 * 1.07);
            Assert.InRange(N(printed[4]) / 21600, 220, 245);
            Assert.True(N(printed[5]) > N(printed[4]));
            Assert.True(N(printed[6]) > 0);
            Assert.Equal("coast to encounter_ut + 60 before a capture", printed[7]);
            Assert.Equal("True", printed[8]);
        }

        [Fact]
        public void The_Mun_to_Minmus_flies_end_to_end_to_the_asked_periapsis()
        {
            TransferToRig.Rig = TransferRig.Circular("Mun", 30000, 0);
            TransferToRig.Rig.Residual = 0.05;
            var printed = Fly("Minmus", 30000);
            Assert.Equal("", printed[0]);
            Assert.InRange(N(printed[1]), 29000, 31000);
            Assert.InRange(N(printed[3]), 200, 350);
            Assert.Contains(TransferToRig.Rig.Body.Name, new[] { "Kerbin", "Minmus" });
        }

        [Fact]
        public void Kerbin_to_the_Mun_flies_as_before()
        {
            TransferToRig.Rig = TransferRig.Circular("Kerbin", 100000, 0);
            var printed = Fly("Mun", 30000);
            Assert.Equal("", printed[0]);
            Assert.InRange(N(printed[1]), 29000, 31000);
            Assert.InRange(N(printed[3]), 800, 900);
            Assert.InRange(N(printed[9]), 0, 2300);
            Assert.Equal("Kerbin", TransferToRig.Rig.Body.Name);
        }

        [Fact]
        public void A_failed_step_is_the_block_s_reason_with_its_report()
        {
            TransferToRig.Rig = TransferRig.Circular("Kerbin", 100000, 0);
            Assert.Equal("Ike does not orbit Kerbin, the body the craft orbits, or Sun", Fly("Ike", 30000)[0]);
        }

        [Fact]
        public void A_burn_that_fails_ends_the_block_with_exec_node_s_reason()
        {
            TransferToRig.Rig = TransferRig.Circular("Kerbin", 100000, 0);
            TransferToRig.FailWith = "out of fuel";
            try
            {
                var printed = Fly("Mun", 30000);
                Assert.Equal("out of fuel", printed[0]);
                Assert.Equal("-1", printed[1]);
            }
            finally
            {
                TransferToRig.FailWith = null;
            }
        }

        [Fact]
        public void A_call_that_threw_ends_the_block_with_its_error()
        {
            // ClaudiaAddon.Guarded's lexicon for a throw: only error (#439).
            var printed = KosHarness.Run(CoreLibrary.Bundle(
                "print transfer_to_run(lexicon(), lexicon(), lexicon(), { parameter s. return lexicon(\"error\", \"TRANSFERSTEP failed: boom\"). }, 0, 0, 0)."));
            Assert.Equal("TRANSFERSTEP failed: boom", printed.Single());
        }
    }

    /// <summary>The rig the kOS functions fly on, and a failure to give every burn.</summary>
    internal static class TransferToRig
    {
        public static TransferRig Rig;
        public static string FailWith;
    }

    /// <summary>The rig's kOS functions: TRANSFERSTEP on the rig now, a flight, a wait.</summary>
    internal static class TransferToRigFunctions
    {
        [Function("test_tr_ask")]
        public class FunctionAsk : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var input = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(BlockCalls.TransferStep(input, TransferToRig.Rig.Snapshot()));
            }
        }

        [Function("test_tr_fly")]
        public class FunctionFly : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var step = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                var failed = TransferToRig.FailWith != null;
                var dv = failed ? 0 : TransferToRig.Rig.Fly((string)step["kind"], (double)step["ut"], (double)step["prograde"], (double)step["normal"], (double)step["radial"]);
                ReturnValue = KosPlain.From(new Dictionary<string, object>
                {
                    { "success", !failed }, { "reason", TransferToRig.FailWith ?? "" }, { "dv_used", dv }, { "node_dv", dv }, { "burn_time", 10.0 },
                });
            }
        }

        [Function("test_tr_wait")]
        public class FunctionWait : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var ut = PopValueAssert(shared, true);
                AssertArgBottomAndConsume(shared);
                TransferToRig.Rig.MoveTo(System.Convert.ToDouble(ut, CultureInfo.InvariantCulture));
                ReturnValue = new StringValue("");
            }
        }
    }
}
