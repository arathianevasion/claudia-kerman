// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Where the last <c>return</c> or <c>deorbit</c> aimed its landing (#892): kept on the Mission for <c>reentry</c>'s late trim. <see cref="TargetPe"/> is the periapsis
    /// the aim held, which centres the trim's corridor; null when the aim named none (<c>deorbit</c>).
    /// </summary>
    public sealed class LandingTarget
    {
        public string Body = "";
        public double Latitude;
        public double Longitude;
        public double? TargetPe;
    }

    public static partial class BlockCalls
    {
        /// <summary>The trim fires only with more than this to the air, s (#892): later, the burn and the drop at 600 s crowd each other.</summary>
        public const double TrimLeastToAirS = 1200;
        /// <summary>Further out than this, s to the air, the trim waits until this far out (#952): from a day away a sideways burn barely moves the landing and 1 m/s along the fall moves it about 35 km.</summary>
        public const double TrimMostToAirS = 2400;
        /// <summary>How far over <see cref="TrimMostToAirS"/> still solves, s: the time to the air read again after the wait can come back a hair over it, and a second wait would mean no trim.</summary>
        public const double TrimWaitSlackS = 5;
        /// <summary>The predicted miss a trim is worth, km (#892): at or under it, the burn's own error is of that size.</summary>
        public const double TrimWorthKm = 5;
        /// <summary>The time to turn to and lead the trim, s, as <c>return</c>'s correction leaves.</summary>
        private const double TrimLeadS = 300;

        /// <summary>The target an aiming block call was given (<c>lat</c>, <c>lng</c> and, if there, <c>target_pe</c>) on <paramref name="body"/>; null when it aims at no landing.</summary>
        public static LandingTarget LandingTargetOf(IDictionary<string, object> input, string body)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var lat = NumberOrNull(ask, "lat");
            var lng = NumberOrNull(ask, "lng");
            if (!lat.HasValue || !lng.HasValue || string.IsNullOrEmpty(body)) return null;
            return new LandingTarget { Body = body, Latitude = lat.Value, Longitude = lng.Value, TargetPe = NumberOrNull(ask, "target_pe") };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENTRYTRIM</c>, for <c>reentry</c> (#892): at most one late trim before the stack drop, toward the Mission's <see cref="GameSnapshot.LandingTarget"/>. Takes <c>to_air</c> (s),
        /// <c>dv_left</c>, <c>isp</c>, <c>has_node</c> and <c>craft</c> (<see cref="EntryCraftCall"/>'s answer, which the adapter adds). Only with over <see cref="TrimLeastToAirS"/> s to the air and a predicted miss over
        /// <see cref="TrimWorthKm"/> km does it solve, as <see cref="CorrectionBurn"/> aimed at a landing: #817's spare delta-v rule and its corridor round the aim's own target_pe. Its answer is CORRECTIONBURN's
        /// shape; any trim not to fly has <c>dv</c> 0, no <c>error</c> (the block goes on, #805) and why in <c>aim_note</c>. Every answer has <c>wait</c> (s): over 0, nothing is solved yet, and the block waits that long and
        /// asks once more (#952). One that was solved also has <c>miss_before_km</c> and <c>miss_after_km</c>, -1 for a burn with no aimed figure, for the block's note.
        /// </summary>
        public static IDictionary<string, object> EntryTrim(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body, IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (snapshot == null) return NoTrim(null, "no Game Snapshot");
            var target = snapshot.LandingTarget;
            if (target == null) return NoTrim(snapshot, "no landing target");
            if (!string.Equals(snapshot.Body, target.Body, StringComparison.OrdinalIgnoreCase)) return NoTrim(snapshot, "the landing target is on " + target.Body + ", not " + snapshot.Body);
            object hasNode;
            if (ask.TryGetValue("has_node", out hasNode) && Equals(hasNode, true)) return NoTrim(snapshot, "a maneuver node is already planned");
            var toAir = Number(ask, "to_air", 0);
            if (toAir > TrimMostToAirS + TrimWaitSlackS)
                return NoTrim(snapshot, toAir.ToString("0", CultureInfo.InvariantCulture) + " s to the air, waiting until " + TrimMostToAirS.ToString("0", CultureInfo.InvariantCulture), toAir - TrimMostToAirS);
            if (!(toAir > TrimLeastToAirS))
                return NoTrim(snapshot, toAir.ToString("0", CultureInfo.InvariantCulture) + " s to the air, not over " + TrimLeastToAirS.ToString("0", CultureInfo.InvariantCulture));
            if (body == null || air == null || craft == null) return NoTrim(snapshot, "the landing needs the planet's air and the craft");

            SnapshotBody at;
            Conic orbit;
            BodyFrame frame;
            var problem = Reentry(snapshot, body, null, out at, out orbit, out frame);
            if (problem != null) return NoTrim(snapshot, problem);
            var ground = Math.Max(0, body.TerrainAltitude(target.Body, target.Latitude, target.Longitude));
            var landing = new Dictionary<string, object> { { "lat", target.Latitude }, { "lng", target.Longitude }, { "ground_alt", ground } };
            if (ask.ContainsKey("craft")) landing["craft"] = ask["craft"];
            var impact = ReentryImpact(landing, snapshot, body, air, craft);
            if (!string.IsNullOrEmpty(impact["error"] as string)) return NoTrim(snapshot, "no landing to read (" + impact["error"] + ")");
            var miss = Number(impact, "error_km", -1);
            if (!(miss > TrimWorthKm))
                return NoTrim(snapshot, miss.ToString("0.0", CultureInfo.InvariantCulture) + " km from the target, within " + TrimWorthKm.ToString("0", CultureInfo.InvariantCulture) + " km");

            var ut = snapshot.Ut + TrimLeadS;
            var fixAsk = new Dictionary<string, object>(landing)
            {
                { "target", target.Body }, { "target_pe", orbit.PeR - at.Radius }, { "earliest_ut", ut }, { "latest_ut", ut }, { "isp", Number(ask, "isp", 0) }, { "dv_left", Number(ask, "dv_left", -1) },
                { "corridor_pe", target.TargetPe ?? orbit.PeR - at.Radius },
            };
            var fix = CorrectionBurn(fixAsk, snapshot, body, air, craft);
            var error = fix["error"] as string;
            if (!string.IsNullOrEmpty(error)) return NoTrim(snapshot, error);
            if (Number(fix, "dv", 0) > 0 && fix.ContainsKey("error_km") && !(Number(fix, "error_km", miss) < miss))
                return NoTrim(snapshot, "the trim lands no nearer (" + miss.ToString("0.0", CultureInfo.InvariantCulture) + " km from the target)");
            fix["wait"] = 0.0;
            fix["miss_before_km"] = miss;
            fix["miss_after_km"] = Number(fix, "error_km", -1);
            return fix;
        }

        /// <summary>A fix that flies nothing: CORRECTIONBURN's shape with <c>dv</c> 0 and <paramref name="why"/> in <c>aim_note</c>; <paramref name="wait"/> over 0 asks again after that many seconds.</summary>
        private static IDictionary<string, object> NoTrim(GameSnapshot snapshot, string why, double wait = 0)
        {
            return new Dictionary<string, object>
            {
                { "error", "" }, { "ut", snapshot != null ? snapshot.Ut : 0.0 }, { "prograde", 0.0 }, { "normal", 0.0 }, { "radial", 0.0 }, { "dv", 0.0 }, { "pe_predicted", 0.0 }, { "inc_predicted", 0.0 },
                { "cross_fix", "" }, { "cross_dv", 0.0 }, { "aim_note", (wait > 0 ? "no trim yet: " : "no trim: ") + why }, { "wait", wait },
            };
        }
    }
}
