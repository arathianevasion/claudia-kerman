# Crew death reaches Core (#874)

1. Fly a crewed Mission with Claudia and one other Kerbal aboard, on a landing step (`return`, `reentry`, `deorbit` or `land`), with no precision goal.
2. Send the second Kerbal out on EVA before the run ends, then land with Claudia. Expect the Failed Debrief, if Claudia writes one, to be sent back once (the `debrief_retry` event), as no Kerbal died.
3. Repeat, but have the second Kerbal killed in flight (crash a part with them aboard, or kill them with the debug menu). Expect the Failed Debrief to be kept with no send-back, and `crewDeaths` in the Mission's save to name them with a UT.
4. Kill Claudia herself: the Mission is Lost as before, and `crewDeaths` does not name her.
