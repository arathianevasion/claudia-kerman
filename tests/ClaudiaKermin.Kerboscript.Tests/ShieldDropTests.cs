// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// A decoupler event is only fired on a part still aboard (#558): one event can split the craft, and the runtime gate
    /// refuses a DOEVENT on another vessel's part, which ended Missions mid-reentry (0de6, 44e9). Parts are fakes whose
    /// decouplers log their events and, when fired, move the parts they carry off the "ship" list the library is given.
    /// </summary>
    public class ShieldDropTests
    {
        /// <summary>
        /// 44e9's craft: pod, HeatShield1 (itself a decoupler), Decoupler.1, then the service module. Firing the shield takes
        /// Decoupler.1 and the service module with it, as one piece of debris.
        /// </summary>
        private const string Craft =
            "local fired is list().\n"
            + "local ship_parts is list().\n"
            + "function mk {\n parameter uid, name, mods, par, leaves.\n"
            + "  local p is lexicon(\"uid\", uid, \"name\", name, \"hasparent\", not par:istype(\"String\"), \"parent\", par, \"children\", list(), \"stage\", 0).\n"
            + "  local m is lexicon(\"alleventnames\", list(\"jettison heat shield\"), \"part\", p).\n"
            + "  set m:doevent to { parameter e. fired:add(uid). for gone in leaves { local keep is list(). for q in ship_parts { if q:uid <> gone { keep:add(q). } } set ship_parts to keep. } }.\n"
            + "  set p:hasmodule to { parameter n. return mods:contains(n). }.\n"
            + "  set p:getmodule to { parameter n. return m. }.\n"
            + "  if not par:istype(\"String\") { par:children:add(p). }\n"
            + "  ship_parts:add(p).\n"
            + "  return p.\n}\n"
            + "local pod is mk(\"pod\", \"mk1pod.v2\", list(\"ModuleCommand\"), \"\", list()).\n"
            + "local shield is mk(\"shield\", \"HeatShield1\", list(\"ModuleAblator\", \"ModuleDecouple\"), pod, list(\"shield\", \"dec1\", \"service\")).\n"
            + "local dec1 is mk(\"dec1\", \"Decoupler.1\", list(\"ModuleDecouple\"), shield, list(\"shield\", \"dec1\", \"service\")).\n"
            + "local service is mk(\"service\", \"fuelTank\", list(), dec1, list()).\n"
            + "local parts_of is { return ship_parts. }.\n";

        private static string[] Run(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(Craft + script)).ToArray();
        }

        [Fact]
        public void The_first_event_splits_the_craft_and_the_second_module_is_found_off_the_ship_and_skipped()
        {
            Assert.Equal(new[] { "1", "shield" },
                Run("print reentry_shield_drop(\"auto\", parts_of). print fired:join(\",\").\n"));
        }

        [Fact]
        public void A_shield_whose_part_left_before_the_event_is_not_fired()
        {
            Assert.Equal(new[] { "0", "" },
                Run("set ship_parts to list(pod, service).\n print reentry_shield_drop(\"auto\", parts_of). print fired:join(\",\").\n"));
        }

        [Fact]
        public void A_decoupler_off_the_ship_is_not_fired_by_claudia_fire_decoupler_and_one_aboard_is()
        {
            // claudia_drop_decoupler's decouplers (drop_parts, deorbit, reentry's separate) and land_drop all fire through it.
            Assert.Equal(new[] { "False", "", "True", "dec1" },
                Run("set ship_parts to list(pod).\n print claudia_fire_decoupler(dec1, parts_of()). print fired:join(\",\").\n"
                    + "set ship_parts to list(pod, dec1).\n print claudia_fire_decoupler(dec1, parts_of()). print fired:join(\",\").\n"));
        }
    }
}
