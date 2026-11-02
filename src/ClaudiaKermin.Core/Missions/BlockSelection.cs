// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Which Building Blocks' full docs go in the per-Mission layer. Every block keeps its Summary in the base
    /// layer, so a block left out here can still be named and its options asked for; this only decides which
    /// ones are spelled out in full. A pure decision from the Mission Goal: a goal that points at nothing
    /// in particular gets every block but those only its words bring (change_orbit, rendezvous), and a block this table doesn't know is
    /// always included.
    /// </summary>
    internal static class BlockSelection
    {
        /// <summary>Blocks almost every flight uses, whatever the goal says.</summary>
        private static readonly string[] Always = { "coast", "exec_node", "circularize" };
        /// <summary>
        /// Blocks whose full docs come only when the goal's words point at them, even for a goal that points at nothing in
        /// particular (#444): change_orbit's orbit changes are a goal's own (a polar orbit, Minmus's plane), and its Summary in
        /// the base layer names it for the rest. So are rendezvous's (#455): only a goal with another vessel in it flies one.
        /// </summary>
        private static readonly HashSet<string> OnlyWhenNamed = new HashSet<string> { "change_orbit", "rendezvous" };

        /// <summary>Each block's trigger words, matched at the start of a word of the Mission Goal ("land" finds "landing", not "island").</summary>
        private static readonly KeyValuePair<string, string[]>[] Triggers =
        {
            Rule("launch", "launch", "lift off", "liftoff", "take off", "takeoff", "orbit", "space", "apoapsis", "periapsis", "kerbin"),
            Rule("transfer_to", "mun", "minmus", "moon", "duna", "eve", "ike", "gilly", "dres", "jool", "laythe", "eeloo", "transfer", "encounter", "flyby", "fly by", "fly-by", "intercept"),
            Rule("land", "land", "touch down", "touchdown", "set down", "surface"),
            Rule("ascend", "ascend", "leave the", "lift off from", "take off from", "launch from", "back to orbit", "return", "come back", "home"),
            Rule("return", "return", "come back", "home", "back to kerbin", "bring", "recover"),
            Rule("reentry", "return", "come back", "home", "reentry", "re-entry", "splash", "parachute", "bring", "recover", "land on kerbin", "back to kerbin"),
            // Not "lan" or "plane": they start "land" and "planet". Minmus's orbit is inclined: reaching it starts with a plane match.
            Rule("change_orbit", "inclin", "polar", "plane change", "orbital plane", "plane of", "equatorial", "ascending node", "resonan", "stationary", "synchronous",
                "eccentric", "semi-major", "minmus"),
            // Docking starts with a rendezvous (#456's dock takes over once the target is loaded).
            Rule("rendezvous", "rendezvous", "dock", "meet", "catch up", "target vessel", "join up"),
            Rule("science", "science", "experiment", "survey", "biome", "sample", "data", "report", "goo", "thermometer"),
        };

        /// <summary>
        /// The bodies with an atmosphere, which land refuses (#627). Core knows no body table (the atmosphere flag is read in the
        /// Adapter from the live game), so the names are listed here; the goal's words are all this decision has.
        /// </summary>
        private static readonly HashSet<string> Airy = new HashSet<string> { "kerbin", "eve", "duna", "jool", "laythe" };
        private static readonly Regex LandingOn = new Regex(
            @"(?<![a-z0-9])(?:land(?:ing|s|ed)?|touch(?:ing)?\s?down|set(?:ting)?\s+down)\s+(?:on|at|onto|in)\s+(?:the\s+)?(?:surface\s+of\s+(?:the\s+)?)?([a-z]+)\b(?!'s)", RegexOptions.Compiled);

        private static KeyValuePair<string, string[]> Rule(string block, params string[] words)
        {
            return new KeyValuePair<string, string[]>(block, words);
        }

        /// <summary>The names of the blocks whose full docs this Mission gets: the always-set, each block the goal's words point at, and any block the table doesn't know.</summary>
        public static IList<string> For(string goal, IEnumerable<string> allBlocks)
        {
            var all = allBlocks.ToList();
            var text = (goal ?? "").ToLowerInvariant();
            var picked = new HashSet<string>(Triggers.Where(t => t.Value.Any(w => Regex.IsMatch(text, @"(?<![a-z0-9])" + Regex.Escape(w)))).Select(t => t.Key));
            // A landing through air is a reentry, not a land (#627): a goal whose every named landing is on a body with an
            // atmosphere drops land; one that also lands on an airless body keeps both.
            var landings = LandingOn.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var airOnly = landings.Count > 0 && landings.All(Airy.Contains);
            if (landings.Any(Airy.Contains)) picked.Add("reentry");
            if (airOnly) picked.Remove("land");
            // One thin match is not enough to leave blocks out: a goal that points at fewer than two blocks gets them all,
            // but for the blocks only its own words bring.
            if (picked.Count < 2) return all.Where(b => (!OnlyWhenNamed.Contains(b) || picked.Contains(b)) && !(airOnly && b == "land")).ToList();
            picked.UnionWith(Always);
            var known = new HashSet<string>(Always.Concat(Triggers.Select(t => t.Key)));
            return all.Where(b => picked.Contains(b) || !known.Contains(b)).ToList();
        }

        /// <summary>The names of the shipped blocks this Mission gets full docs for.</summary>
        public static IList<string> For(string goal)
        {
            return For(goal, CoreLibrary.Blocks.Select(b => b.Name));
        }
    }
}
