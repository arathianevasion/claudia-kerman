// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using UnityEngine;

namespace ClaudiaKerman.KOS
{
    /// <summary>
    /// Puts a <c>.craft</c> into a circular orbit as a new vessel in the current game, for the debug
    /// block test runner, and a test's second craft relative to it (#455). The parts are built from the craft file (so missing module data takes the
    /// part's defaults), snapshotted to PART nodes and added as a ProtoVessel: the way Contract
    /// Configurator spawns craft files.
    /// </summary>
    internal static class CraftSpawner
    {
        /// <summary>
        /// A test's orbit at <paramref name="ut"/> (<c>orbit_*</c>): circular at altitude; or, with a periapsis, an orbit whose
        /// apoapsis is altitude, started at the apoapsis.
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
            float rootHeight;
            return HighLogic.CurrentGame.AddVessel(VesselNode(craftPath, orbit, name, out rootHeight));
        }

        /// <summary>
        /// A block test's second craft landed (#662): upright on <paramref name="body"/> at <paramref name="lat"/>,
        /// <paramref name="lng"/>, its lowest point a metre over the terrain, named <paramref name="name"/>. KSP settles it on the
        /// ground when it loads.
        /// </summary>
        public static ProtoVessel Landed(string craftPath, CelestialBody body, double lat, double lng, string name)
        {
            // A landed vessel's node still carries an orbit; KSP works it out again from the position.
            var orbit = new Orbit(0, 0, body.Radius + 10000, 0, 0, 0, Planetarium.GetUniversalTime(), body);
            float rootHeight;
            var node = VesselNode(craftPath, orbit, name, out rootHeight);
            var terrain = body.TerrainAltitude(lat, lng, false);
            // The craft's up (+Y in the editor) along the surface normal: LookRotation turns +Z onto it, and +Y onto +Z first.
            var up = body.GetRelSurfaceNVector(lat, lng);
            var rotation = Quaternion.LookRotation(new Vector3((float)up.x, (float)up.y, (float)up.z)) * Quaternion.FromToRotation(Vector3.up, Vector3.forward);
            node.SetValue("sit", Vessel.Situations.LANDED.ToString(), true);
            node.SetValue("landed", "True", true);
            node.SetValue("splashed", "False", true);
            node.SetValue("lat", lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture), true);
            node.SetValue("lon", lng.ToString("R", System.Globalization.CultureInfo.InvariantCulture), true);
            node.SetValue("alt", (terrain + rootHeight + 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture), true);
            node.SetValue("hgt", (rootHeight + 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture), true);
            node.SetValue("rot", KSPUtil.WriteQuaternion(rotation), true);
            return HighLogic.CurrentGame.AddVessel(node);
        }

        /// <summary>
        /// The VESSEL node of the craft file on <paramref name="orbit"/>, named <paramref name="name"/> (the craft's own when null), and
        /// <paramref name="rootHeight"/>: m from its lowest collider up to its root part, as built in the editor.
        /// </summary>
        private static ConfigNode VesselNode(string craftPath, Orbit orbit, string name, out float rootHeight)
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
            var lowest = ship.parts.SelectMany(part => part.GetComponentsInChildren<Collider>()).Select(c => c.bounds.min.y).DefaultIfEmpty(ship.parts[0].transform.position.y).Min();
            rootHeight = Math.Max(0, ship.parts[0].localRoot.transform.position.y - lowest);

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
            return vesselNode;
        }
    }
}
