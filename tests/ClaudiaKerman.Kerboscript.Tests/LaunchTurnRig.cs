// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using kOS.Safe;
using kOS.Safe.Function;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// <c>test_launch_pitch(input)</c>: Core's <see cref="LaunchTurn.Call"/> as <c>ADDONS:CLAUDIA:LAUNCHPITCH</c> answers it in the game (#978),
    /// for launch_ascent_sim.ks's climbs on kOS. Registered through <see cref="KosHarness.Register"/>.
    /// </summary>
    internal static class LaunchTurnRigFunctions
    {
        [Function("test_launch_pitch")]
        public class FunctionPitch : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var input = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(LaunchTurn.Call(input));
            }
        }
    }
}
