// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using kOS.Safe;
using kOS.Safe.Function;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// <c>test_land_steer(input)</c>: Core's <see cref="LandSteer.Tick"/> as <c>ADDONS:CLAUDIA:LANDSTEER</c> answers it in the game
    /// (#865), for the descent simulations on kOS. Registered through <see cref="KosHarness.Register"/>.
    /// </summary>
    internal static class LandSteerRigFunctions
    {
        [Function("test_land_steer")]
        public class FunctionSteer : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var input = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(LandSteer.Tick(input));
            }
        }
    }
}
