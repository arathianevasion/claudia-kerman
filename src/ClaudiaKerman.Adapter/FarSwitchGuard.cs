// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Reflection;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Missions;
using HarmonyLib;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// A far switch while a Maneuver Sequence is Running unloads the craft and cuts its script off, so the player is asked
    /// first (spec story 118). Switching to a vessel that isn't loaded goes through <c>FlightGlobals.setActiveVessel</c>,
    /// which saves and reloads Flight around the new vessel (the map's Switch To, the Alarm Clock's jump, the [ and ] keys
    /// never reach an unloaded one). A prefix holds that switch and shows a dialog; Switch tells the core (the run is
    /// interrupted and Debriefed when the player is back in the craft) and repeats it, so the save it writes already has the
    /// interruption. While the run only warps on a coast that passes the safety test, the offer is "Leave at a checkpoint" instead (#775).
    /// Nearby switches to loaded vessels, and any switch with nothing Running, pass straight through. The mod
    /// itself never switches vessels.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class FarSwitchGuard : MonoBehaviour
    {
        private static bool patched;
        private static bool switching;
        private static MethodInfo original;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                original = AccessTools.Method(typeof(FlightGlobals), "setActiveVessel", new[] { typeof(Vessel), typeof(bool), typeof(bool) });
                var harmony = new Harmony("claudiakerman.farswitch");
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(FarSwitchGuard), nameof(AskFirst)));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch vessel switching; a far switch while Running won't ask first: " + e);
            }
        }

        // Returning false holds the switch until the player answers.
        private static bool AskFirst(FlightGlobals __instance, Vessel __0, bool __1, bool __2, ref bool __result)
        {
            var driver = MissionDriver.Current;
            var target = __0;
            if (switching || driver == null || target == null || target.loaded || !HighLogic.LoadedSceneIsFlight) return true;
            if (!driver.LeavingFlightNeedsConfirmation || target == FlightGlobals.ActiveVessel) return true;
            // KSP refuses the switch itself when it can't save now (under thrust, say) unless forced: let it, with its own
            // message, and the run carries on untouched.
            if (!__1 && FlightGlobals.ClearToSave() != ClearToSaveStatus.CLEAR) return true;

            var name = target.GetDisplayName();
            // #775: only warping on a coast that passes the safety test, leaving is clean. Core decides again when the answer comes.
            var leaves = driver.FarSwitchLeavesAtCheckpoint;
            var warning = driver.FarSwitchWarning;
            var dialog = new MultiOptionDialog(
                "ClaudiaKermanFarSwitch",
                leaves
                    ? "The craft is coasting and safe to leave. " + name + " is far away: the script stops here, a KSP alarm calls you back before the next correction, "
                        + "and the mod checks the trajectory when you are back in the craft."
                    : "A Maneuver Sequence is Running. " + name + " is far away: switching to it unloads the craft, so its script is "
                        + "cut off and the sequence is marked interrupted. Claudia will Debrief it when you are back in the craft."
                        + (warning.Length > 0 ? " It isn't safe to leave at a checkpoint now: " + warning + "." : ""),
                "Claudia Kerman",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUIHorizontalLayout(
                        new DialogGUIButton(leaves ? "Leave at a checkpoint" : "Switch", () =>
                        {
                            driver.Send(new FarSwitchConfirmed(name));
                            switching = true;
                            try
                            {
                                original.Invoke(__instance, new object[] { target, __1, __2 });
                            }
                            finally
                            {
                                switching = false;
                            }
                        }, true),
                        new DialogGUIButton("Stay", () => { }, true)),
                });
            PopupDialog.SpawnPopupDialog(dialog, false, HighLogic.UISkin);
            __result = false;
            return false;
        }
    }
}
