// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1029: whether a flight passes through a part test's band, judged on the craft's own figures (ADR 0021: the mod's logic): the launch
    /// to orbit and a hop straight up by plan_ascent's <see cref="AscentSim"/>, the way down by the reentry predictor with the Last Entry's
    /// chutes. A chute's test is its opening (ADR 0019): planned only when the chute is in the Entry's Chute Group and reentry's own rule
    /// opens it inside the band; a stage is never fired for it. Each flight is flown once per check and kept.
    /// </summary>
    internal sealed class PartTestJudge
    {
        /// <summary>The Card's reason when the test chute isn't one the Entry opens.</summary>
        public const string ChuteNotInGroup = "the test chute isn't in this Entry's chute group";

        private readonly GameSnapshot _snapshot;
        private readonly SnapshotBody _home;
        private List<double[]> _ascent;
        private string _ascentWhy;
        private readonly Dictionary<long, Tuple<List<double[]>, string>> _hops = new Dictionary<long, Tuple<List<double[]>, string>>();
        private readonly Dictionary<long, Tuple<ReentryPrediction, Dictionary<ReentryChute, string>>> _falls = new Dictionary<long, Tuple<ReentryPrediction, Dictionary<ReentryChute, string>>>();

        public PartTestJudge(GameSnapshot snapshot, string home)
        {
            _snapshot = snapshot;
            _home = SequenceWords.Body(snapshot, home);
        }

        /// <summary>"this craft can't: the part X isn't on the craft" when it isn't; null when it is.</summary>
        public string Missing(PartTestSpec test)
        {
            return _snapshot.Craft.Parts.Any(p => p.Name == test.Part) ? null : "this craft can't: the part " + test.Part + " isn't on the craft";
        }

        /// <summary>The part tested is a parachute.</summary>
        public bool IsChute(PartTestSpec test)
        {
            return _snapshot.Craft.Parts.Any(p => p.Name == test.Part && (p.Chute != null || p.Tags.Contains("parachute")));
        }

        /// <summary>
        /// Where <paramref name="test"/>'s band is met on a flight with these legs: a hop straight up to <paramref name="hopAp"/> m, or the
        /// launch to orbit with <paramref name="orbit"/>, and the way down home with the craft at <paramref name="descentKg"/> (null: no way
        /// down). The ascent is preferred. <see cref="TestWindow.None"/> with <paramref name="why"/> when none meets it.
        /// </summary>
        public TestWindow Window(PartTestSpec test, double? hopAp, bool orbit, double? descentKg, out string why)
        {
            why = null;
            var band = test.Band;
            if (band == null) return TestWindow.None;
            var misses = new List<string>();
            if (!IsChute(test))
            {
                var up = hopAp.HasValue ? Hop(hopAp.Value) : orbit ? Ascent() : null;
                if (up != null)
                {
                    var miss = up.Item2 ?? band.Misses(up.Item1);
                    if (miss == null) return TestWindow.Ascent;
                    misses.Add((hopAp.HasValue ? "on the hop: " : "on the ascent: ") + miss);
                }
            }
            if (descentKg.HasValue)
            {
                var miss = Down(test, descentKg.Value);
                if (miss == null) return TestWindow.Descent;
                misses.Add(miss);
            }
            why = misses.Count == 0 ? "no leg passes " + band.Through() : string.Join("; ", misses.ToArray());
            return TestWindow.None;
        }

        /// <summary>Why the way down misses the band; null when it passes it (a chute: when reentry opens it inside the band).</summary>
        public string Down(PartTestSpec test, double massKg)
        {
            var fall = Fall(massKg);
            if (fall.Item1.Error != null) return "the way down can't be predicted: " + fall.Item1.Error;
            if (!IsChute(test)) return Prefix("on the way down: ", test.Band.Misses(fall.Item1.Profile));
            var chute = fall.Item2.FirstOrDefault(c => c.Value == test.Part).Key;
            if (chute == null) return ChuteNotInGroup;
            double[] armed;
            if (!fall.Item1.Armed.TryGetValue(chute, out armed)) return "the test chute doesn't open on the way down";
            if (test.Band.HoldsAltitude(armed[0]) && test.Band.HoldsSpeed(armed[1])) return null;
            return "reentry opens the test chute at " + Whole(armed[0]) + " m and " + Whole(armed[1]) + " m/s, outside " + test.Band.Through()
                + (test.Band.MinSpeed.HasValue || test.Band.MaxSpeed.HasValue ? " at " + test.Band.Words("").Split(new[] { " at " }, StringSplitOptions.None).Last().Replace(" over ", "") : "");
        }

        private static string Prefix(string prefix, string miss)
        {
            return miss == null ? null : prefix + miss;
        }

        private Tuple<List<double[]>, string> Ascent()
        {
            if (_ascent == null)
            {
                string why;
                // A climb that fails short of orbit by the default shape still says how fast it passes the heights it reached.
                var flight = SuborbitalHop.Fly(_snapshot, null, true, out why);
                _ascent = flight == null ? new List<double[]>() : Path(flight, _home == null ? 0 : _home.AtmosphereHeight);
                _ascentWhy = flight == null ? why : null;
            }
            return Tuple.Create(_ascent, _ascentWhy);
        }

        private Tuple<List<double[]>, string> Hop(double targetAp)
        {
            var key = (long)Math.Round(targetAp);
            Tuple<List<double[]>, string> hop;
            if (_hops.TryGetValue(key, out hop)) return hop;
            string why;
            var flight = SuborbitalHop.Fly(_snapshot, targetAp, true, out why);
            hop = Tuple.Create(flight == null ? new List<double[]>() : Path(flight, targetAp), flight == null ? why : null);
            _hops[key] = hop;
            return hop;
        }

        /// <summary>The climb as (altitude, speed) points: the pad, each tracked row while burning, then the coast up to <paramref name="top"/>.</summary>
        private List<double[]> Path(AscentFlight flight, double top)
        {
            var path = new List<double[]> { new[] { Math.Max(0, _snapshot.Altitude), 0.0 } };
            path.AddRange(flight.Track.Select(r => new[] { r[0], Math.Sqrt(r[9] * r[9] + r[10] * r[10]) }));
            if (_home == null || flight.Track.Count == 0) return path;
            var last = flight.Track[flight.Track.Count - 1];
            double h = last[0], horizontal = last[9], vertical = last[10];
            // The coast: the climb trades vertical speed for height under the body's pull.
            for (var at = Math.Floor(h / 500) * 500 + 500; at <= top && vertical > 0; at += 500)
            {
                var g = _home.Gm / Math.Pow(_home.Radius + at, 2);
                var left = vertical * vertical - 2 * g * (at - h);
                if (left < 0) break;
                path.Add(new[] { at, Math.Sqrt(horizontal * horizontal + left) });
                vertical = Math.Sqrt(left);
                h = at;
            }
            return path;
        }

        /// <summary>The fall home of a craft of <paramref name="massKg"/>, the Last Entry's Chute Group aboard (each copy with its part's name).</summary>
        private Tuple<ReentryPrediction, Dictionary<ReentryChute, string>> Fall(double massKg)
        {
            var key = (long)Math.Round(massKg / 10);
            Tuple<ReentryPrediction, Dictionary<ReentryChute, string>> fall;
            if (_falls.TryGetValue(key, out fall)) return fall;
            var chutes = new Dictionary<ReentryChute, string>();
            foreach (var chute in ChuteGroups.Choose(ChuteGroups.Usable(_snapshot), true))
            {
                var part = _snapshot.Craft.Parts.First(p => p.Id == chute.PartId);
                chutes[ChuteGroups.Copy(part.Chute)] = part.Name;
            }
            var prediction = _home == null || _home.AtmosphereHeight <= 0
                ? new ReentryPrediction { Error = "the Game Snapshot has no air for the launch body" }
                : ChuteGroups.Fall(_home, massKg, chutes.Keys.ToList(), true);
            fall = Tuple.Create(prediction, chutes);
            _falls[key] = fall;
            return fall;
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
