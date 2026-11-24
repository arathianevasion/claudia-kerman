// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

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
                "The Survey Schedule: a list in flying order of lexicons waypoint, lat, lng, ut, altitude, out_of_band, unreachable, reaching_inclination; then a ground survey's sites in landing order with landing, needs_precision"));
        }

        private ListValue SurveyScheduleNow()
        {
            var vessel = shared.Vessel;
            if (vessel == null || vessel.orbit == null) throw new KOSException("SURVEYSCHEDULE needs a craft in orbit");
            var orbit = vessel.orbit;
            if (orbit.eccentricity >= 1) throw new KOSException("SURVEYSCHEDULE needs a closed orbit");
            var body = orbit.referenceBody;
            var now = Planetarium.GetUniversalTime();
            var turn = body.rotates ? body.rotationPeriod : 0;
            GroundTrack track = ut =>
            {
                var at = body.position + orbit.getRelativePositionAtUT(ut).xzy;
                // The game reads latitude and longitude off the body as it is now; by the UT it has turned on.
                var turned = turn > 0 ? (ut - now) * 360 / turn : 0;
                return new TrackPoint { Latitude = body.GetLatitude(at), Longitude = body.GetLongitude(at) - turned, Altitude = body.GetAltitude(at) };
            };
            var schedule = SurveySchedule.For(new SurveyBody { Radius = body.Radius, RotationPeriod = turn },
                new SurveyOrbit { Track = track, Period = orbit.period, Inclination = orbit.inclination }, now,
                SurveyWaypointReader.Orbital(body.bodyName), SurveyWaypointReader.TriggerRange());
            var list = new ListValue();
            // #1311: a ground survey's sites follow the passes, in landing order: landing (1 is landed on with land, each next flown to with site_hop) and needs_precision.
            var sites = SurveySchedule.Ground(new SurveyBody { Radius = body.Radius }, SurveyWaypointReader.Ground(body.bodyName));
            foreach (var pass in schedule.Concat(sites))
            {
                var line = new Lexicon();
                line.Add(new StringValue("waypoint"), new StringValue(pass.Waypoint.Where()));
                line.Add(new StringValue("lat"), ScalarValue.Create(pass.Waypoint.Latitude));
                line.Add(new StringValue("lng"), ScalarValue.Create(pass.Waypoint.Longitude));
                if (pass.Ut.HasValue) line.Add(new StringValue("ut"), ScalarValue.Create(pass.Ut.Value));
                if (pass.Altitude.HasValue) line.Add(new StringValue("altitude"), ScalarValue.Create(pass.Altitude.Value));
                line.Add(new StringValue("out_of_band"), new StringValue(pass.OutOfBand));
                line.Add(new StringValue("unreachable"), new BooleanValue(pass.Unreachable));
                if (pass.ReachingInclination.HasValue) line.Add(new StringValue("reaching_inclination"), ScalarValue.Create(pass.ReachingInclination.Value));
                if (pass.Landing > 0)
                {
                    line.Add(new StringValue("landing"), ScalarValue.Create(pass.Landing));
                    line.Add(new StringValue("needs_precision"), new BooleanValue(pass.NeedsPrecision));
                }
                list.Add(line);
            }
            return list;
        }
    }
}
