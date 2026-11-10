// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// A body's air for the reentry predictor (#282), from KSP's own curves: pressure, then temperature with the latitude's
    /// day-night mean offset (the sun's place over the fall is not predicted, as the Trajectories mod also averages it), then
    /// stock's density and speed of sound. Main thread; reads only.
    /// </summary>
    public sealed class ReentryAir : IReentryAir
    {
        private readonly CelestialBody _body;

        public ReentryAir(CelestialBody body)
        {
            _body = body;
        }

        public void Air(double altitude, double latitude, out double density, out double speedOfSound)
        {
            density = 0;
            speedOfSound = 0;
            if (!_body.atmosphere || altitude >= _body.atmosphereDepth) return;
            altitude = Math.Max(0, altitude);
            var pressure = _body.GetPressure(altitude);
            if (pressure <= 0) return;
            var abs = (float)Math.Abs(latitude);
            // Stock's offset is the latitude bias plus the sun's share, times how high the sun is (0 to 1): 0.5 over a day.
            var offset = _body.latitudeTemperatureBiasCurve.Evaluate(abs) + _body.latitudeTemperatureSunMultCurve.Evaluate(abs) * 0.5;
            var temperature = _body.GetFullTemperature(altitude, offset);
            density = _body.GetDensity(pressure, temperature);
            speedOfSound = _body.GetSpeedOfSound(pressure, density);
        }

        public double PressureAtm(double altitude)
        {
            if (!_body.atmosphere || altitude >= _body.atmosphereDepth) return 0;
            // Stock's GetPressure is kPa.
            return _body.GetPressure(Math.Max(0, altitude)) * PhysicsGlobals.KpaToAtmospheres;
        }
    }

    /// <summary>
    /// The axis of <see cref="ReentryCraft"/>'s read (#924), by <see cref="EntryAttitude"/>'s rule: the heat shield's own axis turned toward home, or home's up with no shield, and never
    /// the vessel's <c>ReferenceTransform</c>, which at the Mun may be a part on the stack or a Reversed control point. Occlusion needs no such care: flight works it out above the air too
    /// (<c>reentry</c>'s read after its drop, in vacuum and with nothing bared by this code, found the face the drop had just uncovered).
    /// </summary>
    internal sealed class EntryAxis
    {
        public Vector3 Nose;
        public Vector3 Top;
        private string _described;

        public string Describe()
        {
            return _described;
        }

        /// <summary>The axis of <paramref name="vessel"/> with <paramref name="leaveOut"/> gone, by kOS UID as the predictor takes them.</summary>
        public static EntryAxis Of(Vessel vessel, ICollection<string> leaveOut, uint? core = null)
        {
            var gone = new HashSet<string>(leaveOut ?? new string[0]);
            var goneIds = new List<uint>();
            var byId = new Dictionary<uint, Part>();
            foreach (var part in vessel.parts)
            {
                byId[part.persistentId] = part;
                if (gone.Contains(part.flightID.ToString(CultureInfo.InvariantCulture))) goneIds.Add(part.persistentId);
            }
            var attitude = EntryAttitude.Of(SnapshotReader.PartsOf(vessel), goneIds, core);
            var home = attitude.Home != null ? byId[attitude.Home.Id] : vessel.rootPart;
            var shield = attitude.Shield != null ? byId[attitude.Shield.Id] : null;
            var homeUp = home.partTransform.up;
            var read = attitude.NoseOf(id => Doubles(byId[id].partTransform.up), id => Doubles(byId[id].partTransform.position));
            var nose = read != null ? Vector(read) : homeUp;
            var top = Vector3.ProjectOnPlane(-home.partTransform.forward, nose);
            if (top.sqrMagnitude < 1e-6f) top = Vector3.ProjectOnPlane(home.partTransform.right, nose);
            var control = vessel.GetReferenceTransformPart();
            return new EntryAxis
            {
                Nose = nose,
                Top = top.normalized,
                _described = string.Format(CultureInfo.InvariantCulture, "control {0}, axis {1}, against home up {2:0.000}, against control up {3:0.000}",
                    Name(control), shield != null ? Name(shield) + " to " + Name(home) : Name(home) + " up", Vector3.Dot(nose, homeUp), Vector3.Dot(nose, vessel.ReferenceTransform.up)),
            };
        }

        internal static string Name(Part part)
        {
            return part == null ? "none" : part.partInfo != null ? part.partInfo.name : part.name;
        }

        private static double[] Doubles(Vector3 v)
        {
            return new double[] { v.x, v.y, v.z };
        }

        private static Vector3 Vector(double[] v)
        {
            return new Vector3((float)v[0], (float)v[1], (float)v[2]);
        }
    }

    /// <summary>
    /// A vessel for the reentry predictor (#282), held surface retrograde as <c>reentry</c> flies it, along <see cref="EntryAxis"/> (#924): its nose points
    /// back along the airflow, so the air comes from the shield. Each part's drag cubes are read once, with KSP's own
    /// <c>DragCubeList.AddSurfaceDragDirection</c> (as the Trajectories mod does), at a table of Mach numbers; drag between them
    /// is interpolated, times stock's multipliers and pseudo-Reynolds curve. Parts shielded from the airstream add none.
    /// Parts the block will drop (#326, by kOS UID, which is the part's flightID) are left out, mass and drag: a part that
    /// stays faces the air bare where one of them is attached, as it will once they have gone.
    /// Body lift (#462) is read from the same cubes as MechJeb's ReentrySimulation reads it: each part's cube lift for the
    /// airflow, times its and stock's body-lift multipliers, toward the craft's top square to the flow, at a table of angles
    /// off retrograde (the airflow tilted toward the craft's belly); stock's lift-by-Mach curve is applied when asked. Parts
    /// with a lifting-surface module are left out of it, as stock does (their wing lift is not modelled). The parachutes are
    /// each ModuleParachute's own numbers, with the drag areas of its SEMIDEPLOYED and DEPLOYED cubes met as the part meets
    /// the air (held retrograde, or hanging under the canopy alike), at low Mach. Given the parts the heat shield drop takes
    /// (<c>shieldDrop</c>, kOS UIDs), <see cref="AfterShield"/> is the craft without them. A Ship System the entry will stow
    /// (#868, <see cref="ShipSystemsKeeper.StowedForEntry"/>) is read by its RETRACTED cube, occluded as its live faces are:
    /// the craft meets the air with its panels and dishes in, whatever they are when the block asks.
    /// Main thread; reads only (the drag cube data is a local copy; body lift is read from the live cubes, so a part beside a
    /// dropped one keeps that one's occlusion for its lift).
    /// </summary>
    public sealed class ReentryCraft : IReentryCraft
    {
        private static readonly double[] Machs = MachTable();
        private static readonly double[] Angles = { 0, 5, 10, 15, 20, 30, 45, 60, 90 };
        private static readonly Vector3[] Faces = DragCubeDrag.Faces;
        /// <summary>The Mach the open parachutes' cubes are read at: they open subsonic, where stock's curves hardly change.</summary>
        private const float ChuteMach = 0.3f;
        private readonly double[] _area = new double[Machs.Length];
        private readonly double[] _lift = new double[Angles.Length];
        private readonly List<ReentryChute> _chutes = new List<ReentryChute>();
        private readonly double _mass;
        private readonly ReentryCraft _afterShield;
        private readonly EntryAxis _axis;
        private readonly List<string> _read = new List<string>();

        /// <summary>Ship Systems out now that this craft reads stowed (#868), for the block-call lines.</summary>
        public int StowedCount { get; private set; }

        public ReentryCraft(Vessel vessel, ICollection<string> leaveOut = null, ICollection<string> shieldDrop = null, uint? core = null)
            : this(vessel, leaveOut, shieldDrop, ShipSystemsKeeper.StowedForEntry(vessel), EntryAxis.Of(vessel, leaveOut, core))
        {
        }

        private ReentryCraft(Vessel vessel, ICollection<string> leaveOut, ICollection<string> shieldDrop, ICollection<uint> stowed, EntryAxis axis)
        {
            var gone = new HashSet<string>(leaveOut ?? new string[0]);
            _axis = axis;
            // The way the craft moves through the air on its Entry, shield first (#924), whatever part is in control when the block asks.
            var nose = axis.Nose;
            var top = axis.Top;
            var flow = -nose;
            if (gone.Count == 0) _mass = vessel.totalMass * 1000;
            foreach (var part in vessel.parts)
            {
                if (Gone(part, gone)) continue;
                if (gone.Count > 0) _mass += (part.mass + part.GetResourceMass()) * 1000;
                if (!DragCubeDrag.MeetsAir(part))
                {
                    _read.Add(EntryAxis.Name(part) + " shielded");
                    continue;
                }
                var local = part.partTransform.InverseTransformDirection(flow);
                var bared = BaredFaces(part, gone);
                var chute = part.FindModuleImplementing<ModuleParachute>();
                // A chute already open has its canopy in its live cubes; the predictor adds that itself (Chutes), so its own
                // drag here is its PACKED cube's.
                var packed = chute != null && Open(chute) ? Cube(part, "PACKED") : null;
                var retracted = packed == null ? Retracted(part, stowed) : null;
                if (retracted != null) StowedCount++;
                for (var i = 0; i < Machs.Length; i++)
                    _area[i] += PartArea(part, local, (float)Machs[i], packed, retracted, bared);
                _read.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:0.00}", EntryAxis.Name(part), bared != null ? "bare" : "-", Drag(PartArea(part, local, 2f, packed, retracted, bared), 1000)));
                if (!part.hasLiftModule)
                {
                    for (var a = 0; a < Angles.Length; a++)
                    {
                        var rad = Angles[a] * Math.PI / 180;
                        // Tilted toward the belly, the flow meets the craft's belly side, as a nose-up lifting entry does.
                        var tilted = (flow * (float)Math.Cos(rad) - top * (float)Math.Sin(rad)).normalized;
                        var lift = part.partTransform.TransformDirection(DragCubeDrag.Lift(part, part.partTransform.InverseTransformDirection(tilted)));
                        var square = Vector3.ProjectOnPlane(top, tilted).normalized;
                        _lift[a] += Vector3.Dot(lift, square) * part.bodyLiftMultiplier * PhysicsGlobals.BodyLiftMultiplier;
                    }
                }
                if (chute != null) AddChute(part, chute, local);
            }
            if (shieldDrop != null && shieldDrop.Count > 0)
            {
                var without = new HashSet<string>(gone);
                foreach (var uid in shieldDrop) without.Add(uid);
                _afterShield = new ReentryCraft(vessel, without, null, stowed, axis);
            }
        }

        /// <summary>The part's areaDrag at <paramref name="mach"/> as this craft reads it: an open chute's PACKED cube, a stowed Ship System's RETRACTED one, the bared faces, or the part as stock has it.</summary>
        private static double PartArea(Part part, Vector3 local, float mach, DragCube packed, DragCube retracted, BaredCubeFaces bared)
        {
            return packed != null
                ? DragCubeDrag.Area(part.DragCubes, packed.Area, packed.Drag, local, mach)
                : retracted != null
                ? DragCubeDrag.Area(part.DragCubes, StowedFaces(part, retracted, bared), retracted.Drag, local, mach)
                : bared != null
                    ? DragCubeDrag.Area(part.DragCubes, bared.Areas, bared.Drags, local, mach)
                    : DragCubeDrag.Area(part, local, mach);
        }

        /// <summary>The read for KSP.log (#924), never Claudia: the part in control, the axis read and how it lies against home's up and the control point's, then each kept part's name, whether a face was bared, and its area at Mach 2.</summary>
        public string Diagnosis()
        {
            return _axis.Describe() + "; " + string.Join(", ", _read.ToArray());
        }

        /// <summary>The part's RETRACTED cube when the entry will stow it and it isn't in yet; null otherwise, or with no such cube.</summary>
        private static DragCube Retracted(Part part, ICollection<uint> stowed)
        {
            if (stowed == null || !stowed.Contains(part.persistentId)) return null;
            var deployable = part.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable == null || deployable.deployState == ModuleDeployablePart.DeployState.RETRACTED) return null;
            return Cube(part, "RETRACTED");
        }

        /// <summary>The stowed cube's face areas, each occluded by the share its live face is (bared faces as <paramref name="bared"/> has them).</summary>
        private static float[] StowedFaces(Part part, DragCube retracted, BaredCubeFaces bared)
        {
            var live = bared != null ? bared.Areas : part.DragCubes.AreaOccluded;
            var areas = (float[])retracted.Area.Clone();
            for (var f = 0; f < areas.Length; f++)
            {
                var whole = part.DragCubes.WeightedArea[f];
                if (whole > 0) areas[f] *= Mathf.Clamp01(live[f] / whole);
            }
            return areas;
        }

        private void AddChute(Part part, ModuleParachute chute, Vector3 local)
        {
            var read = ChuteOf(part, chute, local);
            if (read != null) _chutes.Add(read);
        }

        /// <summary>
        /// The part's parachute for the predictor as <paramref name="vessel"/> holds it surface retrograde, for the Game Snapshot
        /// (ADR 0019: the Chute Groups read these); null for a part without one, or a cut chute.
        /// </summary>
        public static ReentryChute ChuteOf(Vessel vessel, Part part)
        {
            return ChuteOf(part, vessel.ReferenceTransform.up);
        }

        /// <summary>The same for a part of the editor's ship (#641), held with <paramref name="nose"/> (the root part's up) retrograde.</summary>
        public static ReentryChute ChuteOf(Part part, Vector3 nose)
        {
            var chute = part.FindModuleImplementing<ModuleParachute>();
            if (chute == null) return null;
            return ChuteOf(part, chute, part.partTransform.InverseTransformDirection(-nose));
        }

        /// <summary>
        /// For <c>BRAKETICK</c> (#954), each tick of a powered finish: the title, state (stowed, armed, semi, deployed, cut) and whether it is a drogue
        /// (by its part name, as the block's own rule) of each parachute of <paramref name="uids"/> (kOS part UIDs) still aboard. One gone (burned off, or dropped) isn't waited for.
        /// </summary>
        public static List<object> ChuteStates(Vessel vessel, IEnumerable<string> uids)
        {
            var wanted = new HashSet<uint>();
            foreach (var uid in uids)
            {
                uint id;
                if (uint.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) wanted.Add(id);
            }
            var found = new List<object>();
            foreach (var part in vessel.parts)
            {
                if (!wanted.Contains(part.flightID)) continue;
                var chute = part.FindModuleImplementing<ModuleParachute>();
                if (chute != null) found.Add(new Dictionary<string, object> { { "title", part.partInfo != null ? part.partInfo.title : part.name }, { "state", StateName(chute.deploymentState) }, { "drogue", part.name.IndexOf("drogue", StringComparison.OrdinalIgnoreCase) >= 0 } });
            }
            return found;
        }

        private static string StateName(ModuleParachute.deploymentStates state)
        {
            switch (state)
            {
                case ModuleParachute.deploymentStates.STOWED: return ReentryBrake.Stowed;
                case ModuleParachute.deploymentStates.ACTIVE: return ReentryBrake.Armed;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED: return "semi";
                case ModuleParachute.deploymentStates.DEPLOYED: return ReentryBrake.Deployed;
                default: return ReentryBrake.Cut;
            }
        }

        private static ReentryChute ChuteOf(Part part, ModuleParachute chute, Vector3 local)
        {
            ReentryChuteState state;
            switch (chute.deploymentState)
            {
                case ModuleParachute.deploymentStates.STOWED: state = ReentryChuteState.Stowed; break;
                case ModuleParachute.deploymentStates.ACTIVE: state = ReentryChuteState.Armed; break;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED: state = ReentryChuteState.SemiDeployed; break;
                case ModuleParachute.deploymentStates.DEPLOYED: state = ReentryChuteState.Deployed; break;
                // Cut, or anything newer: no drag to come from it.
                default: return null;
            }
            return new ReentryChute
            {
                // As reentry.ks's reentry_is_drogue reads the part's name.
                Drogue = part.partInfo != null && part.partInfo.name.ToLowerInvariant().Contains("drogue"),
                State = state,
                MinPressureAtm = chute.minAirPressureToOpen,
                DeployAltitude = chute.deployAltitude,
                SemiArea = CubeArea(part, "SEMIDEPLOYED", local),
                FullArea = CubeArea(part, "DEPLOYED", local),
                SemiSeconds = chute.semiDeploymentSpeed > 0 ? 1 / chute.semiDeploymentSpeed : 0,
                FullSeconds = chute.deploymentSpeed > 0 ? 1 / chute.deploymentSpeed : 0,
                Curve = chute.deploymentCurve > 0 ? chute.deploymentCurve : 1,
            };
        }

        /// <summary>The drag area, m², of the part's drag cube <paramref name="name"/> alone, unoccluded, met along <paramref name="local"/>; 0 if it has none.</summary>
        private static double CubeArea(Part part, string name, Vector3 local)
        {
            var cube = Cube(part, name);
            return cube == null ? 0 : DragCubeDrag.Area(part.DragCubes, cube.Area, cube.Drag, local, ChuteMach) * DragCubeDrag.Multiplier;
        }

        private static DragCube Cube(Part part, string name)
        {
            foreach (var cube in part.DragCubes.Cubes)
                if (cube.Name == name) return cube;
            return null;
        }

        private static bool Open(ModuleParachute chute)
        {
            return chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED
                || chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED;
        }

        private static bool Gone(Part part, HashSet<string> gone)
        {
            return gone.Count > 0 && gone.Contains(part.flightID.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The part's face areas with every face toward an attached part that will be dropped bared (unoccluded), or null when
        /// none is: stock occludes a face by what is attached at its node (DragCubeList.SetPartOcclusion), and that goes. A bared
        /// face takes back its coefficient too (#924), which stock rewrote for the covered face.
        /// </summary>
        private static BaredCubeFaces BaredFaces(Part part, HashSet<string> gone)
        {
            var bared = new HashSet<int>();
            foreach (var node in part.attachNodes)
            {
                if (node == null || node.attachedPart == null || !Gone(node.attachedPart, gone)) continue;
                var direction = node.orientation.normalized;
                for (var f = 0; f < Faces.Length; f++)
                    if (Vector3.Dot(direction, Faces[f]) >= 0.7f) bared.Add(f);
            }
            var cubes = part.DragCubes;
            var unoccluded = BaredCubeFaces.UnoccludedDrag(cubes.Cubes.ConvertAll(c => c.Drag), cubes.Cubes.ConvertAll(c => c.Weight));
            return BaredCubeFaces.Bare(cubes.AreaOccluded, cubes.WeightedDrag, cubes.WeightedArea, unoccluded, bared);
        }

        public double MassKg
        {
            get { return _mass; }
        }

        public double DragArea(double mach, double densitySpeed)
        {
            return Drag(Interpolate(mach), densitySpeed);
        }

        private static double Drag(double area, double densitySpeed)
        {
            return area * DragCubeDrag.Multiplier * PhysicsGlobals.DragCurvePseudoReynolds.Evaluate((float)densitySpeed);
        }

        public double LiftArea(double mach, double angleOfAttack)
        {
            var angle = Math.Max(Angles[0], Math.Min(Angles[Angles.Length - 1], Math.Abs(angleOfAttack)));
            var lift = _lift[_lift.Length - 1];
            for (var i = 1; i < Angles.Length; i++)
            {
                if (angle > Angles[i]) continue;
                var t = (angle - Angles[i - 1]) / (Angles[i] - Angles[i - 1]);
                lift = _lift[i - 1] + t * (_lift[i] - _lift[i - 1]);
                break;
            }
            if (lift == 0) return 0;
            return lift * PhysicsGlobals.BodyLiftCurve.liftMachCurve.Evaluate((float)mach);
        }

        public IList<ReentryChute> Chutes
        {
            get { return _chutes; }
        }

        public IReentryCraft AfterShield
        {
            get { return _afterShield; }
        }

        private double Interpolate(double mach)
        {
            if (mach <= Machs[0]) return _area[0];
            for (var i = 1; i < Machs.Length; i++)
            {
                if (mach > Machs[i]) continue;
                var t = (mach - Machs[i - 1]) / (Machs[i] - Machs[i - 1]);
                return _area[i - 1] + t * (_area[i] - _area[i - 1]);
            }
            return _area[Machs.Length - 1];
        }

        /// <summary>Mach numbers to read the cubes at: close where stock's curves bend (round Mach 1), wider at hypersonic speeds.</summary>
        private static double[] MachTable()
        {
            var machs = new System.Collections.Generic.List<double>();
            for (var m = 0.0; m < 3; m += 0.05) machs.Add(m);
            for (var m = 3.0; m < 10; m += 0.25) machs.Add(m);
            for (var m = 10.0; m <= 30; m += 1) machs.Add(m);
            return machs.ToArray();
        }
    }
}
