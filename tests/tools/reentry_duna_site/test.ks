// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// A Duna landing at a site from find_landing_site (tests/tools, #828): the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). The reference craft arrives in an 80 km circular equatorial orbit with no
// stage fired. find_landing_site, through the test-only bridge, gives a daylight site near the equator; deorbit burns for it keeping
// the tank and engine; reentry brings the craft down. The Script Result is reentry's, with the site and the miss from it in km.

@lazyglobal off.

wait until ship:unpacked.

// Great-circle km between two lat/lng pairs on the body the craft is on.
function test_km {
  parameter lat1, lng1, lat2, lng2.
  local h is sin((lat2 - lat1) / 2) ^ 2 + cos(lat1) * cos(lat2) * sin((lng2 - lng1) / 2) ^ 2.
  return 2 * arcsin(min(1, sqrt(h))) * constant:pi / 180 * body:radius / 1000.
}

local found is addons:claudia:planningtool("find_landing_site", lexicon("body", "Duna", "daylight", true, "near", lexicon("latitude", 0, "longitude", 0))).
local result is lexicon("success", false, "reason", "find_landing_site found no site", "sites", 0, "site_sun_deg", -90, "burn_ut", -1, "landed_or_splashed", false, "sun_elevation_deg", -90, "miss_km", 9999).
if found:haskey("error") {
  set result:reason to "find_landing_site refused: " + found:error.
} else if found:sites:length > 0 {
  local site is found:sites[0].
  local aim is lexicon("lat", site:latitude, "lng", site:longitude).
  print "site test: " + site:latitude + ", " + site:longitude + ", sun " + site:sun_elevation_deg + " at UT " + site:landing_ut.
  local burn is deorbit(lexicon("target", aim, "stage_limit", 2, "max_wait", site:max_wait_s)).
  print "site test: deorbit " + burn:success + ", " + burn:reason.
  local sites is found:sites:length.
  set result to burn.
  set result["sites"] to sites.
  set result["site_sun_deg"] to site:sun_elevation_deg.
  set result["landed_or_splashed"] to false.
  set result["miss_km"] to 9999.
  set result["sun_elevation_deg"] to -90.
  if burn:burn_ut > 0 {
    local down is reentry().
    set result["reentry_success"] to down:success.
    set result["reentry_reason"] to down:reason.
    set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
    if down:haskey("sun_elevation_deg") {
      set result["sun_elevation_deg"] to down:sun_elevation_deg.
    }
    if down:haskey("geo") and down:geo:haskey("lat") {
      set result["miss_km"] to round(test_km(site:latitude, site:longitude, down:geo:lat, down:geo:lng), 2).
    }
    print "site test: came down " + result:miss_km + " km from the site, sun " + result:sun_elevation_deg + " degrees up".
  }
}
addons:claudia:report(result).
