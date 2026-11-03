// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block dock (#456): from the rendezvous hand-off (the Target Vessel loaded, under about 2 m/s) to docked. The
// craft controls from its own port, points it along the target port's axis and translates on RCS (claudia_rcs_translate
// holds RCS on; the block drives ship:control:fore/starboard/top itself, as exec_node_rcs_trim does) onto that axis and in
// along it, at a speed by distance. The mod starts its bundled dock_hold on the target's kOS processor, when the target has
// a powered processor with a link to the craft or KSC (the Child Vessel link test), and stops it when the block ends,
// however it ends (ADR 0013); kOS messaging stays forbidden. A passive target is met at its port's current facing, and
// refused if it turns faster than 1 deg/s. Built on claudia_lib.ks; block-only helpers are prefixed dock_. The "//|"
// lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| dock(options): docks with the Target Vessel.
//| From rendezvous's hand-off (loaded, under 2 m/s) on RCS: lines up on the target port's axis and closes along it,
//| up to 5 m/s far out, approach_speed inside 50 m, final_speed inside 10 m. A target with a powered, linked kOS
//| processor turns its port to the craft (the mod's dock_hold); any other must turn under 1 deg/s.
//| Options:
//|   port ["auto"]: the craft's port, by kOS tag or size (size0, size1, ...).
//|   target_port ["auto"]: the target's, by tag or size; auto: the nearest free one of the craft's port's size.
//|   approach_speed [1], final_speed [0.3]: m/s.
//|   max_lateral [0.2]: m off the axis allowed inside 10 m; further, the craft stops and lines up.
//|   max_duration [1800].
//|   plus min_ec_pct, abort_on_part_loss and rcs_tolerance as for every block.
//| Extra results: docked, port and target_port (tag or size), monoprop_used (units).
//| Fails with no loaded Target Vessel, no RCS or monoprop, no free port to match, a passive target turning over 1 deg/s,
//| drifting 50 m past the start distance, a dock_hold fault (after backing off), or an abort.
//| Example: `local res is dock(lexicon()). addons:claudia:report(res).`

// The block's own options and defaults ("auto": chosen by the block).
function dock_defaults {
  return lexicon("port", "auto", "target_port", "auto", "approach_speed", 1, "final_speed", 0.3, "max_lateral", 0.2).
}

// deg/s: the fastest a passive target (no dock_hold) may turn.
function dock_max_spin {
  return 1.
}

// m: how far past its start distance the craft may drift before the block gives up.
function dock_drift_m {
  return 50.
}

// m: off the axis the craft keeps when it starts behind the target port's face, to pass the target's side.
function dock_clear_m {
  return 50.
}

// degrees off the target port's axis the craft's port may point inside 20 m and still close.
function dock_align_deg {
  return 5.
}

// "" or why an option value isn't one the block can use. Pure.
function dock_unreadable {
  parameter opt.
  for key in list("port", "target_port") {
    if not opt[key]:istype("String") or opt[key] = "" {
      return key + " must be a port's kOS tag, a size such as size1, or 'auto'".
    }
  }
  for key in list("approach_speed", "final_speed", "max_lateral") {
    if not opt[key]:istype("Scalar") or opt[key] <= 0 {
      return key + " must be above 0, not " + opt[key].
    }
  }
  if opt:final_speed > opt:approach_speed {
    return "final_speed must not be over approach_speed".
  }
  return "".
}

// m/s: the closing speed at distance d (m): up to 5 m/s beyond 100 m, approach_speed from 100 to 50 m, tapering to
// final_speed at 10 m and holding it inside. Pure.
function dock_speed {
  parameter d, opt.
  if d > 100 {
    return min(5, max(opt:approach_speed, opt:approach_speed * d / 100)).
  }
  if d > 50 {
    return opt:approach_speed.
  }
  if d > 10 {
    return opt:final_speed + (opt:approach_speed - opt:final_speed) * (d - 10) / 40.
  }
  return opt:final_speed.
}

// m: how far off the axis the craft may be at along (m out from the target port's face) and still close: max_lateral
// inside 10 m, widening by 5 cm a metre beyond. Pure.
function dock_cone {
  parameter along, opt.
  return opt:max_lateral + 0.05 * max(0, along - 10).
}

// The relative velocity the craft wants now, in the target port's frame: lexicon("along", m/s out along the port's axis
// (negative closes), "lat", m/s away from the axis (negative toward it)). s: dock_run's state (along, lat, distance,
// align_deg). Over a metre behind the port's face it first moves dock_clear_m off the axis and then out past the face; off the cone it
// holds its distance along (backing out inside 2 m) and moves onto the axis; on the cone it closes at dock_speed, except
// inside 20 m while its port points over dock_align_deg off the axis. Pure.
function dock_want {
  parameter s, opt.
  local speed is dock_speed(s:distance, opt).
  if s:along < -1 {
    if s:lat < dock_clear_m() {
      return lexicon("along", 0, "lat", min(speed, 2)).
    }
    return lexicon("along", min(speed, 2), "lat", 0).
  }
  local in is -min(speed, 0.2 * s:lat).
  if s:lat > dock_cone(s:along, opt) {
    if s:along < 2 {
      return lexicon("along", opt:final_speed, "lat", in).
    }
    return lexicon("along", 0, "lat", in).
  }
  if s:distance < 20 and s:align_deg > dock_align_deg() {
    return lexicon("along", 0, "lat", in).
  }
  return lexicon("along", -speed, "lat", in).
}

// Backs off from the target port at 0.5 m/s for 10 m (at most 30 s), then holds still (at most 10 s). state and push as
// for dock_run. Returns the last state.
function dock_back_off {
  parameter s, state, push.
  local d0 is s:distance.
  local t0 is s:ut.
  until s:abort <> "" or s:docked or s:distance >= d0 + 10 or s:ut > t0 + 30 {
    local d is max(s:distance, 0.01).
    push(0.5 * s:along / d, 0.5 * s:lat / d).
    set s to state().
  }
  local t1 is s:ut.
  until s:abort <> "" or s:docked or s:speed < 0.05 or s:ut > t1 + 10 {
    push(0, 0).
    set s to state().
  }
  return s.
}

// The block's flight, with its contacts with the game passed in so the kOS harness can fly it. state() is the craft's port
// against the target port now: lexicon abort ("" or why the block must stop), docked, distance, along and lat (m, as
// dock_want), speed (relative, m/s), align_deg, spin_dps (the target's turn rate), monoprop (units), hold and hold_reason
// (dock_hold's state: starting, active, passive, fault, docked or none), ut. push(along, lat) steers the relative velocity
// toward that wanted for a tick. name is the Target Vessel's. Returns lexicon("reason", "" or why it fails, "docked",
// "monoprop", the last monoprop read undocked, "state", the last state).
function dock_run {
  parameter opt, name, state, push.
  local s is state().
  local done is lexicon("reason", "", "docked", false, "monoprop", s:monoprop, "state", s).
  local start_d is s:distance.
  until false {
    set done:state to s.
    if s:abort <> "" {
      set done:reason to s:abort.
      return done.
    }
    if s:docked {
      set done:docked to true.
      return done.
    }
    set done:monoprop to s:monoprop.
    local why is "".
    if s:hold = "fault" {
      set why to "dock_hold on " + name + " stopped: " + s:hold_reason.
    } else if s:hold <> "active" and s:hold <> "starting" and s:spin_dps > dock_max_spin() {
      set why to name + " turns at " + round(s:spin_dps, 1) + " deg/s, over " + dock_max_spin() + " deg/s, with no dock_hold (" + s:hold_reason + ")".
    } else if s:monoprop <= 0 {
      set done:reason to "out of monopropellant " + round(s:distance, 1) + " m from " + name + "'s port".
      return done.
    } else if s:distance > start_d + dock_drift_m() {
      set why to "drifted to " + round(s:distance) + " m from " + name + "'s port, " + dock_drift_m() + " m past the start's " + round(start_d) + " m".
    }
    if why <> "" {
      // A fault, or a refusal with the craft close in, backs off before the block fails.
      if s:hold = "fault" or s:distance < 50 {
        set s to dock_back_off(s, state, push).
        set done:state to s.
        if s:abort <> "" {
          set done:reason to s:abort.
          return done.
        }
        set why to why + "; backed off to " + round(s:distance) + " m".
      }
      set done:reason to why.
      return done.
    }
    local want is dock_want(s, opt).
    push(want:along, want:lat).
    set s to state().
  }
}

// dock_run's state when the block stops reading the target: an abort (reason) or docked. Pure.
function dock_stopped {
  parameter reason, docked.
  return lexicon("abort", reason, "docked", docked, "distance", 0, "along", 0, "lat", 0, "speed", 0, "align_deg", 0, "spin_dps", 0,
    "monoprop", 0, "hold", "none", "hold_reason", "", "ut", 0).
}

// The craft's ports option port allows (a kOS tag, a size, or "auto"): its free (Ready) docking ports. Returns
// lexicon("error", "" or why none, "ports", the list); dock pairs them with the target's by size.
function dock_own_ports {
  parameter which.
  local found is list().
  for p in ship:dockingports {
    if p:state = "Ready" and (which = "auto" or p:tag = which or p:nodetype = which) {
      found:add(p).
    }
  }
  if found:length > 0 {
    return lexicon("error", "", "ports", found).
  }
  if which = "auto" {
    return lexicon("error", "the craft has no free docking port").
  }
  return lexicon("error", "the craft has no free docking port tagged or sized " + which).
}

// A port's name in the result: its kOS tag, or its size.
function dock_port_name {
  parameter tag, size.
  if tag <> "" {
    return tag.
  }
  return size.
}

function dock {
  parameter options is lexicon().
  local extra is lexicon("docked", false, "port", "", "target_port", "", "monoprop_used", 0).
  local ctx is claudia_begin("dock", options, dock_defaults(), 1800).
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is dock_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  // The Mission's Target Vessel, read live (ADR 0013); a block-internal call, so it sits in the block (ADR 0007).
  local named is addons:claudia:targetvessel().
  if named:error <> "" {
    return claudia_finish(ctx, false, named:error, extra).
  }
  if ship:modulesnamed("ModuleRCSFX"):length + ship:modulesnamed("ModuleRCS"):length = 0 {
    return claudia_finish(ctx, false, "the craft has no RCS", extra).
  }
  local mono0 is claudia_monoprop():amount.
  if mono0 <= 0 {
    return claudia_finish(ctx, false, "the craft has no monopropellant", extra).
  }
  local mine is dock_own_ports(opt:port).
  if mine:error <> "" {
    return claudia_finish(ctx, false, mine:error, extra).
  }
  // The target is read inline, never passed to a function: the Script Check follows another vessel into every function it is
  // passed to (ADR 0003), so every name holding it or its parts starts dk_ and no other code uses one.
  local dk_target is named:vessel.
  if not dk_target:loaded {
    return claudia_finish(ctx, false, named:name + " is not loaded (" + round(dk_target:distance) + " m): fly rendezvous first", extra).
  }
  // The nearest pair of a craft port and a free target port the options allow, of the same size.
  local own is 0.
  local dk_port is 0.
  local dk_found is false.
  local dk_best is 1e12.
  for dk_p in dk_target:dockingports {
    if dk_p:state = "Ready" and (opt:target_port = "auto" or dk_p:tag = opt:target_port or dk_p:nodetype = opt:target_port) {
      for c in mine:ports {
        local dk_d is (dk_p:nodeposition - c:nodeposition):mag.
        if dk_p:nodetype = c:nodetype and dk_d < dk_best {
          set own to c.
          set dk_port to dk_p.
          set dk_found to true.
          set dk_best to dk_d.
        }
      }
    }
  }
  if not dk_found {
    local sizes is list().
    for c in mine:ports {
      if not sizes:contains(c:nodetype) {
        sizes:add(c:nodetype).
      }
    }
    local wanted is "".
    if opt:target_port <> "auto" {
      set wanted to opt:target_port + " ".
    }
    return claudia_finish(ctx, false, named:name + " has no free " + wanted + "docking port of the craft's size (" + sizes:join(", ") + ")", extra).
  }
  set extra:port to dock_port_name(own:tag, own:nodetype).
  set extra:target_port to dock_port_name("" + dk_port:tag, "" + dk_port:nodetype).
  // The mod starts dock_hold on the target's processor, or says why not (passive); it stops it when this block ends.
  local hold is addons:claudia:dockhold(lexicon("port", "" + dk_port:uid, "toward", own:uid)).
  if hold:error <> "" {
    return claudia_finish(ctx, false, hold:error, extra).
  }
  local was is ship:controlpart.
  own:controlfrom().
  local dk_axis is dk_port:portfacing:forevector.
  local dk_off is v(0, 0, 0).
  // The last state read, for the dock trace (#706): about once a second, as exec_node's.
  local seen is dock_stopped("", false).
  local trace_ut is -1.
  lock steering to claudia_keep_roll(-dk_axis).
  claudia_rcs_translate(ctx, true).
  local done is dock_run(opt, named:name,
    {
      local ticked is claudia_tick(ctx, 0).
      if ticked <> "" {
        return dock_stopped(ticked, false).
      }
      if own:state:startswith("Docked") {
        return dock_stopped("", true).
      }
      local held is addons:claudia:dockholdstate().
      set dk_axis to dk_port:portfacing:forevector.
      local rel is own:nodeposition - dk_port:nodeposition.
      local along is vdot(rel, dk_axis).
      set dk_off to vxcl(dk_axis, rel).
      local lat is dk_off:mag.
      if lat < 0.01 {
        set dk_off to vxcl(dk_axis, ship:facing:starvector).
      }
      local vel is ship:velocity:orbit - dk_target:velocity:orbit.
      local mono is claudia_monoprop():amount.
      if ctx:rcs_dry {
        set mono to 0.
      }
      set seen to lexicon("abort", "", "docked", false, "distance", rel:mag, "along", along, "lat", lat, "speed", vel:mag,
        "align_deg", vang(ship:facing:forevector, -dk_axis), "spin_dps", dk_target:angularvel:mag * constant:radtodeg,
        "monoprop", mono, "hold", held:state, "hold_reason", held:reason, "ut", time:seconds).
      return seen.
    },
    {
      parameter along, lat.
      // Magnetic capture: the ports pull themselves together, so the block stops pushing.
      local translating is not own:state:startswith("Acquire").
      if not translating {
        set ship:control:neutralize to true.
      } else {
        local err is dk_axis * along + dk_off:normalized * lat - (ship:velocity:orbit - dk_target:velocity:orbit).
        set ship:control:fore to max(-1, min(1, vdot(err, ship:facing:forevector) / 0.2)).
        set ship:control:starboard to max(-1, min(1, vdot(err, ship:facing:starvector) / 0.2)).
        set ship:control:top to max(-1, min(1, vdot(err, ship:facing:topvector) / 0.2)).
      }
      if time:seconds >= trace_ut + 1 {
        set trace_ut to time:seconds.
        addons:claudia:blocknote("dock", lexicon("distance", round(seen:distance, 2), "along", round(seen:along, 2), "lat", round(seen:lat, 2),
          "align_deg", round(seen:align_deg, 1), "speed", round(seen:speed, 3), "rcs", translating, "monoprop", round(seen:monoprop, 1), "hold", seen:hold)).
      }
      wait 0.
    }).
  set ship:control:neutralize to true.
  claudia_rcs_translate(ctx, false).
  unlock steering.
  if not done:docked {
    was:controlfrom().
  }
  if done:reason:startswith("abort") {
    claudia_make_safe().
  }
  set extra:docked to done:docked.
  set extra:monoprop_used to round(max(0, mono0 - done:monoprop), 1).
  return claudia_finish(ctx, done:docked, done:reason, extra).
}
