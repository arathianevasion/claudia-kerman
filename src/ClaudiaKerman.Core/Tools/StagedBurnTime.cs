// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The one burn-timing rule (#935, ADR 0014), through the block-internal <c>ADDONS:CLAUDIA:BURNTIME</c> (<see cref="Call"/>) for <c>claudia_burn_timing</c>
    /// and, as <see cref="Of"/>, for <see cref="LaunchBurnThrough"/>. Pure.
    /// </summary>
    public static class StagedBurnTime
    {
        private const double G0 = 9.80665;

        /// <summary>
        /// Takes <c>dv</c> (m/s) and <c>stages</c> (<c>claudia_burn_stages</c>'s list: <c>dv</c>, <c>duration</c>, <c>isp</c>, in burn order). Gives <c>error</c>
        /// ("" or the input it lacks), <c>burn_time</c> (s to burn dv) and <c>half_burn</c> (s to burn half of it), each -1 with no stage to burn.
        /// One call gives both so a block asks once.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var answer = new Dictionary<string, object> { { "error", "" }, { "burn_time", -1.0 }, { "half_burn", -1.0 } };
            if (input == null)
            {
                answer["error"] = "nothing to time";
                return answer;
            }
            object dv;
            if (!input.TryGetValue("dv", out dv) || !(dv is double))
            {
                answer["error"] = "dv is required";
                return answer;
            }
            object stages;
            input.TryGetValue("stages", out stages);
            var list = stages as IList<object>;
            answer["burn_time"] = Of((double)dv, list);
            answer["half_burn"] = Of((double)dv / 2, list);
            return answer;
        }

        /// <summary>
        /// Seconds to burn <paramref name="dv"/> over the stages in burn order: each is burned out before the next, the last
        /// by the rocket equation from its full mass (its duration over 1 - e^(-dv/ve); a straight share with no isp), and a burn past
        /// the last stage runs on at that stage's rate. A stage with no delta-v or no duration is skipped. -1 if none has either.
        /// </summary>
        public static double Of(double dv, IList<object> stages)
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
                    var stageDv = Figure(stage, "dv");
                    var duration = Figure(stage, "duration");
                    var isp = Figure(stage, "isp");
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

        private static double Figure(IDictionary<string, object> stage, string key)
        {
            object value;
            return stage.TryGetValue(key, out value) && value is double ? (double)value : 0;
        }
    }
}
