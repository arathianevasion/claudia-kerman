// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block dock (#456): from the rendezvous hand-off (the Target Vessel loaded, under about 2 m/s) to docked. The
// craft controls from its own port, points it along the target port's axis and translates on RCS onto that axis and in
// along it, at one speed a stretch (#711). Its guidance is Core's (DockGuidance, ADR 0014), one ADDONS:CLAUDIA:DOCKGUIDE call a
// tick: the wanted velocity, and a coast band (#706) that fires RCS only when the velocity error leaves it. kOS fires RCS
// for translation (ship:control:fore/starboard/top, set here) only while the RCS group is on, and while it is on kOS
// steering fires it for attitude too; so RCS goes on (claudia_rcs_translate) only for the ticks it translates, and in
// between RCS Use decides, which switches it off once Aligned. KSP ignores an attitude axis under 5%, so an Aligned craft
// translating fires no attitude RCS. The mod starts its bundled dock_hold on the target's kOS processor, when the target has
// a powered processor with a link to the craft or KSC (the Child Vessel link test), and stops it when the block ends,
// however it ends (ADR 0013); kOS messaging stays forbidden. Until dock_hold reports its target settled the craft holds still; a passive target is met at its port's current facing, and
// refused if it turns faster than 1 deg/s. Built on claudia_lib.ks; block-only helpers are prefixed dock_. The "//|"
// lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| dock(options): docks with the Target Vessel.
//| From rendezvous's hand-off (loaded, under 2 m/s) on RCS: lines up on the target port's axis and closes along it,
//| up to 2 m/s far out, approach_speed inside 50 m, final_speed inside 10 m. A target with a powered, linked kOS
//| processor turns its port to the craft (dock_hold); any other must turn under 1 deg/s.
//| Options:
//|   port ["auto"]: the craft's port, by kOS tag or size (size0, size1, ...).
//|   target_port ["auto"]: the target's, by tag or size; auto: the nearest free one of the port's size.
//|   approach_speed [1], final_speed [0.3]: m/s.
//|   max_lateral [0.2]: m off the axis allowed inside 10 m; further, it stops and lines up.
//|   settle_timeout [300]: s to wait for dock_hold; then a loose settle will do.
//|   max_duration [1800].
//|   plus min_ec_pct, abort_on_part_loss and rcs_tolerance as for every block.
//| Extra results: docked, port and target_port, monoprop_used.
//| Fails with no loaded Target Vessel, no RCS, too little monoprop, no free port, a passive target
//| over 1 deg/s, dock_hold unsettled by 2 x settle_timeout, drifting 50 m past the start, 15% monoprop left or a dock_hold fault (after backing off), or an abort.
//| Example: `local res is dock(lexicon()). addons:claudia:report(res).`

// The block's own options and defaults ("auto": chosen by the block).
function dock_defaults {
  return lexicon("port", "auto", "target_port", "auto", "approach_speed", 1, "final_speed", 0.3, "max_lateral", 0.2, "settle_timeout", 300).
}

// deg/s: the fastest a passive target (no dock_hold) may turn.
function dock_max_spin {
  return 1.
}

// m: how far past its start distance the craft may drift before the block gives up.
function dock_drift_m {
  return 50.
}

// "" or why an option value isn't one the block can use. Pure.
function dock_unreadable {
  parameter opt.
  for key in list("port", "target_port") {
    if not opt[key]:istype("String") or opt[key] = "" {
      return key + " must be a port's kOS tag, a size such as size1, or 'auto'".
    }
  }
  for key in list("approach_speed", "final_speed", "max_lateral", "settle_timeout") {
    if not opt[key]:istype("Scalar") or opt[key] <= 0 {
      return key + " must be above 0, not " + opt[key].
    }
  }
  if opt:final_speed > opt:approach_speed {
    return "final_speed must not be over approach_speed".
  }
  return "".
}

// Backs off from the target port at 0.5 m/s for 10 m (at most 30 s), then holds still (at most 10 s). state and push as
// for dock_run. Returns the last state.
function dock_back_off {
  parameter s, state, push.
  local d0 is s:distance.
  local t0 is s:ut.
  until s:abort <> "" or s:docked or s:hold = "docked" or s:distance >= d0 + 10 or s:ut > t0 + 30 {
    push(s, "back_off").
    set s to state().
  }
  local t1 is s:ut.
  until s:abort <> "" or s:docked or s:hold = "docked" or s:speed < 0.05 or s:ut > t1 + 10 {
    push(s, "hold").
    set s to state().
  }
  return s.
}

// The block's flight, with its contacts with the game passed in so the kOS harness can fly it. state() is the craft's port
// against the target port now: lexicon abort ("" or why the block must stop), docked, distance, along and lat (m, as
// DockGuidance), speed (relative, m/s) and its v_along, v_lat and v_side, align_deg, spin_dps (the target's turn rate),
// monoprop (units), hold and hold_reason (dock_hold's state: starting, active, near, settled, passive, fault, docked or none), hold_off_deg, ut.
// push(s, mode) flies a tick on DOCKGUIDE's answer for s in mode (close, back_off or hold). name is the Target Vessel's.
// At 15% of its starting monoprop it backs off and fails. Returns lexicon("reason", "" or why it fails, "docked",
// "monoprop", the last monoprop read undocked, "state", the last state).
function dock_run {
  parameter name, state, push.
  local s is state().
  local done is lexicon("reason", "", "docked", false, "monoprop", s:monoprop, "state", s).
  local start_d is s:distance.
  local reserve is 0.15 * s:monoprop.
  until false {
    set done:state to s.
    if s:abort <> "" {
      set done:reason to s:abort.
      return done.
    }
    // Docked (the craft's port Docked or Acquire) or dock_hold saw its port caught: success before any other rule (#711).
    if s:docked or s:hold = "docked" {
      set done:docked to true.
      return done.
    }
    set done:monoprop to s:monoprop.
    local why is "".
    if s:hold = "fault" {
      set why to "dock_hold on " + name + " stopped: " + s:hold_reason.
    } else if not list("active", "starting", "near", "settled"):contains(s:hold) and s:spin_dps > dock_max_spin() {
      set why to name + " turns at " + round(s:spin_dps, 1) + " deg/s, over " + dock_max_spin() + " deg/s, with no dock_hold (" + s:hold_reason + ")".
    } else if s:monoprop <= reserve {
      set why to "monopropellant down to " + round(s:monoprop, 1) + " units, 15% of the start's, " + round(s:distance, 1) + " m from " + name + "'s port".
    } else if s:distance > start_d + dock_drift_m() {
      set why to "drifted to " + round(s:distance) + " m from " + name + "'s port, " + dock_drift_m() + " m past the start's " + round(start_d) + " m".
    }
    if why <> "" {
      // A fault, low monoprop, or a refusal with the craft close in, backs off before the block fails.
      if s:hold = "fault" or s:distance < 50 or s:monoprop <= reserve {
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
    push(s, "close").
    set s to state().
  }
}

// dock_run's state when the block stops reading the target: an abort (reason) or docked. Pure.
function dock_stopped {
  parameter reason, docked.
  return lexicon("abort", reason, "docked", docked, "distance", 0, "along", 0, "lat", 0, "speed", 0, "align_deg", 0, "spin_dps", 0,
    "monoprop", 0, "hold", "none", "hold_reason", "", "hold_off_deg", 0, "ut", 0).
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
  // The target is read inline, never passed to a function: a call that passes another vessel taints the callee's parameter
  // (ADR 0003, #654), so every name holding it or its parts starts dk_ and no other code uses one.
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
  // The RCS delta-v on board against the distance (DockGuidance's estimate), before anything moves.
  local fuel is addons:claudia:dockfuel(lexicon("distance", dk_best, "speed", (ship:velocity:orbit - dk_target:velocity:orbit):mag)).
  if fuel:error <> "" {
    return claudia_finish(ctx, false, fuel:error, extra).
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
  local dk_side is v(0, 0, 0).
  local trace_ut is -1.
  local firing is false.
  local dk_since is -1.
  local dk_gave is "".
  lock steering to claudia_keep_roll(-dk_axis).
  local done is dock_run(named:name,
    {
      local ticked is claudia_tick(ctx, 0).
      if ticked <> "" {
        return dock_stopped(ticked, false).
      }
      if dk_gave <> "" {
        return dock_stopped(dk_gave, false).
      }
      if own:state:startswith("Docked") or own:state:startswith("Acquire") {
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
      set dk_off to dk_off:normalized.
      set dk_side to vcrs(dk_axis, dk_off).
      local vel is ship:velocity:orbit - dk_target:velocity:orbit.
      local mono is claudia_monoprop():amount.
      if ctx:rcs_dry {
        set mono to 0.
      }
      return lexicon("abort", "", "docked", false, "distance", rel:mag, "along", along, "lat", lat, "speed", vel:mag,
        "v_along", vdot(vel, dk_axis), "v_lat", vdot(vel, dk_off), "v_side", vdot(vel, dk_side),
        "align_deg", vang(ship:facing:forevector, -dk_axis), "spin_dps", dk_target:angularvel:mag * constant:radtodeg,
        "monoprop", mono, "hold", held:state, "hold_reason", held:reason, "hold_off_deg", vang(dk_axis, rel), "ut", time:seconds).
    },
    {
      parameter s, mode.
      local g is addons:claudia:dockguide(lexicon("mode", mode, "hold", s:hold, "along", s:along, "lat", s:lat, "distance", s:distance, "align_deg", s:align_deg,
        "v_along", s:v_along, "v_lat", s:v_lat, "v_side", s:v_side, "firing", firing,
        "approach_speed", opt:approach_speed, "final_speed", opt:final_speed, "max_lateral", opt:max_lateral,
        "ut", s:ut, "wait_since", dk_since, "settle_timeout", opt:settle_timeout, "target", named:name, "hold_off_deg", s:hold_off_deg, "spin_dps", s:spin_dps)).
      set firing to g:fire.
      set dk_since to g:wait_since.
      set dk_gave to g:wait_error.
      if g:wait_end <> "" {
        addons:claudia:blocknote("dock", lexicon("note", "dock waited " + round(g:wait_s) + " s for " + named:name + "'s dock_hold (" + g:wait_end + ")")).
      }
      if firing <> ctx:rcs_translating {
        claudia_rcs_translate(ctx, firing).
      }
      if firing {
        local cmd is dk_axis * g:cmd_along + dk_off * g:cmd_lat + dk_side * g:cmd_side.
        set ship:control:fore to max(-1, min(1, vdot(cmd, ship:facing:forevector))).
        set ship:control:starboard to max(-1, min(1, vdot(cmd, ship:facing:starvector))).
        set ship:control:top to max(-1, min(1, vdot(cmd, ship:facing:topvector))).
      } else {
        set ship:control:neutralize to true.
      }
      // The dock trace (#706): about once a second, as exec_node's.
      if time:seconds >= trace_ut + 1 {
        set trace_ut to time:seconds.
        addons:claudia:blocknote("dock", lexicon("mode", mode, "distance", round(s:distance, 2), "along", round(s:along, 2), "lat", round(s:lat, 2),
          "align_deg", round(s:align_deg, 1), "speed", round(s:speed, 3), "dv_error", round(g:dv_error, 3), "translating", firing, "monoprop", round(s:monoprop, 1), "hold", s:hold, "wait_s", round(g:wait_s))).
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
