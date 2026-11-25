// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Adapter;
using ClaudiaKerman.Core.Contracts;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Exceptions;

namespace ClaudiaKerman.KOS
{
    /// <summary>
    /// #1307: <c>ADDONS:CLAUDIA:SURVEYSCHEDULE</c>, the Survey Schedule for the craft's orbit and the accepted surveys' orbital waypoints
    /// over its body. A Mission Script calls it once and coasts to each pass, so it never works out the ground track itself. The search
    /// is <see cref="SurveySchedule"/>; this side gives it the game's own propagation of the orbit. It only reads.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddSurveySuffixes()
        {
            AddSuffix("SURVEYSCHEDULE", new NoArgsSuffix<ListValue>(SurveyScheduleNow,
                "The Survey Schedule: a list in flying order of lexicons waypoint, lat, lng, ut, altitude, out_of_band, unreachable, left_for_next_plane, reaching_inclination (only passes within 12 game hours of now carry a ut; left_for_next_plane waypoints wait for the next Survey Plane; when SURVEYPLANE gives a node the passes are those after its burn); then a ground survey's sites in landing order with landing, needs_precision"));
            AddSuffix("SURVEYPLANE", new NoArgsSuffix<Lexicon>(SurveyPlaneNow,
                "The Survey Plane for the orbital survey's remaining waypoints: a lexicon inclination, lan, passes_over (the waypoints it passes over in one revolution) and node: true with ut, radial, normal, prograde and delta_v of the one burn that reaches it (ADD NODE(ut, radial, normal, prograde), then exec_node), false when the craft's own plane serves. Quote the Survey Plane and its burn in the survey's Sequence Plan"));
        }

        /// <summary>
        /// #1341: <c>ADDONS:CLAUDIA:SURVEYPLANE</c>, the Survey Plane and its node from <see cref="SurveySchedule.Plane"/>, the function the
        /// <c>survey_schedule</c> Planning Tool runs, on the craft's orbit elements as the Game Snapshot reads them and the game's own frame reads.
        /// </summary>
        private Lexicon SurveyPlaneNow()
        {
            var vessel = shared.Vessel;
            if (vessel == null || vessel.orbit == null) throw new KOSException("SURVEYPLANE needs a craft in orbit");
            var orbit = vessel.orbit;
            if (orbit.eccentricity >= 1) throw new KOSException("SURVEYPLANE needs a closed orbit");
            var body = orbit.referenceBody;
            var orbital = SurveyWaypointReader.Orbital(body.bodyName);
            if (orbital.Count == 0) throw new KOSException("SURVEYPLANE: no accepted survey has a waypoint to take from orbit round " + body.bodyName);
            var under = UnderFrame(body);
            SurveyPlane plane;
            try
            {
                plane = SurveySchedule.Plane(new SurveyBody { Radius = body.Radius, RotationPeriod = body.rotates ? body.rotationPeriod : 0, Gm = body.gravParameter },
                    SnapshotReader.OrbitOf(orbit), Planetarium.GetUniversalTime(), under, orbital, SurveyWaypointReader.TriggerRange());
            }
            catch (System.InvalidOperationException e)
            {
                throw new KOSException("SURVEYPLANE: " + e.Message);
            }
            var result = new Lexicon();
            result.Add(new StringValue("inclination"), ScalarValue.Create(plane.Inclination));
            result.Add(new StringValue("lan"), ScalarValue.Create(plane.Lan));
            var over = new ListValue();
            foreach (var waypoint in plane.Passed) over.Add(new StringValue(waypoint.Where()));
            result.Add(new StringValue("passes_over"), over);
            result.Add(new StringValue("node"), new BooleanValue(plane.Node != null));
            if (plane.Node != null)
            {
                result.Add(new StringValue("ut"), ScalarValue.Create(plane.Node.Ut));
                result.Add(new StringValue("radial"), ScalarValue.Create(plane.Node.Radial));
                result.Add(new StringValue("normal"), ScalarValue.Create(plane.Node.Normal));
                result.Add(new StringValue("prograde"), ScalarValue.Create(plane.Node.Prograde));
                result.Add(new StringValue("delta_v"), ScalarValue.Create(plane.Node.DeltaV));
            }
            return result;
        }

        /// <summary>As the Planning Tools read the frame (PlanningBody.SurfaceUnder): elements' vectors go into the game through Planetarium.Zup.</summary>
        private static SurfaceUnder UnderFrame(CelestialBody body)
        {
            return (double x, double y, double z, out double latitude, out double longitude) =>
            {
                var game = Planetarium.Zup.WorldToLocal(new Vector3d(x, y, z));
                var at = body.GetLatitudeAndLongitude(game.xzy, true);
                latitude = at.x;
                longitude = at.y;
                return true;
            };
        }

        private ListValue SurveyScheduleNow()
        {
            var vessel = shared.Vessel;
            if (vessel == null || vessel.orbit == null) throw new KOSException("SURVEYSCHEDULE needs a craft in orbit");
            var orbit = vessel.orbit;
            var body = orbit.referenceBody;
            var orbital = SurveyWaypointReader.Orbital(body.bodyName);
            var ground = SurveyWaypointReader.Ground(body.bodyName);
            // #1327: a ground survey's landing order needs no orbit, as in the Planning Tool; only the passes do.
            var closed = orbit.eccentricity < 1;
            if (!closed && ground.Count == 0) throw new KOSException("SURVEYSCHEDULE needs a closed orbit");
            var now = Planetarium.GetUniversalTime();
            var turn = body.rotates ? body.rotationPeriod : 0;
            GroundTrack track = ut =>
            {
                var at = body.position + orbit.getRelativePositionAtUT(ut).xzy;
                // The game reads latitude and longitude off the body as it is now; by the UT it has turned on.
                var turned = turn > 0 ? (ut - now) * 360 / turn : 0;
                return new TrackPoint { Latitude = body.GetLatitude(at), Longitude = body.GetLongitude(at) - turned, Altitude = body.GetAltitude(at) };
            };
            // #1354: with a node on the Survey Plane the passes are those after the burn, as in the Planning Tool (the one Core function).
            List<SurveyPass> entries;
            try
            {
                entries = SurveySchedule.Entries(new SurveyBody { Radius = body.Radius, RotationPeriod = turn, Gm = body.gravParameter },
                    closed ? new SurveyOrbit { Track = track, Period = orbit.period, Inclination = orbit.inclination } : null,
                    closed ? SnapshotReader.OrbitOf(orbit) : null, now, UnderFrame(body), orbital, ground, SurveyWaypointReader.TriggerRange());
            }
            catch (System.InvalidOperationException e)
            {
                throw new KOSException("SURVEYSCHEDULE: " + e.Message);
            }
            var list = new ListValue();
            foreach (var pass in entries)
            {
                var line = new Lexicon();
                line.Add(new StringValue("waypoint"), new StringValue(pass.Waypoint.Where()));
                line.Add(new StringValue("lat"), ScalarValue.Create(pass.Waypoint.Latitude));
                line.Add(new StringValue("lng"), ScalarValue.Create(pass.Waypoint.Longitude));
                if (pass.Ut.HasValue) line.Add(new StringValue("ut"), ScalarValue.Create(pass.Ut.Value));
                if (pass.Altitude.HasValue) line.Add(new StringValue("altitude"), ScalarValue.Create(pass.Altitude.Value));
                line.Add(new StringValue("out_of_band"), new StringValue(pass.OutOfBand));
                line.Add(new StringValue("unreachable"), new BooleanValue(pass.Unreachable));
                line.Add(new StringValue("left_for_next_plane"), new BooleanValue(pass.LeftForNextPlane));
                if (pass.ReachingInclination.HasValue) line.Add(new StringValue("reaching_inclination"), ScalarValue.Create(pass.ReachingInclination.Value));
                if (pass.Landing > 0)
                {
                    line.Add(new StringValue("landing"), ScalarValue.Create(pass.Landing));
                    line.Add(new StringValue("needs_precision"), new BooleanValue(pass.NeedsPrecision));
                    line.Add(new StringValue("radius_unknown"), new BooleanValue(pass.RadiusUnknown));
                }
                list.Add(line);
            }
            return list;
        }
    }
}
