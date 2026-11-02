// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Puts a <c>.craft</c> into a circular orbit as a new vessel in the current game, for the debug
    /// block test runner, and a test's second craft relative to it (#455). The parts are built from the craft file (so missing module data takes the
    /// part's defaults), snapshotted to PART nodes and added as a ProtoVessel: the way Contract
    /// Configurator spawns craft files.
    /// </summary>
    internal static class CraftSpawner
    {
        public static ProtoVessel IntoOrbit(string craftPath, string bodyName, double altitude, double inclination, double? periapsis = null)
        {
            return IntoOrbit(craftPath, OrbitFor(bodyName, altitude, inclination, periapsis, Planetarium.GetUniversalTime()), null);
        }

        /// <summary>
        /// The orbit <see cref="IntoOrbit(string, string, double, double, double?)"/> spawns into at <paramref name="ut"/>: circular at
        /// altitude; or, with a periapsis, an orbit whose apoapsis is altitude, started at the apoapsis.
        /// </summary>
        public static Orbit OrbitFor(string bodyName, double altitude, double inclination, double? periapsis, double ut)
        {
            var body = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == bodyName);
            if (body == null) throw new InvalidOperationException("no body named " + bodyName);
            var apRadius = body.Radius + altitude;
            var peRadius = periapsis.HasValue ? body.Radius + periapsis.Value : apRadius;
            return periapsis.HasValue
                ? new Orbit(inclination, (apRadius - peRadius) / (apRadius + peRadius), (apRadius + peRadius) / 2, 0, 0, Math.PI, ut, body)
                : new Orbit(inclination, 0, apRadius, 0, 0, 0, ut, body);
        }

        /// <summary>
        /// A block test's second craft (#455): on the orbit <see cref="BlockTestTarget.Place"/> gives relative to the craft on
        /// <paramref name="craft"/> at <paramref name="ut"/>, named <paramref name="name"/> so <c>vessel(name)</c> finds it and not the craft.
        /// </summary>
        public static ProtoVessel Relative(string craftPath, Orbit craft, double ut, double along, double prograde, double normal, double radial, string name)
        {
            var at = new SnapshotOrbit
            {
                SemiMajorAxis = craft.semiMajorAxis, Eccentricity = craft.eccentricity, Inclination = craft.inclination, Lan = craft.LAN,
                ArgumentOfPeriapsis = craft.argumentOfPeriapsis, MeanAnomalyAtEpoch = craft.meanAnomalyAtEpoch, Epoch = craft.epoch,
            };
            var placed = BlockTestTarget.Place(craft.referenceBody.gravParameter, at, ut, along, prograde, normal, radial);
            var orbit = new Orbit(placed.Inclination, placed.Eccentricity, placed.SemiMajorAxis, placed.Lan, placed.ArgumentOfPeriapsis,
                placed.MeanAnomalyAtEpoch, placed.Epoch, craft.referenceBody);
            return IntoOrbit(craftPath, orbit, name);
        }

        /// <summary>Spawns the craft file onto <paramref name="orbit"/>, as a vessel named <paramref name="name"/> (the craft's own name when null).</summary>
        public static ProtoVessel IntoOrbit(string craftPath, Orbit orbit, string name)
        {
            var game = HighLogic.CurrentGame;
            var ship = ShipConstruction.LoadShip(craftPath);
            if (ship == null || ship.parts.Count == 0) throw new InvalidOperationException("KSP wouldn't load " + craftPath);

            var missionId = (uint)Guid.NewGuid().GetHashCode();
            var launchId = game.launchID++;
            foreach (var part in ship.parts)
            {
                part.flightID = ShipConstruction.GetUniqueFlightID(game.flightState);
                part.missionID = missionId;
                part.launchID = launchId;
                part.flagURL = game.flagURL;
                part.temperature = 1.0;
            }

            // As on the launchpad, nothing has fired: the current stage is one past the highest stage a part is in.
            var currentStage = ship.parts.Max(part => part.inverseStage) + 1;

            // Snapshot the parts through a throwaway vessel, then let them go.
            var holder = new GameObject("claudia block test spawn");
            var dummyVessel = holder.AddComponent<Vessel>();
            dummyVessel.parts = ship.parts;
            var dummyProto = new ProtoVessel(new ConfigNode(), null) { vesselRef = dummyVessel };
            foreach (var part in ship.parts) dummyProto.protoPartSnapshots.Add(new ProtoPartSnapshot(part, dummyProto));
            foreach (var snapshot in dummyProto.protoPartSnapshots) snapshot.storePartRefs();
            var partNodes = dummyProto.protoPartSnapshots.Select(snapshot =>
            {
                var node = new ConfigNode("PART");
                snapshot.Save(node);
                return node;
            }).ToArray();
            foreach (var part in ship.parts) UnityEngine.Object.Destroy(part.gameObject);
            UnityEngine.Object.Destroy(holder);

            var discovery = ProtoVessel.CreateDiscoveryNode(DiscoveryLevels.Owned, UntrackedObjectClass.A, double.PositiveInfinity, double.PositiveInfinity);
            var vesselNode = ProtoVessel.CreateVesselNode(name ?? ship.shipName, VesselType.Probe, orbit, 0, partNodes, discovery);
            vesselNode.SetValue("stg", currentStage, true); // CreateVesselNode writes 0; Vessel.currentStage loads from this
            return game.AddVessel(vesselNode);
        }
    }
}
