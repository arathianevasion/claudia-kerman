// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>One call of a step, for the Mission log: what was asked and what came back.</summary>
    public sealed class ToolCallRecord
    {
        public string Name = "";
        public string InputJson = "";
        public string Summary = "";
        public bool IsError;
        /// <summary>Why the call was refused without running (an Alert, the pad, a repeat); "" when it ran or hit a limit.</summary>
        public string Refusal = "";
    }

    /// <summary>A step's answers: the user message of tool results that follows Claude's tool_use turn, and a record of each call.</summary>
    public sealed class ToolStepResult
    {
        public Message Results;
        public readonly List<ToolCallRecord> Calls = new List<ToolCallRecord>();
    }

    /// <summary>
    /// One step of a tool loop (#265 "Tool loop"): every tool_use block of Claude's reply gets a tool_result, in order, in one
    /// user message. Up to <see cref="MaxCallsPerStep"/> calls run together, each capped at a timeout; calls past that, calls to a
    /// tool this request doesn't offer, and every call once the loop is at its step limit are answered with an error instead of
    /// run. Blocking: run it on a worker thread.
    /// </summary>
    public static class ToolStep
    {
        public const int MaxCallsPerStep = 6;
        public const int MaxSteps = 8;
        public const string LimitReached = "tool limit reached; reply now";
        public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

        /// <param name="refusal">Why a tool can't be called on this request (e.g. during an Alert), or null when it can.</param>
        /// <param name="atLimit">The loop has had its <see cref="MaxSteps"/> steps: nothing runs, and every result says to reply now.</param>
        public static ToolStepResult Run(PlanningTools tools, AssistantMessage reply, Func<string, string> refusal, bool atLimit,
            GameSnapshot snapshot, IPlanningBody body, TimeSpan timeout)
        {
            return Run(tools, reply, refusal != null ? use => refusal(use.Name) : (Func<ToolUseBlock, string>)null, atLimit, snapshot, body, timeout);
        }

        /// <param name="refusal">Why this call can't run, by its tool and input, asked in the reply's order; null when it can.</param>
        public static ToolStepResult Run(PlanningTools tools, AssistantMessage reply, Func<ToolUseBlock, string> refusal, bool atLimit,
            GameSnapshot snapshot, IPlanningBody body, TimeSpan timeout)
        {
            var uses = reply.Content.OfType<ToolUseBlock>().ToList();
            var outcomes = new ToolOutcome[uses.Count];
            var refusals = new string[uses.Count];
            var running = new List<KeyValuePair<int, Task<ToolOutcome>>>();
            // One source per call: abandoning a call at its timeout cancels only that call's reads (#544).
            var sources = new Dictionary<int, CancellationTokenSource>();
            var ran = 0;
            for (var i = 0; i < uses.Count; i++)
            {
                var use = uses[i];
                var refused = refusal != null ? refusal(use) : null;
                if (atLimit) outcomes[i] = PlanningJson.Error(LimitReached);
                else if (refused != null)
                {
                    outcomes[i] = PlanningJson.Error(refused);
                    refusals[i] = refused;
                }
                else if (ran >= MaxCallsPerStep)
                    outcomes[i] = PlanningJson.Error("at most " + MaxCallsPerStep + " calls run in one step; call this again in the next");
                else
                {
                    ran++;
                    var input = use.InputIsValid ? use.InputJson : "";
                    // A thread of its own, not the pool's: a busy pool would start a call late and charge the wait to its time limit,
                    // and a call that blocks on the adapter's main thread (the body interface) must not hold up the pool.
                    var source = new CancellationTokenSource();
                    sources[i] = source;
                    var token = source.Token;
                    var callBody = body != null ? CancellableBody.Wrap(body, token) : null;
                    running.Add(new KeyValuePair<int, Task<ToolOutcome>>(i, Task.Factory.StartNew(() =>
                        {
                            PlanningCancellation.Set(token);
                            return tools.Run(use.Name, input, snapshot, callBody);
                        },
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
                }
            }

            // All the step's calls run at once; each gets the timeout from the step's start.
            var deadline = DateTime.UtcNow + timeout;
            foreach (var call in running)
            {
                var left = deadline - DateTime.UtcNow;
                if (call.Value.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                    outcomes[call.Key] = call.Value.Result;
                else
                {
                    sources[call.Key].Cancel();
                    outcomes[call.Key] = PlanningJson.Error("the calculation took longer than " + timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s");
                }
            }

            var result = new ToolStepResult { Results = new Message { Role = Role.User } };
            for (var i = 0; i < uses.Count; i++)
            {
                result.Results.Content.Add(new ToolResultBlock { ToolUseId = uses[i].Id, Content = outcomes[i].Json, IsError = outcomes[i].IsError });
                result.Calls.Add(new ToolCallRecord { Name = uses[i].Name ?? "", InputJson = uses[i].InputJson ?? "", Summary = outcomes[i].Summary, IsError = outcomes[i].IsError, Refusal = refusals[i] ?? "" });
            }
            return result;
        }
    }
}
