// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Missions;
using HarmonyLib;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Leaving Flight while a Maneuver Sequence is Running cuts its script off, so the player is asked first.
    /// A prefix on <c>HighLogic.LoadScene</c> holds the change and shows a dialog; Leave marks the sequence
    /// interrupted (the core does that when the scene changes) and repeats the change; to the Space Center or the Tracking Station on a coast that passes the safety
    /// test, Leave is a Coast Checkpoint instead (#777), recorded before the change. A revert to the VAB or SPH (a load of the editor) shows no dialog. Other scene loads,
    /// and leaving Flight when nothing is running, pass straight through.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class LeaveFlightGuard : MonoBehaviour
    {
        private static bool patched;
        private static bool leaving;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                var harmony = new Harmony("claudiakerman.leaveflight");
                harmony.Patch(AccessTools.Method(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) }),
                    prefix: new HarmonyMethod(typeof(LeaveFlightGuard), nameof(AskFirst)));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch scene loading; leaving Flight while Running won't ask first: " + e);
            }
        }

        // Returning false holds the scene change until the player answers.
        private static bool AskFirst(GameScenes scene)
        {
            var driver = MissionDriver.Current;
            if (leaving || driver == null || HighLogic.LoadedScene != GameScenes.FLIGHT || scene == GameScenes.FLIGHT) return true;
            var destination = scene == GameScenes.SPACECENTER ? LeaveTo.SpaceCenter : scene == GameScenes.TRACKSTATION ? LeaveTo.TrackingStation
                : scene == GameScenes.EDITOR ? LeaveTo.Editor : LeaveTo.Other;
            // A revert to the VAB or SPH is unrestricted: no dialog; Core interrupts a Running sequence when the scene changes.
            if (!LeaveFlightRules.AsksFirst(driver.LeavingFlightNeedsConfirmation, destination)) return true;

            // #777: to the Space Center or the Tracking Station on a coast that passes the safety test, leaving is a Coast Checkpoint (the far switch's
            // Core decision). The main menu and every other scene stay interruptions; a revert to the VAB or SPH shows no dialog.
            var canCheckpoint = LeaveFlightRules.CanCheckpoint(destination);
            var leaves = canCheckpoint && driver.FarSwitchLeavesAtCheckpoint;
            var warning = canCheckpoint ? driver.FarSwitchWarning : "";
            var dialog = new MultiOptionDialog(
                "ClaudiaKermanLeaveFlight",
                leaves
                    ? "The craft is coasting and safe to leave. The script stops here, a KSP alarm calls you back before the next correction, "
                        + "and the mod checks the trajectory when you are back in the craft."
                    : "A Maneuver Sequence is Running. If you leave Flight now its script is cut off and the sequence is marked "
                        + "interrupted; Claudia will Debrief it when you come back."
                        + (warning.Length > 0 ? " It isn't safe to leave at a checkpoint now: " + warning + "." : ""),
                "Claudia Kerman",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUIHorizontalLayout(
                        new DialogGUIButton(leaves ? "Leave at a checkpoint" : "Leave Flight", () =>
                        {
                            // Core records the checkpoint (alarm, Debrief, state) now, so the scene change and its save find it and leave it alone.
                            driver.Send(new LeaveFlightConfirmed(destination));
                            leaving = true;
                            try
                            {
                                HighLogic.LoadScene(scene);
                            }
                            finally
                            {
                                leaving = false;
                            }
                        }, true),
                        new DialogGUIButton("Stay", () => { }, true)),
                });
            PopupDialog.SpawnPopupDialog(dialog, false, HighLogic.UISkin);
            return false;
        }
    }
}
