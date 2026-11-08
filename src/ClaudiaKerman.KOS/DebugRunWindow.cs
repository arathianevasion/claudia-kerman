// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Kos;
using UnityEngine;

namespace ClaudiaKerman.KOS
{
    /// <summary>
    /// With <c>debug_tools = true</c>: the "kOS Bridge Tests" tab of the block test window (<see cref="BlockTestRunner"/>), which runs a test script on the active vessel's
    /// processor (nearest the root) through the kOS bridge and shows its report or error. A developer
    /// tool: the player starts each fixed test script by hand and Claude is never involved, so it sits
    /// outside the Approval rule of ADR 0002.
    /// </summary>
    public class DebugRunWindow
    {
        private static readonly string[] Tests = { "report", "runtime_error", "no_report", "long_run" };

        private Vector2 _scroll;
        private uint? _partId;
        private string _status = "Idle.";
        private string _result = "";

        // Read once per session by the runner (BlockTestRunner.Awake). The status, result and part id below carry over between Flight scenes. Any failure reading the player config counts as off.
        internal static bool DebugToolsOn()
        {
            // Stood down beside the old copy (ADR 0015): its config folder is left where it is.
            if (Adapter.DuplicateInstall.Found) return false;
            try
            {
                return PlayerConfig.LoadOrCreate(ConfigDirectory.ResolveCurrent()).DebugTools;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Claudia Kerman: could not read the player config; debug tools stay off: " + e.Message);
                return false;
            }
        }

        /// <summary>Draws the tab's contents: in Flight the processor line, buttons, Abort, status and result; elsewhere a note that the tests need Flight.</summary>
        internal void Draw()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                GUILayout.Label("The kOS bridge tests need Flight: they run on the active vessel's kOS processor.");
                return;
            }
            var vessel = FlightGlobals.ActiveVessel;
            var bridge = KosBridge.Instance;
            var target = KosBridge.ChooseProcessor(vessel, null);

            GUILayout.Label(target.HasValue
                ? "Processor: part " + target.Value + " on '" + vessel.vesselName + "' (nearest the root)"
                : "No kOS processor on the active vessel.");
            if (!KosInternals.Result.IsCompatible) GUILayout.Label(KosInternals.Result.Message);

            GUILayout.BeginHorizontal();
            foreach (var test in Tests)
            {
                if (GUILayout.Button(test) && target.HasValue && bridge != null) Run(bridge, target.Value, test);
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Abort") && bridge != null && _partId.HasValue)
                bridge.Abort(_partId.Value, "aborted from the debug window");

            GUILayout.Label(_status);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(160));
            GUILayout.Label(_result);
            GUILayout.EndScrollView();
        }

        private void Run(KosBridge bridge, uint partId, string test)
        {
            _partId = partId;
            _status = "Starting '" + test + "'...";
            _result = "";
            bridge.BeginRun(partId, "claudia-debug:" + test, LoadScript(test), Show, reason =>
            {
                _status = "Refused: " + reason;
            });
        }

        private void Show(RunOutcome outcome)
        {
            _status = "Ended: " + outcome.Summary;
            _result = outcome.Kind == RunOutcomeKind.Finished
                ? "Report:\n" + KosValues.Format(outcome.Report)
                : "Terminal tail:\n" + (outcome.ErrorText.Length > 0 ? outcome.ErrorText : "(nothing printed)");
        }

        private static string LoadScript(string test)
        {
            var name = "ClaudiaKerman.KOS.debug." + test + ".ks";
            using (var stream = typeof(DebugRunWindow).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
