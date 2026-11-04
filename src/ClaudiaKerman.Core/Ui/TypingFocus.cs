// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKerman.Core.Ui
{
    /// <summary>
    /// Whether a window's text field has the keyboard, so the keyboard lock can stay on the craft's controls while the
    /// player types. The window records focus from inside its draw, where the field is drawn, on every OnGUI pass. A
    /// Layout pass does not report the focused control reliably, so it neither sets nor clears the state: the lock holds
    /// across every pass of a frame and drops only when a later pass sees focus leave, or the owner calls
    /// <see cref="Reset"/> (window closed, hidden with F2, scene change).
    /// </summary>
    public sealed class TypingFocus
    {
        private readonly string[] _fields;

        public TypingFocus(params string[] fields)
        {
            _fields = fields;
        }

        /// <summary>The player is typing in one of the fields.</summary>
        public bool Held { get; private set; }

        /// <summary>Records the focused control's name from a draw pass. Returns true when <see cref="Held"/> changed.</summary>
        public bool Observe(string focusedName, bool layoutPass)
        {
            if (layoutPass) return false;
            return Set(Array.IndexOf(_fields, focusedName ?? "") >= 0);
        }

        /// <summary>The window can't draw (closed, hidden, scene change): nothing is typed. Returns true when <see cref="Held"/> changed.</summary>
        public bool Reset()
        {
            return Set(false);
        }

        private bool Set(bool held)
        {
            if (held == Held) return false;
            Held = held;
            return true;
        }
    }
}
