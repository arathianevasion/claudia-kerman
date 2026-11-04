// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKerman.Core.Kos
{
    /// <summary>
    /// How a mod-started kOS program ended. Only Finished means the script reported; every other kind
    /// is a run that failed, was aborted or was interrupted. (Not a Debrief outcome: a Debrief can be
    /// Nominal, Off-Nominal or Failed whatever the run's kind.)
    /// </summary>
    public enum RunOutcomeKind
    {
        /// <summary>The script reported before the program ended.</summary>
        Finished,
        /// <summary>The script errored, or returned without reporting.</summary>
        NoReport,
        /// <summary>The mod stopped the program (<c>BreakExecution</c>) and recorded why.</summary>
        Aborted,
        /// <summary>The player pressed Ctrl+C in the terminal: kOS printed its break message.</summary>
        StoppedByPlayer,
        /// <summary>The program was cut off from outside: a scene change, or the processor was lost.</summary>
        Interrupted,
    }

    public sealed class RunOutcome
    {
        public RunOutcomeKind Kind { get; private set; }

        /// <summary>
        /// The script's last <c>ADDONS:CLAUDIA:REPORT</c> lexicon when Finished. A run that ended inside a block that had traced
        /// its state (<see cref="KosRun.BlockTraced"/>) with no Script Result carries the mod's stand-in (#365): success false,
        /// block, reason and last_state, with the run's kind unchanged. Null otherwise.
        /// </summary>
        public IDictionary<string, object> Report { get; private set; }

        /// <summary>What kOS printed after the run started (a terminal tail), NULs stripped; "" when Finished.</summary>
        public string ErrorText { get; private set; }

        /// <summary>Why the run was aborted or interrupted; "" otherwise.</summary>
        public string Reason { get; private set; }

        /// <summary>The mod aborted the run, even if the script had already reported.</summary>
        public bool WasAborted { get; private set; }

        /// <summary>The run's warp trace (#335): every warp request and drop, and each block's warp timings; null if none was kept.</summary>
        public WarpTrace Trace { get; private set; }

        public RunOutcome(RunOutcomeKind kind, IDictionary<string, object> report, string errorText, string reason, bool wasAborted,
            WarpTrace trace = null)
        {
            Trace = trace;
            Kind = kind;
            Report = report;
            ErrorText = errorText ?? "";
            Reason = reason ?? "";
            WasAborted = wasAborted;
        }

        /// <summary>
        /// Whether the mod makes the craft safe once the run has ended (#598): an aborted run, and any run that ended without
        /// a Script Result (a kOS error, a return without reporting, Ctrl+C), since kOS leaves the main throttle where the program
        /// last set it. A Finished run is the library's own claudia_finish / claudia_make_safe; an Interrupted one
        /// (scene change, lost processor) has no live craft to steer.
        /// </summary>
        public bool MakesCraftSafe
        {
            get { return WasAborted || (Kind != RunOutcomeKind.Finished && Kind != RunOutcomeKind.Interrupted); }
        }

        /// <summary>One line for logs and the debug window, e.g."Aborted (player pressed Abort)".</summary>
        public string Summary
        {
            get { return Kind + (Reason.Length > 0 ? " (" + Reason + ")" : ""); }
        }
    }

    /// <summary>An open block's last traced state (<see cref="KosRun.BlockTraced"/>), including the "ut" it was traced at.</summary>
    public sealed class TracedBlock
    {
        public string Block { get; private set; }
        public IDictionary<string, object> State { get; private set; }

        public TracedBlock(string block, IDictionary<string, object> state)
        {
            Block = block;
            State = state;
        }
    }

    /// <summary>
    /// One mod-started program on one kOS processor, from start to its outcome. The adapter feeds it
    /// kOS events; it decides how the run ended.
    /// </summary>
    public sealed class KosRun
    {
        /// <summary>
        /// What kOS 1.6 prints when a program is broken manually (Ctrl+C or <c>BreakExecution(true)</c>);
        /// a string literal in kOS.Safe 1.6.0.1.
        /// </summary>
        public const string BreakMessage = "Program aborted.";

        private readonly IList<string> _terminalBefore;
        private IDictionary<string, object> _report;
        private string _abortReason;
        private string _interruptReason;
        private readonly List<TracedBlock> _traced = new List<TracedBlock>();

        /// <param name="terminalBefore">The processor's terminal lines when the run started.</param>
        public KosRun(IList<string> terminalBefore)
        {
            _terminalBefore = terminalBefore;
        }

        /// <summary>The run's warp trace (#335), which the adapter feeds; handed on in the outcome. Null if none is kept.</summary>
        public WarpTrace Trace { get; set; }

        /// <summary>The script called <c>ADDONS:CLAUDIA:REPORT(lexicon)</c>.</summary>
        public void Report(IDictionary<string, object> report)
        {
            if (!HasEnded) _report = report;
        }

        /// <summary>A block traced its state (<c>ADDONS:CLAUDIA:BLOCKTRACE</c>, #365) at ut; the latest is kept until that block returns.
        /// Traced state is kept per open block, in the order the blocks first traced (outermost first). A block that traces again keeps
        /// its place and replaces its own state; a repeated name (recursion, or the same block twice) is one block as far as the trace
        /// goes, so its ending drops that name's state. With keep false (the RCS Use trace and the SAS-off note, #407) the line is for
        /// KSP.log only and nothing kept changes.</summary>
        public void BlockTraced(string block, double ut, IDictionary<string, object> state, bool keep = true)
        {
            if (HasEnded || !keep) return;
            var kept = new Dictionary<string, object>(state ?? new Dictionary<string, object>());
            kept["ut"] = ut;
            var name = block ?? "";
            var entry = new TracedBlock(name, kept);
            var i = _traced.FindIndex(t => t.Block == name);
            if (i >= 0) _traced[i] = entry;
            else _traced.Add(entry);
        }

        /// <summary>A block returned its result (<c>BLOCKRESULT</c>): only its own traced state goes; the blocks around it keep theirs.
        /// A block that ends having never traced changes nothing.</summary>
        public void BlockEnded(string block)
        {
            var name = block ?? "";
            _traced.RemoveAll(t => t.Block == name);
        }

        /// <summary>The innermost open block that has traced, with its last traced state; null when no open block has.
        /// What the stand-in report (#365) names, and what a Lost Mission can record (#375).</summary>
        public TracedBlock InnermostTraced
        {
            get { return _traced.Count == 0 ? null : _traced[_traced.Count - 1]; }
        }

        /// <summary>True once kOS has popped the program context.</summary>
        public bool HasEnded { get; private set; }

        /// <summary>
        /// The mod is stopping the program. Only the mod knows a break was its own, so it records
        /// the reason here before calling <c>BreakExecution</c>.
        /// </summary>
        public void Aborted(string reason)
        {
            _abortReason = reason;
        }

        /// <summary>Something outside the program is ending it (leaving Flight, losing the processor).</summary>
        public void Interrupted(string reason)
        {
            if (_interruptReason == null) _interruptReason = reason;
        }

        /// <summary>kOS popped the program context: the program is over.</summary>
        public void ProgramEnded()
        {
            HasEnded = true;
        }

        /// <summary>
        /// Decides the outcome, given the terminal read a frame after the program ended (kOS prints
        /// the error only after the context pops).
        /// </summary>
        public RunOutcome Finish(IList<string> terminalAfter)
        {
            var aborted = _abortReason != null;
            if (_report != null) return new RunOutcome(RunOutcomeKind.Finished, _report, "", _abortReason, aborted, Trace);
            var lines = TerminalText.LinesSince(_terminalBefore, terminalAfter);
            var printed = string.Join("\n", lines);
            var traced = TracedReport();
            if (aborted) return new RunOutcome(RunOutcomeKind.Aborted, traced, printed, _abortReason, true, Trace);
            if (_interruptReason != null) return new RunOutcome(RunOutcomeKind.Interrupted, traced, printed, _interruptReason, false, Trace);
            if (lines.Contains(BreakMessage)) return new RunOutcome(RunOutcomeKind.StoppedByPlayer, traced, printed, "", false, Trace);
            return new RunOutcome(RunOutcomeKind.NoReport, traced, printed, "", false, Trace);
        }

        /// <summary>What a run that ended mid-block, with no Script Result, says for it: the innermost open block that traced and its
        /// last traced state (the outer blocks' states are not listed); null with none.</summary>
        private IDictionary<string, object> TracedReport()
        {
            var innermost = InnermostTraced;
            if (innermost == null) return null;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "block", innermost.Block },
                { "reason", "the run ended inside " + innermost.Block + " with no Script Result; last_state is the block's last traced state, from the mod" },
                { "last_state", innermost.State },
            };
        }
    }
}
