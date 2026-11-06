// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public class ModInfoTests
    {
        [Fact]
        public void LoadedLineNamesTheModAndItsVersion()
        {
            Assert.Equal("[ClaudiaKerman] Claudia Kerman " + ModInfo.Version + " loaded", ModInfo.LoadedLine);
        }

        [Fact]
        public void VersionHasNoSourceRevisionSuffix()
        {
            Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", ModInfo.Version);
        }

        [Fact]
        public void CoreReferencesNoUnityOrKspAssemblies()
        {
            foreach (var reference in typeof(ModInfo).Assembly.GetReferencedAssemblies())
            {
                Assert.DoesNotContain("Unity", reference.Name);
                Assert.DoesNotContain("Assembly-CSharp", reference.Name);
                Assert.DoesNotContain("KSP", reference.Name);
            }
        }
    }
}
