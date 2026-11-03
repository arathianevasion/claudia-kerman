// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>A biome the Mission Goal names, and the body it is on (#699, <see cref="MissionPrompts.GoalBiomes"/>).</summary>
    public sealed class GoalBiome
    {
        public string Body = "";
        public string Biome = "";
    }

    /// <summary>A landing whose target is in another biome of the body than the one the Mission Goal names (#699).</summary>
    public sealed class GoalBiomeMiss
    {
        /// <summary>The goal's biome on the body the landing is on.</summary>
        public GoalBiome Goal;
        /// <summary>The biome the landing targets.</summary>
        public string Targeted = "";
    }

    /// <summary>
    /// #699: before Approval, the biome each <c>land</c> call in a Mission Script targets, against the biome the Mission Goal
    /// names on that body. A target is judged only when it can be read: a <c>lexicon("biome", ...)</c> with a literal name, or
    /// a <c>LATLNG</c> / lat-lng lexicon of literal numbers on the body the craft is at, whose biome the game gives. Options are
    /// read through <see cref="BlockOptionCheck"/>. "any", "vessel" and anything computed are never judged.
    /// </summary>
    public static class LandingBiomes
    {
        private static readonly Regex LatLng = new Regex(@"^latlng\s*\(\s*(?<lat>-?\d+(\.\d+)?)\s*,\s*(?<lng>-?\d+(\.\d+)?)\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Text = new Regex("^\"(?<s>[^\"]*)\"$", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The first <c>land</c> call aimed at another biome than the goal names on its body, or null when none is.
        /// <paramref name="craftBody"/> is the body the craft is at; <paramref name="biomeAt"/> gives the game's biome at a point
        /// of a body ("" when it can't).
        /// </summary>
        public static GoalBiomeMiss Miss(string script, IList<GoalBiome> goal, GameSnapshot snapshot, string craftBody, Func<string, double, double, string> biomeAt)
        {
            if (goal == null || goal.Count == 0 || string.IsNullOrWhiteSpace(script)) return null;
            foreach (var call in BlockOptionCheck.Calls(script).Where(c => c.Block == "land"))
            {
                var target = call.Option("target");
                if (target == null) continue;
                string body, targeted;
                if (!Targeted(target.Trim(), goal, snapshot, craftBody, biomeAt, out body, out targeted)) continue;
                var here = goal.Where(g => g.Body == body).ToList();
                if (here.Count == 0 || here.Any(g => string.Equals(g.Biome, targeted, StringComparison.OrdinalIgnoreCase))) continue;
                return new GoalBiomeMiss { Goal = here[0], Targeted = targeted };
            }
            return null;
        }

        /// <summary>The body and biome a land target aims at, when it can be read.</summary>
        private static bool Targeted(string target, IList<GoalBiome> goal, GameSnapshot snapshot, string craftBody, Func<string, double, double, string> biomeAt,
            out string body, out string biome)
        {
            var at = craftBody ?? "";
            body = at;
            biome = "";
            double lat, lng;
            var options = BlockOptionCheck.Lexicon(target);
            if (options != null && options.ContainsKey("biome"))
            {
                var named = Text.Match(options["biome"].Trim());
                if (!named.Success || named.Groups["s"].Value.Trim().Length == 0) return false;
                biome = named.Groups["s"].Value.Trim();
                // Off the goal's body for now (a script that flies there first): the goal body whose biomes include the name.
                if (goal.All(g => g.Body != at))
                {
                    var name = biome;
                    var owner = goal.Select(g => g.Body).Distinct().FirstOrDefault(b => snapshot != null && snapshot.Bodies.Any(s => s.Name == b
                        && s.BiomeNames.Contains(name, StringComparer.OrdinalIgnoreCase)));
                    if (owner == null) return false;
                    body = owner;
                }
                return true;
            }
            if (options != null) { if (!Point(options, out lat, out lng)) return false; }
            else
            {
                var point = LatLng.Match(target);
                if (!point.Success) return false;
                lat = double.Parse(point.Groups["lat"].Value, CultureInfo.InvariantCulture);
                lng = double.Parse(point.Groups["lng"].Value, CultureInfo.InvariantCulture);
            }
            if (goal.All(g => g.Body != at) || biomeAt == null) return false;
            biome = biomeAt(body, lat, lng) ?? "";
            return biome.Length > 0;
        }

        private static bool Point(IDictionary<string, string> options, out double lat, out double lng)
        {
            lat = lng = 0;
            string a, b;
            if (!options.TryGetValue("lat", out a) || !options.TryGetValue("lng", out b)) return false;
            a = a.Trim();
            b = b.Trim();
            if (!Number.IsMatch(a) || !Number.IsMatch(b)) return false;
            lat = double.Parse(a, CultureInfo.InvariantCulture);
            lng = double.Parse(b, CultureInfo.InvariantCulture);
            return true;
        }
    }
}
