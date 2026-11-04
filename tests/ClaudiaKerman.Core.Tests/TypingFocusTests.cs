// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public class TypingFocusTests
    {
        [Fact]
        public void FocusInAFieldHoldsTheLockAcrossEveryPassOfAFrame()
        {
            var focus = new TypingFocus("Prompt", "Raise");
            Assert.True(focus.Observe("Prompt", layoutPass: false));
            // Layout reads an empty name, then Repaint and a key event see the field again.
            Assert.False(focus.Observe("", layoutPass: true));
            Assert.True(focus.Held);
            Assert.False(focus.Observe("Prompt", layoutPass: false));
            Assert.True(focus.Held);
            Assert.False(focus.Observe("Raise", layoutPass: false));
            Assert.True(focus.Held);
        }

        [Fact]
        public void FocusLeavingReleasesTheLock()
        {
            var focus = new TypingFocus("Prompt");
            focus.Observe("Prompt", false);
            Assert.True(focus.Observe("", false));
            Assert.False(focus.Held);
        }

        [Fact]
        public void AnotherControlOrNoNameIsNotTyping()
        {
            var focus = new TypingFocus("Prompt");
            Assert.False(focus.Observe("Send", false));
            Assert.False(focus.Observe(null, false));
            Assert.False(focus.Held);
        }

        [Fact]
        public void HidingOrASceneChangeReleasesTheLock()
        {
            var focus = new TypingFocus("Prompt");
            focus.Observe("Prompt", false);
            Assert.True(focus.Reset());
            Assert.False(focus.Held);
            Assert.False(focus.Reset());
        }
    }
}
