// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// Which maneuver nodes a dead Mission Script run leaves behind and the mod removes (#522). A run that ended without a Script
    /// Result (NoReport), was aborted by the mod, or was interrupted has not cleaned up after itself; a run that Finished (blocks
    /// own their cleanup, and some keep a node on purpose), the player stopped, or the player took Manual Control from, is left alone.
    /// </summary>
    public static class NodeCleanup
    {
        /// <summary>Whether the nodes a run added come off the flight plan when it ended this way.</summary>
        public static bool RemovesNodes(RunOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case RunOutcomeKind.NoReport:
                case RunOutcomeKind.Interrupted:
                    return true;
                case RunOutcomeKind.Aborted:
                    return ScriptIpu.EndOf(outcome) != IpuEnd.ManualControl;
                default:
                    return false;
            }
        }

        /// <summary>What to do with a node due for removal (#695).</summary>
        public enum Removal { Now, Wait, Drop }

        /// <summary>
        /// KSP leaves an orphaned map icon when a node is removed while the craft is packed, so: an unloaded craft's node is dropped
        /// (nothing to remove), a packed craft's waits for unpacking, and a loaded, unpacked craft's goes now.
        /// </summary>
        public static Removal WhenToRemove(bool loaded, bool packed)
        {
            if (!loaded) return Removal.Drop;
            return packed ? Removal.Wait : Removal.Now;
        }

        /// <summary>
        /// The nodes in <paramref name="now"/> to remove: those not in <paramref name="atStart"/> (matched by reference, as KSP's
        /// own node objects are), and none at all when <see cref="RemovesNodes"/> says the outcome keeps them.
        /// </summary>
        public static List<T> ToRemove<T>(IEnumerable<T> atStart, IEnumerable<T> now, RunOutcome outcome) where T : class
        {
            if (!RemovesNodes(outcome)) return new List<T>();
            var before = new HashSet<T>(atStart ?? Enumerable.Empty<T>());
            return (now ?? Enumerable.Empty<T>()).Where(n => !before.Contains(n)).ToList();
        }
    }
}
