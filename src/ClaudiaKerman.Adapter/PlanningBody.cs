// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKerman.Core.Tools;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// The live game behind the Planning Tools' body interface (#265, #278): terrain height, biome and the sun, read with stock
    /// calls. Stock's terrain code is main-thread only (the PQS shares a scratch buffer), but tools run on a worker thread, so a
    /// call from a worker is queued, and <see cref="Pump"/>, called every frame from the main thread, serves the queue for at
    /// most <see cref="BudgetMs"/> of the frame; the worker waits meanwhile. A batch is served a sample at a time, so a big one is
    /// spread over frames instead of stalling one. Called on the main thread itself (the test-only kOS bridge) it runs at once.
    /// Reads only: it never changes the game.
    /// </summary>
    public sealed class PlanningBody : PlanningBodyBase
    {
        /// <summary>The most of one frame the queue may use, ms; one sample past it at most, so a job always moves on.</summary>
        public const double BudgetMs = 3;
        /// <summary>A worker stops waiting for a frame to serve it after this long (a paused game, or a scene change).</summary>
        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

        private sealed class Job
        {
            /// <summary>Does one unit of the work; true when the job is done.</summary>
            public Func<bool> Step;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public volatile bool Abandoned;
        }

        private readonly Queue<Job> _queue = new Queue<Job>();
        private readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;

        /// <summary>Serves queued work for this frame's budget. Main thread; call it from an Update.</summary>
        public void Pump()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Job job;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    job = _queue.Peek();
                }
                var finished = job.Abandoned || Step(job);
                if (finished)
                {
                    lock (_queue) _queue.Dequeue();
                    job.Done.Set();
                }
                if (watch.Elapsed.TotalMilliseconds >= BudgetMs) return;
            }
        }

        private static bool Step(Job job)
        {
            try
            {
                return job.Step();
            }
            catch (Exception e)
            {
                // One bad read must cost the tool its answer, not the frame.
                Debug.LogException(e);
                return true;
            }
        }

        /// <summary>Runs <paramref name="step"/> until it says it is done: now on the main thread, or a slice a frame from the queue.</summary>
        private void Run(Func<bool> step)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThread)
            {
                var guard = new Job { Step = step };
                while (!Step(guard)) { }
                return;
            }
            // A call ToolStep has abandoned (#544) queues nothing more, and one waiting is dropped from the queue.
            var token = PlanningCancellation.Current;
            token.ThrowIfCancellationRequested();
            var job = new Job { Step = step };
            lock (_queue) _queue.Enqueue(job);
            try
            {
                if (!job.Done.Wait(WaitLimit, token)) job.Abandoned = true;
            }
            catch (OperationCanceledException)
            {
                job.Abandoned = true;
                throw;
            }
        }

        private static CelestialBody Find(string name)
        {
            return FlightGlobals.Bodies == null ? null
                : FlightGlobals.Bodies.FirstOrDefault(b => string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase));
        }

        public override double TerrainAltitude(string body, double latitude, double longitude)
        {
            var sample = new SiteSample { Latitude = latitude, Longitude = longitude };
            Survey(body, new List<SiteSample> { sample }, SiteFacts.Altitude);
            return sample.Altitude;
        }

        public override string Biome(string body, double latitude, double longitude)
        {
            var sample = new SiteSample { Latitude = latitude, Longitude = longitude };
            Survey(body, new List<SiteSample> { sample }, SiteFacts.Biome);
            return sample.Biome;
        }

        public override IList<string> BiomeNames(string body)
        {
            var names = new List<string>();
            Run(() =>
            {
                var celestial = Find(body);
                if (celestial != null && celestial.BiomeMap != null && celestial.BiomeMap.Attributes != null)
                    names.AddRange(celestial.BiomeMap.Attributes.Select(a => a.name));
                return true;
            });
            return names;
        }

        /// <summary>Each body's surface objects as read, until the next scene load (#713: read once per body per scene).</summary>
        private static readonly Dictionary<string, List<ClaudiaKerman.Core.Tools.SurfaceObject>> SurfaceRead = new Dictionary<string, List<ClaudiaKerman.Core.Tools.SurfaceObject>>(StringComparer.OrdinalIgnoreCase);
        private static bool _sceneHooked;

        /// <summary>
        /// The body's stock surface objects (#713), from <c>CelestialBody.pqsSurfaceObjects</c>: the list stock fills for every body
        /// when the PQS is set up, and that KerbNet's anomaly scan reads, so it holds the PQSCity and PQSCity2 anomalies of bodies the
        /// craft isn't near (walking the PQS's children would find only the loaded ones). Each object's position comes from its
        /// transform, the terrain height from the PQS, and the game's launch-site and facility links mark the facilities. Read on the
        /// main thread once per body per scene, logged to KSP.log, then served from memory.
        /// </summary>
        public override IList<ClaudiaKerman.Core.Tools.SurfaceObject> SurfaceObjects(string body)
        {
            lock (SurfaceRead)
            {
                List<ClaudiaKerman.Core.Tools.SurfaceObject> cached;
                if (SurfaceRead.TryGetValue(body ?? "", out cached)) return cached;
            }
            var read = new List<ClaudiaKerman.Core.Tools.SurfaceObject>();
            var served = false;
            Run(() =>
            {
                if (!_sceneHooked)
                {
                    GameEvents.onLevelWasLoaded.Add(ForgetSurfaceObjects);
                    _sceneHooked = true;
                }
                var celestial = Find(body);
                if (celestial != null) read.AddRange(ReadSurfaceObjects(celestial));
                served = celestial != null;
                return true;
            });
            // A read a frame never served (a paused game, an abandoned call) is asked again next time.
            if (served) lock (SurfaceRead) SurfaceRead[body ?? ""] = read;
            return read;
        }

        private void ForgetSurfaceObjects(GameScenes scene)
        {
            lock (SurfaceRead) SurfaceRead.Clear();
        }

        /// <summary>Main thread only.</summary>
        private static List<ClaudiaKerman.Core.Tools.SurfaceObject> ReadSurfaceObjects(CelestialBody body)
        {
            var read = new List<ClaudiaKerman.Core.Tools.SurfaceObject>();
            if (body.pqsSurfaceObjects != null)
                foreach (var obj in body.pqsSurfaceObjects)
                {
                    if (obj == null) continue;
                    var city = obj as PQSCity;
                    var city2 = obj as PQSCity2;
                    var found = new ClaudiaKerman.Core.Tools.SurfaceObject
                    {
                        Body = body.bodyName, Name = obj.SurfaceObjectName ?? obj.name ?? "",
                        Facility = (city != null && (city.launchSite != null || city.spaceCenterFacility != null))
                            || (city2 != null && (city2.launchSite != null || city2.spaceCenterFacility != null)),
                    };
                    if (obj.transform != null)
                    {
                        var at = body.GetLatitudeAndLongitude(obj.transform.position);
                        found.Latitude = at.x;
                        found.Longitude = at.y;
                    }
                    else if (city2 != null)
                    {
                        found.Latitude = city2.lat;
                        found.Longitude = city2.lon;
                    }
                    if (body.pqsController != null && !double.IsNaN(found.Latitude) && !double.IsNaN(found.Longitude))
                        found.Altitude = body.TerrainAltitude(found.Latitude, found.Longitude, true);
                    read.Add(found);
                }
            Debug.Log(ClaudiaKerman.Core.ModInfo.LogTag + " Points of Interest: " + read.Count + " surface objects on " + body.bodyName + " (pqsSurfaceObjects)"
                + string.Concat(read.Select(o => string.Format(System.Globalization.CultureInfo.InvariantCulture, "; {0} at {1:0.000}, {2:0.000}, {3:0} m{4}",
                    o.Name, o.Latitude, o.Longitude, o.Altitude, o.Facility ? " (facility)" : "")).ToArray()));
            return read;
        }

        public override double SunElevation(string body, double latitude, double longitude, double ut)
        {
            var sample = new SiteSample { Latitude = latitude, Longitude = longitude, Ut = ut };
            Survey(body, new List<SiteSample> { sample }, SiteFacts.Sun);
            return sample.SunElevation;
        }

        public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
        {
            var next = 0;
            Run(() =>
            {
                if (next >= samples.Count) return true;
                var celestial = Find(body);
                var sample = samples[next++];
                if (celestial != null) Read(celestial, sample, facts);
                return next >= samples.Count;
            });
        }

        public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
        {
            double lat = double.NaN, lon = double.NaN;
            Run(() =>
            {
                var celestial = Find(body);
                if (celestial != null)
                {
                    // Orbit elements give inertial vectors (the reference plane, z north). Stock takes them into the game through
                    // Planetarium.Zup, as Orbit does, which its rotating reference frame turns with the body near it while the
                    // body's own frame stands still (#326: skipping it put a landing off by the turn since that frame began).
                    // The game's vectors are then the same with y and z swapped.
                    var game = Planetarium.Zup.WorldToLocal(new Vector3d(x, y, z));
                    var at = celestial.GetLatitudeAndLongitude(game.xzy, true);
                    lat = at.x;
                    lon = at.y;
                }
                return true;
            });
            latitude = lat;
            longitude = lon;
            return !double.IsNaN(lat) && !double.IsNaN(lon);
        }

        /// <summary>Main thread only.</summary>
        private static void Read(CelestialBody body, SiteSample sample, SiteFacts facts)
        {
            if ((facts & SiteFacts.Altitude) != 0 && body.pqsController != null)
                sample.Altitude = body.TerrainAltitude(sample.Latitude, sample.Longitude, true);
            if ((facts & SiteFacts.Biome) != 0) sample.Biome = BiomeAt(body, sample.Latitude, sample.Longitude);
            if ((facts & SiteFacts.Sun) != 0) sample.SunElevation = SunElevationAt(body, sample.Latitude, sample.Longitude, sample.Ut);
        }

        /// <summary>The stock biome name, as science uses it; "" for a body with no biome map (stock throws for those).</summary>
        internal static string BiomeAt(CelestialBody body, double latitude, double longitude)
        {
            if (body.BiomeMap == null || body.BiomeMap.Attributes == null || body.BiomeMap.Attributes.Length == 0) return "";
            return ScienceUtil.GetExperimentBiome(body, latitude, longitude) ?? "";
        }

        /// <summary>
        /// The sun's elevation above the horizon at a fixed point of the body at <paramref name="ut"/>, degrees. The sun's direction
        /// comes from both bodies' positions at the time. The point's up at that time is the surface normal of the longitude it
        /// has under the body's present rotation, moved by the turn since: the body turns east, so a point fixed to it sits under
        /// a longitude further east by the turn.
        /// </summary>
        private static double SunElevationAt(CelestialBody body, double latitude, double longitude, double ut)
        {
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || body.rotationPeriod <= 0) return double.NaN;
            var toSun = sun.getTruePositionAtUT(ut) - body.getTruePositionAtUT(ut);
            var up = body.GetSurfaceNVector(latitude, longitude + 360 * (ut - Planetarium.GetUniversalTime()) / body.rotationPeriod);
            if (toSun.magnitude <= 0 || up.magnitude <= 0) return double.NaN;
            return 90 - Vector3d.Angle(up, toSun);
        }
    }
}
