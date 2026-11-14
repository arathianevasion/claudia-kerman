// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>Mission Control's mark on one Contract row: the Claudia Flag or the hollow "not yet" mark, and its hover text.</summary>
    public sealed class ContractMark
    {
        /// <summary>Claudia can plan it: the row shows the Claudia Flag; else the hollow "not yet" mark.</summary>
        public bool Plannable;
        /// <summary>The row's hover text: "Claudia can plan this: …" or "not yet: …".</summary>
        public string Hover = "";
    }

    /// <summary>
    /// #1027: whether Claudia can plan a Contract offered or accepted in Mission Control, judged by its kind and settings only through
    /// the kind table (<see cref="ContractKinds"/>), never a craft, so the mark grows as each Contract type lands. Like the Contract
    /// check, a Contract is plannable when every Contract Requirement it still needs is (optional ones Claudia can't plan are skipped).
    /// </summary>
    public static class ContractMarks
    {
        /// <summary>A Contract a mod added (its type isn't the game's own) is never planned: Core knows only the stock kinds (ADR 0021).</summary>
        public const string FromMod = "not yet: Contracts added by mods aren't planned yet";

        /// <summary>A Contract with no Contract Requirement left to meet: nothing for Claudia to fly.</summary>
        public const string NothingLeft = "not yet: no Requirement is left to fly for";

        /// <summary>The mark for <paramref name="contract"/>, as the Adapter read it; <paramref name="fromMod"/>: its type isn't the game's own.</summary>
        public static ContractMark Of(SnapshotContract contract, string home, bool fromMod)
        {
            if (fromMod) return new ContractMark { Hover = FromMod };
            var read = ContractKinds.Read(contract, home);
            var blocking = read.FirstOrDefault(r => !r.Plannable && !r.Requirement.Optional);
            if (blocking != null) return new ContractMark { Hover = blocking.NotYet };
            var plannable = read.Where(r => r.Plannable).Select(r => r.Words).Distinct().ToArray();
            var skipped = read.Where(r => !r.Plannable).Select(r => r.NotYet).Distinct().ToArray();
            if (plannable.Length == 0) return new ContractMark { Hover = skipped.Length > 0 ? skipped[0] : NothingLeft };
            var hover = "Claudia can plan this: " + string.Join(", ", plannable);
            if (skipped.Length > 0)
                hover += " (optional Requirement skipped: " + string.Join("; ", skipped.Select(s => s.Substring("not yet: ".Length)).ToArray()) + ")";
            return new ContractMark { Plannable = true, Hover = hover };
        }
    }
}
