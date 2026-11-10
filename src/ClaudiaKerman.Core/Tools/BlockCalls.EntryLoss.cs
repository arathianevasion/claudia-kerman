// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Tools
{
    public static partial class BlockCalls
    {
        /// <summary>How <see cref="EntryPartLoss"/>'s <c>why</c> begins when a chute path's loss took its mains and the finish chose its engines (#963).</summary>
        public const string WhyMainsLostToEngines = "mains lost, on to the engines: ";

        /// <summary>The same when the finish found no engine that can land it, so the craft stays on its drogues (#963).</summary>
        public const string WhyMainsLostOnDrogues = "mains lost, staying on the drogues: ";

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENTRYLOSS</c>, for <c>reentry</c> (#958, ADR 0014), when its part count has fallen below what it last took as the craft. <c>carry_on</c> is whether the block goes on
        /// rather than abort: parts lost (<c>lost</c>) while off the ground (<c>on_ground</c>) at or under the air top (<c>altitude</c>, <c>air_top</c>, m), whatever <c>abort_on_part_loss</c> says. Above the air it is false and the
        /// shared check stands, as in every other block. A drop of the block's own never reaches here: each moves the block's part count itself, after letting the parts leave and before a tick can count them.
        /// <para>Carrying on, it re-reads the craft as it now is: <c>ENTRYFINISH</c> (<paramref name="finishOf"/>, on <c>entry</c>, nothing more to drop) and <c>CHUTEGROUP</c> (<paramref name="groupOf"/>, on <c>group</c>, with <c>engine</c> as this decides it).
        /// <c>powered</c> is the finish's answer, or stays true while the craft still has thrust (<c>powered</c>, <c>thrust</c> kN), so a burn under way isn't dropped because the chutes would now do; unread, what the block had. <c>to_engines</c>: it has just gone over to its engines, so the
        /// block takes up its hold. <c>light</c> (kOS UIDs) are the engines the finish chose, none unless it chose them. <c>chute_mode</c>: "none" with no chute in the group; on a craft with no engine left the script's own (<c>mode</c>), as the chutes are now all it has; otherwise as it was (<c>chute_mode</c>), so a #954 cut stands.
        /// <c>chutes</c>: the addon's reading of the group's chutes aboard (<c>uids</c>), as for <c>BRAKETICK</c>; one reading armed (called, still packed), semi or deployed is out. Both readings count only stowed chutes, so with one out (<c>regroup</c> false) the group stays as it is, unread: a called chute stays in it for <c>BRAKETICK</c>'s wait, the group doesn't widen past it, and a chute path doesn't go over to its engine under a canopy.
        /// The one exception is a chute path whose loss took its mains (#963: a chute of <c>uids</c> gone and no main left aboard uncut, each reading's <c>drogue</c> telling them apart): the drogues alone would land it too fast, so the finish's answer decides, and
        /// <c>why</c> begins "mains lost, on to the engines: " or "mains lost, staying on the drogues: ". The group is still kept, so the powered finish's <c>BRAKETICK</c> waits for the drogues that are out or armed, and the lost main counts there as failed.
        /// Gives <c>error</c> "", <c>carry_on</c>, <c>powered</c>, <c>to_engines</c>, <c>regroup</c>, <c>light</c>, <c>chute_mode</c>, <c>finish</c> and <c>group</c> (as those calls answered) and <c>why</c> for the note. With either delegate null (the addon's answer to a failed call) nothing is read and it still carries on. Pure.</para>
        /// </summary>
        public static IDictionary<string, object> EntryPartLoss(IDictionary<string, object> ask, Func<IDictionary<string, object>, IDictionary<string, object>> finishOf, Func<IDictionary<string, object>, IDictionary<string, object>> groupOf)
        {
            ask = ask ?? new Dictionary<string, object>();
            if (Number(ask, "lost", 0) <= 0 || Flag(ask, "on_ground") || Number(ask, "altitude", double.PositiveInfinity) > Number(ask, "air_top", -1))
                return new Dictionary<string, object> { { "error", "" }, { "carry_on", false } };
            var wasPowered = Flag(ask, "powered");
            var finishAsk = Copied(ask, "entry");
            finishAsk["drop"] = false;
            var finish = finishOf != null ? finishOf(finishAsk) : Error("the finish could not be read");
            var read = finish["error"] as string == "";
            // A chute of the group called or out: the finish and a fresh group count only stowed chutes, so they'd miss it. It keeps the group, and a chute path stays on its chutes,
            // unless the loss took its mains (#963): then the finish's answer decides, and BRAKETICK, on the kept group, waits for the drogues that are out.
            var chutesOut = Opened(ask) > 0;
            var mainsLost = chutesOut && !wasPowered && MainsLost(ask);
            var chosen = read && Flag(finish, "powered") && (wasPowered || !chutesOut || mainsLost);
            var powered = read ? chosen || (wasPowered && Number(ask, "thrust", 0) > 0) : wasPowered;
            IDictionary<string, object> group = Error("kept: a chute of the group is out");
            if (!chutesOut)
            {
                var groupAsk = Copied(ask, "group");
                groupAsk["engine"] = powered;
                group = groupOf != null ? groupOf(groupAsk) : Error("the group could not be read");
            }
            var uids = chutesOut ? 1 : group["error"] as string != "" ? -1 : group.ContainsKey("uids") && group["uids"] is IEnumerable ? ((IEnumerable)group["uids"]).Cast<object>().Count() : 0;
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "carry_on", true },
                { "powered", powered },
                { "to_engines", powered && !wasPowered },
                { "regroup", !chutesOut },
                { "light", chosen && finish.ContainsKey("engines") ? finish["engines"] : new List<object>() },
                { "chute_mode", uids == 0 ? "none" : powered ? Word(ask, "chute_mode") : Word(ask, "mode") },
                { "finish", finish },
                { "group", group },
                { "why", (!mainsLost ? "" : chosen ? WhyMainsLostToEngines : WhyMainsLostOnDrogues) + (read ? finish.ContainsKey("why") ? finish["why"] : "" : finish["error"]) },
            };
        }

        /// <summary>
        /// Whether the parts lost took the group's mains (#963): a chute of <c>uids</c> is no longer aboard (<c>chutes</c> lists only those that are) and no main aboard is left uncut.
        /// A chute with no <c>drogue</c> reading counts as a main, so it keeps the chutes.
        /// </summary>
        private static bool MainsLost(IDictionary<string, object> ask)
        {
            object uids, chutes;
            var group = ask.TryGetValue("uids", out uids) && uids is IEnumerable && !(uids is string) ? ((IEnumerable)uids).Cast<object>().Count() : 0;
            var aboard = ask.TryGetValue("chutes", out chutes) && chutes is IEnumerable ? ((IEnumerable)chutes).OfType<IDictionary<string, object>>().ToList() : new List<IDictionary<string, object>>();
            return group > aboard.Count && !aboard.Any(c => !Flag(c, "drogue") && !(c.ContainsKey("state") && c["state"] as string == ReentryBrake.Cut));
        }

        /// <summary>How many chutes in <c>chutes</c> (each with a <c>state</c>, as <c>BRAKETICK</c> reads them) are called or out: reading armed, semi or deployed.</summary>
        private static int Opened(IDictionary<string, object> ask)
        {
            object chutes;
            if (!ask.TryGetValue("chutes", out chutes) || !(chutes is IEnumerable)) return 0;
            return ((IEnumerable)chutes).OfType<IDictionary<string, object>>().Count(c => c.ContainsKey("state") && (c["state"] as string == ReentryBrake.Deployed || c["state"] as string == "semi" || c["state"] as string == ReentryBrake.Armed));
        }

        private static Dictionary<string, object> Copied(IDictionary<string, object> ask, string key)
        {
            object value;
            return ask.TryGetValue(key, out value) && value is IDictionary<string, object> ? new Dictionary<string, object>((IDictionary<string, object>)value) : new Dictionary<string, object>();
        }

        private static string Word(IDictionary<string, object> ask, string key)
        {
            object value;
            return ask.TryGetValue(key, out value) ? value as string ?? "safe" : "safe";
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:ENTRYSKIP</c>, for <c>reentry</c> after a skip-out (#592), moved from the block (#958, ADR 0014): <c>refusal</c> is "" or why the skip-out is the end: <c>pe</c> (m) above <c>air_top</c>, so there is no next pass; <c>passes</c>
        /// (counting this one) at the 3 it flies; or <c>entry_in</c> (s) under 0, an orbit that never comes back. <c>max_duration</c> (s) is what the block runs under: <c>auto</c> (it was "auto"): <c>elapsed</c> (s) plus the wait plus 1800; else the <c>max_duration</c> the script gave, a hard cap.
        /// Gives <c>error</c> "", <c>refusal</c>, <c>max_duration</c>. With no ask (a failed call) it refuses nothing and sets no cap the wait could trip. Pure.
        /// </summary>
        public static IDictionary<string, object> EntrySkip(IDictionary<string, object> ask)
        {
            if (ask == null) return new Dictionary<string, object> { { "error", "" }, { "refusal", "" }, { "max_duration", 1e9 } };
            var passes = Number(ask, "passes", 0);
            var pe = Number(ask, "pe", 0);
            var entryIn = Number(ask, "entry_in", 0);
            var refusal = "";
            if (pe >= Number(ask, "air_top", 0)) refusal = "skipped out: periapsis " + Whole(pe) + " m is above the atmosphere";
            else if (passes >= 3) refusal = "skipped out of the atmosphere " + Whole(passes) + " times (periapsis " + Whole(pe) + " m)";
            else if (entryIn < 0) refusal = "skipped out onto an orbit that never reaches the atmosphere again (periapsis " + Whole(pe) + " m)";
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "refusal", refusal },
                { "max_duration", Flag(ask, "auto") ? Number(ask, "elapsed", 0) + entryIn + 1800 : Number(ask, "max_duration", 0) },
            };
        }

        private static string Whole(double value)
        {
            return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
