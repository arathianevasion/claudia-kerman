// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public class GameEventHandlerTests
    {
        // #976: KSP's event registration throws on a delegate with no target object, so a GameEvents handler must be an instance method.
        [Fact]
        public void NoGameEventsHandlerInTheAdapterIsStatic()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ClaudiaKerman.Adapter");
            Assert.True(Directory.Exists(dir), "adapter source not found at " + dir);
            var bad = new List<string>();
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var code = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(code, @"GameEvents\.\w+\.(?:Add|Remove)\(\s*(\w+)\s*\)"))
                {
                    var name = m.Groups[1].Value;
                    if (Regex.IsMatch(code, @"\bstatic\s+[\w<>\[\],\s]+?\b" + name + @"\s*\("))
                        bad.Add(Path.GetFileName(file) + ": " + name);
                }
            }
            Assert.True(bad.Count == 0, "static GameEvents handlers: " + string.Join(", ", bad));
        }
    }
}
