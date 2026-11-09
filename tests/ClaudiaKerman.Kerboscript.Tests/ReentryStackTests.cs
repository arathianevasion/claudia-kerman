// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// reentry's firing of what its Entry drops (#557, #823): fake part trees run on kOS.Safe through the real bundle, given what Core's
    /// <c>ENTRYDROP</c> call really answers for that tree and plan. Which shield and which parts is Core's (EntryDropTests); these say
    /// what the block then fires and when it refuses.
    /// </summary>
    public class ReentryStackTests
    {
        private sealed class Spec
        {
            public string Uid;
            public string Name;
            public int Parent;
            public string[] Modules;
        }

        private static readonly string[] None = new string[0];
        private static readonly string[] Command = { "ModuleCommand" };
        private static readonly string[] Shield = { "ModuleAblator", "ModuleDecouple" };
        private static readonly string[] Decoupler = { "ModuleDecouple" };

        private sealed class Tree
        {
            public readonly List<Spec> Parts = new List<Spec>();

            public int Add(string name, int parent, string[] modules, string uid = null)
            {
                Parts.Add(new Spec { Uid = uid ?? name + "#" + Parts.Count, Name = name, Parent = parent, Modules = modules });
                return Parts.Count - 1;
            }

            /// <summary>The tree as kOS parts the library can read and fire: a fired decoupler's uid goes on the list <c>fired</c>.</summary>
            public string Script()
            {
                var b = new StringBuilder("local fired is list().\n");
                for (var i = 0; i < Parts.Count; i++)
                {
                    var s = Parts[i];
                    var test = s.Modules.Length == 0 ? "false" : string.Join(" or ", s.Modules.Select(m => "m = \"" + m + "\""));
                    b.Append("local p_" + i + " is lexicon(\"uid\", \"" + s.Uid + "\", \"name\", \"" + s.Name + "\", \"children\", list(), "
                        + "\"hasparent\", " + (s.Parent >= 0 ? "true" : "false") + ", \"parent\", " + (s.Parent >= 0 ? "p_" + s.Parent : "\"\"") + ", "
                        + "\"hasmodule\", { parameter m. return " + test + ". }, "
                        + "\"getmodule\", { parameter m. return lexicon(\"alleventnames\", list(\"decouple\"), \"part\", lexicon(\"uid\", \"" + s.Uid + "\"), \"doevent\", { parameter e. fired:add(\"" + s.Uid + "\"). }). }).\n");
                    if (s.Parent >= 0) b.Append("p_" + s.Parent + ":children:add(p_" + i + ").\n");
                }
                b.Append("local parts is list(" + string.Join(", ", Enumerable.Range(0, Parts.Count).Select(i => "p_" + i)) + ").\n");
                return b.ToString();
            }

            private static SnapshotPart Part(Spec s, int i)
            {
                return new SnapshotPart
                {
                    Id = (uint)(i + 1), ParentId = s.Parent >= 0 ? (uint)(s.Parent + 1) : (uint?)null, Name = s.Name,
                    Tags = s.Modules.Select(m => m == "ModuleAblator" ? "heat_shield" : m == "ModuleDecouple" ? "decoupler" : "other").ToList(),
                };
            }

            /// <summary><c>local below</c>: what ENTRYDROP answers for this tree at <paramref name="at"/>.</summary>
            public string Below(string at, IList<PlannedSequence> legs, string separate = "auto", string jettisonStage = "auto")
            {
                var snapshot = new GameSnapshot
                {
                    Body = at,
                    Orbit = new SnapshotOrbit { Body = at },
                    Craft = new SnapshotCraft { Parts = Parts.Select(Part).ToList() },
                    Bodies = new List<SnapshotBody>
                    {
                        new SnapshotBody { Name = "Kerbin", AtmosphereHeight = 70000, SeaLevelAtm = 1 },
                        new SnapshotBody { Name = "Duna", AtmosphereHeight = 50000, SeaLevelAtm = 0.07 },
                    },
                };
                var answer = BlockCalls.EntryDropCall(new Dictionary<string, object> { { "home", "pod" }, { "separate", separate }, { "jettison_stage", jettisonStage } },
                    snapshot, legs, 0, "", id => Parts[(int)id - 1].Uid);
                Func<string, string> list = key => "list(" + string.Join(", ", ((IEnumerable<object>)answer[key]).Select(u => "\"" + u + "\"")) + ")";
                return "local below is lexicon(\"error\", \"\", \"shield\", \"" + answer["shield"] + "\", \"jettison\", \"" + answer["jettison"] + "\", \"jettison_first\", \"" + answer["jettison_first"] + "\", \"decouplers\", " + list("decouplers") + ", \"gone\", " + list("gone")
                    + ", \"names\", " + list("names") + ", \"stuck\", " + list("stuck") + ").\n";
            }

            /// <summary>What ENTRYDROP refuses at block start (#815), above the air, for reentry's ask with <paramref name="separate"/>: its error, "" for none.</summary>
            public string Refused(string at, IList<PlannedSequence> legs, string separate = "auto")
            {
                var snapshot = new GameSnapshot
                {
                    Body = at, Altitude = 100000,
                    Orbit = new SnapshotOrbit { Body = at },
                    Craft = new SnapshotCraft { Parts = Parts.Select(Part).ToList() },
                    Bodies = new List<SnapshotBody>
                    {
                        new SnapshotBody { Name = "Kerbin", AtmosphereHeight = 70000, SeaLevelAtm = 1 },
                        new SnapshotBody { Name = "Duna", AtmosphereHeight = 50000, SeaLevelAtm = 0.07 },
                    },
                };
                return (string)BlockCalls.EntryDropCall(new Dictionary<string, object> { { "home", "pod" }, { "separate", separate }, { "chutes", true }, { "core", "pod" } },
                    snapshot, legs, 0, "", id => Parts[(int)id - 1].Uid)["error"];
            }
        }

        private static readonly IList<PlannedSequence> Return = new[] { new PlannedSequence { Name = "Return to Kerbin", DeltaV = 600 } };
        private static readonly IList<PlannedSequence> DunaThenKerbin = new[]
        {
            new PlannedSequence { Name = "Land on Duna", DeltaV = 100 }, new PlannedSequence { Name = "Ascend from Duna", DeltaV = 1336 }, new PlannedSequence { Name = "Return to Kerbin", DeltaV = 623 },
        };

        /// <summary>Mun Shot v2 home from the Mun, as in b5f3 and 0de6: the pod's side, the shield, Decoupler.1, the service module.</summary>
        private static Tree MunShotV2()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            var dec = t.Add("Decoupler.1", shield, Decoupler, "dec1");
            var tank = t.Add("fuelTank", dec, None);
            t.Add("liquidEngine3.v2", tank, new[] { "ModuleEnginesFX" }, "engine");
            return t;
        }

        /// <summary>The flight's layout (m20261106-203509-34a6), cut down: the core stage, its radial tanks on their own decouplers, its engine.</summary>
        private static Tree TheFlight()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            var dec = t.Add("Decoupler.1", shield, Decoupler, "dec1");
            var core = t.Add("fuelTank", dec, None);
            for (var i = 0; i < 4; i++) t.Add("radialTank", t.Add("radialDecoupler", core, Decoupler), None);
            t.Add("toroidalAerospike", core, new[] { "ModuleEnginesFX" }, "engine");
            return t;
        }

        /// <summary>Pod, shield 1, the ascent stage, shield 2, the transfer stage.</summary>
        private static Tree TwoShields()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var s1 = t.Add("shield1", pod, Shield, "s1");
            var d1 = t.Add("dec1", s1, Decoupler, "d1");
            var ascent = t.Add("ascentTank", d1, None);
            var s2 = t.Add("shield2", ascent, Shield, "s2");
            var d2 = t.Add("dec2", s2, Decoupler, "d2");
            t.Add("transferTank", d2, None);
            return t;
        }

        private static string[] Run(Tree tree, string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(tree.Script() + script)).ToArray();
        }

        private const string Options = "local opt is claudia_merge_options(lexicon(), reentry_defaults()).\n";

        /// <summary>Prints what "auto" fires (decoupler uids), its error, and the shield's uid and modules.</summary>
        private const string PrintFires = Options
            + "local plan is reentry_separation(\"auto\", below, parts).\n"
            + "local decs is list(). for d in plan:decouplers { decs:add(d:uid). }\n"
            + "print decs:join(\",\"). print plan:error.\n"
            + "local jettison is below:jettison. print jettison. print reentry_shield_modules(jettison, parts):length.\n";

        [Fact]
        public void On_the_flights_layout_a_non_last_entry_fires_nothing_with_chutes()
        {
            Assert.Equal(new[] { "", "", "", "0" }, Run(TheFlight(), TheFlight().Below("Duna", DunaThenKerbin) + PrintFires));
            Assert.Equal("", TheFlight().Refused("Duna", DunaThenKerbin));
        }

        [Fact]
        public void On_the_flights_layout_the_Last_Entry_fires_the_decoupler_under_the_shield_and_jettisons_it()
        {
            var printed = Run(TheFlight(), TheFlight().Below("Kerbin", Return) + PrintFires);
            // The shield's own decoupler and Decoupler.1 beside it; the block drops the stack first, which takes nothing else.
            Assert.Equal(new[] { "dec1", "", "shield", "2" }, printed);
            Assert.Equal("", TheFlight().Refused("Kerbin", Return));
        }

        [Fact]
        public void A_shield_that_isnt_the_Entry_Shield_is_never_fired()
        {
            var tree = TwoShields();
            var printed = Run(tree, tree.Below("Duna", DunaThenKerbin) + PrintFires
                + "print reentry_shield_drop(jettison, { return parts. }). print fired:join(\",\").\n");
            // Duna uses shield 2: dec2 drops the transfer stage; only shield 2 and the decoupler beside it are jettisoned.
            Assert.Equal(new[] { "d2", "", "s2", "2", "2", "s2,d2" }, printed);
        }

        [Fact]
        public void The_Entry_Shield_still_goes_by_reentry_shield_due()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var body = source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
            Assert.Contains("local shield is reentry_shield_modules(shield_uid, ship:parts).\n  local shield_gone is shield:length = 0.", body);
            var due = body.IndexOf("if reentry_shield_due(shield_gone, last_speed, agl, brake:start) {", StringComparison.Ordinal);
            Assert.True(due > 0);
            Assert.True(due < body.IndexOf("reentry_shield_drop(shield_uid, { return ship:parts. }, below:jettison_first).", StringComparison.Ordinal));
        }

        /// <summary>The Duna ship (#940): the lander's aerospike, a TD-12, its shield, a second TD-12 and the stack.</summary>
        private static Tree DunaShip()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var inner = t.Add("HeatShield1", pod, Shield, "s1");
            var d1 = t.Add("Decoupler.1", inner, Decoupler, "d1");
            var tank = t.Add("fuelTank", d1, None);
            var aero = t.Add("toroidalAerospike", tank, new[] { "ModuleEnginesFX" }, "aero");
            var ring = t.Add("Decoupler.1", aero, Decoupler, "ring");
            var outer = t.Add("HeatShield1", ring, Shield, "s2");
            var d2 = t.Add("Decoupler.1", outer, Decoupler, "d2");
            t.Add("fuelTank.long", d2, None);
            return t;
        }

        [Fact]
        public void Entrydrops_answer_fires_the_decoupler_under_the_engine_before_the_shields_own_whatever_ship_parts_order()
        {
            var tree = DunaShip();
            var printed = Run(tree, tree.Below("Duna", DunaThenKerbin)
                + "print below:jettison. print below:jettison_first.\n"
                + "reentry_shield_drop(below:jettison, { return list(p_6, p_7, p_5). }, below:jettison_first). print fired:join(\",\").\n");

            Assert.Equal(new[] { "s2", "ring", "ring,s2,d2" }, printed);
        }

        [Fact]
        public void A_part_with_no_decoupler_below_the_Entry_Shield_refuses_and_below_another_shield_it_doesnt()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            t.Add("fuelTank", shield, None);
            const string print = "local plan is reentry_separation(\"auto\", below, parts).\n"
                + "print plan:error. print plan:decouplers:length.\n";
            Assert.Equal(new[] { "fuelTank: no decoupler between it and the heat shield", "0" }, Run(t, t.Below("Kerbin", Return) + print));
            // The refusal is ENTRYDROP's at block start (#815), still above the air.
            Assert.Equal("stack attached below the heat shield: fuelTank; drop it first or use separate (separate: fuelTank: no decoupler between it and the heat shield)",
                t.Refused("Kerbin", Return));
            Assert.Equal(new[] { "", "0" }, Run(t, t.Below("Duna", DunaThenKerbin) + print));
            Assert.Equal("", t.Refused("Duna", DunaThenKerbin));
        }

        [Fact]
        public void Separate_none_keeps_the_stack_with_no_refusal_and_keeps_the_shield_that_would_take_it()
        {
            var tree = MunShotV2();
            var printed = Run(tree, tree.Below("Kerbin", Return, "none") + "local opt is claudia_merge_options(lexicon(\"separate\", \"none\"), reentry_defaults()).\n"
                + "print reentry_separation(\"none\", below, parts):decouplers:length.\n"
                + "print below:jettison.\n");
            Assert.Equal(new[] { "0", "" }, printed);
            Assert.Equal("", tree.Refused("Kerbin", Return, "none"));
        }

        [Fact]
        public void Jettison_none_keeps_the_shield_and_the_stack_still_drops()
        {
            var tree = MunShotV2();
            var printed = Run(tree, tree.Below("Kerbin", Return, "auto", "none") + "local opt is claudia_merge_options(lexicon(\"jettison_stage\", \"none\"), reentry_defaults()).\n"
                + "print reentry_separation(\"auto\", below, parts):decouplers:length. print below:jettison.\n");
            Assert.Equal(new[] { "1", "" }, printed);
        }

        // ---- A powered entry (#824): the block sets separate to "none", so it fires nothing, on each of #823's layouts ----

        /// <summary>Prints what a powered entry fires and jettisons, and that it refuses nothing.</summary>
        private const string PrintPowered = "local opt is claudia_merge_options(lexicon(), reentry_defaults()).\n"
            + "set opt:separate to \"none\".\n"
            + "print reentry_separation(opt:separate, below, parts):decouplers:length. print below:jettison.\n";

        private static readonly IList<PlannedSequence> DunaLast = new[] { new PlannedSequence { Name = "Land on Duna", DeltaV = 100 } };

        [Fact]
        public void A_powered_entry_on_the_flights_layout_fires_no_decoupler_so_radial_tanks_and_their_chutes_stay()
        {
            // No Entry Shield at Duna before Kerbin; and the Last Entry, which would drop the stack and the radial tanks with their chutes.
            var tree = TheFlight();
            Assert.Equal(new[] { "0", "" }, Run(tree, tree.Below("Duna", DunaThenKerbin, "none") + PrintPowered + "\n"));
            Assert.Equal(new[] { "0", "" }, Run(tree, tree.Below("Duna", DunaLast, "none") + PrintPowered));
            Assert.Contains("set opt:separate to \"none\".", CoreLibrary.Files.Single(f => f.Name == "reentry").Source);
        }

        [Fact]
        public void A_powered_decision_sets_separate_none_before_the_drop_point_ask_jettison_is_read_from()
        {
            var body = Body();
            var powered = At(body, "set opt:separate to \"none\".");
            var reask = At(body, "\"jettison_stage\", opt:jettison_stage, \"drop_error\"");
            Assert.True(powered < reask, "separate is set to none after the entrydrop re-ask");
            Assert.True(reask < At(body, "local shield_uid is below:jettison."));
        }

        [Fact]
        public void A_powered_entry_on_two_shields_keeps_the_outer_shield_with_the_stage_below_it_and_the_inner_is_never_fired()
        {
            var tree = TwoShields();
            Assert.Equal(new[] { "0", "" }, Run(tree, tree.Below("Duna", DunaThenKerbin, "none") + PrintPowered));
            Assert.Equal(new[] { "0", "" }, Run(tree, tree.Below("Kerbin", Return, "none") + PrintPowered));
        }

        [Fact]
        public void A_powered_entry_with_its_shield_above_the_engines_keeps_it_on_a_pod_shield_stage()
        {
            var tree = MunShotV2();
            Assert.Equal(new[] { "0", "" }, Run(tree, tree.Below("Duna", DunaThenKerbin, "none") + PrintPowered));
            Assert.Equal(new[] { "0", "" }, Run(tree, tree.Below("Kerbin", Return, "none") + PrintPowered));
        }

        [Fact]
        public void A_powered_entry_with_nothing_below_its_shield_jettisons_it_by_todays_timing_or_as_braking_starts()
        {
            // The engines are above the shield, on the pod's side.
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            t.Add("radialEngine", pod, new[] { "ModuleEnginesFX" }, "engine");
            t.Add("HeatShield1", pod, Shield, "shield");
            Assert.Equal(new[] { "0", "shield", "False", "True", "True" },
                Run(t, t.Below("Duna", DunaLast, "none") + PrintPowered
                    + "print reentry_shield_due(false, 900, 20000).\n print reentry_shield_due(false, 400, 20000).\n print reentry_shield_due(false, 900, 20000, true).\n"));
        }

        [Fact]
        public void A_craft_built_from_the_far_side_on_a_powered_entry_refuses_nothing_and_keeps_its_shield()
        {
            var t = new Tree();
            var tank = t.Add("fuelTank", -1, None);
            var dec = t.Add("Decoupler.1", tank, Decoupler);
            var shield = t.Add("HeatShield1", dec, Shield, "shield");
            t.Add("mk1pod.v2", shield, Command, "pod");
            Assert.Equal(new[] { "0", "" }, Run(t, t.Below("Duna", DunaLast, "none") + PrintPowered));
        }

        // ---- When the stack drops (#815): read at block start, dropped at the drop point, its lead s before the air ----

        private static string Body()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            return source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
        }

        private static int At(string body, string text)
        {
            var i = body.IndexOf(text, StringComparison.Ordinal);
            Assert.True(i > 0, "not in reentry: " + text);
            return i;
        }

        private const string StartRead = "local below is addons:claudia:entrydrop(entry_ask).";
        private const string DropPoint = "if entry_in > below:lead {\n";
        private const string DropWait = "    local waited is claudia_wait_until(ctx, time:seconds + entry_in - below:lead).";
        private const string Trim = "  local trim_ask is {\n    return addons:claudia:entrytrim(lexicon(\"to_air\", entry_in, \"home\", entry_ask:home, \"core\", entry_ask:core, \"has_node\", hasnode, \"stage_limit\", opt:stage_limit, \"dv_left\", claudia_dv_recounted(), \"isp\", claudia_engine_isp())).\n  }.\n";
        private const string TrimFly = "  local flown is claudia_fly_correction(ctx, trim).\n";
        private const string AirAgain = "    set entry_in to claudia_fall_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).\n";
        private const string Fire = "set drop_error to claudia_fire_drop(ctx, drop, false).";

        /// <summary>The wait to the drop point: from its if to the closing brace at the block's own indent.</summary>
        private static string DropPointWait(string body)
        {
            var start = At(body, DropPoint);
            return body.Substring(start, body.IndexOf("\n  }\n", start, StringComparison.Ordinal) - start);
        }

        [Fact]
        public void From_far_out_the_drop_fires_after_the_wait_to_the_lead_ENTRYDROP_gives_not_at_block_start()
        {
            var body = Body();
            Assert.True(At(body, "the orbit never reaches the atmosphere") < At(body, DropPoint));
            Assert.True(At(body, DropPoint) < At(body, Fire));
            // The wait warps on rails and is back at 1x 30 s before the point (claudia_wait_until), so the drop fires off rails.
            Assert.DoesNotContain("claudia_fire_drop", body.Substring(0, At(body, DropPoint)));
        }

        /// <summary>#892, #952: Core's ENTRYTRIM, flown through the Course Correction's own path, once, ahead of #815's drop and outside its wait, so the log has the decision however late the block starts; the eta is read again after it, and the burn can't fail the block.</summary>
        [Fact]
        public void The_late_trim_is_asked_and_flown_once_before_the_wait_to_the_drop_and_never_ends_the_block()
        {
            var body = Body();
            Assert.Equal(1, body.Split("addons:claudia:entrytrim(").Length - 1);
            Assert.Equal(1, body.Split("claudia_fly_correction(").Length - 1);
            var trim = At(body, Trim);
            var fly = At(body, TrimFly);
            var from = At(body, DropPoint);
            var again = body.IndexOf(AirAgain, from, StringComparison.Ordinal);
            var wait = At(body, DropWait);
            Assert.Equal(from + DropPoint.Length, again);
            Assert.True(trim < fly && fly < from && again < wait, "trim, then the eta read again, then the wait");
            Assert.True(wait < At(body, Fire));
            Assert.DoesNotContain("claudia_finish", body.Substring(fly, wait - fly));
            // The trim's craft is read in the addon: a second ENTRYCRAFT from the block would overwrite the craft aimed with, which ENTRYFINISH compares (#814).
            Assert.DoesNotContain("addons:claudia:entrycraft(", body);
        }

        // ---- #952: when the trim flies. The block's own lines, from the first ask to the note, run on kOS.Safe with the game faked ----

        private const string TrimRig =
            "global time is lexicon(\"seconds\", 1000).\n"
            + "global hasnode is false.\n"
            + "global ship is lexicon(\"orbit\", lexicon(\"eccentricity\", 0.9, \"semimajoraxis\", 1, \"trueanomaly\", 0), \"parts\", list()).\n"
            + "global body_ is lexicon(\"mu\", 1, \"radius\", 1, \"atm\", lexicon(\"height\", 1)).\n"
            + "global entry_in is 95000.\n"
            + "global entry_ask is lexicon(\"home\", \"h\", \"core\", \"c\").\n"
            + "global opt is lexicon(\"chute_mode\", \"safe\", \"stage_limit\", 0).\n"
            + "global ask is lexicon().\n"
            + "global extra is lexicon().\n"
            + "global ctx is lexicon(\"block\", \"reentry\").\n"
            + "global asked is list().\n"
            + "global waits is list().\n"
            + "global burns is 0.\n"
            + "global aborts is 0.\n"
            + "global notes is list().\n"
            + "global wait_result is \"\".\n"
            + "function claudia_wait_until { parameter c, ut. waits:add(ut - time:seconds). set time:seconds to ut. return wait_result. }\n"
            + "function claudia_fall_eta { parameter a, b, c, d, e. return 2400. }\n"
            + "function claudia_fly_correction { parameter c, fix. set burns to burns + 1. return lexicon(\"dv\", choose fix:dv if fix:haskey(\"dv\") else 0, \"reason\", fix:error). }\n"
            + "function claudia_dv_recounted { return 100. }\n"
            + "function claudia_engine_isp { return 300. }\n"
            + "function reentry_abort { parameter m, g. set aborts to aborts + 1. }\n"
            + "function reentry_group_of { parameter a, b, c. return 0. }\n"
            + "function claudia_finish { parameter c, ok, reason, ex. return \"finished: \" + reason. }\n";

        private const string WaitAnswer = "lexicon(\"error\", \"\", \"wait\", 92600, \"dv\", 0)";
        private const string NoFix = "lexicon(\"error\", \"\", \"wait\", 0, \"dv\", 0)";
        /// <summary>What Guarded answers when the call throws: the error and nothing else.</summary>
        private const string Failed = "lexicon(\"error\", \"ENTRYTRIM failed: boom\")";
        private const string Fix = "lexicon(\"error\", \"\", \"wait\", 0, \"dv\", 12.5, \"miss_before_km\", 41.26, \"miss_after_km\", 3.14)";

        /// <summary>Runs the block's lines on the answers given; prints the to_air figures asked with, the waits, the burns, the aborts, the notes and the outcome.</summary>
        private static string[] TrimRun(string answers, string extraRig = "")
        {
            var body = Body();
            var start = At(body, Trim);
            var note = At(body, "  if trim:error = \"\" and trim:dv > 0 {");
            var slice = body.Substring(start, body.IndexOf('\n', note) + 1 - start);
            var script = TrimRig + extraRig
                + "global answers is list(" + answers + ").\n"
                + "global addons is lexicon(\"claudia\", lexicon(\n"
                + "  \"entrytrim\", { parameter q. asked:add(q:to_air). return answers[asked:length - 1]. },\n"
                + "  \"chutegroup\", { parameter q. return lexicon(). },\n"
                + "  \"blocknote\", { parameter b, n. notes:add(n:phase + \" \" + n:miss_before_km + \" \" + n:dv + \" \" + n:miss_after_km). })).\n"
                + "function flies {\n" + slice + "  return \"went on\".\n}\n"
                + "local out is flies().\n"
                + "print asked:join(\",\"). print waits:join(\",\"). print burns. print aborts. print notes:join(\";\"). print out.\n";
            return KosHarness.Run(script).ToArray();
        }

        [Fact]
        public void From_far_out_it_waits_then_asks_again_with_the_eta_read_again_and_flies_one_trim_and_notes_it()
        {
            Assert.Equal(new[] { "95000,2400", "92600", "1", "0", "late_trim 41.3 12.5 3.1", "went on" },
                TrimRun(WaitAnswer + ", " + Fix));
        }

        [Fact]
        public void A_second_wait_answer_is_not_a_third_ask_and_flies_nothing()
        {
            Assert.Equal(new[] { "95000,2400", "92600", "1", "0", "", "went on" },
                TrimRun(WaitAnswer + ", " + WaitAnswer));
        }

        [Fact]
        public void Inside_the_window_it_flies_at_once_with_no_wait()
        {
            Assert.Equal(new[] { "1500", "", "1", "0", "late_trim 41.3 12.5 3.1", "went on" },
                TrimRun(Fix, "set entry_in to 1500.\n"));
        }

        /// <summary>A call that threw answers only its error: the block goes on, as #892 says, at either ask.</summary>
        [Fact]
        public void A_failed_call_writes_no_note_and_goes_on()
        {
            Assert.Equal(new[] { "95000", "", "1", "0", "", "went on" }, TrimRun(Failed));
            Assert.Equal(new[] { "95000,2400", "92600", "1", "0", "", "went on" }, TrimRun(WaitAnswer + ", " + Failed));
        }

        [Fact]
        public void No_trim_at_once_writes_no_note_and_goes_on()
        {
            Assert.Equal(new[] { "1000", "", "1", "0", "", "went on" },
                TrimRun(NoFix, "set entry_in to 1000.\n"));
        }

        [Fact]
        public void A_wait_cut_short_by_a_ticks_abort_ends_the_block_as_the_drop_wait_does_with_no_second_ask_and_no_burn()
        {
            Assert.Equal(new[] { "95000", "92600", "0", "1", "", "finished: abort min_ec_pct" },
                TrimRun(WaitAnswer, "set wait_result to \"abort min_ec_pct\".\n"));
        }

        [Fact]
        public void The_trim_wait_ends_before_the_drop_points_wait_and_the_hold()
        {
            var body = Body();
            Assert.True(At(body, "claudia_wait_until(ctx, time:seconds + trim:wait)") < At(body, DropWait));
            Assert.True(At(body, DropWait) < At(body, "local waited is choose claudia_wait_until(ctx, hold_ut)"));
        }

        [Fact]
        public void A_stuck_stack_is_still_refused_at_block_start_by_ENTRYDROPs_first_read()
        {
            var body = Body();
            // reentry's ask carries what Core's refusal needs (EntryDropTests, EntryFinishTests); its error ends the block before any wait.
            Assert.Contains("local entry_ask is lexicon(\"separate\", opt:separate, \"jettison_stage\", opt:jettison_stage, \"chutes\", opt:chutes <> \"none\" and opt:chute_mode <> \"none\", \"core\", core:part:uid).", body);
            var read = At(body, StartRead);
            Assert.True(read < At(body, "if below:error <> \"\" {\n    return claudia_finish(ctx, false, below:error, extra)."));
            Assert.True(read < At(body, "claudia_wait_until("));
            Assert.DoesNotContain("reentry_stack_refusal", CoreLibrary.Files.Single(f => f.Name == "reentry").Source);
        }

        [Fact]
        public void The_drop_points_read_flies_the_engines_the_chute_group_the_shield_and_the_prediction_come_after_it()
        {
            var body = Body();
            var reread = At(body, "    set below to addons:claudia:entrydrop(entry_ask).");
            Assert.True(At(body, DropPoint) < reread);
            // ENTRYFINISH is read once, at the drop point, and the drop is the one that read gives.
            Assert.Equal(1, body.Split("addons:claudia:entryfinish(").Length - 1);
            var finish = At(body, "addons:claudia:entryfinish(");
            Assert.True(reread < finish);
            foreach (var after in new[]
            {
                "reentry_light(ship:engines, finish:engines).", "local drop is reentry_separation(opt:separate, below, ship:parts).", Fire,
                "set group to reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts).", "addons:claudia:shipsystems(\"entry\").",
                "local shield_uid is below:jettison.", "addons:claudia:reentryimpact(",
            })
                Assert.True(finish < At(body, after), after);
        }

        [Fact]
        public void An_engine_landing_and_a_craft_with_no_Entry_Shield_fire_nothing()
        {
            var body = Body();
            Assert.Contains("if powered and finish:keeps_stack { set opt:separate to \"none\". }\n    }\n  }\n  local dropped is list().\n  if below:names:length > 0 and not claudia_on_ground() {", body);
            // No Entry Shield: ENTRYDROP names nothing, so "auto" fires nothing.
            var t = new Tree();
            t.Add("fuelTank", t.Add("Decoupler.1", t.Add("mk1pod.v2", -1, Command, "pod"), Decoupler), None);
            Assert.Equal(new[] { "", "" }, Run(t, t.Below("Kerbin", Return) + PrintFires).Take(2));
            Assert.Equal("", t.Refused("Kerbin", Return));
        }

        [Fact]
        public void A_block_starting_inside_the_lead_drops_at_once_and_the_abort_before_the_drop_fires_nothing_and_arms_the_fresh_group()
        {
            var wait = DropPointWait(Body());
            // Only the late trim, the wait and the read are inside the if: inside the lead (or when a warp ran past the point) nothing waits and the drop is next.
            Assert.DoesNotContain("claudia_fire_drop", wait);
            Assert.DoesNotContain("reentry_light(", wait);
            Assert.Contains("    if waited <> \"\" {\n      reentry_abort(opt:chute_mode, reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts)).\n"
                + "      return claudia_finish(ctx, false, waited, extra).\n    }\n    set below to addons:claudia:entrydrop(entry_ask).", wait);
            // Nothing chosen yet: the group is read fresh with no engine lit.
            Assert.True(At(Body(), "local ask is lexicon(\"none\", opt:chutes = \"none\", \"engine\", false).") < At(Body(), DropPoint));
        }

        [Fact]
        public void The_hold_starts_after_the_drop_and_nothing_holds_attitude_before_it()
        {
            var body = Body();
            var hold = At(body, "lock steering to srfretrograde.");
            Assert.True(At(body, Fire) < hold);
            Assert.DoesNotContain("lock steering to", body.Substring(0, hold)); // #942's release, an unlock, is not a hold
            // #941: the hold starts a turn time plus a margin before the air, after a first wait; the wait for the air comes after it.
            Assert.True(At(body, "local waited is choose claudia_wait_until(ctx, hold_ut)") < hold);
            Assert.True(hold < At(body, "set waited to choose claudia_warp_to(ctx, air_ut, air_ut - time:seconds)"));
            // Locked, the wait never warps: warp_end = air_ut - lead = now, so rails cannot freeze the turn. claudia_wait_until (30 s lead) would.
            Assert.DoesNotContain("claudia_wait_until(ctx, air_ut)", body);
        }

        [Fact]
        public void A_resumed_block_fires_only_what_is_still_on_and_a_second_pass_fires_nothing()
        {
            var body = Body();
            // After a Coast Checkpoint the block starts afresh: a stack already gone names nothing and fires nothing; one still on waits as normal.
            Assert.True(At(body, "if below:names:length > 0 and not claudia_on_ground() {") < At(body, Fire));
            // One drop, before the fall; the skip-out loop waits and falls again but drops nothing.
            Assert.Equal(1, body.Split("claudia_fire_drop(").Length - 1);
            Assert.True(At(body, Fire) < At(body, "until fell <> \"\" or not skipped {"));
        }

        [Fact]
        public void The_drop_is_reported_at_the_air_with_the_periapsis_before_and_after_it_and_the_distance_to_the_stack()
        {
            var body = Body();
            var before = At(body, "local pe_before is ship:periapsis.");
            Assert.True(before < At(body, Fire));
            Assert.Contains("if drop_error = \"\" { set dropped to list(pe_before, ship:periapsis, drop:gone[0]:ship). }", body);
            var note = At(body, "lexicon(\"phase\", \"stack_dropped\", \"pe_before\", round(dropped[0]), \"pe_after\", round(dropped[1]), \"stack_m\", choose -1 if dropped[2]:isdead else round(dropped[2]:distance))");
            Assert.True(At(body, "set waited to choose claudia_warp_to(ctx, air_ut, air_ut - time:seconds)") < note);
            Assert.True(note < At(body, "local has_drogues is"));
        }

        [Fact]
        public void After_the_drop_ENTRYDROP_refuses_a_stack_still_on_above_the_air_and_the_shield_is_found_after()
        {
            var body = Body();
            var after = At(body, "set below to addons:claudia:entrydrop(lexicon(\"home\", entry_ask:home, \"separate\", opt:separate, \"jettison_stage\", opt:jettison_stage, \"drop_error\", drop_error)).");
            Assert.True(At(body, Fire) < after);
            Assert.True(after < At(body, "reentry_shield_modules(shield_uid, ship:parts)"));
        }

        // ---- The options ----

        [Fact]
        public void Separate_and_jettison_stage_default_to_auto_and_take_none()
        {
            var t = new Tree();
            Assert.Equal("auto", Run(t, "print claudia_options(lexicon(), reentry_defaults(), \"auto\"):opt:separate.").Single());
            foreach (var key in new[] { "separate", "jettison_stage" })
                foreach (var ok in new[] { "\"none\"", "\"auto\"" })
                    Assert.Equal("", Run(t, "print reentry_unreadable(claudia_merge_options(lexicon(\"" + key + "\", " + ok + "), reentry_defaults())).").Single());
        }

        [Theory]
        [InlineData("separate", "2", "separate must be 'auto' or 'none', not 2")]
        [InlineData("separate", "\"soon\"", "separate must be 'auto' or 'none', not soon")]
        [InlineData("jettison_stage", "1", "jettison_stage must be 'auto' or 'none', not 1")]
        [InlineData("jettison_stage", "\"soon\"", "jettison_stage must be 'auto' or 'none', not soon")]
        public void A_stage_number_or_other_value_is_refused_saying_whats_allowed(string key, string value, string reason)
        {
            Assert.Equal(reason, Run(new Tree(), "print reentry_unreadable(claudia_merge_options(lexicon(\"" + key + "\", " + value + "), reentry_defaults())).").Single());
        }

        [Fact]
        public void The_docs_name_separate_and_the_failure_and_the_catalog_line_says_the_stack_drops()
        {
            var docs = CoreLibrary.FullDocs(new[] { "reentry" });
            Assert.Contains("separate [\"auto\"]", docs);
            Assert.Contains("stack attached below the heat shield: <names>", docs);
            Assert.Contains("drops the stack below this entry's shield", CoreLibrary.Blocks.Single(b => b.Name == "reentry").Docs[0]);
        }
    }
}
