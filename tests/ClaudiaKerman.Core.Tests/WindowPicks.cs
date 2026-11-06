// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>For tests about what follows a window, not who picks it: the pick that takes the window the default setting would (#686).</summary>
    internal static class WindowPicks
    {
        /// <summary>The player's pick matching the default Time versus delta-v, for the transfer <paramref name="goal"/> needs; none when it needs no pick.</summary>
        public static WindowPick AsTheSetting(GameSnapshot snapshot, string goal)
        {
            var body = WindowChoice.Transfer(snapshot, goal);
            if (body == null) return WindowPick.None;
            var config = new PlayerConfig();
            var found = TransferWindows.Find(snapshot, body.Name, MissionPrompts.ArrivalPe(body), true, config.CraftCheckMarginPercent);
            var window = TransferWindows.Pick(found, config.DefaultTimeVersusDeltaV);
            return new WindowPick(body.Name, window == found.Optimal && window != found.Soonest ? WindowKind.Optimal : WindowKind.Soonest);
        }
    }
}
