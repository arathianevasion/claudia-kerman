// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block ascend: flies from the surface of an airless body, or from a fall over it (the
// abort to orbit, #597), to a circular orbit above its highest terrain. Built on claudia_lib.ks and
// exec_node.ks (which flies the circularization); block-only helpers are prefixed ascend_. The "//|"
// lines are Claudia's docs for it; the first is its summary.
//
// The climb: straight up off the ground (from flight, the same pitch law first stops the fall), then
// the pitch that holds half the vertical speed that would coast it up to target_alt (so most of the
// thrust goes into orbital speed), until apoapsis reaches it; then a node at apoapsis circularizes.

@lazyglobal off.

//| ascend(options): from the ground or a fall over an airless body, to a circular orbit
//| (SUB_ORBITAL/FLYING): stops any fall, climbs low for orbital speed, then circularizes at
//| target_alt (exec_node). For a craft falling with periapsis under the terrain (a prograde
//| node there points into the ground). ORBITING clear of the terrain: success, nothing flown.
//| Options:
//|   target_alt ["auto"]: orbit altitude in m; "auto" is 5 km above the body's highest terrain.
//|     It must be above the highest terrain (ADDONS:CLAUDIA:HIGHESTTERRAIN(body)) and inside the SOI.
//|   target_inc [0]: orbit inclination in degrees (0 to 180; under 90 is prograde); one below the
//|     site's latitude gives the nearest. A moving craft in flight keeps its heading.
//|   max_duration [900]: seconds.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: ap_error (m, apoapsis minus target_alt once in orbit), start (ground, flight, orbit).
//| Fails with "ascend flies from airless bodies only", ESCAPING, a bad target_alt, "no thrust",
//| thrust-to-weight under 1.05, too little delta-v or thrust before impact, periapsis
//| still under the highest terrain, out of fuel, or an abort.
//| Example: `local res is ascend(lexicon("target_inc", 0)). addons:claudia:report(res).`

// The block's own options and defaults (target_alt "auto" is resolved from the body's terrain).
function ascend_defaults {
  return lexicon("target_alt", "auto", "target_inc", 0).
}

// ---- Pure helpers ----

// "" or why target_inc isn't one the block can use (target_alt is checked once it is resolved).
function ascend_unreadable {
  parameter opt.
  if not opt:target_inc:istype("Scalar") {
    return "target_inc must be degrees from 0 to 180".
  }
  if opt:target_inc < 0 or opt:target_inc > 180 {
    return "target_inc must be degrees from 0 to 180".
  }
  return "".
}

// target_alt resolved: "auto" is 5 km above the highest terrain; anything else is left as it is.
function ascend_target_alt {
  parameter target_alt, highest.
  if target_alt:istype("String") and target_alt = "auto" {
    return highest + 5000.
  }
  return target_alt.
}

// "" or why a (resolved) target_alt can't be flown to: it must be metres, above the highest
// terrain and below the edge of the sphere of influence (soi_alt, m above sea level).
function ascend_check_alt {
  parameter target_alt, highest, soi_alt, body_name.
  if not target_alt:istype("Scalar") {
    return "target_alt must be " + char(34) + "auto" + char(34) + " or metres, not '" + target_alt + "'".
  }
  if target_alt <= highest {
    return "target_alt " + round(target_alt) + " m is not above the highest terrain, " + round(highest) + " m".
  }
  if target_alt >= soi_alt {
    return "target_alt " + round(target_alt) + " m is outside the " + body_name + "'s sphere of influence".
  }
  return "".
}

// The compass heading (degrees) to fly for inclination inc from latitude lat, given the orbital
// speed to reach (m/s) and the ground's own eastward speed there (m/s). Heads north of east for a
// prograde orbit (the ascending node).
function ascend_azimuth {
  parameter inc, lat, v_orbit, v_ground.
  local inertial is arcsin(max(-1, min(1, cos(inc) / cos(lat)))).
  local east is v_orbit * sin(inertial) - v_ground.
  local v_north is v_orbit * cos(inertial).
  local az is arctan2(east, v_north).
  if az < 0 {
    set az to az + 360.
  }
  return az.
}

// The vertical speed (m/s) to climb at: half of what would coast the craft up to target_alt from
// alt_now under gravity less centrifugal lift, g_eff (m/s^2), so most of the thrust goes into
// horizontal speed and apoapsis reaches target_alt mostly through that (as g_eff falls).
function ascend_vv_target {
  parameter alt_now, target_alt, g_eff.
  return 0.5 * sqrt(2 * max(0.01, g_eff) * max(0, target_alt - alt_now)).
}

// Pitch above the horizon (degrees) that holds vertical speed vv at vv_want with full acceleration
// accel (m/s^2) against g_eff.
function ascend_pitch {
  parameter accel, g_eff, vv, vv_want.
  if accel <= 0 {
    return 90.
  }
  return arcsin(max(0, min(1, (g_eff + 0.2 * (vv_want - vv)) / accel))).
}

// How the climb starts (#597), from the status sit, whether the body has an atmosphere, the periapsis pe
// and the highest terrain (m): "ground", "flight" (SUB_ORBITAL, FLYING, or an orbit whose periapsis
// is under the terrain: the abort to orbit for a falling craft), "orbit" (already clear of the
// terrain: nothing to fly), or why it can't start.
function ascend_start {
  parameter sit, has_atm, pe, highest, body_name.
  if has_atm {
    return "ascend flies from airless bodies only; " + body_name + " has an atmosphere (use launch)".
  }
  if sit = "LANDED" or sit = "SPLASHED" or sit = "PRELAUNCH" {
    return "ground".
  }
  if sit = "SUB_ORBITAL" or sit = "FLYING" {
    return "flight".
  }
  // ESCAPING isn't flown, even into the terrain: past escape speed the climb has no apoapsis to reach.
  if sit = "ORBITING" and pe <= highest {
    return "flight".
  }
  if sit = "ORBITING" {
    return "orbit".
  }
  return "ascend starts landed, flying or sub-orbital, not " + sit.
}

// The climb's pitch above the horizon (degrees) at radius r_now (m from the body's centre) and alt_now
// (m), with horizontal speed v_h and vertical speed vv (m/s, up positive), to target_alt goal, with
// full acceleration accel (m/s^2): ascend_pitch holding ascend_vv_target, so a falling craft first
// points up to stop the fall, and any thrust left over builds horizontal speed.
function ascend_climb_pitch {
  parameter mu, r_now, v_h, vv, alt_now, goal, accel.
  local g_eff is mu / r_now ^ 2 - v_h ^ 2 / r_now.
  return ascend_pitch(accel, g_eff, vv, ascend_vv_target(alt_now, goal, g_eff)).
}

// The compass heading (degrees) to climb on from flight: the craft's own direction of travel az_travel
// once its horizontal (orbital) speed v_h is 20 m/s or more, as turning it costs twice that; az_inc
// (target_inc's) from a hover (the Mun and Minmus turn about 9 m/s at the equator).
function ascend_flight_az {
  parameter az_inc, az_travel, v_h.
  if v_h >= 20 {
    return az_travel.
  }
  return az_inc.
}

// "" or why a craft in flight can't reach orbit before it hits the ground: vertical speed vv and
// horizontal speed v_h (m/s), v_orbit the orbital speed at target_alt, full acceleration accel against
// g_eff (m/s^2), h m above the ground, dv_left m/s (0 or less when stock can't say). The delta-v
// needed is at least the fall to stop plus the horizontal speed still to build.
function ascend_flight_short {
  parameter vv, v_h, v_orbit, accel, g_eff, h, dv_left.
  if vv < 0 {
    if accel <= g_eff {
      return "too little thrust to reach orbit before impact: thrust-to-weight " + round(accel / max(0.001, g_eff), 2) + " while falling".
    }
    local stop is vv ^ 2 / (2 * (accel - g_eff)).
    if stop >= h {
      return "too little thrust to reach orbit before impact: stopping the fall takes " + round(stop) + " m, " + round(h) + " m above the ground".
    }
  }
  local need is max(0, -vv) + max(0, v_orbit - v_h).
  if dv_left > 0 and dv_left < need {
    return "too little delta-v to reach orbit before impact: needs at least " + round(need) + " m/s, " + round(dv_left) + " m/s left".
  }
  return "".
}

// The apoapsis (m) the climb runs to from start "ground" or "flight": goal, or from flight at least
// 1 km above the craft's altitude alt_now.
function ascend_climb_to {
  parameter start, goal, alt_now.
  if start = "flight" {
    return max(goal, alt_now + 1000).
  }
  return goal.
}

// Prograde delta-v (m/s) at apoapsis radius r_ap to circularize an orbit of semi-major axis sma.
function ascend_circularize_dv {
  parameter mu, r_ap, sma.
  return sqrt(mu / r_ap) - sqrt(mu * (2 / r_ap - 1 / sma)).
}

// ---- The block ----

function ascend {
  parameter options is lexicon().
  local extra is lexicon("ap_error", 0, "start", "").
  local ctx is claudia_begin("ascend", options, ascend_defaults(), 900).
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is ascend_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local highest is addons:claudia:highestterrain(body).
  local start is ascend_start(status, body:atm:exists, ship:periapsis, highest, body:name).
  if start <> "ground" and start <> "flight" and start <> "orbit" {
    return claudia_finish(ctx, false, start, extra).
  }
  set extra:start to start.
  local goal is ascend_target_alt(opt:target_alt, highest).
  local bad_alt is ascend_check_alt(goal, highest, body:soiradius - body:radius, body:name).
  if bad_alt <> "" {
    return claudia_finish(ctx, false, bad_alt, extra).
  }
  set opt:target_alt to goal.
  // Already in an orbit clear of the terrain: success, nothing flown.
  if start = "orbit" {
    return claudia_finish(ctx, true, "", extra).
  }

  // A craft whose engines aren't lit yet, or (from flight) whose stage is dry, stages into them (within
  // stage_limit), so the flight check below judges the engines that will fly the climb.
  if start = "flight" {
    local staged is claudia_autostage(ctx).
    if staged <> "" {
      return claudia_finish(ctx, false, staged, extra).
    }
  }
  local lit is claudia_stage_into_engines(ctx, start = "ground").
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }
  local mu is body:mu.
  local g0 is mu / (body:radius + altitude) ^ 2.
  local v_orbit is sqrt(mu / (body:radius + goal)).
  local v_ground is 2 * constant:pi * body:radius * cos(ship:geoposition:lat) / body:rotationperiod.
  local az is ascend_azimuth(opt:target_inc, ship:geoposition:lat, v_orbit, v_ground).
  if start = "ground" {
    local twr is ship:availablethrust / ship:mass / g0.
    if twr < 1.05 {
      return claudia_finish(ctx, false, "thrust-to-weight " + round(twr, 2) + " is too low to lift off", extra).
    }
  } else {
    local hv is vxcl(ship:up:vector, ship:velocity:orbit).
    local radius0 is body:radius + altitude.
    local short is ascend_flight_short(ship:verticalspeed, hv:mag, v_orbit, ship:availablethrust / ship:mass,
      g0 - hv:mag ^ 2 / radius0, alt:radar, ship:deltav:current).
    if short <> "" {
      return claudia_finish(ctx, false, short, extra).
    }
    local az_travel is mod(arctan2(vdot(hv, heading(90, 0):vector), vdot(hv, ship:north:vector)) + 360, 360).
    set az to ascend_flight_az(az, az_travel, hv:mag).
  }

  local stowed is claudia_stow_ship_systems(ctx).
  if stowed <> "" {
    return claudia_finish(ctx, false, stowed, extra).
  }
  local pitch is 90.
  local tset is 1.
  sas off.
  lock steering to heading(az, pitch).
  lock throttle to tset.

  // From flight the climb aims at least 1 km above the craft (ascend_climb_to), so a fall from above
  // goal is stopped and apoapsis is still ahead to circularize at; the aim follows the craft down while
  // it falls and holds once it climbs.
  local aim is ascend_climb_to(start, goal, altitude).
  until ship:verticalspeed >= 0 and ship:apoapsis >= aim {
    if ship:verticalspeed < 0 {
      set aim to ascend_climb_to(start, goal, altitude).
    }
    if start = "ground" and gear and alt:radar > 100 and ship:verticalspeed > 0 { gear off. } // Legs in once clear of the ground (#946).
    local v_h is vxcl(ship:up:vector, ship:velocity:orbit):mag.
    local accel is ship:availablethrust / max(0.001, ship:mass).
    if alt:radar < 100 {
      // Clear the ground first.
      set pitch to 90.
    } else {
      set pitch to ascend_climb_pitch(mu, body:radius + altitude, v_h, ship:verticalspeed, altitude, aim, accel).
    }
    // Ease off as apoapsis nears the target, but never while falling.
    set tset to min(1, max(0.05, (aim - ship:apoapsis) / 2000)).
    if ship:verticalspeed < 0 {
      set tset to 1.
    }
    // Burning the whole climb (#379): tset is never under 0.05, so the tick's default (tset > 0) already stages on a flameout.
    local ticked is claudia_tick(ctx, tset, true).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    wait 0.
  }
  set tset to 0.
  claudia_release_controls().

  // Circularize at apoapsis.
  local nd is node(time:seconds + eta:apoapsis, 0, 0, ascend_circularize_dv(mu, body:radius + ship:apoapsis, ship:orbit:semimajoraxis)).
  add nd.
  local flown is claudia_fly_node(ctx, nd).
  set extra:ap_error to round(ship:apoapsis - goal).
  if flown <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, flown, extra).
  }
  if ship:periapsis <= highest {
    return claudia_finish(ctx, false, "periapsis " + round(ship:periapsis) + " m is still under the highest terrain, " + round(highest) + " m", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
