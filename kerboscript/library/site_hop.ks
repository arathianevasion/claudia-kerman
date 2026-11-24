// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block site_hop (#1310): a Site Hop. Core's SITEHOP (ADR 0014) gives the arc, reach check and burn ticks; this reads the
// state, steers, throttles and stages, then hands the descent to land (land.ks).

@lazyglobal off.

//| site_hop(options): from landed, flies an arc to a site on the same body and lands there, touching down in Daylight.
//| It waits on the ground, warping, until a liftoff gives a touchdown with Daylight at the site (no wait if one now would), then
//| one burn onto the least-speed arc that comes down on the site, then land's powered descent from it; aims for about 5 km of it.
//| Options: target [required]: the site, a LATLNG or lexicon("lat", …, "lng", …), at least 1 km away; and land's descent
//|   options (max_slope, max_divert, max_overshoot, touchdown_speed, gear_alt, burn_margin, descent_shape), passed on to it, plus the common ones.
//| Before liftoff it fails with the reason when the site is under 1 km away, the craft can't lift off, the sun never lights
//| the site within 1.2 days of the body, or the hop needs more delta-v than the craft has (the burn plus 10%, and land's need from the arc). Extra results: land's, and hop_km,
//| hop_need_dv, hop_burn_dv. Example: `addons:claudia:report(site_hop(lexicon("target", latlng(2.46, 81.5)))).`

// Its options: land's descent options, passed on to it ("required": the Mission Script gives target).
function site_hop_defaults {
  return lexicon("target", "required", "max_slope", 8, "max_divert", 300, "max_overshoot", 500, "touchdown_speed", 2, "gear_alt", 1000, "burn_margin", 1.10, "descent_shape", "auto").
}

// The state SITEHOP reads, aimed at the site t; now: the look before liftoff times it for a daylight touchdown (#1322).
function site_hop_state {
  parameter t.
  local vo is ship:velocity:orbit.
  return lexicon("mu", body:mu, "day", body:rotationperiod, "lat", ship:geoposition:lat, "lng", ship:geoposition:lng, "r", body:radius + altitude,
    "agl", alt:radar, "vel_up", vdot(vo, up:vector), "vel_north", vdot(vo, north:vector), "vel_east", vdot(vo, vcrs(up:vector, north:vector)),
    "accel", ship:availablethrust / ship:mass, "target_lat", t:lat, "target_lng", t:lng,
    "target_r", body:radius + max(0, body:geopositionlatlng(t:lat, t:lng):terrainheight), "now", time:seconds).
}

function site_hop {
  parameter options is lexicon().
  local extra is lexicon("hop_km", -1, "hop_need_dv", -1, "hop_burn_dv", 0).
  local ctx is claudia_begin("site_hop", options, site_hop_defaults(), "auto").
  local t is ctx:opt:target.
  local why is ctx:error.
  if why = "" and not t:istype("Lexicon") and not t:istype("GeoCoordinates") { set why to "target must be a LATLNG or a lexicon of lat and lng". }
  if why = "" and not claudia_on_ground() { set why to "site_hop starts landed, not " + status. }
  if why <> "" { return claudia_finish(ctx, false, why, extra). }
  local ask is site_hop_state(t).
  set ask:dv to ship:deltav:current.
  local hop is addons:claudia:sitehop(ask).
  set extra:hop_km to round(hop:km, 2).
  set extra:hop_need_dv to round(hop:need).
  if hop:why <> "" { return claudia_finish(ctx, false, hop:why, extra). }
  if hop:wait > 0 { set why to claudia_wait_until(ctx, hop:liftoff_ut). }
  if why <> "" { return claudia_fail(ctx, why, extra). }
  sas off.
  lock steering to heading(hop:steer_heading, hop:steer_pitch).
  lock throttle to hop:throttle.
  until hop:done {
    set why to claudia_tick(ctx, hop:throttle).
    if why <> "" { return claudia_fail(ctx, why, extra). }
    wait 0.
    set hop to addons:claudia:sitehop(site_hop_state(t)).
  }
  unlock throttle. unlock steering.
  set extra:hop_burn_dv to round(ctx:dv_used, 1).
  local landed is land(options).
  claudia_add_result(ctx, landed).
  for key in landed:keys {
    if not list("success", "reason", "dv_used", "dv_remaining", "duration", "body", "situation", "stage", "geo", "orbit"):contains(key) { set extra[key] to landed[key]. }
  }
  return claudia_finish(ctx, landed:success, landed:reason, extra).
}
