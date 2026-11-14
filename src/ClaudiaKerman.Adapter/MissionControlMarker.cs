// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Contracts;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using UnityEngine;
using UnityEngine.UI;
using StockMissionControl = KSP.UI.Screens.MissionControl;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// #1027: while Mission Control is open, marks each row of the offered and accepted lists with the Claudia Flag when Claudia can
    /// plan the Contract, or a hollow mark when not yet, with the reason as hover text over the whole row (<see cref="ContractMarks"/>,
    /// judged by kind and settings only). Rows are found by sweeping the list every few frames, since the game and Contract Configurator
    /// rebuild it on every tab change and list-changed events are unreliable with Contract Configurator installed, and matched to their
    /// Contract by a Harmony postfix on the row's and the list's public setup calls, or else the row's data. The mark is a child of the
    /// row, so it goes with the row and the next sweep marks the new one. It never touches Accept, Decline or Cancel and never rebuilds
    /// the list. The first error turns the marking off for the rest of the session and is logged. Main thread only.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class MissionControlMarker : MonoBehaviour
    {
        private const string MarkName = "ClaudiaMark";
        /// <summary>Frames between sweeps: a handful a second.</summary>
        private const int SweepEvery = 10;
        /// <summary>The mark's height in UI pixels; it is as wide as the Claudia Flag's 256×160 shape.</summary>
        private const float Height = 14f;
        private const float Width = Height * 1.6f;
        /// <summary>The gap between the mark and the difficulty stars at the row's right edge.</summary>
        private const float Gap = 4f;
        /// <summary>Where the mark goes when the row has no difficulty stars to sit beside.</summary>
        private const float FallbackInset = 40f;

        private static bool _patched;
        private static bool _off;
        private static Texture2D _flag;
        private static Texture2D _hollow;

        /// <summary>Each row's Contract, as the game's setup calls gave it; weak, so a destroyed row lets its entry go.</summary>
        private static readonly ConditionalWeakTable<MCListItem, Contract> RowContracts = new ConditionalWeakTable<MCListItem, Contract>();
        /// <summary>The hover texts this marker added to a row itself, so a reused row's is rewritten, never another mod's.</summary>
        private static readonly ConditionalWeakTable<MCListItem, TooltipController_Text> OurTooltips = new ConditionalWeakTable<MCListItem, TooltipController_Text>();
        /// <summary>The mark per Contract GUID while Mission Control is open; judged once per visit, emptied when it closes.</summary>
        private static readonly Dictionary<Guid, ContractMark> Marks = new Dictionary<Guid, ContractMark>();

        private int _frames;

        private void Start()
        {
            if (_patched || _off || DuplicateInstall.Found) return;
            _patched = true;
            try
            {
                var harmony = new Harmony("claudiakerman.missioncontrol");
                harmony.Patch(AccessTools.Method(typeof(MCListItem), nameof(MCListItem.Setup)), postfix: new HarmonyMethod(typeof(MissionControlMarker), nameof(AfterRowSetup)));
                harmony.Patch(AccessTools.Method(typeof(StockMissionControl), nameof(StockMissionControl.AddItem)), postfix: new HarmonyMethod(typeof(MissionControlMarker), nameof(AfterAddItem)));
            }
            catch (Exception e)
            {
                TurnOff("couldn't patch Mission Control's list", e);
            }
        }

        private void Update()
        {
            if (_off) return;
            var missionControl = StockMissionControl.Instance;
            // Unity's == reads a destroyed screen as null.
            if (missionControl == null)
            {
                Marks.Clear();
                return;
            }
            if (++_frames % SweepEvery != 0) return;
            try
            {
                Sweep(missionControl);
            }
            catch (Exception e)
            {
                TurnOff("marking Mission Control's Contracts failed", e);
            }
        }

        private static void TurnOff(string what, Exception e)
        {
            if (_off) return;
            _off = true;
            Marks.Clear();
            Debug.LogError(ModInfo.LogTag + " " + what + "; the marks are off for the rest of the session: " + e);
        }

        // ---- The Harmony postfixes: they only record which Contract a row shows. Arguments come from __args, as stock names aren't API. ----

        private static void AfterRowSetup(MCListItem __instance, object[] __args)
        {
            if (_off) return;
            try
            {
                Remember(__instance, __args.Length > 0 ? __args[0] as Contract : null);
            }
            catch (Exception e)
            {
                TurnOff("reading a Mission Control row's Contract failed", e);
            }
        }

        /// <summary>The game adds the row last, so after the call it is the list's last row.</summary>
        private static void AfterAddItem(StockMissionControl __instance, object[] __args)
        {
            if (_off) return;
            try
            {
                var contract = __args.Length > 0 ? __args[0] as Contract : null;
                var list = __instance != null ? __instance.scrollListContracts : null;
                if (contract == null || list == null || list.Count == 0) return;
                var last = list.GetUilistItemAt(list.Count - 1);
                Remember(last != null ? last.GetComponent<MCListItem>() : null, contract);
            }
            catch (Exception e)
            {
                TurnOff("reading a Mission Control row's Contract failed", e);
            }
        }

        /// <summary>The row now shows <paramref name="contract"/>; a reused row loses its old mark, so the next sweep marks it afresh.</summary>
        private static void Remember(MCListItem row, Contract contract)
        {
            if (row == null || contract == null) return;
            Contract had;
            if (RowContracts.TryGetValue(row, out had))
            {
                if (had == contract) return;
                RowContracts.Remove(row);
                Unmark(row);
            }
            RowContracts.Add(row, contract);
        }

        // ---- The sweep ----

        private static void Sweep(StockMissionControl missionControl)
        {
            // The offered (Available, and Contract Configurator's "All") and accepted (Active) lists; finished ones aren't marked.
            if (missionControl.displayMode == StockMissionControl.DisplayMode.Archive) return;
            var list = missionControl.scrollListContracts;
            if (list == null) return;
            for (var i = 0; i < list.Count; i++)
            {
                var item = list.GetUilistItemAt(i);
                var row = item != null ? item.GetComponent<MCListItem>() : null;
                if (row == null || row.transform.Find(MarkName) != null) continue;
                // No Contract behind it (a Contract Configurator group row): no mark.
                var contract = ContractOf(row, item);
                if (contract == null) continue;
                Mark(row, MarkOf(contract));
            }
        }

        /// <summary>The row's Contract: from the setup calls, or else any public <c>contract</c> on the row's data (Contract Configurator's container, a selection).</summary>
        private static Contract ContractOf(MCListItem row, UIListItem item)
        {
            Contract contract;
            if (RowContracts.TryGetValue(row, out contract)) return contract;
            var data = item.Data;
            if (data == null) return null;
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
            var field = data.GetType().GetField("contract", Public);
            if (field != null) return field.GetValue(data) as Contract;
            var property = data.GetType().GetProperty("contract", Public);
            return property != null && property.GetIndexParameters().Length == 0 ? property.GetValue(data, null) as Contract : null;
        }

        private static ContractMark MarkOf(Contract contract)
        {
            ContractMark mark;
            if (Marks.TryGetValue(contract.ContractGuid, out mark)) return mark;
            // A Contract type outside the game's own assembly is a mod's: "not yet" whatever it is called.
            var fromMod = contract.GetType().Assembly != typeof(Contract).Assembly;
            mark = ContractMarks.Of(ContractReader.Of(contract), fromMod);
            Marks[contract.ContractGuid] = mark;
            return mark;
        }

        // ---- Drawing ----

        private static void Mark(MCListItem row, ContractMark mark)
        {
            var rowRect = (RectTransform)row.transform;
            var go = new GameObject(MarkName, typeof(RectTransform));
            go.layer = row.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(rowRect, false);
            // Out of the row's layout group, so the list's layout never moves or resizes it.
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(Width, Height);
            rect.anchoredPosition = new Vector2(RightOffset(row, rowRect), 0f);
            var image = go.AddComponent<RawImage>();
            image.texture = mark.Plannable ? Flag() : Hollow();
            image.raycastTarget = false;

            // Hover over the whole row: the row's own background takes the pointer. A row with another's hover text keeps it, and the mark carries ours.
            var tooltip = OurTooltip(row);
            if (tooltip == null)
            {
                image.raycastTarget = true;
                tooltip = go.AddComponent<TooltipController_Text>();
            }
            tooltip.prefab = AssetBase.GetPrefab<Tooltip_Text>("Tooltip_Text");
            tooltip.SetText(mark.Hover);
        }

        /// <summary>The hover text on the row that this marker owns, added if the row has none; null when another's is there.</summary>
        private static TooltipController_Text OurTooltip(MCListItem row)
        {
            TooltipController_Text ours;
            if (OurTooltips.TryGetValue(row, out ours) && ours != null) return ours;
            if (row.GetComponent<TooltipController_Text>() != null) return null;
            ours = row.gameObject.AddComponent<TooltipController_Text>();
            OurTooltips.Remove(row);
            OurTooltips.Add(row, ours);
            return ours;
        }

        /// <summary>The mark's right edge, from the row's right edge: just left of the difficulty stars.</summary>
        private static float RightOffset(MCListItem row, RectTransform rowRect)
        {
            var stars = row.difficulty != null ? row.difficulty.transform as RectTransform : null;
            if (stars == null) return -FallbackInset;
            var corners = new Vector3[4];
            stars.GetWorldCorners(corners);
            var left = rowRect.InverseTransformPoint(corners[0]).x;
            var offset = left - rowRect.rect.xMax - Gap;
            return offset < 0f && offset > -rowRect.rect.width ? offset : -FallbackInset;
        }

        private static void Unmark(MCListItem row)
        {
            var mark = row.transform.Find(MarkName);
            if (mark == null) return;
            // Renamed first: Destroy waits for the frame's end, and the sweep looks the mark up by name.
            mark.gameObject.name = "";
            Destroy(mark.gameObject);
        }

        private static Texture2D Flag()
        {
            if (_flag == null) _flag = GameDatabase.Instance.GetTexture(ClaudiaFlagKeeper.Url, false);
            if (_flag == null) throw new InvalidOperationException("the Claudia Flag texture " + ClaudiaFlagKeeper.Url + " isn't loaded");
            return _flag;
        }

        /// <summary>The "not yet" mark: the flag's outline, hollow.</summary>
        private static Texture2D Hollow()
        {
            if (_hollow != null) return _hollow;
            const int W = 32, H = 20, Line = 2;
            _hollow = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var pixels = new Color32[W * H];
            var edge = new Color32(220, 220, 220, 255);
            var clear = new Color32(0, 0, 0, 0);
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                    pixels[y * W + x] = x < Line || y < Line || x >= W - Line || y >= H - Line ? edge : clear;
            _hollow.SetPixels32(pixels);
            _hollow.Apply();
            return _hollow;
        }
    }
}
