// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block launch: flies from the ground of a body with an atmosphere to a coast out of it.
// Built on claudia_lib.ks; its heading is Core's (ADDONS:CLAUDIA:LAUNCHHEADING, the one plan_ascent reports, #475);
// block-only helpers are prefixed launch_. The "//|" lines are Claudia's docs for it; the first is
// its summary.

@lazyglobal off.

//| launch(options): flies from the ground of a body with an atmosphere to a coast out of it; then circularize.
//| It ends once the craft is coasting out of the atmosphere with its apoapsis at target_ap (a stage too weak to coast and circularize burns on past it until it can, or the periapsis clears the air); follow it
//| with circularize in the same Mission Script (one Maneuver Sequence, one Approval). A target_ap
//| below the atmosphere top makes it a hop that ends at the top of the climb.
//| Options:
//|   launch_at ["now"]: "now", a time (UT s) to wait for on the pad, or a body or vessel (e.g. MUN,
//|     or a body's name: "Minmus") to wait for the pad to pass under the plane of, which also sets
//|     target_inc to its plane.
//|   target_ap ["auto"]: apoapsis to reach, m; "auto" is the atmosphere top + 10 km.
//|   target_inc [0]: inclination, degrees; negative launches southward.
//|   heading ["into_inc"]: compass degrees to fly, or "into_inc" to fly into target_inc.
//|   turn_start_speed [60]: m/s of airspeed at which the gravity turn starts. Give more than the craft
//|     will reach (e.g. 100000) to climb straight up.
//|   turn_shape [0.45]: the turn's pitch is 90 * (1 - f^turn_shape), f the fraction of the way from
//|     the turn start to the atmosphere top (or target_ap if lower); smaller turns over sooner. The
//|     turn starts again from vertical wherever throttle_max_q held it. Pass the turn_shape plan_ascent
//|     gives (the Craft Check's, or a new call if the craft or target_ap changed).
//|   max_aoa [5]: most degrees the nose may be off the airflow while the air is thick enough to matter
//|     (over 500 Pa dynamic pressure), all the way up and through the coast; above 8000 Pa the angle
//|     allowed falls in proportion to q (a tenth of max_aoa at the least).
//|     In thin air (under 500 Pa) the nose is held no more than 3 degrees below the prograde, so the
//|     upper turn follows the trajectory instead of the pitch program.
//|   throttle_max_q ["none"]: Pa of dynamic pressure to throttle back at (to 5% at most; in thick
//|     air never below the craft's weight). While it holds the throttle back in thick air the turn
//|     waits: too slow to turn over without falling, the craft climbs as steeply as max_aoa lets it.
//|   g, atm_height, surface_pressure ["body"]: the body's surface gravity (m/s^2), atmosphere top (m)
//|     and surface pressure (Pa), to override what the game says.
//|   max_duration ["auto"]: the pad wait + 900 s + burn-through.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| In thick air (over a tenth of surface pressure) the throttle never drops below the weight of the
//| craft until cutoff, and the craft never steers more than max_aoa off the airflow. With no thrust (a
//| hop's coast, between stages) only fins and reaction wheels hold it: a craft with neither tumbles in
//| thick air. Engines are staged into from the pad, and launch clamps released.
//| Extra results: ap_error (m, apoapsis minus target_ap when the block ended; a hop's shows drag
//| loss), max_q (Pa, highest dynamic pressure), coast_ap (m, apoapsis at the first coast), lan_actual (degrees, the orbit's LAN at the end).
//| Fails with "launch starts on the ground", "no atmosphere on <body>: use ascend", "no thrust", a
//| launch_at it can't use, "lost control: ..." (saying whether it was burning or
//| coasting, the throttle, q and the nose's angle off the airflow), "fell back into the
//| atmosphere: ...", out of fuel, or an abort.
//| Example: `local res is launch(lexicon("target_ap", 80000)). if res:success { set res to circularize(lexicon("at", "ap")). }
//| addons:claudia:report(res).`

// The block's own options and defaults ("auto", "into_inc", "none" and "body" are resolved once
// the block knows the body and the wait).
function launch_defaults {
  return lexicon(
    "launch_at", "now",
    "target_ap", "auto",
    "target_inc", 0,
    "heading", "into_inc",
    "turn_start_speed", 60,
    "turn_shape", 0.45,
    "max_aoa", 5,
    "throttle_max_q", "none",
    "g", "body",
    "atm_height", "body",
    "surface_pressure", "body").
}

// "" or why a string value of an option isn't one the block knows.
function launch_unreadable {
  parameter opt.
  local words is list(
    list("target_ap", "auto", "m"),
    list("throttle_max_q", "none", "Pa"),
    list("g", "body", "a number"),
    list("atm_height", "body", "a number"),
    list("surface_pressure", "body", "a number")).
  // "lib_lazcalc" is the heading's old name for the default (#475).
  if opt:heading:istype("String") and opt:heading <> "into_inc" and opt:heading <> "lib_lazcalc" {
    return "heading must be 'into_inc' or degrees, not '" + opt:heading + "'".
  }
  for w in words {
    local value is opt[w[0]].
    if value:istype("String") and value <> w[1] {
      return w[0] + " must be '" + w[1] + "' or " + w[2] + ", not '" + value + "'".
    }
  }
  return "".
}

// The pitch (degrees above the horizon) at altitude h (m) of a turn that starts at start_alt and
// ends flat at end_alt: 90 * (1 - fraction^shape); a smaller shape turns over sooner.
function launch_pitch {
  parameter h, start_alt, end_alt, shape.
  if h <= start_alt or end_alt <= start_alt {
    return 90.
  }
  local fraction is min(1, (h - start_alt) / (end_alt - start_alt)).
  return 90 * (1 - fraction ^ shape).
}

// Whether the turn waits at dynamic pressure q_now (Pa) and pressure_ratio of surface pressure:
// while max_q (Pa, -1 for no limit) has the throttle easing back (from 90% of it, as launch_throttle
// eases) in thick air, the craft is too slow to turn over without its path falling with it (#235),
// so it climbs as steeply as max_aoa lets it instead: launch starts the turn again from vertical
// wherever this holds.
function launch_turn_held {
  parameter q_now, max_q, pressure_ratio.
  return max_q > 0 and q_now > 0.9 * max_q and launch_thick_air(pressure_ratio).
}

// How far under target_ap (m) the apoapsis may sag while coasting before the engines light again.
function launch_topup_tol {
  parameter target_ap.
  return max(100, 0.005 * target_ap).
}

// How close to target_ap (m) the engines cut off: half the top-up band, so a cutoff can't be undone
// at once by the top-up.
function launch_cutoff_tol {
  parameter target_ap.
  return launch_topup_tol(target_ap) / 2.
}

// The throttle on the way to target_ap: 0 once ap is within launch_cutoff_tol of it (only that cuts the
// engines off, so the throttle never trickles near the target); easing off linearly over the last ramp metres of apoapsis; easing off from 90% to 110% of max_q
// (Pa, -1 for no limit) at dynamic pressure q_now (Pa), down to 5%; but never below t_floor, so the
// throttle can't collapse in thick air (the floor wins over max_q).
function launch_throttle {
  parameter ap, target_ap, ramp, q_now, max_q, t_floor.
  if ap >= target_ap - launch_cutoff_tol(target_ap) {
    return 0.
  }
  local t is min(1, (target_ap - ap) / ramp).
  if max_q > 0 {
    set t to min(t, max(0.05, 1 - (q_now - 0.9 * max_q) / (0.2 * max_q))).
  }
  return min(1, max(t_floor, max(0, t))).
}

// The phase of the climb, for its trace (#531), named as plan_ascent's sim names it: "coast" with the throttle off; "vertical"
// before the turn starts; "turn_held" while throttle_max_q holds the turn (see launch_turn_held); "thin_air" when the thin-air rule
// raised the pitch (launch_thin_pitch); otherwise "turn". Pure.
function launch_phase {
  parameter coasting, turn_started, held, thin.
  if coasting {
    return "coast".
  }
  if not turn_started {
    return "vertical".
  }
  if held {
    return "turn_held".
  }
  if thin {
    return "thin_air".
  }
  return "turn".
}

// Whether the climb traces a line now (#531), and why: "phase" or "stage" when either changed (no more often than every
// 0.25 s, so a q that wavers about 500 Pa can't flood the log), "tick" 2 s after the last line, "" if not due. Pure.
function launch_trace_due {
  parameter phase, last_phase, stage_now, last_stage, ut, last_ut.
  if ut >= last_ut + 2 {
    return "tick".
  }
  if ut >= last_ut + 0.25 {
    if phase <> last_phase {
      return "phase".
    }
    if stage_now <> last_stage {
      return "stage".
    }
  }
  return "".
}

// Thick air: pressure above a tenth of the surface pressure.
function launch_thick_air {
  parameter pressure_ratio.
  return pressure_ratio > 0.1.
}

// The lowest throttle allowed before cutoff: in thick air (pressure_ratio of surface pressure),
// enough for thrust to match weight (mass_t at g m/s^2 against thrust_kn); otherwise none.
function launch_throttle_floor {
  parameter pressure_ratio, mass_t, g, thrust_kn.
  if not launch_thick_air(pressure_ratio) or thrust_kn <= 0 {
    return 0.
  }
  return min(1, mass_t * g / thrust_kn).
}

// Dynamic pressure (Pa) above which the air pushes hard enough on the craft for max_aoa to apply.
function launch_aero_q {
  return 500.
}

// Dynamic pressure (Pa) up to which the full max_aoa is allowed; above it the allowed angle falls in
// proportion, so a craft with weak aerodynamic stability is held nearer the airflow where q is highest.
function launch_aoa_q_ref {
  return 8000.
}

// Degrees the nose may be off the airflow at dynamic pressure q_pa: max_aoa up to q_ref, then
// max_aoa * q_ref / q_pa, never less than a tenth of max_aoa.
function launch_allowed_aoa {
  parameter max_aoa, q_pa, q_ref.
  if q_pa <= q_ref {
    return max_aoa.
  }
  return max(max_aoa * 0.1, max_aoa * q_ref / q_pa).
}

// Degrees the commanded pitch may be below the surface-prograde pitch in thin air (#236).
function launch_thin_lead {
  return 3.
}

// pitch (degrees above the horizon) raised, in thin air (q_pa under launch_aero_q, where max_aoa
// does not apply), to no lower than lead degrees below prograde_pitch, so the upper turn follows
// the trajectory; otherwise pitch as it was (#236).
function launch_thin_pitch {
  parameter pitch, prograde_pitch, q_pa, lead.
  if q_pa > launch_aero_q() {
    return pitch.
  }
  return max(pitch, prograde_pitch - lead).
}

// The body's values, each option's own or, for "body", the game's: lexicon("g", m/s^2,
// "atm_height", m, "surface_pressure", Pa).
function launch_body {
  parameter opt.
  local b is ship:body.
  local values is lexicon("g", opt:g, "atm_height", opt:atm_height, "surface_pressure", opt:surface_pressure).
  if values:g:istype("String") {
    set values:g to b:mu / b:radius ^ 2.
  }
  if values:atm_height:istype("String") {
    set values:atm_height to 0.
    if b:atm:exists {
      set values:atm_height to b:atm:height.
    }
  }
  if values:surface_pressure:istype("String") {
    set values:surface_pressure to b:atm:altitudepressure(0) * constant:atmtokpa * 1000.
  }
  return values.
}

// want, turned toward the airflow (flow) until it is no more than max_aoa degrees off it.
function launch_limit_aoa {
  parameter want, flow, max_aoa.
  if vang(want, flow) <= max_aoa {
    return want.
  }
  local side is vxcl(flow, want).
  if side:mag < 0.000001 {
    return flow.
  }
  return flow:normalized * cos(max_aoa) + side:normalized * sin(max_aoa).
}

// The pitch (degrees) in the heading's vertical plane nearest want_pitch that is no more than max_aoa off an airflow
// at flow_pitch in that plane and off_plane degrees out of it (under max_aoa). Turning from the airflow itself kept
// its sideways drift, which a fast turn grew into inclination (#676). Pure.
function launch_plane_pitch {
  parameter want_pitch, flow_pitch, off_plane, max_aoa.
  local reach is arccos(cos(max_aoa) / cos(off_plane)).
  return flow_pitch + max(-reach, min(reach, want_pitch - flow_pitch)).
}

// Core's heading into the plane of inc (negative southward) from the craft's latitude: lexicon error, heading (#475).
function launch_heading {
  parameter inc, target_ap.
  return addons:claudia:launchheading(lexicon("inc", inc, "target_ap", target_ap, "lat", ship:latitude,
    "gm", body:mu, "radius", body:radius, "rotation", body:rotationperiod)).
}

// Why the block stopped for lost control: burning is whether an engine gave thrust (not what the
// throttle asked: between stages it asks with none), tset the throttle (0..1), q_pa the dynamic
// pressure (Pa), off_aim and off_flow the nose's degrees off the aim and off the airflow
// (srfprograde), alt_m the altitude (m).
function launch_lost_control {
  parameter burning, tset, q_pa, off_aim, off_flow, alt_m.
  local doing is "coasting".
  if burning {
    set doing to "burning".
  }
  return "lost control: " + round(off_aim) + " degrees off the ascent path at " + round(alt_m) + " m, " + doing + " at " + round(100 * tset) + "% throttle, q " + round(q_pa) + " Pa, " + round(off_flow) + " degrees off the airflow".
}

// When to launch and into what inclination: lexicon("error", "" or why not, "wait", s, "inc", degrees).
// options are the ones given (to tell whether target_inc was).
function launch_when {
  parameter opt, options, target_ap.
  local plan is lexicon("error", "", "wait", 0, "inc", opt:target_inc).
  local go_at is opt:launch_at.
  if go_at:istype("String") {
    if go_at = "now" {
      return plan.
    }
    // A body's name works as the body, as transfer_to's target does.
    if not bodyexists(go_at) {
      set plan:error to "launch_at must be 'now', a time (UT s), or a body (or its name) or vessel to launch into the plane of: no body named '" + go_at + "'".
      return plan.
    }
    set go_at to body(go_at).
  }
  if go_at:istype("Scalar") {
    set plan:wait to max(0, go_at - time:seconds).
    return plan.
  }
  if not go_at:istype("Orbitable") {
    set plan:error to "launch_at must be 'now', a time (UT s) or a body or vessel to launch into the plane of".
    return plan.
  }
  if go_at:body:name <> ship:body:name {
    set plan:error to "launch_at " + go_at:name + " doesn't orbit " + ship:body:name.
    return plan.
  }
  local win is addons:claudia:launchwindow(lexicon("inc", go_at:orbit:inclination, "lan", go_at:orbit:lan, "target_ap", target_ap)).
  return launch_apply_window(plan, win, go_at:name, options:haskey("target_inc")).
}

// plan with the window win (ADDONS:CLAUDIA:LAUNCHWINDOW's lexicon) for the plane of name: its error, or the wait and,
// unless target_inc was given, the inclination (negative for the southbound window).
function launch_apply_window {
  parameter plan, win, name, inc_given.
  if win:error <> "" {
    set plan:error to "launch_at " + name + ": " + win:error.
    return plan.
  }
  set plan:wait to win:wait.
  if not inc_given {
    set plan:inc to win:inc.
  }
  return plan.
}

// Releases launch clamps still holding the craft, staging once (within stage_limit).
function launch_release_clamps {
  parameter ctx.
  for p in ship:parts {
    if p:hasmodule("LaunchClamp") {
      wait until stage:ready.
      if stage:number > ctx:opt:stage_limit {
        stage.
        wait 0.5.
        set ctx:parts to ship:parts:length.
        claudia_restart_clock(ctx).
      }
      return.
    }
  }
}

function launch {
  parameter options is lexicon().
  local extra is lexicon("ap_error", 0, "max_q", 0, "coast_ap", 0, "lan_actual", 0).
  local ctx is claudia_begin("launch", options, launch_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is launch_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if not claudia_on_ground() {
    return claudia_finish(ctx, false, "launch starts on the ground, not " + status, extra).
  }
  local body_values is launch_body(opt).
  if body_values:atm_height <= 0 {
    return claudia_finish(ctx, false, "no atmosphere on " + ship:body:name + ": use ascend", extra).
  }
  local target_ap is opt:target_ap.
  if target_ap:istype("String") {
    set target_ap to body_values:atm_height + 10000.
  }
  set extra:ap_error to round(ship:apoapsis - target_ap).

  // Wait on the pad.
  local go is launch_when(opt, options, target_ap).
  if go:error <> "" {
    return claudia_finish(ctx, false, go:error, extra).
  }
  local auto_limit is opt:max_duration:istype("String").
  if auto_limit {
    set opt:max_duration to go:wait + 900.
  }
  if go:wait > 0 {
    local waited is claudia_wait_until(ctx, time:seconds + go:wait).
    if waited <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, waited, extra).
    }
  }

  // The heading is Core's (launch_heading) for the plane, set for the pad and recomputed on the climb as the latitude moves.
  local hdg is opt:heading.
  local into_plane is hdg:istype("String").
  if into_plane {
    local aimed is launch_heading(go:inc, target_ap).
    if aimed:error <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, "launch heading: " + aimed:error, extra).
    }
    set hdg to aimed:heading.
  }
  local max_q_pa is -1.
  if opt:throttle_max_q:istype("Scalar") {
    set max_q_pa to opt:throttle_max_q.
  }

  local stowed is claudia_stow_ship_systems(ctx).
  if stowed <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, stowed, extra).
  }
  // Light the engines.
  sas off.
  local tset is 1.
  lock throttle to tset.
  local aim is ship:up:vector.
  lock steering to aim.
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, lit, extra).
  }
  launch_release_clamps(ctx).
  local liftoff_ut is time:seconds.

  // Climb, then coast with the steering held, until out of the atmosphere (or, for a hop, at the top).
  local hop is target_ap <= body_values:atm_height.
  local turn_end is min(body_values:atm_height, target_ap).
  local turn_start_alt is -1.
  local ramp is max(500, 0.02 * target_ap).
  local topup_tol is launch_topup_tol(target_ap).
  local coasting is false.
  local burn_through is false.
  local burn_figures is lexicon("due", 0).
  local off_since is -1.
  local q_max is 0.
  local reason is "".
  local done is false.
  // The trace (ADDONS:CLAUDIA:BLOCKTRACE, #531): the last line's phase, stage and UT, and the UT of the last pass of this
  // loop (the gap between passes is traced, as the nose holds its last aim between them).
  local last_phase is "".
  local last_stage is -1.
  local last_trace_ut is -1e9.
  local last_pass_ut is time:seconds.
  local pass_gap is 0.
  // One tick of the climb or the coast: sets tset and aim, and done (with reason if it failed).
  local fly_tick is {
    local q_pa is ship:q * constant:atmtokpa * 1000.
    set pass_gap to time:seconds - last_pass_ut.
    set last_pass_ut to time:seconds.
    set q_max to max(q_max, q_pa).
    if burn_through and auto_limit { set opt:max_duration to opt:max_duration + pass_gap. } // A burn-through's own time doesn't count against "auto" (#932).
    local spd is ship:velocity:surface:mag.
    local ap is ship:apoapsis.
    local ratio is 0.
    if body_values:surface_pressure > 0 {
      set ratio to ship:body:atm:altitudepressure(altitude) * constant:atmtokpa * 1000 / body_values:surface_pressure.
    }

    if burn_through and ship:periapsis > body_values:atm_height {
      set done to true.
      return.
    }
    if coasting {
      if hop and ship:verticalspeed <= 0 {
        set done to true.
        return.
      }
      if not hop and altitude > body_values:atm_height {
        set done to true.
        return.
      }
      if not hop and ship:verticalspeed < 0 {
        set reason to "fell back into the atmosphere: ap " + round(ap) + " m, below " + round(body_values:atm_height) + " m".
        set done to true.
        return.
      }
      // Top up what drag took, but only out of thick air, so a hop coasts smoothly. The throttle
      // waits a tick: the coast's wait ends first and drops its warp.
      if ap < target_ap - topup_tol and not launch_thick_air(ratio) {
        set coasting to false.
        return.
      }
    }
    if coasting {
      set tset to 0.
    } else {
      local t_floor is launch_throttle_floor(ratio, ship:mass, body_values:g, ship:availablethrust).
      // Burning through (#932), as if target_ap were far off: throttle_max_q and the thick-air floor still hold.
      set tset to launch_throttle(ap, choose ap + 1e7 if burn_through else target_ap, ramp, q_pa, max_q_pa, t_floor).
      if tset = 0 or burn_through {
        // The first cutoff, and each tick of a burn-through until the coast works again (coast_ap set latches it): Core says whether this stage can coast to apoapsis and circularize there (#932; figures in KSP.log). Stages and turn only each second: stock's table is the costly part.
        if extra:coast_ap = 0 {
          if time:seconds >= burn_figures:due {
            set burn_figures to lexicon("due", time:seconds + 1, "stages", claudia_burn_stages(opt:stage_limit), "turn", addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime))).
          }
          set burn_through to addons:claudia:burnthrough(lexicon("burning", burn_through, "ap", ap, "sma", ship:orbit:semimajoraxis, "gm", ship:body:mu, "radius", ship:body:radius, "eta_ap", eta:apoapsis, "altitude", altitude, "target_ap", target_ap, "atm_height", body_values:atm_height, "angle", vang(ship:facing:vector, velocityat(ship, time:seconds + eta:apoapsis):orbit), "stages", burn_figures:stages, "turn", burn_figures:turn)):burn_through.
        }
        set tset to choose (choose tset if tset > 0 else 1) if burn_through else 0.
        set coasting to not burn_through.
        if extra:coast_ap = 0 and coasting {
          set extra:coast_ap to round(ap).
        }
      }
    }

    // Steer: the pitch program while burning, the airflow while coasting, never more than max_aoa off
    // the airflow while the air matters.
    if turn_start_alt < 0 and spd >= opt:turn_start_speed {
      set turn_start_alt to altitude.
    }
    // While throttle_max_q holds the throttle back in thick air the turn waits, starting again from vertical here.
    local held is turn_start_alt >= 0 and launch_turn_held(q_pa, max_q_pa, ratio).
    if held {
      set turn_start_alt to altitude.
    }
    local thin is false.
    local want is ship:up:vector.
    local pitch is 90.
    if not coasting and not burn_through {
      if turn_start_alt >= 0 {
        set pitch to launch_pitch(altitude, turn_start_alt, turn_end, opt:turn_shape).
      }
      if into_plane {
        set hdg to launch_heading(go:inc, target_ap):heading.
      }
      if spd > 30 {
        local lifted is launch_thin_pitch(pitch, 90 - vang(ship:srfprograde:vector, ship:up:vector), q_pa, launch_thin_lead()).
        set thin to lifted > pitch.
        set pitch to lifted.
      }
      set want to heading(hdg, pitch):vector.
    } else if spd > 30 {
      if hop or (q_pa > 2 * launch_aero_q() and not burn_through) {
        set want to ship:srfprograde:vector.
      } else {
        set want to ship:prograde:vector.
      }
    }
    if q_pa > launch_aero_q() and spd > 30 {
      // Burning, the aim stays in the heading's vertical plane while the airflow is near enough to it (#676).
      local allowed is launch_allowed_aoa(opt:max_aoa, q_pa, launch_aoa_q_ref()).
      local flow is ship:srfprograde:vector.
      local along is heading(hdg, 0):vector.
      local normal is vcrs(ship:up:vector, along).
      local off_plane is arcsin(min(1, abs(vdot(flow:normalized, normal)))).
      if coasting or burn_through {
        set want to launch_limit_aoa(want, flow, allowed).
      } else if off_plane >= allowed {
        // Further out than allowed: turn from the airflow toward the plane (where the branch below starts).
        set want to launch_limit_aoa(vxcl(normal, flow), flow, allowed).
      } else {
        // Built by hand, not heading(): the pitch passes 90 when the airflow tips back.
        local in_plane is launch_plane_pitch(pitch, arctan2(vdot(flow, ship:up:vector), vdot(flow, along)), off_plane, allowed).
        set want to ship:up:vector * sin(in_plane) + along * cos(in_plane).
      }
    }
    set aim to want.

    // The trace, only when due: the checks are strings and numbers already to hand.
    local phase is choose "burn_through" if burn_through else launch_phase(coasting, turn_start_alt >= 0, held, thin).
    local why is launch_trace_due(phase, last_phase, stage:number, last_stage, time:seconds, last_trace_ut).
    if why <> "" {
      set last_phase to phase.
      set last_stage to stage:number.
      set last_trace_ut to time:seconds.
      addons:claudia:blocktrace("launch", lexicon("phase", phase, "why", why, "alt", round(altitude, 1), "t", round(time:seconds - liftoff_ut, 2),
        "pitch", round(vang(aim, ship:up:vector), 2), "prograde", round(vang(ship:srfprograde:vector, ship:up:vector), 2), "q", round(q_pa),
        "throttle", round(tset, 3), "mass", round(ship:mass, 3), "stage", stage:number, "ap", round(ap),
        "h_speed", round(ship:groundspeed, 1), "v_speed", round(ship:verticalspeed, 1), "gap", round(pass_gap, 3),
        "heading", round(hdg, 2), "north", round(vdot(ship:velocity:orbit, ship:north:vector), 1), "inc", round(ship:orbit:inclination, 3))).
    }

    // A craft far off its steering for 3 s in the air has lost control: stop rather than tumble on.
    local off_aim is vang(ship:facing:forevector, aim).
    if off_aim > 30 and q_pa > launch_aero_q() {
      if off_since < 0 {
        set off_since to time:seconds.
      } else if time:seconds - off_since > 3 {
        local off_flow is vang(ship:facing:forevector, ship:srfprograde:vector).
        set reason to launch_lost_control(ship:thrust > 0, tset, q_pa, off_aim, off_flow, altitude).
        set done to true.
      }
    } else {
      set off_since to -1.
    }
  }.

  until done {
    if coasting {
      // The coast is one wait (claudia_warp_to), fly_tick its stop (it flies each tick and says when the Coast ends): physics warp by the q rule (KSP
      // refuses rails in the air), back at 1x at the top of the atmosphere or the hop, or before a top-up.
      local waited is claudia_warp_to(ctx, time:seconds + max(1, eta:apoapsis), 0, { fly_tick:call(). return done or not coasting. }).
      if waited <> "" {
        set reason to waited.
        set done to true.
      }
    } else {
      fly_tick:call().
      if not done {
        // Burning while climbing, not in the coast out of the air (#379).
        local ticked is claudia_tick(ctx, tset, not coasting).
        if burn_through and ticked:startswith("out of fuel") {
          // Fuel or stage_limit ended the burn-through (#932): the craft coasts on from here, as it would have from the cutoff.
          set burn_through to false.
          set coasting to true.
          set extra:coast_ap to round(ship:apoapsis).
        } else if ticked <> "" {
          set reason to ticked.
          set done to true.
        }
        wait 0.
      }
    }
  }

  set extra:max_q to round(q_max).
  set extra:lan_actual to round(ship:orbit:lan, 2).
  set extra:ap_error to round(ship:apoapsis - target_ap).
  if extra:coast_ap = 0 {
    set extra:coast_ap to round(ship:apoapsis).
  }
  addons:claudia:blocktrace("launch", lexicon("phase", "end", "alt", round(altitude, 1), "t", round(time:seconds - liftoff_ut, 2),
    "ap", round(ship:apoapsis), "coast_ap", extra:coast_ap, "max_q", extra:max_q, "stage", stage:number, "mass", round(ship:mass, 3),
    "dv_used", round(ctx:dv_used, 1), "failed", reason <> "")).
  if reason <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, reason, extra).
  }
  set tset to 0.
  claudia_release_controls().
  return claudia_finish(ctx, true, "", extra).
}
