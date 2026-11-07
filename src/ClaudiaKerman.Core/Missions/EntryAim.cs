// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The craft the last <c>return</c> or <c>deorbit</c> aimed its landing with (#814, <c>ENTRYCRAFT</c>'s key and words), for <c>reentry</c>'s
    /// <c>ENTRYFINISH</c> to name both when it flies another. Kept for the vessel that aimed, and only for an aim that stands: an aiming block
    /// starting again clears it, as does one ending without success (it gave up, so no landing was aimed). Read once.
    /// </summary>
    public sealed class EntryAim
    {
        private uint _vessel;
        private string _key = "";
        private string _why = "";

        /// <summary>The blocks that aim a landing with <c>ENTRYCRAFT</c>.</summary>
        public static bool Aims(string block)
        {
            return block == "return" || block == "deorbit";
        }

        /// <summary>
        /// True when <c>ENTRYFINISH</c>'s <paramref name="ask"/> is <c>reentry</c>'s block-start read (<c>first</c> true, #815): it leaves the aim, so
        /// the drop point's read, the one that flies, is the one compared.
        /// </summary>
        public static bool Leaves(IDictionary<string, object> ask)
        {
            object first = null;
            return ask != null && ask.TryGetValue("first", out first) && Equals(first, true);
        }

        /// <summary><c>ENTRYCRAFT</c> answered for <paramref name="vessel"/> (its persistentId).</summary>
        public void Aimed(uint vessel, string key, string why)
        {
            _vessel = vessel;
            _key = key ?? "";
            _why = why ?? "";
        }

        /// <summary>A block started (<paramref name="result"/> null) or returned its result: an aiming block's start, or its end without success, clears the aim.</summary>
        public void Block(string block, IDictionary<string, object> result)
        {
            if (!Aims(block)) return;
            object success = null;
            if (result != null && result.TryGetValue("success", out success) && Equals(success, true)) return;
            _key = _why = "";
        }

        /// <summary>The aim's key and words for <paramref name="vessel"/>'s Entry, and clears it; "" when it aimed none. Another vessel's Entry leaves it.</summary>
        public string Take(uint vessel, out string why)
        {
            why = "";
            if (_vessel != vessel) return "";
            var key = _key;
            why = _why;
            _key = _why = "";
            return key;
        }
    }
}
