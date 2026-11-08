// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Whether <c>launch</c> burns through past <c>target_ap</c> at its first cutoff (#932, ADR 0014), through the block-internal
    /// <c>ADDONS:CLAUDIA:BURNTHROUGH</c> (<see cref="Call"/>). A stage too weak to circularize in the time left can't coast to
    /// apoapsis and burn there: its half burn plus the craft's turn is longer than the time left to apoapsis. Pure; kOS still
    /// reads the orbit, and keeps the steering, the throttle and the staging.
    /// </summary>
    public static class LaunchBurnThrough
    {
        private const double G0 = 9.80665;

        /// <summary>
        /// Takes <c>ap</c> (m above sea level), <c>sma</c> (m), <c>gm</c>, <c>radius</c> (m), <c>eta_ap</c> (s), <c>target_ap</c>
        /// and <c>atm_height</c> (m), <c>stages</c> (<c>claudia_burn_stages</c>'s list: <c>dv</c>, <c>duration</c>, <c>isp</c>, from
        /// the stage burning now down to stage_limit) and <c>turn</c> (TURNTIME's answer; one with an error counts as no turn time).
        /// Gives <c>error</c> ("" or the input it lacks, which coasts), <c>burn_through</c>, and the figures it decided on:
        /// <c>dv</c> (m/s to circularize at apoapsis), <c>half_burn</c> (s; -1 with no stage to burn), <c>turn_time</c> and
        /// <c>eta_ap</c> (s). A hop (<c>target_ap</c> no higher than the atmosphere) never burns through, nor does an orbit with no apoapsis.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var answer = new Dictionary<string, object>
            {
                { "error", "" }, { "burn_through", false }, { "dv", 0.0 }, { "half_burn", -1.0 }, { "turn_time", 0.0 }, { "eta_ap", 0.0 },
            };
            if (input == null)
            {
                answer["error"] = "nothing to decide on";
                return answer;
            }
            foreach (var key in new[] { "ap", "sma", "gm", "radius", "eta_ap", "target_ap", "atm_height" })
            {
                if (!(Value(input, key) is double))
                {
                    answer["error"] = key + " is required";
                    return answer;
                }
            }
            var eta = (double)input["eta_ap"];
            var turn = Value(input, "turn") as IDictionary<string, object>;
            var turnTime = turn != null && Value(turn, "turn_time") is double ? (double)turn["turn_time"] : 0.0;
            answer["eta_ap"] = eta;
            answer["turn_time"] = turnTime;

            var sma = (double)input["sma"];
            var gm = (double)input["gm"];
            var apR = (double)input["radius"] + (double)input["ap"];
            if (sma <= 0 || apR <= 0 || gm <= 0) return answer;
            var dv = Math.Max(0, Math.Sqrt(gm / apR) - Math.Sqrt(Math.Max(0, gm * (2 / apR - 1 / sma))));
            var halfBurn = StagedBurnTime(dv / 2, Value(input, "stages") as IList<object>);
            answer["dv"] = dv;
            answer["half_burn"] = halfBurn;
            var hop = (double)input["target_ap"] <= (double)input["atm_height"];
            answer["burn_through"] = !hop && halfBurn >= 0 && halfBurn + turnTime > eta;
            return answer;
        }

        /// <summary>
        /// Seconds to burn <paramref name="dv"/> over the stages in burn order, as <c>claudia_staged_burn_time</c>: each is burned out
        /// before the next, the last by the rocket equation from its full mass (a straight share with no isp), and a burn past the
        /// last stage runs on at that stage's rate. -1 if no stage has delta-v.
        /// </summary>
        private static double StagedBurnTime(double dv, IList<object> stages)
        {
            var left = dv;
            var total = 0.0;
            var lastDv = 0.0;
            var lastDuration = 0.0;
            if (stages != null)
            {
                foreach (var item in stages)
                {
                    var stage = item as IDictionary<string, object>;
                    if (stage == null) continue;
                    var stageDv = Value(stage, "dv") is double ? (double)stage["dv"] : 0;
                    var duration = Value(stage, "duration") is double ? (double)stage["duration"] : 0;
                    var isp = Value(stage, "isp") is double ? (double)stage["isp"] : 0;
                    if (stageDv <= 0 || duration <= 0) continue;
                    lastDv = stageDv;
                    lastDuration = duration;
                    if (left <= stageDv)
                    {
                        if (isp <= 0) return total + duration * left / stageDv;
                        var exhaust = isp * G0;
                        return total + duration * (1 - Math.Exp(-left / exhaust)) / (1 - Math.Exp(-stageDv / exhaust));
                    }
                    total += duration;
                    left -= stageDv;
                }
            }
            return lastDv <= 0 ? -1 : total + lastDuration * left / lastDv;
        }

        private static object Value(IDictionary<string, object> input, string key)
        {
            object value;
            return input.TryGetValue(key, out value) ? value : null;
        }
    }
}
