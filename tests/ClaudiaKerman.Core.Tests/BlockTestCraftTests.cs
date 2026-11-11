// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The block-test craft #920 rebuilt (tests/blocks/*/reference.craft and tests/tools/reentry_duna_site): each still parses, every link and node
    /// resolves, no part is orphaned, and each has the parts the ticket says (an Entry Shield, static panels, no winglets, a second chute, an advSasModule).
    /// </summary>
    public class BlockTestCraftTests
    {
        private sealed class CraftPart
        {
            public string Name = "";
            public string Id = "";
            public uint Persistent;
            public int Stage;
            public int Sqor;
            public int Dstg;
            public readonly List<string> Links = new List<string>();
            public readonly List<string> StackNodes = new List<string>();
            public string SurfaceParent = "";
            public uint? Parent;
        }

        /// <summary>The craft's PART blocks: its part, persistentId, link, attN and srfN lines and its staging.</summary>
        private static List<CraftPart> Parse(string path)
        {
            var parts = new List<CraftPart>();
            CraftPart part = null;
            var depth = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (depth == 0)
                {
                    if (line == "PART") part = new CraftPart();
                    else if (line == "{") depth = 1;
                    continue;
                }
                if (line == "{") { depth++; continue; }
                if (line == "}")
                {
                    depth--;
                    if (depth == 0) { parts.Add(part); part = null; }
                    continue;
                }
                if (depth != 1) continue;
                var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) continue;
                var key = line.Substring(0, eq);
                var value = line.Substring(eq + 3);
                switch (key)
                {
                    case "part": part.Id = value; part.Name = value.Substring(0, value.LastIndexOf('_')); break;
                    case "persistentId": part.Persistent = uint.Parse(value, CultureInfo.InvariantCulture); break;
                    case "istg": part.Stage = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "sqor": part.Sqor = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "dstg": part.Dstg = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "link": part.Links.Add(value); break;
                    case "attN":
                        var target = value.Substring(value.IndexOf(',') + 1);
                        part.StackNodes.Add(target.StartsWith("Null_", StringComparison.Ordinal) ? "Null" : target.Substring(0, target.IndexOf("_0|", StringComparison.Ordinal)));
                        break;
                    case "srfN":
                        part.SurfaceParent = value.Split(',')[1];
                        break;
                }
            }
            return parts;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKerman.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string Craft(string test)
        {
            var dir = test.StartsWith("tools/", StringComparison.Ordinal) ? Path.Combine("tests", test) : Path.Combine("tests", "blocks", test);
            return Path.Combine(RepoRoot(), dir, "reference.craft");
        }

        public static readonly string[] Changed =
        {
            "return_target", "deorbit_eve", "deorbit_high_orbit", "capture_duna_ike", "transfer_to_duna", "transfer_to_kerbin_from_duna",
            "transfer_to_minmus_from_mun", "transfer_to_ike_crossing", "deorbit_duna", "reentry_duna_powered", "reentry_duna_weak_top",
            "reentry_duna_keep_stage", "tools/reentry_duna_site", "launch_orbit", "launch_lan", "drop_empty",
        };

        public static IEnumerable<object[]> AllChanged()
        {
            return Changed.Select(c => new object[] { c });
        }

        /// <summary>The root is the part with no parent; every other part has exactly one parent that names it in a link, and every link and node resolves.</summary>
        private static CraftPart Resolve(List<CraftPart> parts, string test)
        {
            Assert.NotEmpty(parts);
            Assert.Equal(parts.Count, parts.Select(p => p.Id).Distinct().Count());
            Assert.Equal(parts.Count, parts.Select(p => p.Persistent).Distinct().Count());
            var byId = parts.ToDictionary(p => p.Id);
            foreach (var p in parts)
            {
                foreach (var link in p.Links) Assert.True(byId.ContainsKey(link), test + ": " + p.Id + " links " + link + ", which is not in the craft");
                foreach (var node in p.StackNodes.Where(n => n != "Null")) Assert.True(byId.ContainsKey(node), test + ": " + p.Id + " has a node to " + node);
                if (p.SurfaceParent != "")
                {
                    Assert.True(byId.ContainsKey(p.SurfaceParent), test + ": " + p.Id + " hangs on " + p.SurfaceParent + ", not in the craft");
                    Assert.Contains(p.Id, byId[p.SurfaceParent].Links);
                }
            }
            var roots = parts.Where(p => !parts.Any(o => o.Links.Contains(p.Id))).ToList();
            Assert.True(roots.Count == 1, test + ": " + roots.Count + " parts have no parent");
            foreach (var p in parts) Assert.True(parts.Count(o => o.Links.Contains(p.Id)) <= 1, test + ": " + p.Id + " has two parents");
            var seen = new HashSet<string>();
            var queue = new Queue<CraftPart>();
            queue.Enqueue(roots[0]);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (!seen.Add(p.Id)) continue;
                foreach (var link in p.Links) { byId[link].Parent = p.Persistent; queue.Enqueue(byId[link]); }
            }
            Assert.True(seen.Count == parts.Count, test + ": orphaned " + string.Join(", ", parts.Where(p => !seen.Contains(p.Id)).Select(p => p.Id)));
            return roots[0];
        }

        [Theory]
        [MemberData(nameof(AllChanged))]
        public void Every_changed_craft_parses_every_link_resolves_and_no_part_is_orphaned(string test)
        {
            var parts = Parse(Craft(test));
            Resolve(parts, test);
        }

        private static List<SnapshotPart> Snapshot(List<CraftPart> parts)
        {
            return parts.Select(p =>
            {
                var s = new SnapshotPart { Id = p.Persistent, ParentId = p.Parent, Name = p.Name, Title = p.Name, SurfaceAttached = p.SurfaceParent != "" };
                if (p.Name == "HeatShield1") { s.Tags.Add("heat_shield"); s.Tags.Add("decoupler"); }
                if (p.Name.StartsWith("Decoupler", StringComparison.Ordinal)) s.Tags.Add("decoupler");
                if (p.Name.StartsWith("parachute", StringComparison.Ordinal)) s.Tags.Add("parachute");
                if (p.Name.StartsWith("liquidEngine", StringComparison.Ordinal)) s.Tags.Add("engine");
                return s;
            }).ToList();
        }

        [Theory]
        [InlineData("return_target")]
        [InlineData("deorbit_eve")]
        [InlineData("deorbit_high_orbit")]
        [InlineData("deorbit_kerbin_defaults")]
        [InlineData("transfer_to_kerbin_from_duna")]
        public void The_STD_plus_S_craft_names_an_Entry_Shield_and_drops_the_stack_below_it(string test)
        {
            var parts = Parse(Craft(test));
            var root = Resolve(parts, test);
            var stack = EntryStack.Of(Snapshot(parts), root.Persistent, 1);
            Assert.NotNull(stack.Shield);
            Assert.Equal("HeatShield1", stack.Shield.Name);
            Assert.Empty(stack.Stuck);
            Assert.Equal(new[] { "Decoupler.1" }, EntryStack.Names(stack.Decouplers));
            // The stack: the tank and the engine (and any winglets on the tank) go; the capsule above the shield stays.
            Assert.Contains("fuelTank.long", EntryStack.Names(stack.Gone));
            Assert.Contains("liquidEngine2.v2", EntryStack.Names(stack.Gone));
            Assert.DoesNotContain("probeCoreOcto2.v2", EntryStack.Names(stack.Gone));
            Assert.DoesNotContain("parachuteSingle", EntryStack.Names(stack.Gone));
        }

        [Theory]
        [InlineData("return_target", false)]
        [InlineData("deorbit_eve", true)]
        [InlineData("deorbit_high_orbit", false)]
        [InlineData("capture_duna_ike", true)]
        [InlineData("transfer_to_duna", true)]
        [InlineData("transfer_to_kerbin_from_duna", true)]
        [InlineData("transfer_to_minmus_from_mun", true)]
        [InlineData("transfer_to_ike_crossing", true)]
        [InlineData("reentry_duna_powered", true)]
        [InlineData("reentry_duna_weak_top", true)]
        [InlineData("reentry_duna_keep_stage", true)]
        [InlineData("tools/reentry_duna_site", true)]
        public void The_long_tests_carry_two_to_four_static_panels_on_the_pod_above_the_decoupler(string test, bool wantPanels)
        {
            var parts = Parse(Craft(test));
            var panels = parts.Where(p => p.Name == "solarPanels5").ToList();
            if (!wantPanels) { Assert.Empty(panels); return; }
            Assert.InRange(panels.Count, 2, 4);
            var decoupler = parts.First(p => p.Name == "Decoupler.1");
            foreach (var panel in panels)
            {
                Assert.Equal("kOSMachine1m", parts.First(p => p.Id == panel.SurfaceParent).Name);
                Assert.Equal(-1, panel.Stage); // not staged
            }
            Assert.DoesNotContain(parts.Where(p => p.Name == "solarPanels5"), p => decoupler.Links.Contains(p.Id));
        }

        [Theory]
        [InlineData("reentry_duna_powered")]
        [InlineData("reentry_duna_weak_top")]
        [InlineData("reentry_duna_keep_stage")]
        [InlineData("tools/reentry_duna_site")]
        [InlineData("deorbit_duna")]
        public void The_Duna_entry_craft_have_no_winglets(string test)
        {
            Assert.DoesNotContain(Parse(Craft(test)), p => p.Name == "R8winglet");
        }

        [Theory]
        [InlineData("launch_orbit")]
        [InlineData("launch_lan")]
        [InlineData("drop_empty")]
        public void The_two_stage_launch_craft_keep_their_fins_and_their_staging_with_an_advSasModule(string test)
        {
            var parts = Parse(Craft(test));
            Resolve(parts, test);
            Assert.DoesNotContain(parts, p => p.Name == "sasModule");
            var sas = Assert.Single(parts, p => p.Name == "advSasModule");
            Assert.Equal(-1, sas.Stage);
            Assert.Equal(-1, sas.Sqor);
            Assert.Equal(3, parts.Count(p => p.Name == "R8winglet"));
            // Between the probe core and the chute, as the sasModule was.
            var probe = Assert.Single(parts, p => p.Name == "probeCoreOcto2.v2");
            Assert.Contains(sas.Id, probe.Links);
            Assert.Single(sas.Links);
            Assert.Equal("parachuteSingle", parts.First(p => p.Id == sas.Links[0]).Name);
        }

        [Fact]
        public void The_deorbit_duna_craft_has_a_second_Mk16_radial_on_the_kOS_part_in_stage_0()
        {
            var parts = Parse(Craft("deorbit_duna"));
            Resolve(parts, "deorbit_duna");
            var chutes = parts.Where(p => p.Name == "parachuteSingle").ToList();
            Assert.Equal(2, chutes.Count);
            Assert.All(chutes, c => Assert.Equal(0, c.Stage));
            var radial = Assert.Single(chutes, c => c.SurfaceParent != "");
            Assert.Equal("kOSMachine1m", parts.First(p => p.Id == radial.SurfaceParent).Name);
            Assert.Equal(0, radial.Sqor);
        }
    }
}
