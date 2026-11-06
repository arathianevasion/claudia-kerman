// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block transfer_to, the spec's `transfer` (kOS has a built-in TRANSFER, for moving resources): flies from orbit to an
// encounter. Its flight is the mod's (ADDONS:CLAUDIA:TRANSFERSTEP, #459, ADR 0014): each step plans a node, a wait or the end; the block only
// flies the nodes with exec_node, warps and reports. Block-only helpers are prefixed transfer_to_. The "//|" lines are Claudia's docs.

@lazyglobal off.

//| transfer_to(options): flies from orbit to an encounter with a moon of the body the craft orbits (the
//| Mun from Kerbin) or a body sharing its parent (Duna from Kerbin): the mod's solver finds the ejection,
//| exec_node flies it, and corrections keep the encounter (far out of plane, a plane change at its
//| planned time); a planet's or sibling's pe is trimmed in its SOI. It ends before arrival. (kOS's TRANSFER moves resources.)
//| Options:
//|   target [required]: the body to reach, a name ("Mun") or a body (body("Minmus")).
//|   target_pe [required]: periapsis altitude at the target, m.
//|   plane ["auto"]: arrival plane: a number (inclination; over 90 retrograde), body or vessel, or "none". auto: a Target Vessel orbiting target (at its pe), else the next leg's site or moon.
//|   window ["next"]: "next" departs in the next transfer window once the craft can have turned to
//|     the burn; a time (UT seconds): the window from then (a planet's: round it). It warps
//|     there on rails: no Due Time needed.
//|   correction [true]: false leaves out the mid-course correction.
//|   checkpoint ["off"]: the mod's own, for a coast it resumes.
//|   pe_tolerance [1000]: m off target_pe allowed; past it, a finer correction, then a fail.
//|   max_duration ["auto"]: the time to the planned arrival plus 900 s; a shorter one moves the correction sooner.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: encounter_ut (UT the craft enters the target's SOI),
//| target_pe_actual (m, the periapsis predicted at the target), correction_dv (m/s the corrections
//| burned, restoring a lost encounter too; recovered: why it was lost); plane_dv (m/s the plane change burned).
//| departure_ut, arrival_ut (UT of ejection, periapsis), v_inf (m/s).
//| ejection_node_dv, ejection_dv_used (m/s the node asked for, the burn used) and ejection_burn_time
//| (s the engines burned): a long burn's dv_used is ~2% over. All -1 if it failed before the ejection burn or resumed.
//| ejection_wait (s from the block's start to the ejection node): -1 if it failed before the node was planned.
//| Fails with "target is required", "target_pe is required", an option value it can't read, "no body
//| named <x>", a target that doesn't orbit the craft's body or parent, "not in orbit", "a maneuver node is
//| already planned: remove it first", "found no transfer: <why>", "found no correction: <why>", "no encounter with
//| <target> after the ejection burn, and no correction fits: <why>" (or "the correction failed:
//| <why>"; or "after the correction", "the plane change"), "after the correction, the periapsis ... off
//| target_pe", an exec_node failure (its node stays), or an abort. The craft must be the active vessel.
//| Capture isn't part of transfer_to: fly `capture()`: in this sequence or the next, after
//| `coast(lexicon("until", res:encounter_ut + 60))` (no Due Time needed).
//| Example: `local res is transfer_to(lexicon("target", "Mun", "target_pe", 30000)). addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function transfer_to_defaults {
  return lexicon("target", "required", "target_pe", "required", "plane", "auto", "window", "next", "correction", true, "pe_tolerance", 1000, "checkpoint", "off").
}

// "" or why an option value isn't one the block can use.
function transfer_to_unreadable {
  parameter opt.
  if opt:target:istype("String") {
    if opt:target = "required" {
      return "target is required".
    }
  } else if not opt:target:istype("Body") {
    return "target must be a body or its name".
  }
  if opt:target_pe:istype("String") {
    if opt:target_pe = "required" {
      return "target_pe is required".
    }
  }
  local pe is claudia_pe_unreadable(opt:target_pe).
  if pe <> "" {
    return pe.
  }
  local plane is claudia_plane_unreadable(opt:plane).
  if plane <> "" {
    return plane.
  }
  local window is claudia_window_unreadable(opt:window).
  if window <> "" {
    return window.
  }
  if not opt:pe_tolerance:istype("Scalar") or opt:pe_tolerance <= 0 {
    return "pe_tolerance must be metres above 0, not " + opt:pe_tolerance.
  }
  return claudia_correction_unreadable(opt:correction).
}

// The flight: the mod's steps from state (TRANSFERSTEP's ask) until one ends it, each folded into extra and opt. ask(state) gives the next step (a throw
// gives only its error, #439); fly(step) gives exec_node's result; wait(step) and tick() give "" or why the block must stop. Returns "" or why it fails.
function transfer_to_run {
  parameter state, opt, extra, ask, fly, wait, tick.
  local first is true.
  until false {
    if not first {
      local ticked is tick().
      if ticked <> "" {
        return ticked.
      }
    }
    set first to false.
    local step is ask(state).
    if not step:haskey("do") {
      return step:error.
    }
    for key in step:report:keys {
      set extra[key] to step:report[key].
    }
    if step:error <> "" or step:do = "done" {
      return step:error.
    }
    for key in step:carry:keys {
      set state[key] to step:carry[key].
    }
    if step:max_duration >= 0 {
      set opt:max_duration to step:max_duration.
      set state:max_duration to step:max_duration.
    }
    if step:do = "wait" {
      local waited is wait(step).
      if waited <> "" {
        return waited.
      }
    } else {
      local burned is fly(step).
      set state:last_dv to burned:dv_used.
      if step:kind = "ejection" {
        set extra:ejection_node_dv to burned:node_dv.
        set extra:ejection_dv_used to round(burned:dv_used, 1).
        set extra:ejection_burn_time to burned:burn_time.
      } else {
        set extra[step:kind + "_dv"] to round(extra[step:kind + "_dv"] + burned:dv_used, 2).
      }
      if not burned:success {
        if not step:keep_going {
          return step:fail_prefix + burned:reason.
        }
        set state:why to burned:reason.
      }
    }
  }
}

function transfer_to {
  parameter options is lexicon().
  local extra is lexicon("encounter_ut", -1, "target_pe_actual", -1, "correction_dv", 0, "plane_dv", 0, "ejection_wait", -1,
    "ejection_node_dv", -1, "ejection_dv_used", -1, "ejection_burn_time", -1, "departure_ut", -1, "arrival_ut", -1, "v_inf", -1).
  local ctx is claudia_begin("transfer_to", options, transfer_to_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is transfer_to_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }

  local dest is opt:target.
  if dest:istype("String") {
    if not bodyexists(dest) {
      return claudia_finish(ctx, false, "no body named " + dest, extra).
    }
    set dest to body(dest).
  }
  if status <> "ORBITING" and not opt:checkpoint:startswith("resume") {
    return claudia_finish(ctx, false, "not in orbit: transfer_to starts from orbit around " + ship:body:name, extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }

  // The steps are the mod's, block-internal calls (ADR 0007, ADR 0014). The earliest departure leaves time to turn to the burn (ADR 0004).
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:error <> "" {
    return claudia_finish(ctx, false, turn:error, extra).
  }
  local state is lexicon("target", dest:name, "target_pe", opt:target_pe, "window", opt:window, "correction", opt:correction,
    "plane", claudia_plane_ask(opt:plane), "pe_tolerance", opt:pe_tolerance, "start_ut", ctx:start_ut, "max_duration", opt:max_duration,
    "auto", opt:max_duration:istype("String"), "turn_time", turn:turn_time, "phase", "start", "checkpoint", opt:checkpoint).
  local reason is transfer_to_run(state, opt, extra, {
    parameter s.
    set s:dv_left to claudia_dv_remaining().
    return addons:claudia:transferstep(s).
  }, {
    parameter step.
    add node(step:ut, step:radial, step:normal, step:prograde).
    return claudia_burn(ctx, nextnode, step:max_residual).
  }, {
    parameter step.
    // The mod's Encounter Watch (#770) may end the wait early; the next step then plans the restore.
    addons:claudia:encounterwatch(step:watch).
    local waited is claudia_warp_to(ctx, step:ut, 30, { return addons:claudia:encounterwatchdropped. }).
    set state:watch_dropped to addons:claudia:encounterwatchdropped.
    return waited.
  }, {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
    }
    return ticked.
  }).
  return claudia_finish(ctx, reason = "", reason, extra).
}
