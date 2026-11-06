// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block return: flies home from orbit around any moon. The ejection is the mod's solver, ADDONS:CLAUDIA:RETURNBURN. Block-only helpers
// are prefixed return_. kOS lets a function be named return when it is called as a value. The "//|" lines are Claudia's docs.

@lazyglobal off.

//| return(options): flies home from orbit around any moon to its planet: an ejection
//| burn onto a path whose periapsis at the planet is target_pe, then a mid-course correction halfway to
//| reentry, both flown with exec_node. It ends after the correction, still in space; the craft comes
//| down anywhere on the planet. Call it as a value, `local res is return().`: a bare `return().` is kOS's
//| own return statement.
//| Options:
//|   target_pe [35000; 30000 with a target]: the periapsis altitude at the planet to aim for, m; inside its atmosphere the
//|     craft comes down.
//|   target_inc ["any"]: the inclination round the planet to arrive at, degrees from 0 to 180; "any" for none.
//|   window ["next"]: "next" ejects at the burn point's next pass once the craft can have turned to the
//|     burn; a time (UT seconds) ejects at its first pass from that time. It
//|     warps on rails to the pass.
//|   target ["none"]: LATLNG(lat, lng), lexicon("lat", x, "lng", y) or "ksc": where to come down. The
//|     ejection picks the window and times its burn to land there (about 1 m/s more); the correction
//|     trims the rest, without target_inc. Read predicted_error_km; when too far for the Mission Goal,
//|     capture and call deorbit. Aim a splashdown offshore.
//|   max_wait ["auto"]: with a target, s the ejection may wait; "auto" is one turn of the landing (about 7 h).
//|   correction [true]: false leaves out the mid-course correction.
//|   checkpoint ["off"]: the mod's own, for a coast it resumes.
//|   max_duration ["auto"]: the time to the predicted reentry plus 900 s; a shorter one moves the correction sooner.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: planet_pe (m, the periapsis at the planet), reentry_ut (UT of the predicted
//| entry into the planet's atmosphere, or of periapsis if that is above it), correction_dv (m/s the
//| mid-course correction burned), predicted_geo (lexicon lat, lng: where the craft is predicted to come
//| down, from the path it is on after the correction, with the prediction leaving out parachutes and
//| lift; empty without a target or a prediction), predicted_error_km (km from the target to
//| predicted_geo; -1 without one), aim_dv (m/s) and aim_spread_km (km per 0.5 m/s of burn error).
//| The correction is skipped when the prediction is already within 1 km of target_pe and 0.5 degrees of
//| target_inc (with a target: within 2 km of it) or the fix is under 0.1 m/s. When leaving the moon's
//| sphere of influence comes before halfway, the block warps through that first.
//| Fails with an option value it can't read, "not in orbit", "already orbiting <body>, a planet: lower
//| the periapsis with exec_node instead", "return starts from orbit around a moon, not <body>", "a
//| maneuver node is already planned: remove it first", "found no return: <why>", "found no correction:
//| <why>", "no return to <planet> after the ejection burn" (or "after the correction"), "the return
//| flown arrives at periapsis <m>, not <target_pe> m" (1 km, 0.5 deg off), an exec_node
//| failure (that burn's node is left in place), or an abort.
//| Follow it with reentry, in the same sequence or the next (no Due Time needed): once the craft is
//| in the planet's sphere of influence (after the correction it usually is; if ship:body is still the
//| moon, coast until ship:orbit:nextpatcheta has passed), reentry warps to the atmosphere itself.
//| Tool: stage_performance for the delta-v; it plans its own burns, so don't plan them with predict_burn or solve_burn.
//| Example: `local res is return(). addons:claudia:report(res).`

// The block's own options and defaults.
function return_defaults {
  return lexicon("target_pe", 35000, "target_inc", "any", "window", "next", "correction", true, "target", "none", "max_wait", "auto", "checkpoint", "off").
}

// Whether the Mission Script gave a landing target.
function return_aims {
  parameter opt.
  return not (opt:target:istype("String") and opt:target = "none").
}

// What return asks REENTRYIMPACT: where orb, from from_ut, comes down, and how far from site whose ground is ground_alt m. Pure.
function return_impact_query {
  parameter orb, from_ut, site, ground_alt.
  return lexicon("lat", site:lat, "lng", site:lng, "ground_alt", max(0, ground_alt), "from_ut", from_ut,
    "orbit_sma", orb:semimajoraxis, "orbit_ecc", orb:eccentricity, "orbit_inc", orb:inclination,
    "orbit_lan", orb:lan, "orbit_argpe", orb:argumentofperiapsis, "orbit_mna", orb:meananomalyatepoch,
    "orbit_epoch", orb:epoch, "orbit_body", orb:body:name).
}

// "" or why an option value isn't one the block can use.
function return_unreadable {
  parameter opt.
  local pe is claudia_pe_unreadable(opt:target_pe).
  if pe <> "" {
    return pe.
  }
  if opt:target_inc:istype("String") {
    if opt:target_inc <> "any" {
      return "target_inc must be 'any' or degrees from 0 to 180, not '" + opt:target_inc + "'".
    }
  } else if not opt:target_inc:istype("Scalar") {
    return "target_inc must be 'any' or degrees from 0 to 180, not a " + opt:target_inc:typename.
  } else if opt:target_inc < 0 or opt:target_inc > 180 {
    return "target_inc must be 'any' or degrees from 0 to 180, not " + opt:target_inc.
  }
  if return_aims(opt) {
    local bad_target is claudia_target_unreadable(opt:target).
    if bad_target <> "" {
      return bad_target.
    }
  }
  local window is claudia_window_unreadable(opt:window).
  if window <> "" {
    return window.
  }
  return claudia_correction_unreadable(opt:correction).
}

// The ask of RETURNBURN or CORRECTIONBURN with target_inc in it when the Mission Script gave one ("any" leaves it out). Pure.
function return_with_inc {
  parameter ask, target_inc.
  if target_inc:istype("Scalar") {
    set ask:target_inc to target_inc.
  }
  return ask.
}

// Core's word on the path p round the planet against the ask: "" or why it is not the return asked, with the figures.
function return_miss {
  parameter p, opt.
  if p:istype("String") {
    return "no return to the planet".
  }
  return addons:claudia:returnmiss(return_with_inc(lexicon("target_pe", opt:target_pe, "pe", p:orbit:periapsis, "inc", p:orbit:inclination), opt:target_inc)):error.
}

// Seconds an orbit (a in m, negative for a hyperbola) takes from radius (m), coming in, to periapsis; 0 if it never comes that low.
function return_time_to_pe {
  parameter a, ecc, mu, radius.
  if ecc < 0.000001 or radius <= a * (1 - ecc) {
    return 0.
  }
  local c is (1 - radius / a) / ecc.
  if a > 0 {
    // Kepler's equation with the eccentric anomaly; radius beyond apoapsis counts from apoapsis.
    local big_e is arccos(max(-1, c)).
    local mean is big_e * 3.141592653589793 / 180 - ecc * sin(big_e).
    return mean / sqrt(mu / a ^ 3).
  }
  local sinh_h is sqrt(c ^ 2 - 1).
  local mean is ecc * sinh_h - ln(c + sinh_h).
  return mean / sqrt(mu / abs(a) ^ 3).
}

// UT of the next periapsis of the orbit patch orb: mean anomaly is zero there; an ellipse's wraps into its next orbit.
function return_pe_ut {
  parameter orb.
  local n is sqrt(orb:body:mu / abs(orb:semimajoraxis ^ 3)).
  local pi is 3.141592653589793.
  local delta is -orb:meananomalyatepoch * pi / 180.
  if delta < 0 {
    set delta to delta + 2 * pi.
  }
  return orb:epoch + delta / n.
}

// UT of the craft entering the atmosphere on the orbit patch orb (or of periapsis, if it stays above it).
function return_reentry_ut {
  parameter orb.
  local pe_ut is return_pe_ut(orb).
  local top is orb:body:radius + orb:body:atm:height.
  return pe_ut - return_time_to_pe(orb:semimajoraxis, orb:eccentricity, orb:body:mu, top).
}

// Before the correction at mid (UT): leaving the moon's sphere of influence before then is waited out (on rails, however
// long), so the node is placed on the orbit round the planet the craft will be on. Returns "" or why the wait failed.
function return_leave_moon {
  parameter ctx, mid.
  if ship:orbit:hasnextpatch {
    local soi_ut is time:seconds + ship:orbit:nextpatcheta.
    if soi_ut < mid - 120 {
      return claudia_wait_until(ctx, soi_ut + 30).
    }
  }
  return "".
}

// The Course Correction's ask with the landing in it (#793), from the ejection's: Core holds target_pe and moves the landing. Pure.
function return_aimed_ask {
  parameter ask, burn_ask, isp, dv_left.
  for key in list("lat", "lng", "ground_alt") {
    set ask[key] to burn_ask[key].
  }
  set ask:isp to isp.
  set ask:dv_left to dv_left.
  return ask.
}

function return {
  parameter options is lexicon().
  local extra is lexicon("planet_pe", -1, "reentry_ut", -1, "correction_dv", 0, "predicted_geo", lexicon(), "predicted_error_km", -1, "aim_dv", 0, "aim_spread_km", -1).
  local ctx is claudia_begin("return", options, return_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is return_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }

  local moon is ship:body.
  local resumed is opt:checkpoint:startswith("resume").
  if status <> "ORBITING" and not resumed {
    return claudia_finish(ctx, false, "not in orbit: return starts from orbit around a moon", extra).
  }
  if not moon:hasbody {
    return claudia_finish(ctx, false, "return starts from orbit around a moon, not " + moon:name, extra).
  }
  if not moon:body:hasbody and not resumed {
    return claudia_finish(ctx, false, "already orbiting " + moon:name + ", a planet: lower the periapsis with exec_node instead", extra).
  }
  // Resumed (#775), the craft may be past the moon already.
  local planet is choose moon if resumed and not moon:body:hasbody else moon:body.
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }

  // With a target, the mod's reentry prediction says where a path comes down, for a path the craft may not be on yet (ADR 0007).
  local predict is 0.
  local burn_ask is lexicon().
  if return_aims(opt) {
    if not options:haskey("target_pe") {
      set opt:target_pe to 30000.
    }
    local site is claudia_target_site(opt:target, planet:name).
    if site:error <> "" {
      return claudia_finish(ctx, false, site:error, extra).
    }
    local ground_alt is planet:geopositionlatlng(site:lat, site:lng):terrainheight.
    set burn_ask to lexicon("lat", site:lat, "lng", site:lng, "ground_alt", max(0, ground_alt), "max_wait", opt:max_wait, "max_duration", opt:max_duration, "start_ut", ctx:start_ut, "isp", claudia_engine_isp()).
    set predict to {
      parameter orb, from_ut.
      return addons:claudia:reentryimpact(return_impact_query(orb, from_ut, site, ground_alt)).
    }.
  }

  // The ejection is the mod's solver, a block-internal call (ADR 0007). Its earliest departure leaves time to turn to the burn (TRANSFERSTEP).
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:error <> "" {
    return claudia_finish(ctx, false, turn:error, extra).
  }
  if not resumed {
    local first is addons:claudia:transferstep(lexicon("timing", "earliest", "start_ut", ctx:start_ut,
      "turn_time", turn:turn_time, "window", opt:window)).
    if first:error <> "" {
      return claudia_finish(ctx, false, first:error, extra).
    }
    set burn_ask:target_pe to opt:target_pe.
    set burn_ask:earliest_ut to first:ut.
    local found is addons:claudia:returnburn(return_with_inc(burn_ask, opt:target_inc)).
    if found:error <> "" {
      return claudia_finish(ctx, false, found:error, extra).
    }
    set extra:aim_dv to round(found:aim_dv, 2).
    set extra:aim_spread_km to round(found:spread_km).
    local nd is node(found:ut, found:radial, found:normal, found:prograde).
    add nd.
    local planned is claudia_patch(nd:orbit, planet).
    if planned:istype("String") {
      remove nd.
      return claudia_finish(ctx, false, "found no return: the ejection node does not reach " + planet:name, extra).
    }
    if opt:max_duration:istype("String") {
      set opt:max_duration to return_reentry_ut(planned:orbit) - ctx:start_ut + 900.
    }
    local ejected is claudia_burn(ctx, nd).
    if not ejected:success {
      return claudia_finish(ctx, false, ejected:reason, extra).
    }
  }
  local home is claudia_patch(ship:orbit, planet).
  if home:istype("String") {
    return claudia_finish(ctx, false, "no return to " + planet:name + " after the ejection burn", extra).
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    // Halfway to reentry, once out of the moon's sphere of influence; sooner if max_duration ends first (#368).
    local mid is (time:seconds + return_reentry_ut(home:orbit)) / 2.
    local waited is return_leave_moon(ctx, mid).
    if waited <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, waited, extra).
    }
    local fixed is lexicon("reason", "", "dv", 0).
    local needed is false.
    if return_aims(opt) {
      local coming is claudia_patch(ship:orbit, planet).
      local landing is choose lexicon("error", "no path") if coming:istype("String") else predict(coming:orbit, coming:soi_ut).
      set needed to landing:error <> "" or landing:error_km > 2.
    } else {
      set needed to return_miss(claudia_patch(ship:orbit, planet), opt) <> "".
    }
    if needed {
      // The mod decides (#775): stop at a Coast Checkpoint, or when resumed, the watched wait to the correction.
      local ahead is addons:claudia:transferstep(lexicon("timing", "coast", "target", planet:name, "mid", mid, "turn_time", turn:turn_time,
        "arrive_ut", return_reentry_ut(home:orbit), "checkpoint", opt:checkpoint)).
      if ahead:do = "done" {
        return claudia_finish(ctx, true, "", ahead:report).
      }
      set mid to ahead:ut.
      if ahead:do = "wait" {
        addons:claudia:encounterwatch(ahead:watch).
        set waited to claudia_warp_to(ctx, ahead:wait_ut, 30, { return addons:claudia:encounterwatchdropped. }).
        if waited <> "" {
          claudia_make_safe().
          return claudia_finish(ctx, false, waited, extra).
        }
      }
      // 300 s to turn to and lead the burn, as before #450.
      local when is addons:claudia:transferstep(lexicon("timing", "correction", "mid", mid, "now", time:seconds,
        "lead", 300, "start_ut", ctx:start_ut, "max_duration", opt:max_duration)).
      if when:error <> "" {
        return claudia_finish(ctx, false, when:error, extra).
      }
      if when:why <> "" {
        return claudia_finish(ctx, false, when:why, extra).
      }
      // The Course Correction is the mod's solver, a block-internal call (ADR 0007), so it sits in the block.
      if return_aims(opt) {
        set fixed to claudia_fly_correction(ctx, addons:claudia:correctionburn(
          return_aimed_ask(claudia_correction_ask(planet:name, opt:target_pe, when:ut), burn_ask, claudia_engine_isp(), claudia_dv_remaining()))).
      } else {
        set fixed to claudia_fly_correction(ctx, addons:claudia:correctionburn(
          return_with_inc(claudia_correction_ask(planet:name, opt:target_pe, when:ut), opt:target_inc))).
      }
    }
    set extra:correction_dv to round(fixed:dv, 2).
    if fixed:reason <> "" {
      return claudia_finish(ctx, false, fixed:reason, extra).
    }
    set home to claudia_patch(ship:orbit, planet).
    if home:istype("String") {
      return claudia_finish(ctx, false, "no return to " + planet:name + " after the correction", extra).
    }
  }
  set extra:planet_pe to round(home:orbit:periapsis).
  set extra:reentry_ut to round(return_reentry_ut(home:orbit)).
  if return_aims(opt) {
    // Where the path flown comes down, after the correction's own error.
    local landing is predict(home:orbit, home:soi_ut).
    if landing:error = "" {
      set extra:predicted_geo to claudia_geo(landing).
      set extra:predicted_error_km to round(landing:error_km, 2).
    }
  } else {
    // The path flown, whatever the correction left: a return off what was asked fails with the figure (#759).
    local miss is return_miss(home, opt).
    if miss <> "" {
      return claudia_finish(ctx, false, miss, extra).
    }
  }
  return claudia_finish(ctx, true, "", extra).
}
