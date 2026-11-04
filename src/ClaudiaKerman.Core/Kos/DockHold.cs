// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudiaKerman.Core.Kos
{
    /// <summary>
    /// <c>dock_hold</c> (#456, ADR 0013): the mod's own bundled program that it runs on the Target Vessel's kOS processor while the
    /// <c>dock</c> Building Block flies, turning the target's port to the craft's. Claudia doesn't write it, the Script Check
    /// doesn't read it, and it costs no tokens; it runs under ADR 0003's runtime gates like every run the mod starts.
    /// </summary>
    public static class DockHold
    {
        /// <summary>The block whose result ends the hold.</summary>
        public const string Block = "dock";

        private static readonly Lazy<string> Source = new Lazy<string>(() =>
        {
            var assembly = typeof(DockHold).Assembly;
            using (var stream = assembly.GetManifestResourceStream("ClaudiaKerman.Core.dock_hold.ks"))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd().Replace("\r\n", "\n");
        });

        /// <summary>
        /// The program for the target port <paramref name="portUid"/> turned toward the craft's port <paramref name="towardUid"/>,
        /// both as kOS gives a part's <c>UID</c> (digits). Anything else is refused, so nothing but the two numbers reaches the source.
        /// </summary>
        public static string Program(string portUid, string towardUid)
        {
            if (!IsUid(portUid)) throw new ArgumentException("not a part uid: " + portUid, "portUid");
            if (!IsUid(towardUid)) throw new ArgumentException("not a part uid: " + towardUid, "towardUid");
            return Source.Value.Replace("%PORT%", portUid).Replace("%TOWARD%", towardUid);
        }

        /// <summary>A kOS part <c>UID</c>: 1 to 20 digits.</summary>
        public static bool IsUid(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Length <= 20 && text.All(c => c >= '0' && c <= '9');
        }

        /// <summary>
        /// Why the Target Vessel gets no <c>dock_hold</c>, so <c>dock</c> meets it passive; "" when it gets one. It needs to be
        /// loaded, a kOS processor, electric charge, and a link to the craft or KSC by the Child Vessel link test
        /// (<see cref="Missions.ChildVessels.LinkedToClaudia"/>).
        /// </summary>
        public static string PassiveReason(bool loaded, bool hasProcessor, bool charged, bool linked)
        {
            if (!loaded) return "the target is not loaded";
            if (!hasProcessor) return "the target has no kOS processor";
            if (!charged) return "the target has no electric charge";
            if (!linked) return "the target has no link to Claudia";
            return "";
        }

        /// <summary>How a <c>dock_hold</c> run ended, as <c>dock</c> reads it: null when its port was caught (docked), else why it stopped.</summary>
        public static string Fault(RunOutcome outcome)
        {
            if (outcome == null) return "it ended";
            var reason = outcome.Report != null && outcome.Report.ContainsKey("reason") ? Convert.ToString(outcome.Report["reason"]) : "";
            switch (outcome.Kind)
            {
                case RunOutcomeKind.Finished:
                    return reason == "docked" && !outcome.WasAborted ? null : outcome.WasAborted ? outcome.Reason : "it ended: " + reason;
                case RunOutcomeKind.Aborted:
                case RunOutcomeKind.Interrupted:
                    return outcome.Reason.Length > 0 ? outcome.Reason : outcome.Kind.ToString().ToLowerInvariant();
                case RunOutcomeKind.StoppedByPlayer:
                    return "the player stopped it";
                default:
                    var last = outcome.ErrorText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                    return "it failed" + (last != null ? ": " + last : "");
            }
        }
    }

    /// <summary>A <c>dock_hold</c>'s state as <c>dock</c> reads it (<c>ADDONS:CLAUDIA:DOCKHOLDSTATE</c>).</summary>
    public enum DockHoldState
    {
        /// <summary>No hold for this craft's processor.</summary>
        None,
        /// <summary>The mod is starting dock_hold (it waits for the processor, up to 10 s).</summary>
        Starting,
        /// <summary>dock_hold runs, turning the target's port to the craft's.</summary>
        Active,
        /// <summary>dock_hold runs and reports its port settled on its aim (#711): only now does dock close.</summary>
        Settled,
        /// <summary>dock_hold reports a loose settle (#714): its port near its aim; dock closes on it only after settle_timeout.</summary>
        Near,
        /// <summary>No hold: the target is met as it is (why in the reason).</summary>
        Passive,
        /// <summary>The hold stopped while dock still flew (a gate, lost power, an error): dock backs off and fails.</summary>
        Fault,
        /// <summary>The hold saw its port caught.</summary>
        Docked,
    }

    /// <summary>
    /// The mod's coordination of the two runs (#456): <c>dock</c> on the craft's processor and <c>dock_hold</c> on the target's,
    /// with no kOS messaging. One hold per craft processor. The hold is stopped when <c>dock</c> returns (its block result) or the
    /// craft's run ends, however either ends; a hold that ends on its own first, other than by its port being caught, is a
    /// fault that <c>dock</c> reads and backs off from. Main thread only.
    /// </summary>
    public sealed class DockHolds
    {
        private sealed class Hold
        {
            /// <summary>The craft processor whose dock asked for the hold.</summary>
            public uint Craft;
            /// <summary>The target's processor running dock_hold; 0 when passive.</summary>
            public uint Part;
            /// <summary>The hold's run key (never 0); 0 when passive.</summary>
            public int Run;
            public DockHoldState State;
            /// <summary>Why passive or faulted; "" otherwise.</summary>
            public string Reason = "";
        }

        private readonly Dictionary<uint, Hold> _byCraft = new Dictionary<uint, Hold>();

        /// <summary>
        /// <c>dock</c> on processor <paramref name="craft"/> asked for a hold, and the mod is starting <c>dock_hold</c> on
        /// processor <paramref name="part"/> as run <paramref name="run"/>. A hold the craft already had is returned to be stopped.
        /// </summary>
        public bool Starting(uint craft, uint part, int run, out uint oldPart, out int oldRun)
        {
            var old = Take(craft, out oldPart, out oldRun);
            _byCraft[craft] = new Hold { Craft = craft, Part = part, Run = run, State = DockHoldState.Starting };
            return old;
        }

        /// <summary>The target gets no hold (<paramref name="why"/>); a hold the craft already had is returned to be stopped.</summary>
        public bool Passive(uint craft, string why, out uint oldPart, out int oldRun)
        {
            var old = Take(craft, out oldPart, out oldRun);
            _byCraft[craft] = new Hold { Craft = craft, State = DockHoldState.Passive, Reason = why ?? "" };
            return old;
        }

        /// <summary>The run began on its processor.</summary>
        public void Started(uint part, int run)
        {
            var hold = Find(part, run);
            if (hold != null && hold.State == DockHoldState.Starting) hold.State = DockHoldState.Active;
        }

        /// <summary>The run never began (a busy or unpowered processor): dock meets the target passive.</summary>
        public void Refused(uint part, int run, string why)
        {
            var hold = Find(part, run);
            if (hold == null || hold.State != DockHoldState.Starting) return;
            hold.State = DockHoldState.Passive;
            hold.Reason = "dock_hold didn't start: " + (why ?? "");
        }

        /// <summary>
        /// dock_hold on <paramref name="part"/> reported its port settled on its aim, or unsettled by a re-aim (#711,
        /// <c>ADDONS:CLAUDIA:DOCKHOLDSETTLED</c>) at <paramref name="level"/>: "settled", "near" (#714, a loose settle) or "" (unsettled).
        /// Only a running hold moves between active, near and settled; any other report is ignored. True when the report was a running hold's.
        /// </summary>
        public bool Settled(uint part, int run, string level)
        {
            var hold = Find(part, run);
            if (hold == null || !IsLive(hold.State) || hold.State == DockHoldState.Starting) return false;
            hold.State = level == "settled" ? DockHoldState.Settled : level == "near" ? DockHoldState.Near : DockHoldState.Active;
            return true;
        }

        /// <summary>A runtime gate refused a call from the hold, or its processor lost power: a fault, whatever the outcome says after.</summary>
        public void Faulted(uint part, int run, string why)
        {
            var hold = Find(part, run);
            if (hold == null || !IsLive(hold.State)) return;
            hold.State = DockHoldState.Fault;
            hold.Reason = why ?? "";
        }

        /// <summary>The hold's run ended (<paramref name="outcome"/>) while dock still flew: docked, or a fault.</summary>
        public void Ended(uint part, int run, RunOutcome outcome)
        {
            var hold = Find(part, run);
            if (hold == null || !IsLive(hold.State)) return;
            var fault = DockHold.Fault(outcome);
            hold.State = fault == null ? DockHoldState.Docked : DockHoldState.Fault;
            hold.Reason = fault ?? "";
        }

        /// <summary>
        /// <c>dock</c> returned, or the craft's run ended: the hold on <paramref name="craft"/>'s behalf is forgotten, and true with
        /// its run when it may still be running and must be stopped.
        /// </summary>
        public bool Stop(uint craft, out uint part, out int run)
        {
            return Take(craft, out part, out run);
        }

        /// <summary>The hold's state for <paramref name="craft"/>, and why when passive or faulted.</summary>
        public DockHoldState State(uint craft, out string reason)
        {
            Hold hold;
            if (!_byCraft.TryGetValue(craft, out hold))
            {
                reason = "";
                return DockHoldState.None;
            }
            reason = hold.Reason;
            return hold.State;
        }

        /// <summary>The holds whose run is starting or running: part and run, for the per-frame power check.</summary>
        public IList<KeyValuePair<uint, int>> Live()
        {
            return _byCraft.Values.Where(h => IsLive(h.State))
                .Select(h => new KeyValuePair<uint, int>(h.Part, h.Run)).ToList();
        }

        /// <summary>Whether <paramref name="part"/>'s run <paramref name="run"/> is a hold the mod started.</summary>
        public bool IsHold(uint part, int run)
        {
            return Find(part, run) != null;
        }

        /// <summary>The hold's run is starting or running (settled or not).</summary>
        private static bool IsLive(DockHoldState state)
        {
            return state == DockHoldState.Starting || state == DockHoldState.Active || state == DockHoldState.Settled || state == DockHoldState.Near;
        }

        private Hold Find(uint part, int run)
        {
            // A passive hold has no run (0); a started one's key is never 0.
            return _byCraft.Values.FirstOrDefault(h => h.Run != 0 && h.Part == part && h.Run == run);
        }

        private bool Take(uint craft, out uint part, out int run)
        {
            Hold hold;
            part = 0;
            run = 0;
            if (!_byCraft.TryGetValue(craft, out hold)) return false;
            _byCraft.Remove(craft);
            part = hold.Part;
            run = hold.Run;
            // A faulted hold may still run (lost power); stopping one that has ended does nothing.
            return hold.Run != 0 && hold.State != DockHoldState.Passive && hold.State != DockHoldState.Docked;
        }
    }
}
