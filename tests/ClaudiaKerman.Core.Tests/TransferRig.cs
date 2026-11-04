// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// A scripted universe for transfer_to (#459): the stock Sun, Kerbin, the Mun, Minmus, Duna and Ike, and a craft on a conic
    /// that moves on the patched conics (<see cref="BodySystem.Follow"/>) between the steps. A node is flown as an impulse at its
    /// UT; a wait moves the craft to its UT. The Core tests fly <see cref="TransferFlight"/> on it, and the Kerboscript tests link
    /// this file to fly the block itself.
    /// </summary>
    internal sealed class TransferRig
    {
        public readonly List<SnapshotBody> Bodies = StockBodies();
        public readonly BodySystem System;
        public SnapshotBody Body;
        public Conic Orbit;
        public double Ut;
        /// <summary>The burns flown: kind and m/s.</summary>
        public readonly List<KeyValuePair<string, double>> Burns = new List<KeyValuePair<string, double>>();
        /// <summary>An error added to the ejection, as a share of it along the burn (0: flown exactly).</summary>
        public double BurnError { get; set; }
        /// <summary>m/s every burn is left short by, as exec_node leaves its residual (0: flown exactly).</summary>
        public double Residual { get; set; }

        private TransferRig(string body, double altitude, double ut, double inclination = 0)
        {
            System = new BodySystem(Bodies);
            Body = System.Find(body);
            var r = Body.Radius + altitude;
            Orbit = Conic.FromElements(Body.Gm, r, 0, inclination, 0, 0, 0, ut);
            Ut = ut;
        }

        /// <summary>A craft in a circular equatorial orbit <paramref name="altitude"/> m over <paramref name="body"/> at <paramref name="ut"/>.</summary>
        public static TransferRig Circular(string body, double altitude, double ut, double inclination = 0)
        {
            return new TransferRig(body, altitude, ut, inclination);
        }

        public GameSnapshot Snapshot()
        {
            var snapshot = new GameSnapshot { Ut = Ut, Situation = Orbit.Closed ? "ORBITING" : "ESCAPING", Body = Body.Name, Orbit = Elements(Orbit, Body) };
            snapshot.Bodies.AddRange(Bodies);
            return snapshot;
        }

        /// <summary>Moves the craft on along its patches to <paramref name="ut"/>.</summary>
        public void MoveTo(double ut)
        {
            if (ut <= Ut) return;
            var path = System.Follow(Body, Orbit, Ut, 8);
            foreach (var patch in path)
            {
                if (patch.EndUt >= ut || patch == path[path.Count - 1])
                {
                    Body = patch.Body;
                    Orbit = patch.Orbit;
                    break;
                }
            }
            Ut = ut;
        }

        /// <summary>Flies a node (stock's components, m/s) at <paramref name="ut"/> as an impulse; gives the m/s burned.</summary>
        public double Fly(string kind, double ut, double prograde, double normal, double radial)
        {
            MoveTo(ut);
            Vec3 r, v;
            Orbit.State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var burn = along * prograde + up * normal + Vec3.Cross(along, up) * radial;
            if (kind == "ejection") burn = burn * (1 + BurnError);
            burn = burn * (Math.Max(0, burn.Length - Residual) / burn.Length);
            Orbit = Conic.FromState(Body.Gm, r, v + burn, ut);
            Burns.Add(new KeyValuePair<string, double>(kind, burn.Length));
            return burn.Length;
        }

        /// <summary>
        /// transfer_to's loop over <see cref="TransferFlight.Next"/> on the rig: each burn flown, each wait waited, until it ends.
        /// Gives the last step, its <see cref="TransferFlightStep.Report"/> holding every step's.
        /// </summary>
        public TransferFlightStep Run(TransferFlightAsk ask, int maxSteps = 20)
        {
            var report = new Dictionary<string, object>();
            for (var i = 0; i < maxSteps; i++)
            {
                var step = TransferFlight.Next(Snapshot(), ask);
                foreach (var entry in step.Report) report[entry.Key] = entry.Value;
                if (step.Error != "" || step.Do == TransferFlightStep.Done)
                {
                    foreach (var entry in report) step.Report[entry.Key] = entry.Value;
                    return step;
                }
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else MoveTo(step.Ut);
                TransferFlight.Carry(ask, step);
            }
            throw new InvalidOperationException("transfer_to took over " + maxSteps + " steps");
        }

        /// <summary>m/s burned on burns of <paramref name="kind"/>.</summary>
        public double Burned(string kind)
        {
            var sum = 0.0;
            foreach (var burn in Burns)
                if (burn.Key == kind) sum += burn.Value;
            return sum;
        }

        /// <summary>KSP's elements of <paramref name="orbit"/> round <paramref name="body"/>, as the Game Snapshot carries the craft's orbit.</summary>
        internal static SnapshotOrbit Elements(Conic orbit, SnapshotBody body)
        {
            var lan = Math.Atan2(orbit.W.X, -orbit.W.Y);
            var node = new Vec3(Math.Cos(lan), Math.Sin(lan), 0);
            var argPe = Math.Atan2(Vec3.Dot(orbit.P, Vec3.Cross(orbit.W, node)), Vec3.Dot(orbit.P, node));
            return new SnapshotOrbit
            {
                SemiMajorAxis = orbit.A, Eccentricity = orbit.E, Inclination = orbit.Inclination, Lan = lan * 180 / Math.PI,
                ArgumentOfPeriapsis = argPe * 180 / Math.PI, MeanAnomalyAtEpoch = orbit.M0, Epoch = orbit.Epoch, Body = body.Name,
                Periapsis = orbit.PeR - body.Radius, Apoapsis = orbit.Closed ? orbit.ApR - body.Radius : 0,
            };
        }

        /// <summary>The stock bodies a transfer to Duna or between Kerbin's moons meets, as a recorded Game Snapshot has them.</summary>
        private static List<SnapshotBody> StockBodies()
        {
            return new List<SnapshotBody>
            {
                new SnapshotBody { Name = "Sun", Gm = 1.1723327948324905e18, Radius = 261600000, AtmosphereHeight = 600000, RotationPeriod = 432000 },
                new SnapshotBody
                {
                    Name = "Kerbin", Parent = "Sun", Gm = 3531600000000, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, Sma = 13599840256,
                    SoiRadius = 84159286.47963049, RotationPeriod = 21549.425183089825, MeanAnomalyAtEpoch = 3.14000010490417,
                },
                new SnapshotBody
                {
                    Name = "Mun", Parent = "Kerbin", Gm = 65138397520.78069, Radius = 200000, Sma = 12000000, SoiRadius = 2429559.1165647474,
                    RotationPeriod = 138984.37657447575, MeanAnomalyAtEpoch = 1.70000004768372,
                },
                new SnapshotBody
                {
                    Name = "Minmus", Parent = "Kerbin", Gm = 1765800026.3124719, Radius = 60000, Sma = 47000000, Inclination = 6, Lan = 78,
                    ArgumentOfPeriapsis = 38, SoiRadius = 2247428.3879023045, RotationPeriod = 40400, MeanAnomalyAtEpoch = 0.899999976158142,
                },
                new SnapshotBody
                {
                    Name = "Duna", Parent = "Sun", Gm = 301363211975.0977, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0666666677961107,
                    Sma = 20726155264, Inclination = 0.0599999986588955, Eccentricity = 0.0509999990463257, Lan = 135.5, SoiRadius = 47921949.369738035,
                    RotationPeriod = 65517.859375, MeanAnomalyAtEpoch = 3.14000010490417,
                },
                new SnapshotBody
                {
                    Name = "Ike", Parent = "Duna", Gm = 18568368573.14401, Radius = 130000, Sma = 3200000, Inclination = 0.200000002980232,
                    Eccentricity = 0.0299999993294477, SoiRadius = 1049598.939311619, RotationPeriod = 65517.86213480807, MeanAnomalyAtEpoch = 1.70000004768372,
                },
            };
        }
    }
}
