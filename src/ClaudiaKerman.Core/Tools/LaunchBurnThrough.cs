// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Whether <c>launch</c> burns through past <c>target_ap</c> at its first cutoff and on every tick of the burn-through after it (#932, ADR 0014), through the
    /// block-internal <c>ADDONS:CLAUDIA:BURNTHROUGH</c> (<see cref="Call"/>): the same decision each time, so the burn-through ends as soon as a coast would work. A stage too weak to circularize in the time left can't coast to
    /// apoapsis and burn there: its half burn plus the craft's turn is longer than the time left when <c>launch</c> ends at the atmosphere
    /// top, since <c>circularize</c> can't begin turning before that. Pure; kOS still reads the orbit, and keeps the steering, the throttle and the staging.
    /// </summary>
    public static class LaunchBurnThrough
    {
        /// <summary>
        /// Takes <c>ap</c> (m above sea level), <c>sma</c> (m), <c>gm</c>, <c>radius</c> (m), <c>eta_ap</c> (s), <c>altitude</c>, <c>target_ap</c>
        /// and <c>atm_height</c> (m), <c>angle</c> (degrees from the nose now to orbital prograde at apoapsis), <c>stages</c>
        /// (<c>claudia_burn_stages</c>'s list: <c>dv</c>, <c>duration</c>, <c>isp</c>, from the stage burning now down to stage_limit)
        /// and <c>turn</c> (TURNTIME's answer; one with an error counts as no turn time). <c>burning</c> (optional, default false) says a burn-through is already under way.
        /// Gives <c>error</c> ("" or the input it lacks, which coasts), <c>burn_through</c>, and the figures it decided on:
        /// <c>dv</c> (m/s to circularize at apoapsis), <c>half_burn</c> (s; -1 with no stage to burn), <c>turn_time</c> (s, the turn
        /// through <c>angle</c> and its settle time), <c>angle</c>, <c>eta_ap</c>, <c>climb</c> (s to the atmosphere top, 0 if already above it) and
        /// <c>time_left</c> (s, <c>eta_ap</c> less <c>climb</c>: what the half burn and turn are held against). A hop (<c>target_ap</c> no higher than the
        /// atmosphere) never burns through, nor does an orbit with no apoapsis. Past apoapsis (<c>eta_ap</c> over half the orbit) no time is left, as the next apoapsis is across the air.
        /// A stage table with no figures coasts, unless <c>burning</c>: stock's can read empty for a moment around a stage change, and an end isn't undone.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input)
        {
            var answer = new Dictionary<string, object>
            {
                { "error", "" }, { "burn_through", false }, { "dv", 0.0 }, { "half_burn", -1.0 }, { "turn_time", 0.0 }, { "angle", 0.0 }, { "eta_ap", 0.0 }, { "climb", 0.0 }, { "time_left", 0.0 },
            };
            if (input == null)
            {
                answer["error"] = "nothing to decide on";
                return answer;
            }
            foreach (var key in new[] { "ap", "sma", "gm", "radius", "eta_ap", "altitude", "target_ap", "atm_height", "angle" })
            {
                if (!(Value(input, key) is double))
                {
                    answer["error"] = key + " is required";
                    return answer;
                }
            }
            var eta = (double)input["eta_ap"];
            var angle = (double)input["angle"];
            var turnTime = SecondsToTurn(angle, Value(input, "turn") as IDictionary<string, object>);
            answer["eta_ap"] = eta;
            answer["time_left"] = eta;
            answer["angle"] = angle;
            answer["turn_time"] = turnTime;

            var sma = (double)input["sma"];
            var gm = (double)input["gm"];
            var radius = (double)input["radius"];
            var atmHeight = (double)input["atm_height"];
            var apR = radius + (double)input["ap"];
            if (sma <= 0 || apR <= 0 || gm <= 0) return answer;
            var dv = Math.Max(0, Math.Sqrt(gm / apR) - Math.Sqrt(Math.Max(0, gm * (2 / apR - 1 / sma))));
            var halfBurn = StagedBurnTime.Of(dv / 2, Value(input, "stages") as IList<object>);
            answer["dv"] = dv;
            answer["half_burn"] = halfBurn;
            var climb = SecondsToAtmosphereTop(radius + (double)input["altitude"], radius + atmHeight, apR, sma, gm);
            var pastApoapsis = eta > Math.PI * Math.Sqrt(sma * sma * sma / gm);
            var timeLeft = pastApoapsis ? 0 : Math.Max(0, eta - climb);
            answer["climb"] = climb;
            answer["time_left"] = timeLeft;
            var hop = (double)input["target_ap"] <= atmHeight;
            var burning = Value(input, "burning") is bool && (bool)input["burning"];
            answer["burn_through"] = !hop && (halfBurn >= 0 ? halfBurn + turnTime > timeLeft : burning);
            return answer;
        }

        /// <summary>
        /// Seconds to climb from <paramref name="r"/> to <paramref name="rTop"/> on the orbit at the cutoff, unpowered (Kepler: an estimate, as drag and
        /// <c>launch</c>'s top-ups change the orbit a little on the way). 0 if the craft is already above the top, or the orbit never reaches it.
        /// </summary>
        private static double SecondsToAtmosphereTop(double r, double rTop, double apR, double sma, double gm)
        {
            if (r >= rTop || apR <= rTop) return 0;
            var orbit = Conic.FromElements(gm, sma, apR / sma - 1, 0, 0, 0, 0, 0);
            var atCutoff = orbit.TimeAtRadius(r, 0, true);
            var atTop = orbit.TimeAtRadius(rTop, 0, true);
            return atCutoff.HasValue && atTop.HasValue ? Math.Max(0, atTop.Value - atCutoff.Value) : 0;
        }

        /// <summary>
        /// Seconds to turn <paramref name="angle"/> degrees and settle, from TURNTIME's 190 degree figures: the craft is already near
        /// prograde at the cutoff, so its whole turn_time would call a healthy craft too slow (#932). The torque is worked back from
        /// top_rate and turn_s, as <c>land_turn_seconds</c> does. Not TURNTIME's 30 s floor, which is a block's margin before a burn:
        /// a coast that works is what flies today. 0 with no turn to make or no figures to make it from.
        /// </summary>
        private static double SecondsToTurn(double angle, IDictionary<string, object> turn)
        {
            if (turn == null || !(Value(turn, "turn_s") is double) || !(Value(turn, "top_rate") is double)) return 0;
            var turnS = (double)turn["turn_s"];
            var topRate = (double)turn["top_rate"];
            if (angle <= 0 || turnS <= 0 || topRate <= 0) return 0;
            // The seconds to reach top_rate: turn_s less the 190 degrees at top_rate, whether that turn reaches the cap or peaks at it
            // (a burst turn's turn_s also holds the wheels' settle, so its figure runs a few seconds long).
            var ramp = Math.Max(0.001, turnS - TurnTime.TurnDegrees / topRate);
            double peak;
            return TurnTime.CappedTurn(angle, topRate / ramp, ramp, out peak) + TurnTime.SettleSeconds;
        }

        private static object Value(IDictionary<string, object> input, string key)
        {
            object value;
            return input.TryGetValue(key, out value) ? value : null;
        }
    }
}
