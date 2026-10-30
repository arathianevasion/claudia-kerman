// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block land: lands on an airless body from a low, near-circular orbit. Built on
// claudia_lib.ks and exec_node.ks (which flies its deorbit and correction burns); block-only helpers
// are prefixed land_. The "//|" lines are Claudia's docs for it; the first is its summary.
//
// The descent (ADR 0011): the pass search (ADDONS:CLAUDIA:LANDINGPASS) simulates braking over the terrain
// under each pass's approach and picks the periapsis and Descent Shape that clear it most cheaply; a deorbit
// burn half an orbit before the site lowers periapsis to that height; a correction burn on the way down
// swings the orbit plane over the site; the fall turns the craft onto its first braking command; then, from
// the stopping distance (times burn_margin, plus the distance covered in any turn still left), a powered
// descent checks the slope at the site (diverting to flatter ground through ADDONS:CLAUDIA:FLATSPOT),
// predicts its braking again from the real state (ADDONS:CLAUDIA:BRAKEPATH), then brakes once along the way
// it was moving (land_guide: never turning back unless it stopped over max_overshoot past the site, never
// faster than when braking began) holding the Descent Shape's height above the highest terrain ahead
// (land_clearance), comes straight down once stopped and touches down at touchdown_speed. It traces its
// descent to the log (ADDONS:CLAUDIA:BLOCKTRACE) as it goes. No Trajectories: an airless body's fall is plain orbit maths.

@lazyglobal off.

//| land(options): lands on an airless body (the Mun, Minmus...) from a low, near-circular orbit:
//| a deorbit burn timed for the site, a correction burn that puts the ground track over it, and a
//| powered descent that brakes onto it, rejects slopes over max_slope (diverting up to max_divert to
//| flatter ground) and touches down at about touchdown_speed. Aims for about 5 km of the site.
//| Options:
//|   target ["any"]: "any", LATLNG(lat, lng), lexicon("lat", x, "lng", y) or
//|     lexicon("biome", name) on the body orbited. "any" lands about half an orbit on. A biome target
//|     (e.g. lexicon("biome", "Midlands")), with an optional "near" point (LATLNG or lat/lng lexicon),
//|     finds its site when it flies, with find_landing_site's search: the soonest pass (or the one
//|     nearest near) over the biome under the ground track in the window, 5 km inside its edge if it
//|     can, in daylight if daylight is set, within max_slope. A site, given or found, flies on the
//|     soonest pass in the window whose correction burn is within max_correction_dv (warping there on
//|     rails); land solves that pass again shortly before the deorbit burn and, if it missed it, takes the
//|     next (up to 2 times). Use find_landing_site to see sites when you plan, then pass land the biome
//|     with the site it showed as near, or that site alone as the target. land never changes the plane: for a site
//|     off the track, fly a change_orbit from find_landing_site's plane_changes first, as its own step, and budget its total_dv.
//|   within_hours ["auto"]: the window, hours: the larger of 6 and a turn of the body (11.2 at Minmus,
//|     38.6 at the Mun), up to 48. Pass the window_hours find_landing_site gave.
//|   max_correction_dv ["auto"]: m/s the correction burn onto the site may cost: the smaller of 30 and 5%
//|     of the craft's delta-v (30 m/s reaches about 11 km off the ground track). Pass any you gave find_landing_site.
//|   daylight [true]: set it from the Mission Setting Daylight landing. true touches down only with
//|     Daylight at the site: the sun more than 20 degrees up. For a site it looks up to a day of the body ahead
//|     for such a pass (about 46 h at the Mun, 13 h at Minmus) where that is longer than the window, and warps
//|     there on rails, with no Due Time needed; give expected_duration_s that wait. "any" with daylight fails
//|     only on an orbit that runs along the day-night line.
//|   max_slope [8]: degrees; steeper ground is rejected during final descent.
//|   max_divert [300]: m it may move off the site to find ground within max_slope.
//|   max_overshoot [500]: m past the site it may land rather than turn back. The descent brakes
//|     once and never turns back for the site while it stops within this; stopped farther past, it
//|     flies back. Set it from the Mission Goal's accuracy (e.g. 50 for "land within 50 m of the
//|     flag"); a small figure may cost a turn back and its delta-v.
//|   touchdown_speed [2]: m/s at touchdown.
//|   gear_alt [1000]: m above the ground to lower the landing gear.
//|   burn_margin [1.10]: braking starts this much farther out than the stopping distance needs; the delta-v check is the predicted landing's times this.
//|   descent_shape ["auto"]: the Descent Shape braking flies, 0.05 to 10: small holds height and drops late (steep, safe, dearer), large sinks
//|     early (cheap). "auto" simulates braking over the terrain under each pass's approach and flies the cheapest periapsis and shape that clear it
//|     by 300 m or 2 s of descent (150 m over ground level with the site); it moves a site given as coordinates up to 5 km, or takes the next pass, for terrain, and fails "terrain on the approach" if no pass
//|     clears. After a guard abort whose report shows terrain or a climb off the predicted path, retry with a smaller shape (e.g. half the
//|     descent_shape reported); otherwise leave it to the block. A shape that doesn't clear is refused before the deorbit burn, with shape_range.
//|   underhang_agl [2000]: m above the ground to drop any Underhang.
//|   max_duration ["auto"]: the wait to the deorbit burn, plus the planned fall and braking, plus 1200 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Stages off a stage that can't cover the braking before the fall (within stage_limit), so plan the deorbit on it if you like.
//| Drops any Underhang (parts below the landing legs) at underhang_agl [2000] m above the ground during braking, or earlier if it runs dry, then relights on the lander's engines and lands clear of where the Underhang falls. Fails before the deorbit burn if the lander alone can't fly the last underhang_agl m.
//| Extra results: dropped_underhang (the names of the parts dropped), underhang_kept (Underhang parts it couldn't drop), underhang_drop_agl (m above the ground at the drop; -1 if none),
//| staged_before_braking (true if it did; stage_left is the stage it left), slope (degrees where it came to rest), touchdown_speed_actual (m/s),
//| touchdown_h_speed (m/s sideways at touchdown), lean (degrees from upright once settled),
//| reversals (times the descent turned back after braking began; 0 normally), distance_to_target
//| (m from the site asked for, or the spot chosen for "any" or a biome), best_effort (true if nothing within
//| max_divert was flat enough and it landed on the least steep spot found), burn_start_ut (when braking was planned to start, before the fall; -1 if it never got that far),
//| braking_began_ut (when braking actually began; -1 if it never did), impact_ut (when the orbit after the deorbit burn meets
//| the ground on the way down; -1 if it clears it or it never got that far), biome (the biome it came to rest in; "" if it never landed),
//| passes_missed (passes it missed and took the next for; 0 normally), next_pass_ut (when it fails for a missed pass, the touchdown UT of the next it could fly; -1 for none),
//| descent_shape (the shape flown), descent_pe (m, the periapsis chosen), predicted_dv and actual_dv (m/s: the deorbit, correction and braking, simulated and flown),
//| predicted_clearance (m, the predicted path's lowest over the terrain), moved_for_terrain (true if the site moved up to 5 km for terrain), shape_range (the shapes
//| that clear, when it refuses a descent_shape; e.g. "0.2 to 1.2"); find_landing_site gives each site's predicted_dv and descent_shape, from the same simulation;
//| geo (lat, lng, alt) is the common field once landed. Fails with "land flies airless
//| bodies only", an orbit it can't land from, no pass over the site within max_correction_dv, no daylight pass (both before any burn, naming
//| the change_orbit plane changes that reach the site, with their delta-v), a missed pass,
//| "no site in the <biome> biome" (saying how far the search got), landing outside the biome asked for,
//| not enough delta-v, "terrain on the approach" (before the deorbit burn, or as braking begins, keeping its orbit), "deorbit left periapsis" under the plan,
//| braking that raised periapsis or "climbed off its predicted path" (the report says whether it brought the orbit back to near-circular), "tipped over",
//| out of fuel, or an abort; a failure in the descent leaves the craft safe but falling, so give it the delta-v (and TWR over 1.5) it needs.
//| Tool: stage_performance for the delta-v and TWR at the body (and find_landing_site, as above).
//| Example: `local res is land(lexicon("target", latlng(-0.5, 22), "daylight", true)). addons:claudia:report(res).`

// The block's own options and defaults.
function land_defaults {
  return lexicon("target", "any", "daylight", true, "max_slope", 8, "max_divert", 300,
    "max_overshoot", 500, "touchdown_speed", 2, "gear_alt", 1000, "burn_margin", 1.10, "underhang_agl", 2000,
    "within_hours", "auto", "max_correction_dv", "auto", "descent_shape", "auto").
}

// The block's extra results at their defaults, before it flies. Pure.
function land_extra {
  return lexicon("slope", -1, "touchdown_speed_actual", -1, "touchdown_h_speed", -1, "lean", -1, "reversals", 0,
    "distance_to_target", -1, "best_effort", false, "burn_start_ut", -1, "braking_began_ut", -1, "impact_ut", -1, "biome", "",
    "staged_before_braking", false, "stage_left", -1, "dropped_underhang", list(), "underhang_kept", list(), "underhang_drop_agl", -1,
    "passes_missed", 0, "next_pass_ut", -1, "descent_shape", -1, "descent_pe", -1, "predicted_dv", -1, "actual_dv", -1,
    "predicted_clearance", -1, "moved_for_terrain", false, "shape_range", "").
}

// "" or why an option's value isn't one the block can use.
function land_unreadable {
  parameter opt.
  local t is opt:target.
  local target_rule is "target must be " + char(34) + "any" + char(34) + ", LATLNG(lat, lng), lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y) or lexicon(" + char(34) + "biome" + char(34) + ", name), not ".
  if t:istype("String") {
    if t <> "any" {
      return target_rule + "'" + t + "'".
    }
  } else if land_is_biome(t) {
    local biome_problem is land_biome_unreadable(t).
    if biome_problem <> "" {
      return biome_problem.
    }
  } else if t:istype("Lexicon") {
    if not (t:haskey("lat") and t:haskey("lng")) {
      return target_rule + "a lexicon without lat and lng".
    }
    if not (t:lat:istype("Scalar") and t:lng:istype("Scalar")) {
      return target_rule + "a lexicon whose lat and lng aren't numbers".
    }
  } else if not t:istype("GeoCoordinates") {
    return target_rule + "a " + t:typename.
  }
  if not opt:daylight:istype("Boolean") {
    return "daylight must be true or false".
  }
  if not land_number_in(opt:max_slope, 0, 45) {
    return "max_slope must be a number of degrees from 0 to 45".
  }
  if not land_number_in(opt:max_divert, 0, 2000) {
    return "max_divert must be metres from 0 to 2000".
  }
  if not land_number_in(opt:max_overshoot, 0, 10000) {
    return "max_overshoot must be metres from 0 to 10000".
  }
  if not land_number_in(opt:touchdown_speed, 0.01, 50) {
    return "touchdown_speed must be a speed above 0 m/s".
  }
  if not land_number_in(opt:gear_alt, 0, 100000) {
    return "gear_alt must be metres, 0 or more".
  }
  if not land_number_in(opt:burn_margin, 1, 10) {
    return "burn_margin must be a number, 1 or more".
  }
  if not land_number_in(opt:underhang_agl, 0, 100000) {
    return "underhang_agl must be metres, 0 or more".
  }
  if not land_auto_or_in(opt:within_hours, 0.05, 48) {
    return "within_hours must be " + char(34) + "auto" + char(34) + " or hours from 0.05 to 48".
  }
  if not land_auto_or_in(opt:max_correction_dv, 0, 1000) {
    return "max_correction_dv must be " + char(34) + "auto" + char(34) + " or m/s from 0 to 1000".
  }
  if not land_auto_or_in(opt:descent_shape, 0.05, 10) {
    return "descent_shape must be " + char(34) + "auto" + char(34) + " or a Descent Shape from 0.05 to 10 (smaller is steeper)".
  }
  return "".
}

// True if x is "auto" or a number from low to high. Pure.
function land_auto_or_in {
  parameter x, low, high.
  if x:istype("String") {
    return x = "auto".
  }
  return land_number_in(x, low, high).
}

// "" or why a biome target (a lexicon with the key biome) isn't one the block can use: biome a name,
// and near, if given, a LATLNG or a lat/lng lexicon; no other keys.
function land_biome_unreadable {
  parameter t.
  local name_rule is "target's biome must be a biome's name, e.g. " + char(34) + "Midlands" + char(34).
  if not t:biome:istype("String") {
    return name_rule.
  }
  if t:biome:trim = "" {
    return name_rule.
  }
  for key in t:keys {
    if key <> "biome" and key <> "near" {
      return "a biome target takes only biome and near, not " + key.
    }
  }
  if t:haskey("near") {
    local near is t:near.
    local near_rule is "target's near must be LATLNG(lat, lng) or lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y) with numbers".
    if near:istype("Lexicon") {
      if not (near:haskey("lat") and near:haskey("lng")) {
        return near_rule.
      }
      if not (near:lat:istype("Scalar") and near:lng:istype("Scalar")) {
        return near_rule.
      }
    } else if not near:istype("GeoCoordinates") {
      return "target's near must be LATLNG(lat, lng) or a lat/lng lexicon, not a " + near:typename.
    }
  }
  return "".
}

// True if the target option is a biome target.
function land_is_biome {
  parameter target_opt.
  if not target_opt:istype("Lexicon") {
    return false.
  }
  return target_opt:haskey("biome").
}

// What land asks ADDONS:CLAUDIA:LANDINGSITE (find_landing_site's search) for a biome target: a
// site in the biome, in daylight if wanted, no steeper than max_slope, under the ground track in the
// window (within_hours, or for "auto" the one find_landing_site defaults to: no within_hours), at
// least the search's default margin inside the biome, nearest the near point if there is one, and
// with a pass in reach of max_correction_dv (reach, m/s). The search wants a slope over 0, so
// max_slope 0 asks for 0.1 degrees. Pure.
function land_site_query {
  parameter target_opt, daylight, max_slope, within_hours is "auto", reach is 30.
  local query is lexicon("biome", target_opt:biome, "daylight", daylight, "max_slope_deg", max(0.1, max_slope), "max_correction_dv", reach).
  if target_opt:haskey("near") {
    query:add("near", lexicon("latitude", target_opt:near:lat, "longitude", target_opt:near:lng)).
  }
  if not within_hours:istype("String") {
    query:add("within_hours", within_hours).
  }
  return query.
}

// The m/s land's correction burn onto the site may cost: max_correction_dv, or for "auto" the smaller of
// 30 and 5% of the craft's delta-v (lander_dv, m/s; 0 or less when stock reads none: 30), as
// LandingPass.DefaultMaxCorrectionDv works it out for find_landing_site. Pure.
function land_reach_dv {
  parameter max_correction_dv, lander_dv.
  if not max_correction_dv:istype("String") {
    return max_correction_dv.
  }
  if lander_dv <= 0 {
    return 30.
  }
  return min(30, 0.05 * lander_dv).
}

// True if two biome names are the same one, ignoring case and spaces ("northern basin" is stock's
// NorthernBasin), as the site search matches them. Pure.
function land_same_biome {
  parameter a, b.
  return a:replace(" ", "") = b:replace(" ", "").
}

// True if x is a number from low to high.
function land_number_in {
  parameter x, low, high.
  if not x:istype("Scalar") {
    return false.
  }
  return x >= low and x <= high.
}

// ---- Pure helpers: geometry and orbits ----

// A longitude in (-180, 180].
function land_wrap_lng {
  parameter lng.
  local wrapped is mod(mod(lng + 180, 360) + 360, 360) - 180.
  if wrapped = -180 {
    return 180.
  }
  return wrapped.
}

// Metres along the surface between two spots (degrees), on a body of the given radius.
function land_surface_distance {
  parameter lat1, lng1, lat2, lng2, radius.
  local a is sin((lat2 - lat1) / 2) ^ 2 + cos(lat1) * cos(lat2) * sin((lng2 - lng1) / 2) ^ 2.
  // Literal pi rather than CONSTANT:PI, so this stays pure.
  return 2 * radius * arcsin(min(1, sqrt(a))) * 3.141592653589793 / 180.
}

// Degrees clockwise from north at spot 1 of the way along the surface to spot 2 (degrees). Pure.
function land_bearing {
  parameter lat1, lng1, lat2, lng2.
  local dl is lng2 - lng1.
  return mod(arctan2(sin(dl) * cos(lat2), cos(lat1) * sin(lat2) - sin(lat1) * cos(lat2) * cos(dl)) + 360, 360).
}

// Where a site (site_lat, site_lng, degrees) lies from a craft at (lat, lng) heading hdg (degrees), along
// the surface of a body of the given radius (#431): lexicon("dist" (m, land_surface_distance), "along" (m
// along the great circle the heading sets out on; negative once behind), "across" (m off it; positive to
// the right)). Right anywhere on the body, the far side too. Pure.
function land_site_figures {
  parameter lat, lng, site_lat, site_lng, hdg, radius.
  local dist is land_surface_distance(lat, lng, site_lat, site_lng, radius).
  local m_per_deg is radius * 3.141592653589793 / 180.
  local d is dist / m_per_deg.
  local off is land_bearing(lat, lng, site_lat, site_lng) - hdg.
  return lexicon("dist", dist, "along", arctan2(sin(d) * cos(off), cos(d)) * m_per_deg,
    "across", arcsin(max(-1, min(1, sin(d) * sin(off)))) * m_per_deg).
}

// The longitude, in today's body-fixed frame, of the point a site at lng will have turned to in dt
// seconds: bodies turn east, so after dt the site is where the point east of it is now.
function land_site_lng {
  parameter lng, dt, rotation_period.
  return land_wrap_lng(lng + 360 * dt / rotation_period).
}

// The prograde delta-v (m/s, negative) at radius r0 (m), moving at speed (m/s) horizontally,
// that lowers periapsis to radius r_pe.
function land_deorbit_dv {
  parameter mu, r0, speed, r_pe.
  return sqrt(mu * (2 / r0 - 2 / (r0 + r_pe))) - speed.
}

// Seconds from the deorbit burn at radius r0 to periapsis at r_pe: half the transfer orbit.
function land_transfer_time {
  parameter mu, r0, r_pe.
  return 3.141592653589793 * sqrt(((r0 + r_pe) / 2) ^ 3 / mu).
}

// Speed (m/s) at periapsis r_pe of the transfer from r0.
function land_periapsis_speed {
  parameter mu, r0, r_pe.
  return sqrt(mu * (2 / r_pe - 2 / (r0 + r_pe))).
}

// ---- Pure helpers: the coast's end condition for the ground ----

// Seconds until a craft h m above the terrain, moving down at v_down (m/s, negative if climbing),
// reaches it under gravity g (m/s^2): the root of h = v_down t + g t^2 / 2. 0 at or below the ground.
function land_fall_eta {
  parameter h, v_down, g.
  if h <= 0 {
    return 0.
  }
  return (sqrt(v_down ^ 2 + 2 * g * h) - v_down) / g.
}

// Whether the coast must end and the descent start: the time to impact (t_impact, from land_fall_eta) is
// inside the time to stop the downward speed at accel (m/s^2), times margin, plus 30 s.
function land_brake_now {
  parameter t_impact, v_down, accel, margin.
  return land_brake_in(t_impact, v_down, accel, margin) < 0.
}

// Seconds until land_brake_now turns true: negative once it is.
function land_brake_in {
  parameter t_impact, v_down, accel, margin.
  return t_impact - (max(0, v_down) / accel * margin + 30).
}

// The physics warp rate (1 to 4) the fall may use: 4x, 2x from 60 s before braking is due, 1x from 30 s.
function land_warp_cap {
  parameter t_impact, v_down, accel, margin.
  local until_brake is land_brake_in(t_impact, v_down, accel, margin).
  if until_brake <= 30 {
    return 1.
  }
  if until_brake <= 60 {
    return 2.
  }
  return 4.
}

// Whether periapsis after the deorbit burn (m above sea level) is within 1500 m under the planned one.
function land_periapsis_ok {
  parameter actual, planned.
  return actual >= planned - 1500.
}

// ---- Pure helpers: descent guidance ----

// Metres needed to stop from speed (m/s) at accel (m/s^2), times margin.
function land_stop_distance {
  parameter speed, accel, margin.
  return speed ^ 2 / (2 * accel) * margin.
}

// Horizontal speed (m/s) to close on a site dist m away: the speed it can still stop from at
// accel / margin, eased to half the distance per second near the site. Following it costs
// accel / margin times v / (v + 2 accel / margin) of deceleration: never more than accel / margin,
// tapering to v / 2 at the site, with no step where a sudden demand could overshoot (#233).
function land_vh_target {
  parameter dist, accel, margin.
  local a_ok is accel / margin.
  return sqrt(2 * a_ok * max(0, dist) + (2 * a_ok) ^ 2) - 2 * a_ok.
}

// The Descent Shape's curve (#511, ADR 0011): the height (m) braking holds with v_h of v0 m/s of
// horizontal speed left, from h_start where braking began (v_h = v0) down to h_end once stopped:
// h_end + (h_start - h_end) (v_h / v0)^shape. A small shape holds its height until nearly stopped;
// a large one sinks early. Pure.
function land_shape_height {
  parameter v_h, v0, h_start, h_end, shape.
  local left is min(1, max(0, v_h / max(0.001, v0))).
  return h_end + (h_start - h_end) * left ^ shape.
}

// Where braking's Descent Shape ends, m above the terrain: the vertical terminal descent takes over from here. Pure.
function land_h_end {
  return 200.
}

// The height (m above the terrain) the descent holds to_go m from where it stops, at v_h of v0 m/s
// (land_shape_height down to land_h_end), then down once within 30 m of the site. Pure.
function land_shape_ref {
  parameter to_go, v_h, v0, h_start, shape.
  if to_go < 30 {
    return 0.
  }
  return land_shape_height(v_h, v0, max(land_h_end(), h_start), land_h_end(), shape).
}

// The clearance (m) the predicted braking path must keep over the terrain at every point (#511):
// 300 m, or 2 s of the descent rate v_down (m/s) where that is more. Pure.
function land_clear_margin {
  parameter v_down.
  return max(300, 2 * max(0, v_down)).
}

// Distances (m) along the ground track at which the descent reads the terrain ahead: where the craft
// will be in 0.5 s to a minute at v_h m/s, every 3 s past the first few, and never past to_go (m to
// where it stops), since the ground beyond doesn't matter (#365). Pure.
function land_lookahead {
  parameter v_h, to_go.
  local out is list().
  for t in list(0.5, 1, 2) {
    out:add(min(max(0, to_go), v_h * t)).
  }
  for i in range(1, 21) {
    out:add(min(max(0, to_go), v_h * 3 * i)).
  }
  return out.
}

// The height (m) the descent steers by: the least of radar (m above the terrain under the craft)
// and its height above each of heights (m above sea level, from land_lookahead's points) at alt_now
// (m above sea level), so it holds its height above the highest ground ahead, not only below (#365). Pure.
function land_clearance {
  parameter radar, alt_now, heights.
  local h is radar.
  for each in heights {
    set h to min(h, alt_now - each).
  }
  return h.
}

// Vertical speed (m/s, up positive) at h m above the terrain, aiming for h_ref: climbs back (up to
// 20 m/s) when below it, and descends at a tenth of the height to lose per second when above,
// never slower than touchdown speed and never faster than it can stop from at a_stop (m/s^2), nor
// than 100 m/s, about a free fall from the descent's planned 3 km (#365).
function land_vv_target {
  parameter h, h_ref, a_stop, touchdown.
  if h < h_ref {
    return min(20, 0.1 * (h_ref - h)).
  }
  local fastest is max(touchdown, min(100, sqrt(2 * a_stop * max(0, h)))).
  return -min(fastest, max(touchdown, 0.1 * (h - h_ref))).
}

// The most the thrust may lean from vertical (degrees) at h m above the ground: upright for the
// last few metres, free from about 40 m up.
function land_max_tilt {
  parameter h.
  return min(90, 5 + 2 * max(0, h)).
}

// Splits max_accel between the vertical acceleration wanted (first, 0 to max_accel) and the
// horizontal (what is left). Returns lexicon("v", m/s^2, "h", m/s^2).
function land_split_accel {
  parameter max_accel, vert, horiz.
  local a_v is min(max_accel, max(0, vert)).
  local a_h is min(max(0, horiz), sqrt(max(0, max_accel ^ 2 - a_v ^ 2))).
  return lexicon("v", a_v, "h", a_h).
}

// Acceleration (m/s^2, positive ahead) along one horizontal axis onto a site s m ahead, moving
// v_ahead m/s (positive ahead) with a_max m/s^2 to spend. It holds land_vh_target's speed: the
// deceleration that speed needs as the distance closes (its slope times the speed), plus a pull onto
// it over 2 s; faster than that, at least the deceleration that stops it on the site. On or past the
// site it only stops, so it lands long rather than turning back. Never over v_top m/s: braking far
// out, it holds its speed rather than speeding up to the curve's (#365). All capped at a_max.
function land_axis_accel {
  parameter s, v_ahead, a_max, margin, v_top is 1e9.
  // Stopping past the site: over 4 s, slow enough that a turn lagging a second or two behind
  // doesn't carry it on through zero.
  local a is -v_ahead / 4.
  if s > 0 {
    local v_want is land_vh_target(s, a_max, margin).
    local a_ok is a_max / margin.
    // d(v_want)/ds, 0 where v_top holds it.
    local slope is a_ok / max(0.001, v_want + 2 * a_ok).
    if v_want > v_top {
      set v_want to v_top.
      set slope to 0.
    }
    set a to (v_want - v_ahead) / 2 - slope * max(0, v_ahead).
    if v_ahead > v_want {
      set a to min(a, -v_ahead ^ 2 / (2 * s)).
    }
  }
  return max(-a_max, min(a_max, a)).
}

// The braking (m/s^2) land_guide asks for while climbing faster than it wants at v_al m/s along the axis, s m short of the
// site (#530): enough to get under the speed at which gravity g (m/s^2) holds it down radius m from the body's centre, over
// 2 s, or to stop on the site, whichever is more; never more than braking_needs, the stopping curve's rate. At the stopping
// curve's rate however far out, it slowed from 192 to 50 m/s 22 km short of Minmus Flats. Pure.
function land_climb_braking {
  parameter s, v_al, g, radius, braking_needs.
  local held is sqrt(max(0, g * radius)).
  return min(braking_needs, max(v_al ^ 2 / (2 * max(1, s)), (v_al - held) / 2)).
}

// The fastest braking may move along its axis (m/s), from v_top, the last tick's, and v_h, the speed across the ground now
// (#530): never speeding back up past the slowest it has flown since braking began, but always 20 m/s, enough to close on a
// site moved up to max_divert. Holding the starting speed, the approach pushed back up towards it under 50 m/s and the cone
// turned the push up and back over it: 430 s flip-flopping at 50 m/s, climbing 2.4 km. Pure.
function land_v_top {
  parameter v_top, v_h.
  return min(v_top, max(20, v_h)).
}

// land_axis_accel across the braking axis, where the site may be on either side: c m to it (either
// sign), moving v_across m/s the same way.
function land_cross_accel {
  parameter c, v_across, a_max, margin.
  if c < 0 {
    return -land_axis_accel(-c, -v_across, a_max, margin).
  }
  return land_axis_accel(c, v_across, a_max, margin).
}

// One tick of the powered descent's guidance in the braking frame: the axis along the horizontal
// velocity when braking began (carried round the body), and across it. st holds s (m to the site along the axis, negative once
// past it), c (m to it across), v_al and v_cr (m/s along and across), v_up (m/s), h (m above the
// terrain), h_start (h when braking began), v0 (m/s across the ground then), shape (the Descent Shape), v_top (the most m/s
// along the axis), a_max (m/s^2 the engines give), g (m/s^2) and radius (m from the body's centre). It holds the Descent
// Shape's height (land_shape_ref) down to where it stops, the site or its stopping distance on, so it comes straight down
// once it has stopped; gives the vertical acceleration first, brakes along the axis with what is left (land_axis_accel) and
// closes across it with the rest, upright near the ground. Climbing faster than it wants while still fast, it brakes along at
// least land_climb_braking's rate (#511, #530): only slowing brings an orbiting craft down, and holding its speed there left it
// coasting up out of periapsis at zero throttle. Returns lexicon("al", "cr", "up"): m/s^2 wanted, with, for the trace,
// "h_ref" (m), "vh" and "vv" (m/s) it aims for and "to_go" (m to where it stops).
function land_guide {
  parameter st, touchdown, margin.
  local v_h is sqrt(st:v_al ^ 2 + st:v_cr ^ 2).
  local g_eff is st:g - v_h ^ 2 / st:radius.
  local a_most is 0.95 * st:a_max.
  local a_h_most is sqrt(max(0.01, a_most ^ 2 - max(0, g_eff) ^ 2)).
  local to_go is max(max(0, st:s), max(abs(st:c), land_stop_distance(v_h, a_h_most, margin))).
  // It falls no faster than half the thrust braking leaves (land_vh_target's deceleration at this
  // speed) can stop, so stopping the fall never takes the thrust the braking needs.
  local a_ok is a_h_most / margin.
  local braking_needs is a_ok * v_h / (v_h + 2 * a_ok).
  local a_stop is max(0.2, 0.5 * (sqrt(max(0, a_most ^ 2 - braking_needs ^ 2)) - st:g)).
  local h_ref is land_shape_ref(to_go, v_h, st:v0, st:h_start, st:shape).
  local vv_want is land_vv_target(st:h, h_ref, a_stop, touchdown).
  local split is land_split_accel(a_most, g_eff + 0.5 * (vv_want - st:v_up), a_most).
  local al is land_axis_accel(st:s, st:v_al, split:h, margin, st:v_top).
  if v_h > 50 and st:v_up > 0 and vv_want < st:v_up and st:v_al > 0 {
    set al to min(al, -min(split:h, land_climb_braking(st:s, st:v_al, st:g, st:radius, braking_needs))).
  }
  local cr is land_cross_accel(st:c, st:v_cr, sqrt(max(0, split:h ^ 2 - al ^ 2)), margin).
  local tilt is land_max_tilt(st:h).
  if tilt < 90 {
    local most is split:v * tan(tilt).
    local sideways is sqrt(al ^ 2 + cr ^ 2).
    if sideways > most {
      set al to al * most / sideways.
      set cr to cr * most / sideways.
    }
  }
  return lexicon("al", al, "cr", cr, "up", split:v, "h_ref", h_ref, "vv", vv_want,
    "vh", min(st:v_top, land_vh_target(st:s, split:h, margin)), "to_go", to_go).
}

// The descent's phase, for its trace: "fall" before braking, "braking" while over 50 m/s across the
// ground, "approach" closing in, "terminal" once coming straight down (h_ref, its height to hold, 0). Pure.
function land_phase {
  parameter braking, v_h, h_ref.
  if not braking {
    return "fall".
  }
  if h_ref <= 0 {
    return "terminal".
  }
  if v_h > 50 {
    return "braking".
  }
  return "approach".
}

// Whether the descent means its engines to thrust in this phase: every phase but the fall before
// braking (so a stage that runs dry while braking stages even though the throttle falls to 0). Pure.
function land_burning {
  parameter phase.
  return phase <> "fall".
}

// Whether the descent traces a line now: at a phase change, or 2 s (ut) after the last line. Pure.
function land_trace_due {
  parameter phase, last_phase, ut, last_ut.
  return phase <> last_phase or ut >= last_ut + 2.
}

// Whether a craft moving v_ahead m/s along the braking axis has turned back: under -1 m/s (jitter
// below that is ignored), and until it is moving ahead again (0 or more); was: the last answer.
function land_is_reversed {
  parameter v_ahead, was.
  if v_ahead < -1 {
    return true.
  }
  if v_ahead >= 0 {
    return false.
  }
  return was.
}

// Whether the descent must turn back for the site: stopped (horizontal speed under 1 m/s) more than
// max_overshoot m past it (s, m to the site along the braking axis, is under -max_overshoot).
function land_turn_back {
  parameter s, speed, max_overshoot.
  return s < -max_overshoot and speed < 1.
}

// Why braking that raises periapsis must stop (#431), or "": periapsis pe (m; negative below sea level)
// 1000 m or more over its value when braking began (pe0). Periapsis, not speed: free fall gains speed
// but never raises periapsis; only thrust along the track does (#434). Pure.
function land_brake_guard {
  parameter pe0, pe.
  if pe - pe0 >= 1000 {
    return "braking raised periapsis: " + round(pe / 1000, 1) + " km, " + round(pe0 / 1000, 1) + " when braking began".
  }
  return "".
}

// The predicted braking path (#511; ADDONS:CLAUDIA:BRAKEPATH's path: lexicons of s, m to the site along the track,
// falling as it flies, alt, m above sea level, and ground, the terrain's height there) at s m to the site:
// lexicon("alt", "ground"), between the points either side, the end points beyond them; "" for no path. Pure.
function land_path_at {
  parameter predicted, s.
  if predicted:length = 0 {
    return "".
  }
  local first is predicted[0].
  if s >= first:s {
    return lexicon("alt", first:alt, "ground", first:ground).
  }
  for i in range(1, predicted:length) {
    local here is predicted[i].
    if s >= here:s {
      local before is predicted[i - 1].
      local k is (s - here:s) / max(0.001, before:s - here:s).
      return lexicon("alt", here:alt + (before:alt - here:alt) * k, "ground", here:ground + (before:ground - here:ground) * k).
    }
  }
  local last is predicted[predicted:length - 1].
  return lexicon("alt", last:alt, "ground", last:ground).
}

// The highest ground (m above sea level) on the rest of the predicted path from s m to the site on (the
// points under s), for the descent to steer by beyond the look-ahead in view (#511); -1e9 for none. Pure.
function land_path_ground_ahead {
  parameter predicted, s.
  local most is -1e9.
  for point in predicted {
    if point:s < s {
      set most to max(most, point:ground).
    }
  }
  return most.
}

// Why braking that climbs off its predicted path must stop (#511), or "": altitude alt_now (m) more than
// max(1 km, 25% of the predicted height over the ground) above the predicted altitude at this point along
// the track (at, land_path_at's). Altitude against the prediction, not clearance against its start: a
// basin after a ridge, or a rim the path climbs over, doesn't trip it; a real climb off the path does. Pure.
function land_path_guard {
  parameter alt_now, at.
  if at:istype("String") {
    return "".
  }
  local over is alt_now - at:alt.
  if over > max(1000, 0.25 * max(0, at:alt - at:ground)) {
    return "braking climbed off its predicted path: " + round(over / 1000, 1) + " km over it".
  }
  return "".
}

// The steering for a demanded acceleration (#511), in the braking frame (lexicons of al, cr, up: the demand
// want, m/s^2; the last direction last; the velocity vel, m/s): the last direction while the demand is
// under 5% of a_max (m/s^2), so a tiny demand flipping each tick doesn't swing the craft; otherwise the
// demand, held within 45 degrees of surface retrograde while braking (over 50 m/s across the ground).
// Returns lexicon("al", "cr", "up"), a unit vector, with "mag": the m/s^2 to thrust along it, the demand's, or where the
// cone turned it, the demand's part along the turned direction (none for a demand ahead, which the cone would turn up and
// back, #530), or enough to keep the demand's upward part (a climb over a rim isn't starved), whichever is more. Pure.
function land_steer {
  parameter want, last, vel, a_max.
  local mag is sqrt(want:al ^ 2 + want:cr ^ 2 + want:up ^ 2).
  if mag < 0.05 * a_max {
    return lexicon("al", last:al, "cr", last:cr, "up", last:up, "mag", mag).
  }
  local d is lexicon("al", want:al / mag, "cr", want:cr / mag, "up", want:up / mag, "mag", mag).
  local speed is sqrt(vel:al ^ 2 + vel:cr ^ 2 + vel:up ^ 2).
  if sqrt(vel:al ^ 2 + vel:cr ^ 2) <= 50 {
    return d.
  }
  local back is lexicon("al", -vel:al / speed, "cr", -vel:cr / speed, "up", -vel:up / speed).
  local c is d:al * back:al + d:cr * back:cr + d:up * back:up.
  if c >= cos(45) {
    return d.
  }
  // The part of the demand square to retrograde, laid 45 degrees off it.
  local side is lexicon("al", d:al - c * back:al, "cr", d:cr - c * back:cr, "up", d:up - c * back:up).
  local side_mag is sqrt(side:al ^ 2 + side:cr ^ 2 + side:up ^ 2).
  local turned is lexicon("al", back:al, "cr", back:cr, "up", back:up, "mag", mag).
  if side_mag >= 0.000001 {
    local k is sin(45) / side_mag.
    set turned to lexicon("al", cos(45) * back:al + k * side:al, "cr", cos(45) * back:cr + k * side:cr, "up", cos(45) * back:up + k * side:up, "mag", mag).
  }
  set turned["mag"] to max(0, want:al * turned:al + want:cr * turned:cr + want:up * turned:up).
  if turned:up > 0.01 {
    set turned["mag"] to max(turned:mag, want:up / turned:up).
  }
  return turned.
}

// What land does once it predicts its braking again from the real state as braking begins (#511): "fly" the
// planned shape (planned_clears), "switch" to the cheapest that clears (best_shape, -1 for none), or, with
// none clearing, "hold": don't brake, keeping an orbit whose periapsis is above the terrain on the track
// (pe_clear), or brake on the steepest shape anyway ("steepest") when it isn't, rather than fall into it. Pure.
function land_repredict_verdict {
  parameter planned_clears, best_shape, pe_clear.
  if planned_clears {
    return "fly".
  }
  if best_shape > 0 {
    return "switch".
  }
  if pe_clear {
    return "hold".
  }
  return "steepest".
}

// After braking aborts (#511): "circularize" back to a near-circular orbit only when the abort leaves
// periapsis under the terrain (pe_under), the delta-v left (dv_left, m/s; 0 or less for unknown) covers the
// burn (need, m/s) and another pass is in the window (next_pass); otherwise "leave" the orbit as it is. Pure.
function land_recover {
  parameter pe_under, dv_left, need, next_pass.
  if pe_under and dv_left > need and next_pass {
    return "circularize".
  }
  return "leave".
}

// The burn (m/s) that makes a craft at radius radius_at (m) moving v_h m/s horizontally and v_up m/s vertically (orbital
// frame) circular there under mu: the horizontal speed up to the circular one, the vertical speed to 0. Pure.
function land_circular_dv {
  parameter mu, radius_at, v_h, v_up.
  return sqrt((sqrt(mu / radius_at) - v_h) ^ 2 + v_up ^ 2).
}

// ---- Pure helpers: staging off a short stage before the fall (#382) ----

// Whether the current stage, with stage_dv m/s (vacuum) left, can't cover the braking need (m/s, as land's
// delta-v check works it out) times margin, so it should be staged off before the fall. Pure.
function land_stage_short {
  parameter stage_dv, brake_need, margin.
  return stage_dv < brake_need * margin.
}

// "" if the stages below the current one can be fired early, else why not: "stage_limit" (the next stage
// is at or below it), "unsafe" (a stage on the way would fire parachutes or separate a heat shield) or
// "no engines" (the run of stages never reaches engines). engines_below / unsafe_below as for
// claudia_stage_chain, from the next stage to fire downwards. Pure.
function land_stage_early_block {
  parameter stage_number, stage_limit, engines_below, unsafe_below.
  if claudia_stage_chain(stage_number, stage_limit, false, false, engines_below, unsafe_below) {
    return "".
  }
  local step is 0.
  until step >= engines_below:length {
    if stage_number - step <= stage_limit or stage_number - step <= 0 {
      return "stage_limit".
    }
    if unsafe_below[step] {
      return "unsafe".
    }
    set step to step + 1.
  }
  // No stage below to fire: the current one is at stage_limit.
  if engines_below:length = 0 {
    return "stage_limit".
  }
  return "no engines".
}

// ---- Pure helpers: the Underhang (#427) ----

// The indices, into lows, of the parts that are Underhang: those whose lowest point (lows, m along the
// craft's down axis) reaches more than margin m past the landing legs' lowest (the most of leg_lows,
// the legs' lowest points). No legs (leg_lows empty): none. Pure.
function land_underhang {
  parameter lows, leg_lows, margin.
  local out is list().
  if leg_lows:length = 0 {
    return out.
  }
  local foot is leg_lows[0].
  for each in leg_lows {
    set foot to max(foot, each).
  }
  for i in range(lows:length) {
    if lows[i] > foot + margin {
      out:add(i).
    }
  }
  return out.
}

// Delta-v (m/s) the lander alone needs to fly the last h m (underhang_agl) under g (m/s^2): the speed of a
// free fall from h, plus 2 s of gravity (the thrust gap at the drop) and 10 s of hover, times 1.2. Pure.
function land_lander_need {
  parameter h, g.
  return (sqrt(2 * g * max(0, h)) + 12 * g) * 1.2.
}

// Delta-v (m/s) of isp (s, vacuum) from mass m_wet down to m_dry (t); 0 if it can't burn. Pure.
function land_rocket_dv {
  parameter isp, m_wet, m_dry.
  if isp <= 0 or m_dry <= 0 or m_wet <= m_dry {
    return 0.
  }
  return isp * 9.80665 * ln(m_wet / m_dry).
}

// True if one of the lander's engines can fire: lit already (ignited, parallel to stages), or in a stage
// (stages: the stage each activates in) at or above stage_limit. Pure.
function land_lander_engine_ok {
  parameter stages, ignited, stage_limit.
  for i in range(stages:length) {
    if ignited[i] or stages[i] >= stage_limit {
      return true.
    }
  }
  return false.
}

// Why land won't fly with a droppable Underhang (names, the Underhang's parts), or "": no lander engine
// within stage_limit once it is gone, or the lander alone (lander_dv m/s) short of need (m/s,
// land_lander_need) for the last agl m. Pure.
function land_underhang_unflyable {
  parameter names, lander_dv, need, engine_ok, agl, stage_limit.
  local what is "Underhang (" + names:join(", ") + ")".
  local figures is "the lander alone has about " + round(lander_dv) + " m/s, " + round(need) + " m/s needed to fly the last " + round(agl) + " m once it is dropped".
  if not engine_ok {
    return what + ": no lander engine at or above stage_limit " + stage_limit + " once it is dropped (" + figures + ")".
  }
  if lander_dv < need {
    return what + ": " + figures.
  }
  return "".
}

// Whether the Underhang drops now: the radar altitude (m) under underhang_agl, or its engines ran dry
// while braking (ran_dry), whichever comes first. Pure.
function land_drop_due {
  parameter radar, underhang_agl, ran_dry.
  return ran_dry or radar < underhang_agl.
}

// Whether the wait after firing the Underhang's decouplers is over: what left is gap m (-1 until any
// has) clear of the craft's lowest point, 2 m or more, or waited s reached 1. Pure.
function land_drop_waited {
  parameter gap, waited.
  return gap >= 2 or waited >= 1.
}

// Metres (sideways) a dropped part falling from h m up, v_down m/s (down positive) and v_h m/s across,
// travels before it hits under gravity g: flat ground, no air. Pure.
function land_fall_reach {
  parameter h, v_down, v_h, g.
  return v_h * land_fall_eta(h, v_down, g).
}

// True if a touchdown point dist m from where the Underhang hits is clear of it: 50 m or more. Pure.
function land_clear_of {
  parameter dist.
  return dist >= 50.
}

// Metres to move a touchdown point dist m from where the Underhang hits, away from it, to be clear
// (land_clear_of): 0 if it is, never over max_divert. Pure.
function land_clear_shift {
  parameter dist, max_divert.
  if land_clear_of(dist) {
    return 0.
  }
  return min(max_divert, 50 - dist).
}

// ---- Pure helpers: when braking starts, for a craft that turns slowly (#380) ----

// The horizontal deceleration (m/s^2) braking plans on: 95% of a_max (m/s^2) with g (m/s^2) held up.
function land_brake_accel {
  parameter a_max, g.
  return sqrt(max(0.01, (0.95 * a_max) ^ 2 - g ^ 2)).
}

// Seconds to turn angle degrees from rest to rest under TURNTIME's model of the craft: full torque up
// to top_rate (deg/s), then at it, then down, as its 190-degree turn takes turn_s (s); the torque is
// worked back from those two. The last 5 degrees count as pointed (the throttle, scaled by the
// cosine of the angle off, is over 99% there). 0 with no turn rate (no TURNTIME). Pure.
function land_turn_seconds {
  parameter angle, top_rate, turn_s.
  local left is max(0, angle - 5).
  if top_rate <= 0 or left <= 0 {
    return 0.
  }
  // The seconds to reach top_rate (top_rate / the angular acceleration): turn_s less the 190 degrees
  // at top_rate, whether the 190-degree turn reaches the cap or peaks at top_rate.
  local ramp is max(0.001, turn_s - 190 / top_rate).
  local alpha is top_rate / ramp.
  if sqrt(alpha * left) <= top_rate {
    return 2 * sqrt(left / alpha).
  }
  return left / top_rate + ramp.
}

// Metres from the site at which braking starts at v_h m/s: where land_vh_target(dist, a_brake,
// margin) falls to v_h, plus v_h times turn_left (s still to turn onto the braking direction,
// land_turn_seconds), the way it travels while turning. Pure.
function land_brake_distance {
  parameter v_h, a_brake, margin, turn_left.
  local a_ok is a_brake / margin.
  return v_h ^ 2 / (2 * a_ok) + 2 * v_h + v_h * max(0, turn_left).
}

// Whether braking starts now: within land_brake_distance of the site (dist m away along the surface;
// ahead, m of it along the horizontal velocity's track (land_site_figures), is negative once it has
// fallen behind), where the predicted braking path starts (#511), or, as a safety net only, the ground
// coming up first: h (m, land_clearance) under the path's margin at v_down (m/s, land_clear_margin).
// A low pass over a rim far out no longer starts braking at orbital speed, with nothing to brake for. Pure.
function land_brake_due {
  parameter dist, ahead, v_h, v_down, h, a_brake, margin, turn_left.
  return dist <= land_brake_distance(v_h, a_brake, margin, turn_left) or ahead < 0 or h < land_clear_margin(v_down).
}

// The site distance (m along the braking axis) the guidance flies by while the craft is still
// turning onto its first braking command: s less what it travels at v_h (m/s) in the turn_left
// (s) still to go, so the braking it starts early is braking land_guide asks for. At least 1 m;
// s itself once past the site. Pure.
function land_turning_s {
  parameter s, v_h, turn_left.
  if s <= 0 {
    return s.
  }
  return max(min(s, 1), s - v_h * max(0, turn_left)).
}

// What land_guide will first ask for once braking begins, for the fall to steer by (#380). st is
// land_guide's without h_start and v_top, along the horizontal velocity; the site is taken no
// farther along than land_brake_distance, where braking starts. There the stopping curve is at the
// speed braking holds to (v_top), so land_guide asks only for the curve's deceleration once it
// falls under it, a moment on; this asks for it as braking begins, with no v_top. Pure.
function land_fall_guide {
  parameter st, touchdown, margin.
  local v_h is sqrt(st:v_al ^ 2 + st:v_cr ^ 2).
  local first is st:copy.
  set first["s"] to min(st:s, land_brake_distance(v_h, land_brake_accel(st:a_max, st:g), margin, 0)).
  set first["h_start"] to max(land_h_end(), st:h).
  // At v0 the Descent Shape's curve is at h_start whatever the shape.
  set first["v0"] to v_h.
  set first["shape"] to 1.
  set first["v_top"] to 1e9.
  return land_guide(first, touchdown, margin).
}

// When the rails coast ends: the turn (turn_time s, TURNTIME's) plus 30 s before braking is
// estimated to start (brake_ut), and never under 90 s before it. Pure.
function land_coast_end {
  parameter brake_ut, turn_time.
  return brake_ut - max(90, turn_time + 30).
}

// The UT braking will start by, from samples of the coming fall (oldest first, each a lexicon of
// ut, dist and ahead (m to the site along the surface, and along the horizontal velocity's track:
// land_site_figures' dist and along), v_h, v_down (m/s), alt and
// ground (m above sea level: the craft, and the terrain under it)): the sample before the first at
// which land_brake_due fires, with h the clearance over the ground under the next 60 s of samples.
// Never later than braking starts, however high the terrain on the way down. -1 if none fires. Pure.
function land_brake_estimate {
  parameter samples, a_brake, margin.
  for i in range(samples:length) {
    local p is samples[i].
    local heights is list().
    local j is i + 1.
    until j >= samples:length {
      if samples[j]:ut > p:ut + 60 {
        break.
      }
      heights:add(samples[j]:ground).
      set j to j + 1.
    }
    local h is land_clearance(p:alt - p:ground, p:alt, heights).
    if land_brake_due(p:dist, p:ahead, p:v_h, p:v_down, h, a_brake, margin, 0) {
      return samples[max(0, i - 1)]:ut.
    }
  }
  return -1.
}

// When the unbraked fall meets the ground, from land_brake_estimate's samples: the UT between the
// last sample above the terrain under it and the first at or below it, by their clearance; -1 if
// none reaches the ground. Allows for high terrain on the way down, not just the site's height. Pure.
function land_impact_ut {
  parameter samples.
  for i in range(samples:length) {
    local p is samples[i].
    local over is p:alt - p:ground.
    if over <= 0 {
      if i = 0 {
        return p:ut.
      }
      local before is samples[i - 1].
      local was is before:alt - before:ground.
      return before:ut + (p:ut - before:ut) * was / (was - over).
    }
  }
  return -1.
}

// ---- Planning (reads the ship and the clock) ----

// Samples of the fall on this orbit, every step s from from_ut to until_ut or the ground under it, for
// land_brake_estimate: where the site and the terrain under the craft will be then, as the body turns,
// the site measured along the surface (land_site_figures) in today's frame, as the craft's spot is.
function land_fall_samples {
  parameter site, from_ut, until_ut, step.
  local out is list().
  local t is from_ut.
  until t > until_ut {
    local pos is positionat(ship, t) - body:position.
    local up_v is pos:normalized.
    local vel is velocityat(ship, t):surface.
    local v_h is vxcl(up_v, vel).
    local under is body:geopositionof(body:position + pos).
    local site_lng is land_site_lng(site:lng, t - time:seconds, body:rotationperiod).
    local hdg is land_bearing(under:lat, under:lng, site:lat, site_lng).
    if v_h:mag >= 1 {
      local on is body:geopositionof(body:position + pos + v_h:normalized * 1000).
      set hdg to land_bearing(under:lat, under:lng, on:lat, on:lng).
    }
    local fig is land_site_figures(under:lat, under:lng, site:lat, site_lng, hdg, body:radius).
    local ground is body:geopositionlatlng(under:lat, land_site_lng(under:lng, -(t - time:seconds), body:rotationperiod)):terrainheight.
    local alt_then is pos:mag - body:radius.
    out:add(lexicon("ut", t, "dist", fig:dist, "ahead", fig:along, "v_h", v_h:mag, "v_down", -vdot(vel, up_v),
      "alt", alt_then, "ground", ground)).
    if alt_then <= ground {
      break.
    }
    set t to t + step.
  }
  return out.
}

// The site's lat and lng from the target option, or "" for "any".
function land_site_of {
  parameter target_opt.
  if target_opt:istype("String") {
    return "".
  }
  return lexicon("lat", target_opt:lat, "lng", target_opt:lng).
}

// Degrees the sun stands above the horizon at a point (a vector from the body's centre) at ut.
function land_sun_elevation {
  parameter point, ut.
  local sun_dir is positionat(body("Sun"), ut) - positionat(body, ut).
  return 90 - vang(point, sun_dir).
}

// Where the site (lat, lng) will be at ut, as a vector from the body's centre in today's frame.
function land_site_vector {
  parameter site, ut.
  local spot is body:geopositionlatlng(site:lat, land_site_lng(site:lng, ut - time:seconds, body:rotationperiod)).
  return spot:position - body:position.
}

// The periapsis radius to aim for: approach m above the site's terrain; -1 if the orbit (semi-major
// axis sma) isn't at least 1 km above that.
function land_pe_radius {
  parameter site, approach, sma.
  local r_pe is body:radius + max(0, body:geopositionlatlng(site:lat, site:lng):terrainheight) + approach.
  if r_pe > sma - 1000 {
    return -1.
  }
  return r_pe.
}

// The failure when the orbit is too low to start the descent from over the site.
function land_too_low {
  parameter site, approach.
  local lowest is max(0, body:geopositionlatlng(site:lat, site:lng):terrainheight) + approach + 1000.
  return lexicon("error", "orbit too low to land from: it must be at least " + round(lowest) + " m up, 1 km over the descent's start").
}

// Daylight is the sun more than this many degrees up at the touchdown (GLOSSARY.md); the same as
// LandingSiteSearch.MinSunElevationDeg in find_landing_site. Pure.
function land_daylight_sun_deg {
  return 20.
}

// True if the sun sun_deg up is Daylight: more than land_daylight_sun_deg up. Pure.
function land_is_daylight {
  parameter sun_deg.
  return sun_deg > land_daylight_sun_deg().
}

// ---- The pass search over a site (#510): ADDONS:CLAUDIA:LANDINGPASS, the one find_landing_site keeps its sites by ----

// What land asks ADDONS:CLAUDIA:LANDINGPASS for: the first pass over site (lat, lng) whose deorbit burn is
// at earliest (UT) or later, for a descent to periapsis r_pe (m from the body's centre), in daylight if
// wanted, whose correction burn costs at most reach (m/s, land_reach_dv), in the window (within_hours, or
// for "auto" the one find_landing_site defaults to: no within_hours). Pure.
function land_pass_query {
  parameter site, r_pe, earliest, daylight, reach, within_hours, craft is lexicon(), shape is "auto", may_move is false.
  local query is lexicon("lat", site:lat, "lng", site:lng, "r_pe", r_pe, "earliest_ut", earliest, "daylight", daylight, "max_correction_dv", reach).
  if not within_hours:istype("String") {
    query:add("within_hours", within_hours).
  }
  // #511: the craft that brakes (land_craft's), so the search plans each pass's descent over its terrain, the Descent Shape
  // asked for, and whether a site given as coordinates may move (may_move) up to 5 km for terrain.
  for key in craft:keys {
    query:add(key, craft[key]).
  }
  query:add("shape", shape).
  query:add("move_site", may_move).
  return query.
}

// The delta-v land checks for before the deorbit burn, and stages for before the fall (#382, #511): the simulated figure
// (predicted, m/s: the landing's, or its braking's) times margin (burn_margin); without a simulation (0 or less), the estimate (old, m/s). Pure.
function land_dv_needed {
  parameter predicted, margin, old.
  if predicted > 0 {
    return predicted * margin.
  }
  return old.
}

// The shapes that clear, for a refused descent_shape's report: "lo to hi", or "" for none known. Pure.
function land_shape_range {
  parameter lo, hi.
  if lo <= 0 {
    return "".
  }
  return round(lo, 3) + " to " + round(hi, 3).
}

// The plan from LANDINGPASS's answer (found) for site, descending to r_pe: lexicon("error" ("" or why
// there is no pass), "burn_ut", "arrive_ut" (-1 with no pass), "site", "r_pe", "next_pass_ut" (the
// touchdown of the pass after it; -1 for none)). Traces each pass the search looked at (its burn and touchdown UTs, km off the
// track, the correction it needs, the sun, and why it was rejected, "chosen" or "next") and the plan (#383).
function land_pass_plan {
  parameter found, site, r_pe.
  if found:haskey("passes") {
    for p in found:passes {
      addons:claudia:blocktrace("land", lexicon("phase", "pass", "burn_ut", p:burn_ut, "arrive_ut", p:arrive_ut, "cross_km", p:cross_km,
        "correction_dv", p:correction_dv, "sun", p:sun, "landing_dv", land_or(p, "landing_dv", -1), "verdict", p:verdict)).
    }
  }
  local shape_range is land_shape_range(land_or(found, "shape_lo", -1), land_or(found, "shape_hi", -1)).
  if found:error <> "" {
    addons:claudia:blocktrace("land", lexicon("phase", "plan", "error", found:error)).
    return lexicon("error", found:error, "burn_ut", -1, "arrive_ut", -1, "next_pass_ut", -1, "shape_range", shape_range).
  }
  // #511: the descent planned over the pass's terrain, with its own periapsis, and the site, moved for terrain if it had to be.
  if land_or(found, "descent_shape", -1) > 0 {
    set site to lexicon("lat", found:lat, "lng", found:lng).
    set r_pe to found:r_pe.
  }
  local plan is lexicon("error", "", "burn_ut", found:burn_ut, "arrive_ut", found:arrive_ut, "site", site, "r_pe", r_pe, "next_pass_ut", found:next_pass_ut,
    "descent_shape", land_or(found, "descent_shape", -1), "predicted_dv", land_or(found, "predicted_dv", -1), "brake_dv", land_or(found, "brake_dv", -1),
    "predicted_clearance", land_or(found, "predicted_clearance", -1), "moved", land_or(found, "moved_for_terrain", false),
    "path", land_or(found, "path", list()), "shape_range", shape_range).
  addons:claudia:blocktrace("land", lexicon("phase", "plan", "burn_ut", round(found:burn_ut, 1), "arrive_ut", round(found:arrive_ut, 1),
    "lat", round(site:lat, 3), "lng", round(site:lng, 3), "correction_dv", round(found:correction_dv, 1), "window_h", round(found:window_hours, 1),
    "descent_shape", plan:descent_shape, "pe", round(r_pe - body:radius), "predicted_dv", round(plan:predicted_dv), "brake_dv", round(plan:brake_dv),
    "clearance", round(plan:predicted_clearance), "moved", plan:moved)).
  return plan.
}

// source[key], or fallback when source has no such key. Pure.
function land_or {
  parameter source, key, fallback.
  if source:haskey(key) {
    return source[key].
  }
  return fallback.
}

// When land solves its pass again before the deorbit burn at burn_ut (#510): 600 s before it, leaving
// exec_node time to turn and warp the rest. Pure.
function land_resolve_ut {
  parameter burn_ut.
  return burn_ut - 600.
}

// What land does once it has solved its pass again before the deorbit burn (#510), from the burn it
// planned (planned_ut) and the first pass the search finds now (its burn found_ut; found_ok false if it
// found none): "fly" that pass (the same one: its burn within half an orbit, period s, of the plan),
// "replan" to the later pass it found while it has missed fewer than 2 passes (missed) and the orbit is
// still near-circular (eccentricity ecc under 0.1), or "fail". Pure.
function land_pass_verdict {
  parameter planned_ut, found_ut, found_ok, period, missed, ecc.
  if found_ok and abs(found_ut - planned_ut) < period / 2 {
    return "fly".
  }
  if found_ok and missed < 2 and ecc < 0.1 {
    return "replan".
  }
  return "fail".
}

// Why land fails for a missed pass (land_pass_verdict "fail"), with the search's answer now: found_ok and
// its error (found_error), the touchdown UT of the next pass it found (next_ut), the passes already missed
// (missed) and the orbit's eccentricity (ecc). Pure.
function land_missed_reason {
  parameter found_ok, found_error, next_ut, missed, ecc.
  local what is "missed its pass over the site".
  if not found_ok {
    return what + " and has no other: " + found_error.
  }
  local next is "; the next is at UT " + round(next_ut) + " (next_pass_ut)".
  if missed >= 2 {
    return what + " a third time" + next.
  }
  return what + " and the orbit is no longer near-circular (eccentricity " + round(ecc, 3) + ")" + next.
}

// The deorbit burn's figures for a plan (burn_ut, r_pe): lexicon("deorbit_dv" (m/s along prograde,
// negative), "v_pe" (m/s at periapsis), "needed" (m/s: the burn, 1.2 times the speed to brake at
// periapsis, and 100 m/s of margin)).
function land_deorbit_figures {
  parameter plan.
  local mu is body:mu.
  local burn_pos is positionat(ship, plan:burn_ut) - body:position.
  local burn_vel is velocityat(ship, plan:burn_ut):orbit.
  local deorbit_dv is land_deorbit_dv(mu, burn_pos:mag, burn_vel:mag, plan:r_pe).
  local v_pe is land_periapsis_speed(mu, burn_pos:mag, plan:r_pe).
  return lexicon("deorbit_dv", deorbit_dv, "v_pe", v_pe, "needed", abs(deorbit_dv) + 1.2 * v_pe + 100).
}

// max_duration's "auto": the wait (s) to the deorbit burn, plus land's planned time from it to touchdown
// (fall_s to periapsis and brake_s of braking), plus 1200 s. Pure.
function land_max_duration {
  parameter wait_s, fall_s, brake_s.
  return max(0, wait_s) + fall_s + brake_s + 1200.
}

// When to burn to land anywhere (target "any"), from UT earliest, up to 3 orbits ahead: lexicon("error", "" or why
// not, "burn_ut", "arrive_ut", "site" (lat, lng), "r_pe"). land then plans the descent over that pass alone with the
// pass search (land_pass_plan, #511). There is no wait cap: the block warps there (ADR 0004).
function land_plan_any {
  parameter opt, approach, earliest.
  local mu is body:mu.
  local sma is ship:orbit:semimajoraxis.
  local period is ship:orbit:period.
  local step is period / 360.
  local steps is ceiling(3 * period / step).
  // Anywhere: the first burn whose arrival point (opposite the burn) is in daylight if wanted.
  for i in range(steps) {
    local burn_ut is earliest + i * step.
    local here is positionat(ship, burn_ut) - body:position.
    local arrive_ut is burn_ut + land_transfer_time(mu, here:mag, body:radius + approach).
    local arrival is -here:normalized * body:radius.
    if not opt:daylight or land_is_daylight(land_sun_elevation(arrival, arrive_ut)) {
      local now_geo is body:geopositionof(body:position + arrival).
      local lng is land_site_lng(now_geo:lng, -(arrive_ut - time:seconds), body:rotationperiod).
      local chosen is lexicon("lat", now_geo:lat, "lng", lng).
      local chosen_pe is land_pe_radius(chosen, approach, sma).
      if chosen_pe < 0 {
        return land_too_low(chosen, approach).
      }
      return lexicon("error", "", "burn_ut", burn_ut, "arrive_ut", burn_ut + land_transfer_time(mu, here:mag, chosen_pe), "site", chosen, "r_pe", chosen_pe).
    }
  }
  // Anywhere on the ring is dark for 3 orbits only when the orbit runs along the day-night line,
  // which turns too slowly (with the body's year) to wait out.
  return lexicon("error", "no daylight touchdown in the next 3 orbits: the sun never gets over " + land_daylight_sun_deg() + " degrees on the orbit's ring in that time, as it runs along the day-night line; change its plane, or land with daylight false").
}

// Adds a node at ut whose burn is the vector dv (m/s, raw frame), choosing the radial and normal
// signs whose burn matches it best. Returns the node.
function land_node_for {
  parameter ut, dv.
  local vel is velocityat(ship, ut):orbit.
  local pos is positionat(ship, ut) - body:position.
  local pro is vel:normalized.
  local rad is vxcl(pro, pos):normalized.
  local nrm is vcrs(pro, rad):normalized.
  local along is vdot(dv, pro).
  local out is vdot(dv, rad).
  local across is vdot(dv, nrm).
  local best is 0.
  local best_angle is 1000.
  for signs in list(list(1, 1), list(1, -1), list(-1, 1), list(-1, -1)) {
    local trial is node(ut, signs[0] * out, signs[1] * across, along).
    add trial.
    local off is vang(trial:deltav, dv).
    remove trial.
    if off < best_angle {
      set best_angle to off.
      set best to signs.
    }
  }
  local chosen is node(ut, best[0] * out, best[1] * across, along).
  add chosen.
  return chosen.
}

// ---- The block ----

function land {
  parameter options is lexicon().
  local extra is land_extra().
  local ctx is claudia_begin("land", options, land_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is land_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if opt:target:istype("GeoCoordinates") {
    if opt:target:body:name <> body:name {
      return claudia_finish(ctx, false, "target is on " + opt:target:body:name + ", not " + body:name, extra).
    }
  }
  local by_biome is land_is_biome(opt:target).
  if by_biome {
    if opt:target:haskey("near") {
      if opt:target:near:istype("GeoCoordinates") {
        if opt:target:near:body:name <> body:name {
          return claudia_finish(ctx, false, "target's near is on " + opt:target:near:body:name + ", not " + body:name, extra).
        }
      }
    }
  }
  if body:atm:exists {
    return claudia_finish(ctx, false, "land flies airless bodies only; " + body:name + " has an atmosphere", extra).
  }
  if claudia_on_ground() {
    return claudia_finish(ctx, false, "already down (" + status + ")", extra).
  }
  if status <> "ORBITING" or ship:orbit:eccentricity > 0.1 {
    return claudia_finish(ctx, false, "land needs a near-circular orbit (eccentricity under 0.1); situation " + status + ", eccentricity " + round(ship:orbit:eccentricity, 3), extra).
  }

  // A biome target becomes a site, then flies as a LATLNG would. The site search is the mod's
  // (find_landing_site's), through a block-internal call the Script Check allows only here in the
  // Core Library (ADR 0007), so it stays inline rather than in a helper a Mission Script could call.
  local site is "".
  local reach is land_reach_dv(opt:max_correction_dv, ship:deltav:current).
  if by_biome {
    local found is addons:claudia:landingsite(land_site_query(opt:target, opt:daylight, opt:max_slope, opt:within_hours, reach)).
    if found:error <> "" {
      local advice is "raise max_slope, try another biome or near point, or change the orbit's plane".
      if opt:daylight {
        set advice to advice + ", or land with daylight false".
      }
      return claudia_finish(ctx, false, found:error + "; " + advice, extra).
    }
    set site to lexicon("lat", found:lat, "lng", found:lng).
    if found:near_edge {
      addons:claudia:blocktrace("land", lexicon("phase", "site", "note", "at the " + opt:target:biome + " biome edge: nothing deeper reachable")).
    }
  } else {
    set site to land_site_of(opt:target).
  }

  // A craft whose engines aren't lit yet stages into them (within stage_limit).
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }

  // The Underhang (#427), measured now, before the deorbit burn: dropped during braking, or kept.
  local under is land_measure_underhang(ctx).
  if under:error <> "" {
    return claudia_finish(ctx, false, under:error, extra).
  }
  for name in under:kept {
    extra:underhang_kept:add(name).
  }

  // The craft's turn, read once for the descent: the fall turns it onto its braking direction, and
  // braking starts earlier by the turn still left (#380); the braking simulation flies its lag (#511). The call is
  // block-internal (ADR 0006, ADR 0007), so it sits in the block. A craft with no torque gets no turn allowance.
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:error <> "" {
    set turn to lexicon("error", turn:error, "turn_time", 0, "turn_s", 0, "top_rate", 0).
  }
  // The craft as the braking simulation flies it (#511): the pass search plans each pass's Descent Shape and periapsis from it.
  local craft is land_craft(under, turn, opt).

  local approach is 3000.
  local plan is "".
  local r_pe is -1.
  // A site given as coordinates (or "any"'s) may move up to 5 km for terrain; a biome's was already chosen for it (#511).
  local may_move is not by_biome.
  if site:istype("String") {
    // "any": the arrival point of the first pass in daylight, then its descent over that pass alone (#511); if no shape
    // clears it, the next arrival point on, up to 10.
    local earliest is time:seconds + 120.
    from { local tries is 0. } until tries >= 10 step { set tries to tries + 1. } do {
      local any is land_plan_any(opt, approach, earliest).
      if any:error <> "" {
        set plan to any.
        break.
      }
      local any_query is land_pass_query(any:site, any:r_pe, any:burn_ut - 60, opt:daylight, 1000, opt:within_hours, craft, opt:descent_shape, true).
      any_query:add("latest_ut", any:arrive_ut + 600).
      set plan to land_pass_plan(addons:claudia:landingpass(any_query), any:site, any:r_pe).
      if plan:error = "" or plan:error:find("terrain on the approach") < 0 {
        break.
      }
      set earliest to any:burn_ut + ship:orbit:period / 12.
    }
  } else {
    set r_pe to land_pe_radius(site, approach, ship:orbit:semimajoraxis).
    if r_pe < 0 {
      return claudia_finish(ctx, false, land_too_low(site, approach):error, extra).
    }
    // The pass search is the mod's too (find_landing_site keeps its sites by it, so both choose the
    // same pass), through a block-internal call that stays inline for the same reason (ADR 0007).
    local site_query is land_pass_query(site, r_pe, time:seconds + 120, opt:daylight, reach, opt:within_hours, craft, opt:descent_shape, may_move).
    // #521: with no pass, the failure names the change_orbit plane changes that reach the site; land never flies them.
    site_query:add("plane_changes", true).
    set plan to land_pass_plan(addons:claudia:landingpass(site_query), site, r_pe).
  }
  if plan:error <> "" {
    set extra:shape_range to land_or(plan, "shape_range", "").
    return claudia_finish(ctx, false, plan:error, extra).
  }
  land_note_plan(extra, plan).
  local mu is body:mu.
  local figures is land_deorbit_figures(plan).
  local needed_now is land_dv_needed(plan:predicted_dv, opt:burn_margin, figures:needed).
  if ship:deltav:current > 0 and ship:deltav:current < needed_now {
    return claudia_finish(ctx, false, "not enough delta-v: about " + round(needed_now) + " m/s needed, " + round(ship:deltav:current) + " m/s left", extra).
  }
  // With an Underhang to drop, the lander alone must fly the last underhang_agl m on an engine it can light.
  if under:droppable:length > 0 {
    local unflyable is land_underhang_unflyable(under:names, under:lander_dv, under:need, under:engine_ok, opt:underhang_agl, opt:stage_limit).
    if unflyable <> "" {
      return claudia_finish(ctx, false, unflyable, extra).
    }
  }

  // A site's pass is solved again from the orbit as it is shortly before the deorbit burn (ADR 0006):
  // a pass missed before the burn commits it (a warp that overshot it, an orbit that moved) gives way to
  // the next in the window, up to 2 times, while the orbit is near-circular (#510). The call is inline (ADR 0007).
  if not site:istype("String") {
    until land_resolve_ut(plan:burn_ut) <= time:seconds {
      local waited is claudia_warp_to(ctx, land_resolve_ut(plan:burn_ut), 30).
      if waited <> "" {
        claudia_make_safe().
        return claudia_finish(ctx, false, waited, extra).
      }
      local again is land_pass_plan(addons:claudia:landingpass(land_pass_query(site, r_pe, time:seconds + 120, opt:daylight, reach, opt:within_hours,
        craft, opt:descent_shape, may_move)), site, r_pe).
      local found_ok is again:error = "".
      local verdict is land_pass_verdict(plan:burn_ut, again:burn_ut, found_ok, ship:orbit:period, extra:passes_missed, ship:orbit:eccentricity).
      addons:claudia:blocktrace("land", lexicon("phase", "resolve", "planned_burn_ut", round(plan:burn_ut, 1), "burn_ut", round(again:burn_ut, 1),
        "verdict", verdict, "passes_missed", extra:passes_missed)).
      if verdict = "fail" {
        if found_ok {
          set extra:next_pass_ut to round(again:arrive_ut, 1).
        }
        return claudia_finish(ctx, false, land_missed_reason(found_ok, again:error, again:arrive_ut, extra:passes_missed, ship:orbit:eccentricity), extra).
      }
      if verdict = "replan" {
        set extra:passes_missed to extra:passes_missed + 1.
      }
      set plan to again.
      land_note_plan(extra, plan).
      if verdict = "fly" {
        break.
      }
    }
    // A later pass needs its own deorbit burn, and the delta-v for it.
    set figures to land_deorbit_figures(plan).
    set needed_now to land_dv_needed(plan:predicted_dv, opt:burn_margin, figures:needed).
    if ship:deltav:current > 0 and ship:deltav:current < needed_now {
      return claudia_finish(ctx, false, "not enough delta-v: about " + round(needed_now) + " m/s needed, " + round(ship:deltav:current) + " m/s left", extra).
    }
  }
  local aim is plan:site.
  local deorbit_dv is figures:deorbit_dv.
  local needed is figures:needed.
  // The deorbit burn may be up to a day of the body off (a daylight pass): exec_node warps there.
  if opt:max_duration:istype("String") {
    local brake_s is figures:v_pe * opt:burn_margin / land_brake_accel(ship:availablethrust / ship:mass, mu / plan:r_pe ^ 2).
    set opt:max_duration to land_max_duration(plan:burn_ut - ctx:start_ut, plan:arrive_ut - plan:burn_ut, brake_s).
  }
  sas off.

  // Deorbit: retrograde, half an orbit before the site.
  local descent_node is node(plan:burn_ut, 0, 0, deorbit_dv).
  add descent_node.
  local flown is claudia_fly_node(ctx, descent_node).
  if flown <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, flown, extra).
  }
  // actual_dv (#511): the burns flown, then the braking land_descend adds, against predicted_dv.
  set extra:actual_dv to abs(deorbit_dv).

  // A burn that left periapsis well under the plan (a bad burn or bad terrain data) would put the
  // craft into the ground before the descent could be flown to plan.
  local planned_pe is plan:r_pe - body:radius.
  if not land_periapsis_ok(ship:orbit:periapsis, planned_pe) {
    claudia_make_safe().
    return claudia_finish(ctx, false, "deorbit left periapsis at " + round(ship:orbit:periapsis) + " m, planned " + round(planned_pe) + " m", extra).
  }

  // Correction, a quarter orbit before the site: turn the plane about the craft's position so it
  // holds the site at periapsis.
  local pe_ut is time:seconds + eta:periapsis.
  local fix_ut is time:seconds + eta:periapsis / 2.
  if fix_ut > time:seconds + 90 {
    local fix_pos is positionat(ship, fix_ut) - body:position.
    local fix_vel is velocityat(ship, fix_ut):orbit.
    local up_dir is fix_pos:normalized.
    local plane is vcrs(fix_pos, land_site_vector(aim, pe_ut)):normalized.
    if vdot(plane, vcrs(fix_pos, fix_vel)) < 0 {
      set plane to -plane.
    }
    local radial is vdot(fix_vel, up_dir) * up_dir.
    local wanted is radial + vxcl(up_dir, fix_vel):mag * vcrs(plane, up_dir):normalized.
    local fix_dv is wanted - fix_vel.
    if fix_dv:mag > 0.5 {
      local fix is land_node_for(fix_ut, fix_dv).
      set flown to claudia_fly_node(ctx, fix).
      if flown <> "" {
        claudia_make_safe().
        return claudia_finish(ctx, false, flown, extra).
      }
      set extra:actual_dv to extra:actual_dv + fix_dv:mag.
    }
  }

  // After the deorbit and correction burns, before the fall: a current stage that can't cover the braking
  // is staged off now, in vacuum and not braking, so the craft has the fall to settle and turn (#382).
  // The braking need is the simulated braking times burn_margin (#511), the same figure as the check before
  // the deorbit burn; without a simulation, the old estimate less the deorbit burn already flown (it carries its
  // own 1.2 x and 100 m/s of margin). It stays put if the craft would not have the braking left without the
  // stage, or if stock reads no delta-v (as with the check above).
  local brake_need is land_dv_needed(plan:brake_dv, opt:burn_margin, needed - abs(deorbit_dv)).
  local stage_dv is ship:stagedeltav(stage:number):vacuum.
  if ship:deltav:current > 0 and land_stage_short(stage_dv, brake_need, 1) and ship:deltav:current - stage_dv >= brake_need {
    local early is land_stage_early(ctx).
    if early:staged {
      set extra:staged_before_braking to true.
      set extra:stage_left to early:left.
    }
    addons:claudia:blocktrace("land", lexicon("phase", "stage", "staged", early:staged, "note", early:note,
      "brake_need", round(brake_need))).
  }

  // Coast (on rails) to the turn and a margin before braking starts, worked out by the rule that
  // starts it over the ground the fall passes over (the craft meets high terrain before periapsis).
  local a_max is ship:availablethrust / ship:mass.
  local g_pe is mu / plan:r_pe ^ 2.
  local a_brake is sqrt(max(0.01, a_max ^ 2 - g_pe ^ 2)).
  // Samples every 5 s to 5 minutes past periapsis, braking on at least sea level's gravity (more
  // than the descent meets, so it starts no later).
  local samples is land_fall_samples(aim, time:seconds, time:seconds + eta:periapsis + 300, 5).
  local brake_ut is land_brake_estimate(samples, land_brake_accel(a_max, mu / body:radius ^ 2), opt:burn_margin).
  if brake_ut < 0 {
    // Nothing fires (it shouldn't: the site falls behind by then): braking at periapsis, as before #380.
    local v_pe is land_periapsis_speed(mu, ship:orbit:apoapsis + body:radius, ship:orbit:periapsis + body:radius).
    set brake_ut to time:seconds + eta:periapsis - v_pe * opt:burn_margin / (2 * a_brake).
  }
  local coast_ut is land_coast_end(brake_ut, turn:turn_time).
  addons:claudia:blocktrace("land", lexicon("phase", "turn", "turn_time", round(turn:turn_time, 1), "turn_s", round(turn:turn_s, 1),
    "top_rate", round(turn:top_rate, 2), "error", turn:error, "brake_ut", round(brake_ut, 1), "coast_ut", round(coast_ut, 1))).
  // When braking is planned to start, and when this orbit meets the terrain the fall passes over (-1:
  // it clears it to 5 minutes past periapsis), so a braking burn planned after the impact shows in the
  // result. braking_began_ut is set when braking does begin.
  set extra:burn_start_ut to round(brake_ut, 1).
  local impact_ut is land_impact_ut(samples).
  if impact_ut >= 0 {
    set extra:impact_ut to round(impact_ut, 1).
  }
  if coast_ut > time:seconds {
    local coasted is land_coast(ctx, coast_ut, a_brake).
    if coasted <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, coasted, extra).
    }
  }

  // BRAKEPATH (#511): braking predicted again from the craft's real state as it begins, through a delegate made here so the
  // block-internal call stays in the block (ADR 0007); the craft is read again then, after any staging.
  local predict is {
    parameter state.
    local ask is land_craft(under, turn, opt).
    for key in state:keys {
      ask:add(key, state[key]).
    }
    return addons:claudia:brakepath(ask).
  }.
  local landed is land_descend(ctx, aim, extra, turn, under, plan, predict).
  set extra:actual_dv to round(extra:actual_dv).
  if landed <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, landed, extra).
  }
  set extra:distance_to_target to round(land_surface_distance(aim:lat, aim:lng, ship:geoposition:lat, ship:geoposition:lng, body:radius)).
  // SLOPE and BIOME give lexicon("error", reason) when they throw (#439): the landing stands, the reading is -1 or "".
  local read_slope is addons:claudia:slope(ship:geoposition).
  set extra:slope to choose -1 if read_slope:istype("Lexicon") else round(read_slope, 1).
  set extra:lean to round(vang(ship:facing:forevector, ship:up:vector), 1).
  local read_biome is addons:claudia:biome(ship:geoposition).
  set extra:biome to choose "" if read_biome:istype("Lexicon") else read_biome.
  if extra:lean > 30 {
    return claudia_finish(ctx, false, "tipped over on landing", extra).
  }
  if by_biome {
    if not land_same_biome(extra:biome, opt:target:biome) {
      return claudia_finish(ctx, false, "landed in the " + extra:biome + ", outside the " + opt:target:biome + " biome (" + extra:distance_to_target + " m from the site chosen in it)", extra).
    }
  }
  return claudia_finish(ctx, true, "", extra).
}

// The seconds the braking simulation lags the steering by (#511): the time to turn 20 degrees under TURNTIME's model
// (top_rate deg/s, the 190-degree turn in turn_s s), at least 1 s. Pure.
function land_turn_lag {
  parameter top_rate, turn_s.
  return max(1, land_turn_seconds(20, top_rate, turn_s)).
}

// The craft as ADDONS:CLAUDIA:LANDINGPASS and BRAKEPATH simulate its braking (#511): thrust (kN) and mass (t) now, the
// lit engines' Isp (s), the turn lag (land_turn_lag, from turn, TURNTIME's), burn_margin, the mass it runs dry at (t; from
// stock's delta-v, when it reads one and there is no Underhang to drop), and with an Underhang to drop (under,
// land_measure_underhang's) the lander alone and underhang_agl.
function land_craft {
  parameter under, turn, opt.
  local thrusts is list().
  local isps is list().
  local engs is list().
  list engines in engs.
  for e in engs {
    if e:ignition {
      thrusts:add(e:possiblethrust).
      isps:add(e:vacuumisp).
    }
  }
  local isp is claudia_mean_isp(thrusts, isps).
  local craft is lexicon("thrust", ship:availablethrust, "mass", ship:mass, "isp", isp,
    "turn_lag", land_turn_lag(turn:top_rate, turn:turn_s), "burn_margin", opt:burn_margin).
  local dropping is under:plan:decouplers:length > 0.
  if dropping {
    craft:add("drop_agl", opt:underhang_agl).
    craft:add("lander_thrust", under:lander_thrust).
    craft:add("lander_mass", under:lander_mass).
    craft:add("lander_isp", under:lander_isp).
  } else if ship:deltav:current > 0 and isp > 0 {
    craft:add("dry_mass", ship:mass / constant:e ^ (ship:deltav:current / (isp * 9.80665))).
  }
  return craft.
}

// The plan's descent in land's results (#511): descent_shape, descent_pe (m), predicted_dv, predicted_clearance and
// moved_for_terrain.
function land_note_plan {
  parameter extra, plan.
  set extra:descent_shape to round(plan:descent_shape, 3).
  set extra:descent_pe to round(plan:r_pe - body:radius).
  set extra:predicted_dv to round(plan:predicted_dv).
  set extra:predicted_clearance to round(plan:predicted_clearance).
  set extra:moved_for_terrain to plan:moved.
}

// Stages off the current stage, on through decoupler-only stages into engines (the run claudia_autostage
// uses), if the stages below allow it. Returns lexicon(staged, left (the stage it left), note).
function land_stage_early {
  parameter ctx.
  local left is stage:number.
  local limit is ctx:opt:stage_limit.
  local engines_below is list().
  local unsafe_below is list().
  local n is left - 1.
  until n < 0 or n < limit {
    engines_below:add(claudia_stage_has_engine(n)).
    unsafe_below:add(claudia_stage_unsafe(n)).
    set n to n - 1.
  }
  local blocked is land_stage_early_block(left, limit, engines_below, unsafe_below).
  if blocked <> "" {
    return lexicon("staged", false, "left", left, "note", "not staged early, carrying on: " + blocked).
  }
  wait until stage:ready.
  local fired is left - 1.
  stage.
  // The current stage still gives thrust, so claudia_stage_run_on gets had_thrust false to run on
  // through any decoupler-only stages to the engines.
  local fired_engines is claudia_stage_has_engine(fired).
  until not claudia_stage_run_on(fired, limit, fired_engines, false) {
    wait until stage:ready.
    stage.
    set fired to fired - 1.
    set fired_engines to claudia_stage_has_engine(fired).
  }
  wait 0.5.
  set ctx:parts to ship:parts:length.
  set ctx:last_ut to time:seconds.
  return lexicon("staged", true, "left", left, "note", "staged from stage " + left + " to " + stage:number).
}

// How far part p reaches along down (a unit vector): its bounding box's corner furthest that way, in m
// along down from the craft's centre of mass (kOS 1.6 Part:BOUNDS).
function land_part_low {
  parameter p, down.
  return vdot(p:bounds:furthestcorner(down), down).
}

// Measures the craft's Underhang (#427): with the gear down for the check, so the legs are at full
// length (and back up after, if it was up), each part's lowest point along the craft's down axis
// (-ship:facing:forevector) against the legs' lowest (land_underhang, 0.2 m margin). The legs are the
// parts that deploy (ModuleWheelDeployment). Splits the Underhang into the parts a decoupler can drop,
// as drop_parts decides (one whose firing takes neither the part running the script nor the root),
// and those it keeps; works out the lander's own delta-v and engines once the droppable ones are gone.
// No legs, or no Underhang: nothing to drop, and no gear moved for no legs. Traces the measurement.
// Returns lexicon("error" ("" or an abort reason), "names" (the Underhang's), "droppable" (its
// parts that drop), "kept" (names of those that don't), "plan" (claudia_drop_plan's shape),
// "lander_dv", "need" (m/s), "engine_ok").
function land_measure_underhang {
  parameter ctx.
  local g is body:mu / body:radius ^ 2.
  local found is lexicon("error", "", "names", list(), "droppable", list(), "droppable_uids", list(), "droppable_names", list(), "kept", list(),
    "plan", lexicon("error", "", "decouplers", list(), "gone", list()), "lander_dv", 0,
    "need", land_lander_need(ctx:opt:underhang_agl, g), "engine_ok", true).
  local leg_parts is list().
  for p in ship:parts {
    if p:hasmodule("ModuleWheelDeployment") {
      leg_parts:add(p).
    }
  }
  if leg_parts:length = 0 {
    addons:claudia:blocktrace("land", lexicon("phase", "underhang", "legs", 0, "note", "no landing legs: nothing measured")).
    return found.
  }
  local was_down is gear.
  if not was_down {
    gear on.
    // The legs take a second or two to swing out.
    local deployed_ut is time:seconds + 4.
    until time:seconds > deployed_ut {
      local ticked is claudia_tick(ctx, 0).
      if ticked <> "" {
        gear off.
        set found:error to ticked.
        return found.
      }
      wait 0.
    }
  }
  local down is -ship:facing:forevector.
  local leg_lows is list().
  for p in leg_parts {
    leg_lows:add(land_part_low(p, down)).
  }
  local all_parts is ship:parts.
  local lows is list().
  for p in all_parts {
    lows:add(land_part_low(p, down)).
  }
  if not was_down {
    gear off.
  }
  local seen is list().
  for i in land_underhang(lows, leg_lows, 0.2) {
    local p is all_parts[i].
    found:names:add(p:name).
    local d is claudia_decoupler_of(p).
    local droppable is false.
    if not d:istype("String") {
      local with is claudia_dropped_with(d).
      if not claudia_drop_holds_core(with) {
        set droppable to true.
        if not seen:contains(d:uid) {
          seen:add(d:uid).
          claudia_drop_add(found:plan, d, with).
        }
      }
    }
    if droppable {
      found:droppable:add(p).
      found:droppable_uids:add(p:uid).
      found:droppable_names:add(p:name).
    } else {
      found:kept:add(p:name).
    }
  }
  // The lander once the droppable Underhang is gone: its mass, wet and dry, and its engines.
  local gone is claudia_drop_uids(found:plan).
  local m_wet is 0.
  local m_dry is 0.
  for p in all_parts {
    if not gone:contains(p:uid) {
      set m_wet to m_wet + p:mass.
      set m_dry to m_dry + p:drymass.
    }
  }
  local thrusts is list().
  local isps is list().
  local stages is list().
  local ignited is list().
  local engs is list().
  list engines in engs.
  for e in engs {
    if not gone:contains(e:uid) {
      thrusts:add(e:possiblethrust).
      isps:add(e:vacuumisp).
      stages:add(e:stage).
      ignited:add(e:ignition).
    }
  }
  set found:lander_dv to land_rocket_dv(claudia_mean_isp(thrusts, isps), m_wet, m_dry).
  // The lander alone, for the braking simulation's drop (#511).
  local lander_thrust is 0.
  for each in thrusts {
    set lander_thrust to lander_thrust + each.
  }
  found:add("lander_thrust", lander_thrust).
  found:add("lander_mass", m_wet).
  found:add("lander_isp", claudia_mean_isp(thrusts, isps)).
  set found:engine_ok to land_lander_engine_ok(stages, ignited, ctx:opt:stage_limit).
  local leg_low is leg_lows[0].
  for each in leg_lows {
    set leg_low to max(leg_low, each).
  }
  local droppable_names is list().
  for p in found:droppable {
    droppable_names:add(p:name).
  }
  addons:claudia:blocktrace("land", lexicon("phase", "underhang", "legs", leg_parts:length, "foot_m", round(leg_low, 2),
    "parts", found:names:join(", "), "droppable", droppable_names:join(", "), "kept", found:kept:join(", "),
    "lander_dv", round(found:lander_dv), "need", round(found:need), "engine_ok", found:engine_ok)).
  return found.
}

// The Underhang's drop (#427), with the throttle already at 0 and steering held on the braking
// direction: fires the decouplers that hold it (claudia_fire_decoupler, as drop_parts does; not
// staging), waits until what left is 2 m clear of the craft's lowest point or for 1 s
// (land_drop_waited), then, if nothing still gives thrust, stages into the lander's engines
// (claudia_stage_into_engines, within stage_limit). Nothing separated: it keeps the Underhang
// (underhang_kept) and relights what it had. Sets dropped_underhang, underhang_kept and
// underhang_drop_agl (radar, m) in extra and traces the drop, the thrust gap and the relit stage.
// Never fails: a falling craft keeps braking, and the descent's own tick catches a craft out of fuel.
// Returns true if any of it left now.
function land_drop {
  parameter ctx, under, extra, radar.
  local start is time:seconds.
  local on_ship is list().
  for p in ship:parts {
    on_ship:add(p:uid).
  }
  // Only what is still aboard: a stage staged off before the fall (#382) may have taken some or all of
  // it, and a part that left is never read again (its vessel may be gone).
  local still is list().
  for d in under:plan:decouplers {
    if on_ship:contains(d:uid) {
      still:add(d).
    }
  }
  local leaving is list().
  local leaving_uids is list().
  local leaving_names is list().
  for g in under:plan:gone {
    if on_ship:contains(g:uid) {
      leaving:add(g).
      leaving_uids:add(g:uid).
      leaving_names:add(g:name).
    }
  }
  // Names and uids were taken at the measuring, while the parts were aboard.
  local hanging is list().
  from { local i is 0. } until i >= under:droppable:length step { set i to i + 1. } do {
    if on_ship:contains(under:droppable_uids[i]) {
      hanging:add(under:droppable[i]).
    }
  }
  for n in claudia_names_left(under:droppable_uids, under:droppable_names, on_ship) {
    extra:dropped_underhang:add(n).
  }
  if still:length = 0 or hanging:length = 0 {
    addons:claudia:blocktrace("land", lexicon("phase", "drop", "note", "the Underhang already left when the stage was staged off before the fall")).
    return false.
  }
  local dropped_before is extra:dropped_underhang:length.
  set extra:underhang_drop_agl to round(radar).
  local fired is 0.
  for d in still {
    if claudia_fire_decoupler(d) {
      set fired to fired + 1.
    }
  }
  local down is -ship:facing:forevector.
  // No claudia_tick here: its auto-staging must not fire mid-drop, and this lasts 1 s at most.
  until false {
    // The drop is the block's own: not part loss.
    set ctx:parts to ship:parts:length.
    local aboard is list().
    local lowest is -1e9.
    for p in ship:parts {
      aboard:add(p:uid).
      set lowest to max(lowest, land_part_low(p, down)).
    }
    // How far the highest part that left is below the craft's lowest point; -1 until one has left.
    local gap is -1.
    local top is 1e9.
    for g in leaving {
      if not aboard:contains(g:uid) {
        set top to min(top, -land_part_low(g, -down)).
      }
    }
    if top < 1e9 {
      set gap to top - lowest.
    }
    if land_drop_waited(gap, time:seconds - start) {
      break.
    }
    wait 0.
  }
  local now_on is list().
  for p in ship:parts {
    now_on:add(p:uid).
  }
  for n in claudia_names_left(leaving_uids, leaving_names, now_on) {
    extra:dropped_underhang:add(n).
  }
  for p in hanging {
    if now_on:contains(p:uid) {
      extra:underhang_kept:add(p:name).
    }
  }
  local separated is extra:dropped_underhang:length > dropped_before.
  local outcome is "".
  if not separated {
    set outcome to "nothing separated: the decouplers did not fire; keeping the Underhang and braking on".
  }
  set ctx:parts to ship:parts:length.
  set ctx:last_ut to time:seconds.
  local relit is "".
  if ship:availablethrust <= 0 {
    set relit to claudia_stage_into_engines(ctx).
  }
  addons:claudia:blocktrace("land", lexicon("phase", "drop", "agl", extra:underhang_drop_agl, "fired", fired,
    "dropped", extra:dropped_underhang:length, "kept", extra:underhang_kept:join(", "), "gap_s", round(time:seconds - start, 2),
    "stage", stage:number, "relit", relit, "note", outcome)).
  return separated.
}

// The compass heading (degrees) of the horizontal direction vec at the craft.
function land_heading_of {
  parameter vec.
  return mod(arctan2(vdot(vec, heading(90, 0):vector), vdot(vec, ship:north:vector)) + 360, 360).
}

// Where the Underhang dropped now will hit (a GeoCoordinates): its fall from the radar height at the
// craft's speed down and across (land_fall_reach), on flat ground.
function land_drop_impact {
  parameter radar, v_v, v_h, g.
  if v_h:mag < 0.01 {
    return ship:geoposition.
  }
  return body:geopositionof(ship:position + v_h:normalized * land_fall_reach(radar, -v_v, v_h:mag, g)).
}

// The touchdown point (lat, lng) moved at least 50 m clear of where the Underhang hits (impact, a
// GeoCoordinates), straight away from it, by no more than max_divert (land_clear_shift); as it was
// if already clear. track: the way to move if the two are on the same spot. Traces a move.
function land_clear_site {
  parameter site, impact, max_divert, track.
  local dist is land_surface_distance(site:lat, site:lng, impact:lat, impact:lng, body:radius).
  local shift is land_clear_shift(dist, max_divert).
  if shift <= 0 {
    return site.
  }
  local site_pos is body:geopositionlatlng(site:lat, site:lng):position.
  local up_v is (site_pos - body:position):normalized.
  local away is vxcl(up_v, site_pos - impact:position).
  if away:mag < 1 {
    set away to vxcl(up_v, track).
  }
  if away:mag < 0.001 {
    set away to ship:north:vector.
  }
  local spot is body:geopositionof(site_pos + away:normalized * shift).
  addons:claudia:blocktrace("land", lexicon("phase", "clear", "from_impact", round(dist), "moved", round(shift))).
  return lexicon("lat", spot:lat, "lng", spot:lng).
}

// The shared wait loop (claudia_warp_to) to ut, with a stop condition: ended early (returns "") once
// land_brake_now says the terrain under the craft is coming too soon, since KSP can cut rails warp
// short near the ground. a_brake: m/s^2 the engines brake with. Returns "" (at ut, or early for the
// ground; the descent follows either way) or an abort reason.
function land_coast {
  parameter ctx, ut, a_brake.
  local impact_in is {
    local g is body:mu / (body:radius + altitude) ^ 2.
    return land_fall_eta(alt:radar, -ship:verticalspeed, g).
  }.
  local ground_near is {
    return land_brake_now(impact_in:call(), -ship:verticalspeed, a_brake, ctx:opt:burn_margin).
  }.
  local cap is {
    return land_warp_cap(impact_in:call(), -ship:verticalspeed, a_brake, ctx:opt:burn_margin).
  }.
  return claudia_warp_to(ctx, ut, 30, ground_near, cap).
}

// The height (m above sea level) a near-circular orbit must have after braking aborts (#530): the body's highest terrain
// (highest, m; ADDONS:CLAUDIA:HIGHESTTERRAIN) by the path's margin, since its ground track will cross the whole band of
// the body under it, or alt_now, where the craft is, if that is higher. Circularized at 2.6 km over the 1.6 km of ground on
// Minmus Flats' approach, the next block flew it into 5.7 km terrain. Pure.
function land_recover_alt {
  parameter alt_now, highest.
  return max(alt_now, highest + land_clear_margin(0)).
}

// The vertical speed (m/s, up positive) the recovery climbs at with rise m still to climb: a tenth of it a second, at
// most 20 m/s, as land_vv_target climbs back to its height. Pure.
function land_recover_climb {
  parameter rise.
  return min(20, 0.1 * max(0, rise)).
}

// The burn (m/s) that brings a craft at alt_now m moving v_h m/s horizontally and v_up m/s vertically (orbital frame) to a
// near-circular orbit at target_alt m (land_recover_alt) round a body of body_radius m under mu: circular there, with the
// climb's speed gained and lost on the way up (land_recover_climb). Pure.
function land_recover_dv {
  parameter mu, body_radius, alt_now, target_alt, v_h, v_up.
  return land_circular_dv(mu, body_radius + max(alt_now, target_alt), v_h, v_up) + 2 * land_recover_climb(target_alt - alt_now).
}

// After braking aborts (#511): brings the orbit back to near-circular, burning now, only when the abort leaves
// periapsis under the body's highest terrain by the margin, the delta-v left covers the burn and plan has another pass in the window
// (land_recover); otherwise leaves it alone. The orbit it makes clears the body's highest terrain (land_recover_alt, #530):
// it climbs to that height first if it is under it. Traced. Returns the note for the failure: what it did with the orbit,
// and whether that orbit is under the highest terrain, so will meet the ground.
function land_after_abort {
  parameter ctx, plan.
  local highest is addons:claudia:highestterrain(body).
  local target_alt is land_recover_alt(altitude, highest).
  local up_now is ship:up:vector.
  local v_now is ship:velocity:orbit.
  local need is land_recover_dv(body:mu, body:radius, altitude, target_alt, vxcl(up_now, v_now):mag, vdot(v_now, up_now)).
  local verdict is land_recover(ship:orbit:periapsis < highest + land_clear_margin(0), ship:deltav:current, need, plan:next_pass_ut > 0).
  addons:claudia:blocktrace("land", lexicon("phase", "recover", "verdict", verdict, "pe", round(ship:orbit:periapsis), "highest", round(highest),
    "alt", round(altitude), "target_alt", round(target_alt), "need", round(need), "dv_left", round(ship:deltav:current), "next_pass_ut", round(plan:next_pass_ut))).
  if verdict <> "circularize" {
    return "; the orbit is left as it is (periapsis " + round(ship:orbit:periapsis) + " m)" + land_recover_under(highest).
  }
  local steer is v(0, 0, 0).
  local power is 0.
  lock steering to claudia_keep_roll(steer).
  lock throttle to power.
  // The 300 s the burn had before, and the climb to the target at land_recover_climb's 20 m/s (its slower last 200 m fit in the 300).
  local give_up is time:seconds + 300 + max(0, target_alt - altitude) / 20.
  until time:seconds > give_up {
    local up_v is ship:up:vector.
    local vel is ship:velocity:orbit.
    // Circular at the target's height, climbing to it (land_recover_climb).
    local want is vxcl(up_v, vel):normalized * sqrt(body:mu / (body:radius + max(altitude, target_alt))) + up_v * land_recover_climb(target_alt - altitude).
    set steer to want - vel.
    if steer:mag < 1 and altitude >= target_alt - 50 {
      break.
    }
    local a_max is ship:availablethrust / max(0.001, ship:mass).
    set power to min(1, steer:mag / max(0.001, a_max)) * max(0, cos(vang(ship:facing:forevector, steer))).
    local ticked is claudia_tick(ctx, power).
    if ticked <> "" {
      break.
    }
    wait 0.
  }
  set power to 0.
  return "; the orbit was brought back to near-circular (periapsis " + round(ship:orbit:periapsis) + " m)" + land_recover_under(highest).
}

// The note on an orbit left after an abort (#530): "" if periapsis clears the body's highest terrain (highest, m), else
// that it doesn't and will meet the ground.
function land_recover_under {
  parameter highest.
  if ship:orbit:periapsis >= highest {
    return "".
  }
  return ", under " + body:name + "'s highest terrain (" + round(highest) + " m): it will meet the ground unless raised".
}

// The powered descent onto aim, to touchdown and a settled craft. Sets braking_began_ut, best_effort, reversals,
// touchdown_speed_actual and touchdown_h_speed in extra (and lean, if settling fails), and traces each
// phase and every 2 s to the log (#365). turn: the TURNTIME lexicon (top_rate, turn_s) for the turn
// allowance (#380). under: land_measure_underhang's, whose droppable Underhang it drops (land_drop_due,
// land_drop) and then lands clear of (#427). It measures the site along the surface and carries the
// braking direction round the body (#431). It flies plan's Descent Shape (#511): as braking begins it predicts
// again from the real state (predict, a delegate to ADDONS:CLAUDIA:BRAKEPATH), switching to the cheapest shape
// that still clears or, with none, not braking (land_repredict_verdict); it steers by the highest ground on the
// rest of the predicted path, within 45 degrees of retrograde and holding still for a tiny demand (land_steer),
// and stops braking that raises periapsis (land_brake_guard) or climbs off the predicted path (land_path_guard):
// throttle 0, the orbit brought back to near-circular if it must be (land_recover), safe, traced. Adds the
// braking it flew to actual_dv. Returns "" or why it failed.
function land_descend {
  parameter ctx, aim, extra, turn, under, plan, predict.
  local opt is ctx:opt.
  local site is lexicon("lat", aim:lat, "lng", aim:lng).
  local braking is false.
  local h_start is 0.
  // The Descent Shape flown and the path predicted for it (#511): the plan's, until braking begins.
  local shape is plan:descent_shape.
  if shape <= 0 {
    set shape to 2.
  }
  local predicted is plan:path.
  local dir is lexicon("al", -1, "cr", 0, "up", 0).
  local last_tick is time:seconds.
  // The braking direction: the horizontal velocity when braking began (towards the site again after
  // turning back), carried round the body onto each tick's horizon (#431), and whether the craft is
  // moving back along it (land_is_reversed).
  local axis is v(0, 0, 0).
  local reversed is false.
  // Periapsis, horizontal speed and clearance (h, unclamped) when braking began, for the guard (land_brake_guard).
  local pe_start is 0.
  local v_h_start is 0.
  local h_began is 0.
  set extra:reversals to 0.
  local steer is -ship:velocity:surface.
  local tset is 0.
  lock steering to claudia_keep_roll(steer).
  lock throttle to tset.
  local last_speed is ship:velocity:surface:mag.
  local last_h_speed is 0.
  // The fastest it may move along the braking axis: its speed when braking began (#365), then never more than the slowest
  // since (land_v_top, #530).
  local v_top is 0.
  // The terrain heights at land_lookahead's points along the ground track, a few read each tick.
  local ahead_h is list().
  local next_read is 0.
  local to_go is 1e9.
  // The trace (ADDONS:CLAUDIA:BLOCKTRACE): the last line's phase and UT.
  local last_phase is "".
  local last_trace_ut is -1e9.
  // Seconds still to turn onto the last steering command (land_turn_seconds), and whether braking is
  // still turning onto its first command, flying by land_turning_s until it is pointed and braking (#380).
  local turn_left is 0.
  local first_turn is false.
  // RCS Use: the pre-point has until braking is planned to start; braking keeps RCS off (#506) unless the wheels have no torque.
  set ctx:turn_by to extra:burn_start_ut.
  // The Underhang (#427): still to drop, and where it was predicted to hit once dropped ("" until then).
  local drop_pending is under:plan:decouplers:length > 0.
  local drop_impact is "".

  until status = "LANDED" or status = "SPLASHED" {
    local up_v is ship:up:vector.
    // The site along the surface (#431), from where the craft is now.
    local here is ship:geoposition.
    local dist is land_surface_distance(here:lat, here:lng, site:lat, site:lng, body:radius).
    local vel is ship:velocity:surface.
    local v_v is vdot(vel, up_v).
    local v_h is vxcl(up_v, vel).
    local radar is alt:radar.
    local a_max is ship:availablethrust / max(0.001, ship:mass).
    local radius_now is body:radius + altitude.
    local g is body:mu / radius_now ^ 2.
    // The braking flown, for actual_dv (#511): the last tick's throttle at the engines' acceleration.
    set extra:actual_dv to extra:actual_dv + tset * a_max * (time:seconds - last_tick).
    set last_tick to time:seconds.
    set last_speed to vel:mag.
    set last_h_speed to v_h:mag.
    if not braking {
      set to_go to dist.
    }
    // The ground ahead: all of it on the first tick, then four points a tick, oldest first.
    local points is land_lookahead(v_h:mag, to_go).
    local track is v(0, 0, 0).
    if v_h:mag > 0.01 {
      set track to v_h:normalized.
    }
    local reads is 4.
    if ahead_h:length = 0 {
      set reads to points:length.
    }
    for k in range(reads) {
      local i is mod(next_read, points:length).
      local height is body:geopositionof(ship:position + track * points[i]):terrainheight.
      if i < ahead_h:length {
        set ahead_h[i] to height.
      } else {
        ahead_h:add(height).
      }
      set next_read to next_read + 1.
    }
    local h is land_clearance(radar, altitude, ahead_h).
    local want is lexicon("h_ref", -1, "vh", -1, "vv", -1).
    set turn_left to land_turn_seconds(vang(ship:facing:forevector, steer), turn:top_rate, turn:turn_s).

    if not braking {
      // The braking axis if braking began now: along the horizontal velocity, else towards the site;
      // stopped over the site, any axis will do, as long as it has a direction (land_bearing gives
      // north there).
      local fall_hdg is land_bearing(here:lat, here:lng, site:lat, site:lng).
      if v_h:mag >= 1 {
        set fall_hdg to land_heading_of(v_h).
      }
      local fall_ahead is heading(fall_hdg, 0):vector.
      local fall_across is heading(fall_hdg + 90, 0):vector.
      local fig is land_site_figures(here:lat, here:lng, site:lat, site:lng, fall_hdg, body:radius).
      // Steer along what braking will first ask for, so it starts pointed (#380).
      local first is land_fall_guide(lexicon("s", fig:along, "c", fig:across, "v_al", vdot(v_h, fall_ahead),
        "v_cr", vdot(v_h, fall_across), "v_up", v_v, "h", h, "a_max", a_max, "g", g, "radius", radius_now), opt:touchdown_speed, opt:burn_margin).
      local first_accel is fall_ahead * first:al + fall_across * first:cr + up_v * first:up.
      if first_accel:mag > 0.001 {
        set steer to first_accel.
      }
      // Brake once land_vh_target's speed is down to this one (allowing for the turn still left), if
      // the site falls behind, or if the ground comes up first.
      if land_brake_due(dist, fig:along, v_h:mag, -v_v, h, land_brake_accel(a_max, g), opt:burn_margin, turn_left) {
        set braking to true.
        set ctx:turn_by to 0.
        set ctx:rcs_braking to true.
        set extra:braking_began_ut to round(time:seconds, 1).
        set first_turn to true.
        set h_start to max(land_h_end(), h).
        // At least enough to close on a site moved up to max_divert.
        set v_top to max(50, v_h:mag).
        set pe_start to ship:orbit:periapsis.
        set v_h_start to v_h:mag.
        set h_began to h.
        set axis to fall_ahead.
        // Is the ground at the site flat enough? If not, divert to flatter ground now, while the
        // descent has the whole braking distance to take the new site in (a later move could put
        // the site behind it).
        local spot is addons:claudia:flatspot(body:geopositionlatlng(site:lat, site:lng), opt:max_divert, opt:max_slope).
        if spot:haskey("error") {
          // FLATSPOT threw (#439): stay on the site, as one that found nothing flatter.
          set spot to lexicon("lat", site:lat, "lng", site:lng, "flat", false).
        }
        local moved is land_surface_distance(site:lat, site:lng, spot:lat, spot:lng, body:radius).
        set site to lexicon("lat", spot:lat, "lng", spot:lng).
        if not drop_impact:istype("String") {
          set site to land_clear_site(site, drop_impact, opt:max_divert, fall_ahead).
        }
        set extra:best_effort to not spot:flat.
        set dist to land_surface_distance(here:lat, here:lng, site:lat, site:lng, body:radius).
        if moved > 1 {
          addons:claudia:blocktrace("land", lexicon("phase", "divert", "moved", round(moved), "flat", spot:flat, "dist", round(dist))).
        }
        // Braking predicted again from the real state (#511): the planned shape if it still clears, else the cheapest
        // that does; with none, it doesn't brake while its periapsis is over the terrain on the track.
        local now_fig is land_site_figures(here:lat, here:lng, site:lat, site:lng, fall_hdg, body:radius).
        local state is lexicon("lat", site:lat, "lng", site:lng, "heading", fall_hdg, "s", now_fig:along, "alt", altitude,
          "v_h", v_h:mag, "v_up", v_v, "shape", shape).
        local again is predict:call(state).
        local planned_clears is again:error <> "" or land_or(again, "clears", false).
        local best is -1.
        if not planned_clears {
          set state["shape"] to "auto".
          local cheapest is predict:call(state).
          if land_or(cheapest, "clears", false) {
            set best to cheapest:descent_shape.
            set again to cheapest.
          }
        }
        local pe_clear is ship:orbit:periapsis > land_path_ground_ahead(predicted, 1e12).
        local verdict is land_repredict_verdict(planned_clears, best, pe_clear).
        addons:claudia:blocktrace("land", lexicon("phase", "repredict", "verdict", verdict, "planned_shape", shape, "shape", land_or(again, "descent_shape", -1),
          "clearance", round(land_or(again, "predicted_clearance", -1)), "error", again:error, "pe", round(ship:orbit:periapsis))).
        if verdict = "hold" {
          set tset to 0.
          claudia_make_safe().
          if plan:next_pass_ut > 0 {
            set extra:next_pass_ut to round(plan:next_pass_ut, 1).
          }
          return "terrain on the approach: no Descent Shape clears it from where braking began, so it didn't brake and kept its orbit (periapsis "
            + round(ship:orbit:periapsis) + " m); the next pass is at UT " + round(plan:next_pass_ut) + " (next_pass_ut; -1 for none)".
        }
        if verdict = "switch" {
          set shape to best.
        } else if verdict = "steepest" {
          // No path clears to guard against: the plan's would trip on the steeper shape's height, so there is none.
          set shape to min(shape, 0.2).
          set predicted to list().
        }
        if again:error = "" and verdict <> "steepest" and land_or(again, "path", list()):length > 0 {
          set predicted to again:path.
          set extra:predicted_clearance to round(land_or(again, "predicted_clearance", -1)).
        }
        set extra:descent_shape to round(shape, 3).
      }
      set tset to 0.
    }
    if braking {
      // The braking direction carried onto this tick's horizon, and the site along and across it.
      set axis to vxcl(up_v, axis):normalized.
      local hdg is land_heading_of(axis).
      local fig is land_site_figures(here:lat, here:lng, site:lat, site:lng, hdg, body:radius).
      if land_turn_back(fig:along, v_h:mag, opt:max_overshoot) {
        // Stopped more than max_overshoot past the site: the one case it turns back.
        set hdg to land_bearing(here:lat, here:lng, site:lat, site:lng).
        set axis to heading(hdg, 0):vector.
        set fig to land_site_figures(here:lat, here:lng, site:lat, site:lng, hdg, body:radius).
        set extra:reversals to extra:reversals + 1.
        set reversed to false.
      }
      local s is fig:along.
      // The highest ground on the rest of the predicted path, not only the look-ahead in view (#511).
      set h to min(h, altitude - land_path_ground_ahead(predicted, s)).
      // Braking that raises periapsis, or climbs off the predicted path, is making things worse: stop, and leave the
      // rest of the fuel in the tanks (#431, #511).
      local on_path is land_path_at(predicted, s).
      local tripped is land_brake_guard(pe_start, ship:orbit:periapsis).
      if tripped = "" {
        set tripped to land_path_guard(altitude, on_path).
      }
      if tripped <> "" {
        set tset to 0.
        addons:claudia:blocktrace("land", lexicon("phase", "guard", "error", tripped, "v_h", round(v_h:mag, 1), "v_h_start", round(v_h_start, 1),
          "alt", round(altitude), "predicted_alt", round(land_or(on_path, "alt", -1)), "s", round(s), "h", round(h), "h_start", round(h_began),
          "dist", round(dist), "pe", round(ship:orbit:periapsis), "pe_start", round(pe_start))).
        local orbit_note is land_after_abort(ctx, plan).
        claudia_make_safe().
        return tripped + orbit_note.
      }
      local ahead is heading(hdg, 0):vector.
      local across is heading(hdg + 90, 0):vector.
      local v_ahead is vdot(v_h, ahead).
      local was is reversed.
      set reversed to land_is_reversed(v_ahead, was).
      if reversed and not was {
        set extra:reversals to extra:reversals + 1.
      }
      // Still turning onto its first braking command: braking that started early for the turn flies
      // by the site that much nearer (so a nose pulled off it, as land_guide holds v_top for a
      // moment, is pulled back), until the craft is pointed and braking along (or slowed to the approach).
      if v_h:mag <= 50 {
        set first_turn to false.
      }
      local s_guide is s.
      if first_turn {
        set s_guide to land_turning_s(s, v_h:mag, turn_left).
      }
      set v_top to land_v_top(v_top, v_h:mag).
      set want to land_guide(lexicon("s", s_guide, "c", fig:across, "v_al", v_ahead, "v_cr", vdot(v_h, across),
        "v_up", v_v, "h", h, "h_start", h_start, "v0", v_h_start, "shape", shape, "v_top", v_top, "a_max", a_max, "g", g, "radius", radius_now),
        opt:touchdown_speed, opt:burn_margin).
      if turn_left <= 0 and want:al < -0.1 {
        set first_turn to false.
      }
      set to_go to want:to_go.
      // Within 45 degrees of surface retrograde, and still for a demand under 5% of the engines' (#511).
      set dir to land_steer(want, dir, lexicon("al", v_ahead, "cr", vdot(v_h, across), "up", v_v), a_max).
      local pointing is ahead * dir:al + across * dir:cr + up_v * dir:up.
      if pointing:mag > 0.001 {
        set steer to pointing.
      } else {
        set steer to up_v.
      }
      set tset to min(1, dir:mag / max(0.001, a_max)) * max(0, cos(vang(ship:facing:forevector, steer))).
      if radar < opt:gear_alt and not gear {
        gear on.
      }
    }
    // The descent trace, for the log: a craft destroyed mid-descent leaves its last line as the block's state.
    local phase is land_phase(braking, v_h:mag, want:h_ref).
    if land_trace_due(phase, last_phase, time:seconds, last_trace_ut) {
      addons:claudia:blocktrace("land", lexicon("phase", phase, "h", round(h), "radar", round(radar), "v_h", round(v_h:mag, 1),
        "v_down", round(-v_v, 1), "dist", round(dist), "throttle", round(tset, 2), "h_ref", round(want:h_ref),
        "vh_want", round(want:vh, 1), "vv_want", round(want:vv, 1), "turn_left", round(turn_left, 1), "pe", round(ship:orbit:periapsis))).
      set last_phase to phase.
      set last_trace_ut to time:seconds.
    }
    // The Underhang drops under underhang_agl, or where its engines run dry while braking, before the
    // tick's auto-staging would stage them off. The throttle goes to 0 for it, steering held; the next
    // tick works the braking out again from the craft as it is then.
    if drop_pending {
      if land_drop_due(radar, opt:underhang_agl, braking and claudia_needs_stage()) {
        set drop_pending to false.
        set tset to 0.
        local impact is land_drop_impact(radar, v_v, v_h, g).
        // A tick for the throttle lock to take the 0 before the decouplers fire.
        wait 0.
        if land_drop(ctx, under, extra, radar) {
          set drop_impact to impact.
          set site to land_clear_site(site, drop_impact, opt:max_divert, v_h).
        }
      }
    }
    // While the Underhang is still to drop the tick never auto-stages, so a stage running dry between
    // the check above and here waits for the next pass's drop rather than being staged off.
    local ticked is claudia_tick(ctx, tset, land_burning(phase) and not drop_pending).
    if ticked <> "" {
      return ticked.
    }
    wait 0.
  }
  set tset to 0.
  set ship:control:pilotmainthrottle to 0.
  set extra:touchdown_speed_actual to round(last_speed, 2).
  set extra:touchdown_h_speed to round(last_h_speed, 2).
  addons:claudia:blocktrace("land", lexicon("phase", "touchdown", "speed", extra:touchdown_speed_actual, "v_h", extra:touchdown_h_speed)).
  unlock throttle.
  // Hold upright while it settles on its legs, then leave it safe.
  set steer to ship:up:vector.
  local settle_ut is time:seconds + 5.
  until time:seconds > settle_ut {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      // A part lost as it tips stops the settle: report how far over it went.
      set extra:lean to round(vang(ship:facing:forevector, ship:up:vector), 1).
      return ticked.
    }
    wait 0.
  }
  claudia_make_safe().
  return "".
}
