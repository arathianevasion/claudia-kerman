// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Ui;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Where Flight's stock altimeter and app launcher bar are on screen, for placing the unmoved Capcom strip between
    /// them (<see cref="CapcomStripLayout"/>). Read through public KSP and Unity members only: the altimeter's panel from
    /// the flight UI controller, the bar from its visible buttons, both converted from the stock UI's camera to IMGUI
    /// screen pixels (top left origin). Read once a frame, so a new UI scale, resolution or button count re-fits the strip.
    /// </summary>
    public static class StockUiEdges
    {
        private static readonly Vector3[] Corners = new Vector3[4];
        private static readonly List<ApplicationLauncherButton> Buttons = new List<ApplicationLauncherButton>();
        private static int _readFrame = -1;
        private static StockUi _read;
        private static bool _failureLogged;

        /// <summary>
        /// This frame's edges, or <see cref="StockUi.Unknown"/> when either can't be read, including when the stock UI is
        /// torn down under the read (a scene change): that is logged once and never reaches the strip's draw.
        /// </summary>
        public static StockUi Read()
        {
            if (_readFrame == Time.frameCount) return _read;
            _readFrame = Time.frameCount;
            try
            {
                _read = ReadNow();
            }
            catch (Exception e)
            {
                Buttons.Clear();
                _read = StockUi.Unknown;
                if (!_failureLogged) Debug.LogWarning(ModInfo.LogTag + " couldn't read the altimeter and app bar edges; the strip takes its fallback spot: " + e.Message);
                _failureLogged = true;
            }
            return _read;
        }

        private static StockUi ReadNow()
        {
            var camera = UIMasterController.Instance != null ? UIMasterController.Instance.uiCamera : null;
            var flightUi = FlightUIModeController.Instance;
            if (camera == null || flightUi == null || flightUi.altimeterFrame == null || ApplicationLauncher.Instance == null) return StockUi.Unknown;

            Rect altimeter;
            if (!ScreenRect(flightUi.altimeterFrame.transform as RectTransform, camera, out altimeter)) return StockUi.Unknown;

            // The bar is as big as its buttons: its left edge is the leftmost visible one's, its bottom the lowest one's.
            ApplicationLauncher.Instance.GetComponentsInChildren(false, Buttons);
            float barLeft = float.MaxValue, barBottom = float.MinValue;
            foreach (var button in Buttons)
            {
                Rect rect;
                if (button == null || !ScreenRect(button.transform as RectTransform, camera, out rect)) continue;
                barLeft = Mathf.Min(barLeft, rect.xMin);
                barBottom = Mathf.Max(barBottom, rect.yMax);
            }
            Buttons.Clear();
            if (barLeft == float.MaxValue) return StockUi.Unknown;
            return new StockUi(altimeter.xMin, altimeter.xMax, altimeter.yMin, altimeter.yMax, barLeft, barBottom);
        }

        // The rect in IMGUI screen pixels; false when there is none or it isn't on screen.
        private static bool ScreenRect(RectTransform transform, Camera camera, out Rect rect)
        {
            rect = default(Rect);
            if (transform == null || !transform.gameObject.activeInHierarchy) return false;
            transform.GetWorldCorners(Corners);
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            foreach (var corner in Corners)
            {
                var point = camera.WorldToScreenPoint(corner);
                // Unity's screen y runs up from the bottom; IMGUI's runs down from the top.
                var y = Screen.height - point.y;
                xMin = Mathf.Min(xMin, point.x);
                xMax = Mathf.Max(xMax, point.x);
                yMin = Mathf.Min(yMin, y);
                yMax = Mathf.Max(yMax, y);
            }
            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return rect.width > 0 && rect.height > 0 && rect.xMax > 0 && rect.xMin < Screen.width && rect.yMax > 0 && rect.yMin < Screen.height;
        }
    }
}
