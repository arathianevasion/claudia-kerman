// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Adapter;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudiaKerman.KOS
{
    /// <summary>
    /// With <c>debug_tools = true</c>: the debug block test runner (spec, Testing: seam 3). One click
    /// from the main menu or Space Center runs a Building Block test unattended in real physics: it
    /// writes the test's scenario save and reference craft (from <c>tests/blocks/&lt;test&gt;/</c>,
    /// shipped inside this DLL) to <c>saves/claudia_block_tests/</c>, loads that save, spawns the craft
    /// into the test's orbit (or rolls it out to the launch pad), flies to it, runs the test's Mission Script bundled with the Core Library
    /// through the kOS bridge, and judges its Script Result against the contract ranges in
    /// <c>test.cfg</c>. Results go to KSP.log and <c>saves/claudia_block_tests/block-tests.log</c>.
    /// Planning Tool scenarios (<c>tests/tools/&lt;name&gt;/</c>, the same four files) run the same way as <c>tools/&lt;name&gt;</c>;
    /// while any test runs, its processor may call Planning Tools through the test-only bridge (<see cref="ToolTestGate"/>).
    /// A developer tool: Claude is never involved, so it sits outside the Approval rule of ADR 0002.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class BlockTestRunner : MonoBehaviour
    {
        private const string SaveFolder = "claudia_block_tests";
        private const string ResourcePrefix = "ClaudiaKerman.KOS.blocktests.";
        /// <summary>Planning Tool scenarios (<c>tests/tools/&lt;name&gt;/</c>), run as <c>tools/&lt;name&gt;</c> with the test-only tool bridge open.</summary>
        private const string ToolResourcePrefix = "ClaudiaKerman.KOS.tooltests.";
        private const string ToolTestPrefix = "tools/";
        /// <summary>The name a test's second craft (<c>target_craft</c>) is spawned with, so a test script finds it by <c>vessel(name)</c>.</summary>
        private const string TargetName = "Block Test Target";

        private enum Stage
        {
            Idle,
            LoadingSave,
            WaitingForFlight,
            Running,
        }

        private readonly List<string> _log = new List<string>();
        private Rect _rect = new Rect(80, 240, 640, 0);
        private string _filter = "";
        private Vector2 _scroll;
        private bool _visible = true;
        private readonly DebugRunWindow _bridgeTab = new DebugRunWindow();
        private static readonly string[] TabNames = { "Block Tests - Unvalidated", "Block Tests - Failed", "Block Tests - Passed", "kOS Bridge Tests" };
        private const int BridgeTab = 3;
        /// <summary>The tab last picked, kept for the session (a static survives the scene changes a run goes through).</summary>
        private static int _tab;
        /// <summary>The tab picked in this frame's input event; applied at the next Layout event so Layout and Repaint draw the same controls.</summary>
        private static int _pendingTab = -1;
        private readonly Vector2[] _listScroll = new Vector2[3];
        private Dictionary<string, BlockTestLogEntry> _results = new Dictionary<string, BlockTestLogEntry>();

        private Stage _stage = Stage.Idle;
        private string _test;
        private BlockTestSpec _spec;
        private uint _vesselId;
        private uint _partId;
        private float _deadline;
        private int _sceneFrames;
        /// <summary>#1358: a landed start's wait for the craft to come to rest; null for other starts, and once it has.</summary>
        private BlockTestLandedStart.Settle _settle;
        /// <summary>#1363: a landed test's hop target, moved to flat ground, handed to test.ks as test_hop_target; null for none.</summary>
        private Spot _hopTarget;

        private void Awake()
        {
            if (!DebugRunWindow.DebugToolsOn())
            {
                Destroy(this);
                return;
            }
            DontDestroyOnLoad(this);
            Debug.Log(ModInfo.LogTag + " block test: shipped " + string.Join(", ", TestNames().ToArray()));
            ReadResults();
        }

        /// <summary>Reads block-tests.log once (at start, and after each run); a missing or unreadable log leaves every test Unvalidated.</summary>
        private void ReadResults()
        {
            try
            {
                var path = Path.Combine(SaveDirectory, "block-tests.log");
                _results = BlockTestLog.Latest(File.Exists(path) ? File.ReadAllText(path) : "");
                // #1361: the window shows the tab of the test run last.
                _pendingTab = BlockTestList.OpeningTab(TestNames(), _results);
            }
            catch (Exception e)
            {
                _results = new Dictionary<string, BlockTestLogEntry>();
                Debug.LogWarning(ModInfo.LogTag + " couldn't read block-tests.log: " + e.Message);
            }
        }

        private static string SaveDirectory
        {
            get { return Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), SaveFolder); }
        }

        /// <summary>The tests shipped in this DLL: by folder name under <c>tests/blocks/</c>, then <c>tools/</c> and the folder name under <c>tests/tools/</c>.</summary>
        private static IEnumerable<string> TestNames()
        {
            return Shipped(ResourcePrefix).Concat(Shipped(ToolResourcePrefix).Select(n => ToolTestPrefix + n));
        }

        private static IEnumerable<string> Shipped(string prefix)
        {
            return typeof(BlockTestRunner).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".test.cfg", StringComparison.Ordinal))
                .Select(n => n.Substring(prefix.Length, n.Length - prefix.Length - ".test.cfg".Length))
                .OrderBy(n => n, StringComparer.Ordinal);
        }

        private static bool IsToolTest(string test)
        {
            return test.StartsWith(ToolTestPrefix, StringComparison.Ordinal);
        }

        private static string Resource(string test, string file)
        {
            var name = IsToolTest(test) ? ToolResourcePrefix + test.Substring(ToolTestPrefix.Length) : ResourcePrefix + test;
            using (var stream = typeof(BlockTestRunner).Assembly.GetManifestResourceStream(name + "." + file))
            {
                if (stream == null)
                    throw new FileNotFoundException("not shipped in the DLL: tests/" + (IsToolTest(test) ? test : "blocks/" + test) + "/" + file);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        // ---- The window ----

        /// <summary>#1361: the window's width and its two scrolling areas at design size (before <see cref="ClaudiaSkin.FontScale"/>); the window itself is as tall as its contents.</summary>
        private const float DesignWidth = 640, DesignListHeight = 240, DesignLogHeight = 180;
        private float _builtScale;

        private void OnGUI()
        {
            var scene = HighLogic.LoadedScene;
            if (scene != GameScenes.MAINMENU && scene != GameScenes.SPACECENTER && scene != GameScenes.FLIGHT) return;
            if (!_visible)
            {
                GUILayout.BeginArea(new Rect(_rect.x, _rect.y, ClaudiaSkin.Scaled(200), ClaudiaSkin.ButtonHeight));
                if (ClaudiaSkin.SecondaryButton("Block tests (debug)")) _visible = true;
                GUILayout.EndArea();
                return;
            }
            // A new UI scale resizes the window on its next draw; GUILayout.Window never shrinks, so the height starts over.
            if (_builtScale != ClaudiaSkin.FontScale)
            {
                _builtScale = ClaudiaSkin.FontScale;
                _rect.height = 0;
            }
            _rect.width = ClaudiaSkin.Scaled(DesignWidth);
            _rect = GUILayout.Window(GetInstanceID(), _rect, Draw, "", ClaudiaSkin.Window, GUILayout.Width(_rect.width));
        }

        private void Draw(int id)
        {
            ClaudiaSkin.DrawTitleBar(_rect, "Claudia Kerman: block test runner (debug_tools)", null);
            if (Event.current.type == EventType.Layout && _pendingTab >= 0)
            {
                if (_pendingTab != _tab) _rect.height = 0; // GUILayout.Window never shrinks after a tall tab
                _tab = _pendingTab;
                _pendingTab = -1;
            }
            var tests = TestNames().ToList();
            var counts = new int[3];
            foreach (var test in tests) counts[BlockTestList.TabOf(test, _results)]++;
            var labels = new string[TabNames.Length];
            for (var i = 0; i < labels.Length; i++) labels[i] = i < 3 ? TabNames[i] + " (" + counts[i] + ")" : TabNames[i];
            // Two tabs to a row, as the old selection grid had them.
            for (var row = 0; row < labels.Length; row += 2)
            {
                GUILayout.BeginHorizontal();
                for (var i = row; i < Math.Min(row + 2, labels.Length); i++)
                    if (ClaudiaSkin.ToggleButton(_tab == i, labels[i], GUILayout.ExpandWidth(true)) && i != _tab) _pendingTab = i;
                GUILayout.EndHorizontal();
            }

            if (_tab == BridgeTab)
            {
                _bridgeTab.Draw();
                if (ClaudiaSkin.SmallButton("Hide", GUILayout.Width(ClaudiaSkin.Scaled(60)))) _visible = false;
                GUI.DragWindow();
                return;
            }

            // The filter box (#1361): keeps the tests whose names contain the text, on every tab.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", ClaudiaSkin.Body, GUILayout.ExpandWidth(false));
            _filter = GUILayout.TextField(_filter, ClaudiaSkin.TextField, GUILayout.ExpandWidth(true));
            if (_filter.Length > 0 && ClaudiaSkin.SmallButton("Clear", GUILayout.Width(ClaudiaSkin.Scaled(60)))) _filter = "";
            GUILayout.EndHorizontal();

            // Rows of a few buttons: one row of every test runs wider than the screen, and the count
            // below says how many there are to run, so a skipped test is easy to spot. Failed and Passed
            // show a date (and Failed its reason), so they take a row each. The test run last is first.
            var shown = BlockTestList.Shown(tests, _results, _tab, _filter);
            var perRow = _tab == 0 ? 3 : 1;
            _listScroll[_tab] = GUILayout.BeginScrollView(_listScroll[_tab], GUILayout.Height(ClaudiaSkin.Scaled(DesignListHeight)));
            for (var row = 0; row < shown.Count; row += perRow)
            {
                GUILayout.BeginHorizontal();
                foreach (var test in shown.Skip(row).Take(perRow))
                {
                    GUI.enabled = _stage == Stage.Idle && HighLogic.LoadedScene != GameScenes.FLIGHT;
                    var label = "Run " + test;
                    BlockTestLogEntry entry;
                    if (_tab != 0 && _results.TryGetValue(test, out entry))
                    {
                        label += " (" + entry.Date + ")";
                        if (_tab == 1 && entry.Reason.Length > 0) label += " " + Cut(entry.Reason, 70);
                    }
                    if (ClaudiaSkin.SecondaryButton(label, GUILayout.ExpandWidth(true))) Begin(test);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (shown.Count == 0) GUILayout.Label(_filter.Trim().Length > 0 ? "No test on this tab has '" + _filter.Trim() + "' in its name." : "No tests on this tab.", ClaudiaSkin.Secondary);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            GUILayout.Label(shown.Count + " shown of " + tests.Count + " tests shipped", ClaudiaSkin.Body);
            if (ClaudiaSkin.SmallButton("Hide", GUILayout.Width(ClaudiaSkin.Scaled(60)))) _visible = false;
            GUILayout.EndHorizontal();
            GUILayout.Label(_stage == Stage.Idle
                ? "Idle. Running a test loads its own save (" + SaveFolder + "); leave Flight first."
                : "Running '" + _test + "': " + _stage, ClaudiaSkin.Secondary);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(ClaudiaSkin.Scaled(DesignLogHeight)));
            GUILayout.Label(string.Join("\n", _log.ToArray()), ClaudiaSkin.Secondary);
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private static string Cut(string text, int max)
        {
            var line = text.Split('\n')[0];
            return line.Length <= max ? line : line.Substring(0, max - 1) + "...";
        }

        // ---- The run ----

        private void Begin(string test)
        {
            _test = test;
            _hopTarget = null;
            _log.Clear();
            try
            {
                _spec = BlockTestSpec.Parse(test, Resource(test, "test.cfg"));
                Directory.CreateDirectory(Path.Combine(SaveDirectory, "Ships/VAB"));
                File.WriteAllText(Path.Combine(SaveDirectory, "persistent.sfs"), Resource(test, "scenario.sfs"));
                File.WriteAllText(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.Craft), Resource(test, _spec.Craft));
                if (_spec.TargetCraft != null)
                    File.WriteAllText(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.TargetCraft), Resource(test, _spec.TargetCraft));
                MissionDriver.BlockTestTargetId = null;
                Core.Tools.BlockTestAim.Clear(); // a block test never sees an earlier test's aim (#892)
                MissionDriver.BlockTestPlan = _spec.Plan.Count > 0 ? _spec.Plan : null;
                MissionDriver.BlockTestPartTests = _spec.PartTests.Count > 0 ? new Core.Contracts.PartTestWatch(_spec.PartTests, "Kerbin") : null;

                var game = GamePersistence.LoadGame("persistent", SaveFolder, true, false);
                if (game == null) throw new InvalidOperationException("KSP wouldn't load the scenario save");
                Note("loaded the scenario save; going to the Space Center");
                HighLogic.CurrentGame = game;
                HighLogic.SaveFolder = SaveFolder;
                game.startScene = GameScenes.SPACECENTER;
                _stage = Stage.LoadingSave;
                _sceneFrames = 0;
                game.Start();
            }
            catch (Exception e)
            {
                Fail("couldn't start: " + e.Message);
                Debug.LogException(e);
            }
        }

        private void Update()
        {
            try
            {
                if (_stage == Stage.LoadingSave) InSpaceCenter();
                else if (_stage == Stage.WaitingForFlight) InFlight();
                else if (_stage == Stage.Running && Time.realtimeSinceStartup > _deadline) TimedOut();
            }
            catch (Exception e)
            {
                Fail("runner error: " + e.Message);
                Debug.LogException(e);
            }
        }

        /// <summary>Once the Space Center has settled, spawn the craft into orbit or landed and fly to it, or roll it out to the launch pad.</summary>
        private void InSpaceCenter()
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER || ++_sceneFrames < 30) return;
            if (_spec.Start == BlockTestStart.LaunchPad)
            {
                // The vessel's id isn't known until Flight builds it; InFlight takes the active vessel.
                _vesselId = 0;
                Note("rolling '" + _spec.Craft + "' out to the launch pad; going to Flight");
                _stage = Stage.WaitingForFlight;
                _sceneFrames = 0;
                FlightDriver.StartWithNewLaunch(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.Craft),
                    HighLogic.CurrentGame.flagURL, "LaunchPad", new VesselCrewManifest());
                return;
            }
            if (_spec.Start == BlockTestStart.Landed)
            {
                // #1355: standing on the ground at the test's site, as a Site Hop starts.
                var ground = FlightGlobals.GetBodyByName(_spec.LandedBody);
                if (ground == null) throw new InvalidOperationException("no body called '" + _spec.LandedBody + "'");
                // #1358, #1363: a lander set down on a slope topples; move the start, and the hop's target, to flat ground before spawning.
                var place = BlockTestLandedStart.Place(ClaudiaAddon.HeightOn(ground), ground.Radius, _spec.LandedLat, _spec.LandedLng, _spec.HopLat, _spec.HopLng);
                if (place.Failure != null)
                {
                    Fail(place.Failure);
                    return;
                }
                foreach (var line in place.Notes) Note(line);
                _hopTarget = place.Target;
                var landed = CraftSpawner.Landed(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.Craft), ground, place.Start.Lat, place.Start.Lng, null);
                _vesselId = landed.persistentId;
                Note("landed '" + landed.vesselName + "' on " + ground.bodyName + " at " + place.Start.Lat.ToString("0.###") + ", " + place.Start.Lng.ToString("0.###") + "; going to Flight");
                _settle = new BlockTestLandedStart.Settle();
                FocusSpawned(landed);
                return;
            }
            var ut = Planetarium.GetUniversalTime();
            var orbit = CraftSpawner.OrbitFor(_spec.OrbitBody, _spec.OrbitAltitude, _spec.OrbitInclination, _spec.OrbitPeriapsis, ut);
            var proto = CraftSpawner.IntoOrbit(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.Craft), orbit, null);
            _vesselId = proto.persistentId;
            Note("spawned '" + proto.vesselName + "' in a " + _spec.OrbitAltitude + " m orbit of " + _spec.OrbitBody + "; going to Flight");
            if (_spec.TargetCraft != null && _spec.TargetLanded)
            {
                // #662: landed, target_landed_ahead degrees of longitude east of the point under the craft now.
                var body = orbit.referenceBody;
                var lng = body.GetLongitude(orbit.getPositionAtUT(ut)) + _spec.TargetLandedAhead;
                lng -= 360 * Math.Floor((lng + 180) / 360);
                var target = CraftSpawner.Landed(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.TargetCraft), body, _spec.TargetLandedLat, lng, TargetName);
                MissionDriver.BlockTestTargetId = target.persistentId;
                Note("landed the target '" + TargetName + "' on " + body.bodyName + " at " + _spec.TargetLandedLat.ToString("0.###") + ", " + lng.ToString("0.###"));
            }
            else if (_spec.TargetCraft != null)
            {
                // The second craft (#455): placed relative to the first, and read as the Target Vessel while the test runs.
                var target = CraftSpawner.Relative(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.TargetCraft), orbit, ut, _spec.TargetOrbitAlong,
                    _spec.TargetOrbitPrograde, _spec.TargetOrbitNormal, _spec.TargetOrbitRadial, TargetName);
                MissionDriver.BlockTestTargetId = target.persistentId;
                Note("spawned the target '" + TargetName + "' " + _spec.TargetOrbitAlong + " m along the track, moving " + _spec.TargetOrbitPrograde + " prograde, "
                    + _spec.TargetOrbitNormal + " normal, " + _spec.TargetOrbitRadial + " radial m/s relative");
            }
            FocusSpawned(proto);
        }

        /// <summary>Saves the game with the spawned craft in it and goes to Flight focused on it.</summary>
        private void FocusSpawned(ProtoVessel proto)
        {
            GamePersistence.SaveGame("persistent", SaveFolder, SaveMode.OVERWRITE);
            _stage = Stage.WaitingForFlight;
            _sceneFrames = 0;
            FlightDriver.StartAndFocusVessel("persistent", HighLogic.CurrentGame.flightState.protoVessels.IndexOf(proto));
        }

        /// <summary>Once the craft is the active, unpacked vessel, start the test script on its processor.</summary>
        private void InFlight()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return;
            var vessel = FlightGlobals.ActiveVessel;
            var bridge = KosBridge.Instance;
            if (vessel == null || (_vesselId != 0 && vessel.persistentId != _vesselId) || vessel.packed || bridge == null || ++_sceneFrames < 60) return;
            if (_settle != null && !Settled(vessel)) return;

            var processor = KosBridge.ChooseProcessor(vessel, null);
            if (!processor.HasValue)
            {
                Fail("the reference craft has no kOS processor");
                return;
            }
            _partId = processor.Value;
            if (_spec.TumbleRate != 0) SpinUp(vessel, _spec.TumbleRate);
            _stage = Stage.Running;
            _deadline = Time.realtimeSinceStartup + (float)_spec.TimeoutSeconds;
            Note("running " + _spec.Block + " (timeout " + _spec.TimeoutSeconds + " s)");
            var script = Resource(_test, "test.ks");
            if (_hopTarget != null) script = BlockTestLandedStart.WithHopTarget(script, _hopTarget);
            var source = CoreLibrary.Bundle(script);
            // Planning Tools open to this test's processor until Finish (ADR 0006); never to a Mission Script.
            ToolTestGate.Shared.Open(_partId);
            bridge.BeginRun(_partId, "claudia-blocktest:" + _test, source, Judge, reason => Fail("the bridge refused: " + reason));
        }

        /// <summary>
        /// #1358: whether a landed start's craft has come to rest (LANDED and still for a few seconds of game time), noting it in the log
        /// when it has; fails the test, with the craft's lean and the ground's slope, if it doesn't in time.
        /// </summary>
        private bool Settled(Vessel vessel)
        {
            if (_sceneFrames == 60) Note("waiting for '" + vessel.vesselName + "' to settle: LANDED under " + BlockTestLandedStart.SettleSpeed + " m/s for "
                + BlockTestLandedStart.SettleSeconds + " s, at most " + BlockTestLandedStart.GiveUpSeconds + " s");
            var state = _settle.Step(Time.time, vessel.Landed, vessel.srfSpeed);
            if (state == BlockTestLandedStart.SettleState.Waiting) return false;
            var lean = Vector3.Angle(vessel.ReferenceTransform.up, vessel.upAxis);
            var body = vessel.mainBody;
            var slope = Terrain.Slope(ClaudiaAddon.HeightOn(body), body.Radius, vessel.latitude, vessel.longitude);
            _settle = null;
            if (state == BlockTestLandedStart.SettleState.NeverSettled)
            {
                Fail(BlockTestLandedStart.NeverSettled(lean, slope));
                return false;
            }
            Note("settled: LANDED and still, leaning " + lean.ToString("0.0") + " degrees on ground sloping " + slope.ToString("0.0") + " degrees");
            return true;
        }

        /// <summary>Sets the unpacked craft spinning about its pitch (right) axis at rate degrees per second: a tumbling start.</summary>
        private void SpinUp(Vessel vessel, double rate)
        {
            var spin = vessel.ReferenceTransform.right * (float)(rate * Math.PI / 180);
            var centre = vessel.CoM;
            var drift = vessel.rootPart.Rigidbody.velocity;
            foreach (var part in vessel.parts)
            {
                if (part.Rigidbody == null) continue;
                // A rigid spin about the centre of mass: each body also moves with the spin at its offset.
                part.Rigidbody.angularVelocity = spin;
                part.Rigidbody.velocity = drift + Vector3.Cross(spin, part.Rigidbody.worldCenterOfMass - centre);
            }
            Note("set the craft tumbling at " + rate + " deg/s");
        }

        private void Judge(RunOutcome outcome)
        {
            if (_stage != Stage.Running) return;
            var result = _spec.Judge(outcome);
            // The warp trace under the checks, so block-tests.log shows what the warp.* checks read.
            if (outcome.Trace != null)
                foreach (var line in outcome.Trace.Lines()) result.Lines.Add("warp trace: " + line);
            Finish(result);
        }

        private void TimedOut()
        {
            var result = _spec.TimedOut();
            Finish(result);
            if (KosBridge.Instance != null) KosBridge.Instance.Abort(_partId, "block test timed out");
        }

        private void Fail(string why)
        {
            Finish(new BlockTestResult(false, "FAIL " + (_test ?? "block test") + ": " + why, new List<string>()));
        }

        private void Finish(BlockTestResult result)
        {
            _stage = Stage.Idle;
            _settle = null;
            ToolTestGate.Shared.Close();
            MissionDriver.BlockTestTargetId = null;
            MissionDriver.BlockTestPlan = null;
            MissionDriver.BlockTestPartTests = null;
            Note(result.Headline);
            foreach (var line in result.Lines) Note("  " + line);
            try
            {
                Directory.CreateDirectory(SaveDirectory);
                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z ";
                File.AppendAllText(Path.Combine(SaveDirectory, "block-tests.log"),
                    stamp + result.Headline + "\n" + string.Concat(result.Lines.Select(l => "    " + l + "\n").ToArray()));
            }
            catch (IOException e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't write block-tests.log: " + e.Message);
            }
            ReadResults();
        }

        private void Note(string line)
        {
            _log.Add(line);
            Debug.Log(ModInfo.LogTag + " block test: " + line);
        }
    }
}
