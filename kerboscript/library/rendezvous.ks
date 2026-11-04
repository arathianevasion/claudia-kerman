// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block rendezvous (#455): flies from orbit round the body the Target Vessel orbits to within its physics range
// (loaded) and under 2 m/s relative to it, where dock (#456) takes over. Its steps follow the rule order of MechJeb2's
// MechJebModuleRendezvousAutopilot (GPL-3): kill the relative speed once the target is loaded; match planes; match
// velocities at a closest approach already close enough; a Course Correction to one after the transfer; the
// bi-impulse transfer (Transfer, with the Target Vessel's orbit as its target orbit) when its window is near; else a
// resonant phasing orbit. Each step is re-solved from the live orbit at fly time through the block-internal calls
// (ORBITBURN, EJECTIONBURN, CORRECTIONBURN; ADR 0006, ADR 0007) and flown with exec_node. Built on claudia_lib.ks and
// exec_node.ks; block-only helpers are prefixed rendezvous_. The "//|" lines are Claudia's docs for it; the first is its
// summary. (Not the Rendezvous transfer mode: that is how a transfer meets its target; this block flies one.)

@lazyglobal off.

//| rendezvous(options): meets the Target Vessel.
//| From orbit round its body to within its physics range (loaded, about 2.2 km) under 2 m/s relative; then dock.
//| Each step is solved when it starts and flown by exec_node: a plane match over max_rel_inc, a phasing orbit when
//| the transfer window is over max_phasing_orbits away, the transfer, a Course Correction to about 300 m, and
//| matching velocities there.
//| Options:
//|   target ["auto"]: the Mission's Target Vessel; a name given must be its.
//|   max_rel_inc [0.1]: degrees off the target's plane before the plane match.
//|   max_phasing_orbits [5]: orbits the craft may wait for the window before it phases.
//|   max_dv ["none"]: m/s all the burns may cost; a burn past it fails before it flies.
//|   max_duration ["auto"]: none.
//|   plus min_ec_pct, abort_on_part_loss, stage_limit and drop_empty as for every block.
//| Extra results: distance_actual (m), relative_speed_actual (m/s), burns (the steps flown: match_plane, phasing,
//| transfer, correction, match_velocities, kill_speed).
//| Fails with "the Game Snapshot has no Target Vessel ..." (or it is landed), "<target> is not the Target Vessel <name>",
//| the target round another body, "not in orbit", "a maneuver node is already planned: remove it first", a solver's
//| reason, "needs <dv> m/s, over max_dv", an exec_node failure (its node stays), an abort, or "no hand-off after 8 steps".
//| Example: `local res is rendezvous(lexicon()). addons:claudia:report(res).`

// The block's own options and defaults ("auto": the Mission's Target Vessel; "none", no limit).
function rendezvous_defaults {
  return lexicon("target", "auto", "max_rel_inc", 0.1, "max_phasing_orbits", 5, "max_dv", "none").
}

// The steps the block may fly before it gives up without a hand-off.
function rendezvous_max_steps {
  return 8.
}

// m: the closest approach the Course Correction aims at, and the band a closest approach is matched in (nearer risks a
// collision, farther is outside the physics range). m/s: the hand-off's relative speed.
function rendezvous_aim_m {
  return 300.
}

function rendezvous_band_low_m {
  return 100.
}

function rendezvous_band_high_m {
  return 2000.
}

function rendezvous_handoff_speed {
  return 2.
}

// "" or why an option value isn't one the block can use. Pure.
function rendezvous_unreadable {
  parameter opt.
  if not opt:target:istype("String") {
    return "target must be the Target Vessel's name".
  }
  if not opt:max_rel_inc:istype("Scalar") or opt:max_rel_inc <= 0 {
    return "max_rel_inc must be degrees above 0, not " + opt:max_rel_inc.
  }
  if not opt:max_phasing_orbits:istype("Scalar") or opt:max_phasing_orbits < 1 {
    return "max_phasing_orbits must be a number of orbits, 1 or more, not " + opt:max_phasing_orbits.
  }
  if opt:max_dv:istype("String") {
    if opt:max_dv <> "none" {
      return "max_dv must be m/s above 0 or 'none', not " + opt:max_dv.
    }
  } else if not opt:max_dv:istype("Scalar") or opt:max_dv <= 0 {
    return "max_dv must be m/s above 0 or 'none', not " + opt:max_dv.
  }
  return "".
}

// Degrees the target leads the craft when a Hohmann transfer from radius craft_r to target_r (m) departs: it arrives as the
// craft does, half a transfer orbit on. target_period (s), mu (m^3/s^2). Pure.
function rendezvous_lead {
  parameter craft_r, target_r, target_period, mu.
  local hohmann is 3.141592653589793 * sqrt(((craft_r + target_r) / 2) ^ 3 / mu).
  return 180 - 360 * hohmann / target_period.
}

// "transfer" or "phasing" (step 2 before step 3): seconds until the target's lead is the Hohmann transfer's, as the craft
// gains on it (or it on the craft) at the two orbits' rates; over max_phasing_orbits of the craft's orbits, a phasing
// orbit first, but only once. s: rendezvous_state's keys. Pure.
function rendezvous_transfer_or_phase {
  parameter s, max_phasing_orbits, phased.
  if phased {
    return "transfer".
  }
  local gain is 360 / s:period - 360 / s:target_period.
  local lead is rendezvous_lead(s:r, s:target_r, s:target_period, s:mu).
  local wait is 1e12.
  if gain > 0 {
    set wait to claudia_wrap_angle(s:phase - lead) / gain.
  } else if gain < 0 {
    set wait to claudia_wrap_angle(lead - s:phase) / -gain.
  }
  if wait > max_phasing_orbits * s:period {
    return "phasing".
  }
  return "transfer".
}

// The resonant phasing orbit (step 2) for ORBITBURN's resonant goal: lexicon("value", orbits, "of", of): the craft makes
// orbits laps while the target makes of, so that afterwards the target leads the craft by the Hohmann transfer's angle and
// the transfer's window opens. phase: degrees the target leads the craft at the burn; period, target_period (s); craft_r,
// target_r: the craft's radius at the burn and the target's semi-major axis (m); mu (m^3/s^2); min_period: the shortest
// period whose low point clears the body (s). Of the whole numbers of extra target laps, the one whose period is nearest
// the craft's own is cheapest. lexicon("value", 0) when no period clears the body. Pure.
function rendezvous_phasing {
  parameter phase, period, target_period, craft_r, target_r, mu, min_period, orbits.
  local frac is claudia_wrap_angle(rendezvous_lead(craft_r, target_r, target_period, mu) - phase) / 360.
  local best is lexicon("value", 0).
  local best_off is -1.
  local m0 is round(orbits * period / target_period - frac).
  for m in list(m0 - 1, m0, m0 + 1) {
    local of is m + frac.
    local p is of * target_period / orbits.
    if of > 0 and p >= min_period {
      local off is abs(p - period).
      if best_off < 0 or off < best_off {
        set best to lexicon("value", orbits, "of", of).
        set best_off to off.
      }
    }
  }
  return best.
}

// What EJECTIONBURN is asked for the transfer (step 3): the Target Vessel's orbit (elements as kOS gives them) met where
// the vessel is, the soonest burn the craft can turn to, and only a window within max_phasing_orbits orbits and one more
// (a phasing orbit's window comes at its end). One burn: no broken-plane plan. Pure.
function rendezvous_transfer_ask {
  parameter elements, now, lead, period, max_phasing_orbits.
  local ask is lexicon("mode", "rendezvous", "earliest_ut", now + lead, "latest_ut", now + lead + (max_phasing_orbits + 1) * period, "plane_change", false).
  for key in elements:keys {
    set ask[key] to elements[key].
  }
  return ask.
}

// The step the block flies next from the state s (rendezvous_state's keys), the closest approach the craft's orbit
// predicts (approach: CORRECTIONBURN's match_velocities answer there, or one with an error), and whether the transfer has
// flown: "handoff", "kill_speed", "match_plane", "match_velocities", "correction" or "transfer" (the transfer, or the
// phasing orbit when its window is too far). MechJeb's rule order. Pure.
function rendezvous_next {
  parameter s, approach, transferred, max_rel_inc.
  if s:loaded and s:rel_speed < rendezvous_handoff_speed() {
    return "handoff".
  }
  if s:loaded {
    return "kill_speed".
  }
  if s:rel_inc > max_rel_inc and not transferred {
    return "match_plane".
  }
  if approach:error = "" and approach:distance_predicted >= rendezvous_band_low_m() and approach:distance_predicted <= rendezvous_band_high_m() {
    return "match_velocities".
  }
  if transferred {
    return "correction".
  }
  return "transfer".
}

// The Course Correction's burn UT (step 4): halfway from the soonest the craft can turn to it (now plus lead) to the
// closest approach at approach_ut; -1 when there is no room for it before the approach. Pure.
function rendezvous_correction_ut {
  parameter now, lead, approach_ut.
  if approach_ut - now < 2 * lead {
    return -1.
  }
  return (now + lead + approach_ut) / 2.
}

// The block's flight, with its contacts with the game passed in so the kOS harness can fly it: state() is
// rendezvous_state's lexicon now; ask(kind, input) is the block-internal call kind ("orbit", "transfer", "correction")
// answered for the orbit now; fly(plan) flies plan's node with exec_node and returns its result; lead is the s to turn to
// a burn and lead it. name is the Target Vessel's. Returns lexicon("reason", "" or why it fails, "burns", the steps
// flown, "state", the last state read).
function rendezvous_run {
  parameter opt, name, state, ask, fly, lead.
  local done is lexicon("reason", "", "burns", list(), "dv", 0, "state", lexicon()).
  local transferred is false.
  local phased is false.
  local steps is 0.
  until false {
    local s is state().
    if s:haskey("abort") {
      set done:reason to s:abort.
      return done.
    }
    set done:state to s.
    local approach is lexicon("error", "no closest approach yet").
    if not s:loaded and (transferred or s:rel_inc <= opt:max_rel_inc) {
      set approach to ask("correction", lexicon("match_velocities", true, "target", name)).
    }
    local step is rendezvous_next(s, approach, transferred, opt:max_rel_inc).
    if step = "handoff" {
      return done.
    }
    if steps >= rendezvous_max_steps() {
      set done:reason to "no hand-off after " + steps + " steps: " + round(s:distance) + " m from " + name + " at " + round(s:rel_speed, 1) + " m/s".
      return done.
    }
    set steps to steps + 1.
    local plan is lexicon().
    if step = "kill_speed" {
      set plan to ask("correction", lexicon("match_velocities", true, "target", name, "at", s:ut + lead)).
    } else if step = "match_plane" {
      set plan to ask("orbit", lexicon("goal", "match_plane", "target", name)).
    } else if step = "match_velocities" {
      set plan to approach.
    } else if step = "correction" {
      if approach:error <> "" {
        set done:reason to approach:error.
        return done.
      }
      local when is rendezvous_correction_ut(s:ut, lead, approach:ut).
      if when < 0 {
        set done:reason to "no time for a Course Correction before the closest approach to " + name.
        return done.
      }
      set plan to ask("correction", lexicon("target_distance", rendezvous_aim_m(), "target", name, "earliest_ut", when, "latest_ut", when)).
    } else {
      set step to rendezvous_transfer_or_phase(s, opt:max_phasing_orbits, phased).
      // The phasing orbit is weighed once: after it, or after choosing the transfer, the transfer flies.
      set phased to true.
      if step = "transfer" {
        set plan to ask("transfer", rendezvous_transfer_ask(s:orbit, s:ut, lead, s:period, opt:max_phasing_orbits)).
        set transferred to true.
      } else {
        // The window is further off than max_phasing_orbits: a resonant orbit brings it to the end of its laps (step 2).
        local burn_ut is s:ut + lead.
        local phase is claudia_wrap_angle(s:phase + 360 * (burn_ut - s:ut) * (1 / s:target_period - 1 / s:period)).
        local laps is rendezvous_phasing(phase, s:period, s:target_period, s:r, s:target_r, s:mu, s:min_period, opt:max_phasing_orbits).
        if laps:value = 0 {
          set done:reason to "no phasing orbit clears " + s:body + "'s air and ground".
          return done.
        }
        set plan to ask("orbit", lexicon("goal", "resonant", "target", name, "value", laps:value, "of", laps:of, "at", burn_ut)).
      }
    }
    if plan:error <> "" {
      set done:reason to plan:error.
      return done.
    }
    if opt:max_dv:istype("Scalar") and done:dv + plan:dv > opt:max_dv {
      set done:reason to "needs " + round(done:dv + plan:dv, 1) + " m/s, over max_dv " + opt:max_dv.
      return done.
    }
    if plan:dv >= 0.05 {
      local flown is fly(plan).
      set done:dv to done:dv + flown:dv_used.
      done:burns:add(step).
      if not flown:success {
        set done:reason to flown:reason.
        return done.
      }
    }
  }
}

// The craft against the Target Vessel named name now, for rendezvous_next: loaded, distance (m), rel_speed (m/s), rel_inc
// (degrees between the planes), phase (degrees the target leads the craft round the body), period and target_period (s), r
// (the craft's semi-major axis) and target_r (the target's), mu, min_period (the shortest period from here whose low point
// is 10 km over the air or the surface), body, ut and orbit (the target's elements as EJECTIONBURN takes a target orbit,
// degrees and the mean anomaly as kOS gives them). Loading lags a frame behind the range, so a target inside it is given up
// to 5 s to load, ticking (ctx, the block's context): an abort then gives lexicon("abort", reason). The vessel is only read, and only here, never passed to a function: a call that passes another
// vessel taints the callee's parameter (ADR 0003, #654), so rv_target is a name no other code uses.
function rendezvous_state {
  parameter ctx, name.
  local rv_target is addons:claudia:targetvessel():vessel.
  local wait_until is time:seconds + 5.
  until rv_target:loaded or rv_target:distance >= 2200 or time:seconds > wait_until {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      return lexicon("abort", ticked).
    }
    wait 0.
  }
  local rc is ship:position - ship:body:position.
  local vc is ship:velocity:orbit.
  local rv_tr is rv_target:position - ship:body:position.
  local rv_tv is rv_target:velocity:orbit.
  local own is vcrs(rc, vc).
  local phase is vang(rc, rv_tr).
  if vdot(vcrs(rc, rv_tr), own) < 0 {
    set phase to 360 - phase.
  }
  local low is ship:body:radius + max(0, ship:body:atm:height) + 10000.
  local a is (rc:mag + low) / 2.
  local rv_to is rv_target:orbit.
  return lexicon("ut", time:seconds, "loaded", rv_target:loaded, "distance", (rv_tr - rc):mag, "rel_speed", (rv_tv - vc):mag,
    "rel_inc", vang(own, vcrs(rv_tr, rv_tv)), "phase", phase, "period", ship:orbit:period, "target_period", rv_to:period,
    "r", ship:orbit:semimajoraxis, "target_r", rv_to:semimajoraxis, "mu", ship:body:mu,
    "min_period", 2 * 3.141592653589793 * sqrt(a ^ 3 / ship:body:mu), "body", ship:body:name,
    "orbit", lexicon("target_sma", rv_to:semimajoraxis, "target_ecc", rv_to:eccentricity, "target_inc", rv_to:inclination, "target_lan", rv_to:lan,
      "target_argpe", rv_to:argumentofperiapsis, "target_mna", rv_to:meananomalyatepoch, "target_epoch", rv_to:epoch)).
}

function rendezvous {
  parameter options is lexicon().
  local extra is lexicon("distance_actual", -1, "relative_speed_actual", -1, "burns", list()).
  local ctx is claudia_begin("rendezvous", options, rendezvous_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is rendezvous_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  // The Mission's Target Vessel, read live (ADR 0013); a block-internal call, so it sits in the block (ADR 0007).
  local named is addons:claudia:targetvessel().
  if named:error <> "" {
    return claudia_finish(ctx, false, named:error, extra).
  }
  if opt:target <> "auto" and opt:target <> named:name {
    return claudia_finish(ctx, false, opt:target + " is not the Target Vessel " + named:name, extra).
  }
  if named:body <> ship:body:name {
    return claudia_finish(ctx, false, "the Target Vessel " + named:name + " is round " + named:body + ", not " + ship:body:name + ": transfer there first", extra).
  }
  if status <> "ORBITING" {
    return claudia_finish(ctx, false, "not in orbit: rendezvous starts from orbit round " + ship:body:name, extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:haskey("error") and turn:error <> "" {
    return claudia_finish(ctx, false, turn:error, extra).
  }
  local done is rendezvous_run(opt, named:name,
    {
      local ticked is claudia_tick(ctx, 0).
      if ticked <> "" {
        claudia_make_safe().
        return lexicon("abort", ticked).
      }
      return rendezvous_state(ctx, named:name).
    },
    {
      parameter kind, input.
      if kind = "orbit" {
        return addons:claudia:orbitburn(input).
      }
      if kind = "transfer" {
        return addons:claudia:ejectionburn(input).
      }
      return addons:claudia:correctionburn(input).
    },
    {
      parameter plan.
      add node(plan:ut, plan:radial, plan:normal, plan:prograde).
      return claudia_burn(ctx, nextnode).
    },
    turn:turn_time + 60).
  set extra:burns to done:burns.
  if done:state:haskey("distance") {
    set extra:distance_actual to round(done:state:distance).
    set extra:relative_speed_actual to round(done:state:rel_speed, 2).
  }
  return claudia_finish(ctx, done:reason = "", done:reason, extra).
}
