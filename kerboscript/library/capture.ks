// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block capture (#788): the arrival burn, any leftover tilt, and a Target Vessel's orbit. Its flight is the mod's
// (ADDONS:CLAUDIA:CAPTURESTEP, ADR 0014); the block only flies the nodes with exec_node and reports, on transfer_to's loop. The "//|" lines are Claudia's docs.

@lazyglobal off.

//| capture(options): arrival burn in a new SOI: a circle in transfer_to's plane.
//| Options:
//|   alt ["auto"]: m: a Target Vessel's pe if it orbits here, else the pe arrived at, at least low orbit.
//|   plane ["auto"]: as transfer_to's; auto: the plane it aimed for.
//|   max_plane_dv ["auto"]: m/s a tilt may add (larger of 50, 5% of the burn). Over it: captures as it is and reports, no failure.
//| Extra results: capture_dv, plane_dv, capture_method, plane_leftover_deg, plane_leftover_dv, ecc, inc.
//| Fails with a pe in the air or past, or a change of SOI first (coast).

// The block's own options and defaults.
function capture_defaults {
  return lexicon("alt", "auto", "plane", "auto", "max_plane_dv", "auto").
}

function capture {
  parameter options is lexicon().
  local extra is lexicon("capture_dv", 0, "plane_dv", 0, "capture_method", "", "plane_leftover_deg", 0, "plane_leftover_dv", 0, "ecc", -1, "inc", -1).
  local ctx is claudia_begin("capture", options, capture_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is claudia_plane_unreadable(opt:plane).
  if unreadable = "" and hasnode {
    set unreadable to "a maneuver node is already planned: remove it first".
  }
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if unreadable = "" {
    set unreadable to turn:error.
  }
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local state is lexicon("alt", opt:alt, "plane", claudia_plane_ask(opt:plane), "max_plane_dv", opt:max_plane_dv, "turn_time", turn:turn_time, "phase", "start").
  local reason is transfer_to_run(state, opt, extra, {
    parameter s.
    return addons:claudia:capturestep(s).
  }, {
    parameter step.
    add node(step:ut, step:radial, step:normal, step:prograde).
    // A burn to a circle is judged by how round it leaves the orbit: the mod asks that with a max_residual no node reaches.
    return claudia_burn(ctx, nextnode, step:max_residual, step:max_residual >= 1000000).
  }, {
    parameter step.
    return "".
  }, {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
    }
    return ticked.
  }).
  return claudia_finish(ctx, reason = "", reason, extra).
}
