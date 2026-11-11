// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public sealed class WindowLayoutsTests
    {
        [Fact]
        public void EachSceneRemembersItsOwnBox()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 700, 600));
            layouts.Set("editor", "mission", new WindowBox(300, 40, 900, 500));
            WindowBox flight, editor, other;
            Assert.True(layouts.TryGet("flight", "mission", out flight));
            Assert.True(layouts.TryGet("editor", "mission", out editor));
            Assert.Equal(10, flight.X);
            Assert.Equal(900, editor.Width);
            Assert.False(layouts.TryGet("spacecenter", "mission", out other));
        }

        [Fact]
        public void SettingTheSameBoxAgainIsNotAChange()
        {
            var layouts = new WindowLayouts();
            Assert.True(layouts.Set("flight", "chat", new WindowBox(1, 2, 3, 4)));
            Assert.False(layouts.Set("flight", "chat", new WindowBox(1.2f, 2, 3, 4)));
            Assert.True(layouts.Set("flight", "chat", new WindowBox(5, 2, 3, 4)));
        }

        [Fact]
        public void BoxesSurviveSaveAndLoad()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ck-layouts-" + Path.GetRandomFileName());
            try
            {
                var layouts = new WindowLayouts();
                layouts.Set("flight", "mission", new WindowBox(10, 20, 700, 600));
                layouts.Set("editor", "chat", new WindowBox(0, 5, 320, 260));
                layouts.Save(directory);

                var loaded = WindowLayouts.Load(directory);
                WindowBox box;
                Assert.True(loaded.TryGet("flight", "mission", out box));
                Assert.Equal(new WindowBox(10, 20, 700, 600), box);
                Assert.True(loaded.TryGet("editor", "chat", out box));
                Assert.Equal(260, box.Height);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void AMissingFileMeansDefaults()
        {
            WindowBox box;
            Assert.False(WindowLayouts.Load(Path.Combine(Path.GetTempPath(), "ck-none-" + Path.GetRandomFileName())).TryGet("flight", "mission", out box));
        }

        [Fact]
        public void LinesThatDontParseAreIgnored()
        {
            var layouts = new WindowLayouts();
            layouts.Parse(new[]
            {
                "# comment", "", "nonsense", "flight.mission = 1, 2, 3", "flight.mission = a, b, c, d",
                "flight.mission = 1, 2, 0, 4", "flight.chat = 5, 6, 7, 8", "x = 1, 2, 3, 4",
            });
            WindowBox box;
            Assert.False(layouts.TryGet("flight", "mission", out box));
            Assert.True(layouts.TryGet("flight", "chat", out box));
            Assert.Equal(new WindowBox(5, 6, 7, 8), box);
        }

        [Fact]
        public void ABoxRememberedAtOneUiScaleOpensResizedForTheScaleNow()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 500, 800), 1.2f);
            WindowBox box;
            Assert.True(layouts.TryGet("flight", "mission", 1.8f, out box));
            Assert.Equal(new WindowBox(10, 20, 750, 1200), box);
            Assert.True(layouts.TryGet("flight", "mission", 0.96f, out box));
            Assert.Equal(new WindowBox(10, 20, 400, 640), box);
        }

        [Fact]
        public void AnOldLineWithoutAScaleWasKeptAtTheOld1_2Base()
        {
            var layouts = new WindowLayouts();
            layouts.Parse(new[] { "flight.mission = 10, 20, 500, 800" });
            WindowBox box;
            Assert.True(layouts.TryGet("flight", "mission", 1.8f, out box));
            Assert.Equal(new WindowBox(10, 20, 750, 1200), box);
        }

        [Fact]
        public void AnOldLineWithoutAScaleOpens20PercentSmallerAt100PercentAnd100Percent()
        {
            // Sized before #993 at 100% and 100%, when that was 1.2: now 0.96, so 0.8 times the size, in the same place.
            var layouts = new WindowLayouts();
            layouts.Parse(new[] { "flight.mission = 10, 20, 500, 800", "flight.capcomPanel = 30, 0, 740, 640" });
            WindowBox box;
            Assert.True(layouts.TryGet("flight", "mission", UiScale.Factor(1f, 1f), out box));
            Assert.Equal(new WindowBox(10, 20, 400, 640), box);
            Assert.True(layouts.TryGet("flight", "capcomPanel", UiScale.Factor(1f, 1f), out box));
            Assert.Equal(new WindowBox(30, 0, 592, 512), box);
        }

        [Fact]
        public void ALineKeptAtUiScale130PercentBeforeOpens20PercentSmallerAtIt()
        {
            // 1.2 x 1.3 was written with the line; at 130% now the scale is 1.248.
            var layouts = new WindowLayouts();
            layouts.Parse(new[] { "flight.mission = 10, 20, 650, 1040, 1.56" });
            WindowBox box;
            Assert.True(layouts.TryGet("flight", "mission", UiScale.Factor(1.3f, 1f), out box));
            Assert.Equal(new WindowBox(10, 20, 520, 832), box);
        }

        [Fact]
        public void ABoxSizedAtTheNewBaseKeepsItsScaleInTheFile()
        {
            // Written without it, the line would read back as the old 1.2 and open 25% bigger.
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 400, 640), UiScale.Base);
            Assert.Contains("flight.mission = 10, 20, 400, 640, 0.96\n", layouts.Format());
            var loaded = new WindowLayouts();
            loaded.Parse(layouts.Format().Split('\n'));
            WindowBox box;
            Assert.True(loaded.TryGet("flight", "mission", UiScale.Base, out box));
            Assert.Equal(new WindowBox(10, 20, 400, 640), box);
        }

        [Fact]
        public void TheScaleIsWrittenWithTheBoxAndReadBack()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 750, 1200), 1.8f);
            Assert.Contains("flight.mission = 10, 20, 750, 1200, 1.8", layouts.Format());
            var loaded = new WindowLayouts();
            loaded.Parse(layouts.Format().Split('\n'));
            WindowBox box;
            Assert.True(loaded.TryGet("flight", "mission", 1.2f, out box));
            Assert.Equal(new WindowBox(10, 20, 500, 800), box);
        }

        [Fact]
        public void AtTheOld1_2ScaleTheLineIsWrittenAsBefore()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 500, 800), 1.2f);
            Assert.Contains("flight.mission = 10, 20, 500, 800\n", layouts.Format());
        }

        [Fact]
        public void AScaleReadBackFromTheFileIsTheSameScale()
        {
            // 1.2 x 1.1 x 0.9 is kept as 1.188; the live float is a hair off it.
            var live = 1.2f * 1.1f * 0.9f;
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 501, 799), live);
            var loaded = new WindowLayouts();
            loaded.Parse(layouts.Format().Split('\n'));
            Assert.False(loaded.Set("flight", "mission", new WindowBox(10, 20, 501, 799), live));
            WindowBox box;
            Assert.True(loaded.TryGet("flight", "mission", live, out box));
            Assert.Equal(new WindowBox(10, 20, 501, 799), box);
        }

        [Fact]
        public void TheSameBoxAtANewScaleIsAChange()
        {
            var layouts = new WindowLayouts();
            Assert.True(layouts.Set("flight", "chat", new WindowBox(1, 2, 3, 4), 1.2f));
            Assert.False(layouts.Set("flight", "chat", new WindowBox(1, 2, 3, 4), 1.2f));
            Assert.True(layouts.Set("flight", "chat", new WindowBox(1, 2, 3, 4), 1.8f));
        }

        [Fact]
        public void ALineWithABadScaleIsIgnored()
        {
            var layouts = new WindowLayouts();
            layouts.Parse(new[] { "flight.mission = 10, 20, 500, 800, 0", "flight.chat = 10, 20, 500, 800, x" });
            WindowBox box;
            Assert.False(layouts.TryGet("flight", "mission", 1.2f, out box));
            Assert.False(layouts.TryGet("flight", "chat", 1.2f, out box));
        }

        [Fact]
        public void TheFileIsOutsideTheSaveAndNamedForWindows()
        {
            Assert.Equal("windows.cfg", WindowLayouts.FileName);
        }

        [Fact]
        public void ForgettingABoxPutsTheWindowBackToItsDefaultInThatSceneOnly()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "capcom", new WindowBox(10, 600, 620, 120));
            layouts.Set("vab", "capcom", new WindowBox(10, 600, 620, 120));
            Assert.True(layouts.Remove("flight", "capcom"));
            Assert.False(layouts.Remove("flight", "capcom"));
            WindowBox box;
            Assert.False(layouts.TryGet("flight", "capcom", out box));
            Assert.True(layouts.TryGet("vab", "capcom", out box));
            Assert.DoesNotContain("flight.capcom", layouts.Format());
        }
    }
}
