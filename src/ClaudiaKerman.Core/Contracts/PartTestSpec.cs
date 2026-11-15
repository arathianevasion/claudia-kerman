// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1029: where a part test's moment falls in the flight: which block runs it.</summary>
    public enum TestWindow
    {
        /// <summary>No moment to time: a haul, or a test met in the situation alone.</summary>
        None = 0,
        /// <summary>On the way up, in <c>launch</c> (a hop's climb included).</summary>
        Ascent = 1,
        /// <summary>On the way down, in <c>reentry</c>: through the band, or home again landed or splashed down.</summary>
        Descent = 2,
    }

    /// <summary>#1029: a part test or haul as the kind table reads it: the part, whether it is only carried, the situation and its band.</summary>
    public sealed class PartTestSpec
    {
        /// <summary>The part's name as KSP's part list has it, "solidBooster.sm.v2".</summary>
        public string Part = "";
        /// <summary>The part is only carried there: no test action.</summary>
        public bool Haul;
        /// <summary>The game's situation it asks: FLYING, SUB_ORBITAL, LANDED, SPLASHED, ORBITING.</summary>
        public string Situation = "";
        /// <summary>The altitude band and speed band it asks, over the launch body; null for none.</summary>
        public HopBand Band;
        /// <summary>The part is a parachute: its test is its opening, by ADR 0019's group and reentry's own timing.</summary>
        public bool Chute;
        /// <summary>The block whose flight runs it, as the Contract check found; <see cref="TestWindow.None"/> for a haul.</summary>
        public TestWindow Window;

        public PartTestSpec Copy()
        {
            return (PartTestSpec)MemberwiseClone();
        }
    }
}
