# A weak engine above a strong one (#842, ADR 0020)

`reentry` lands on the highest stage whose fuelled engines can stop the fall: thrust at the body's surface of at least 1.5 times the weight of the craft as kept (`EntryFinish.Choose`). With none able it lands on the stage with the most thrust. All of the chosen stage's engines light; the others stay spares and light on a flameout.

1. **Run `reentry_duna_weak_top`** (`tests/blocks/`, run as in `deorbit_kerbin/test.cfg`). Its craft is `reentry_duna_powered`'s with a 2 kN LV-1R Spider on the FL-T800 in stage 3, above the LV-T45 in stage 2. **The craft file was edited by hand and has never been loaded: if KSP rejects it, or the Spider floats off the tank, say so on #842 and fix the craft, not the test.** Also check that `deorbit` (`stage_limit` 2, as in `reentry_duna_powered`) still burns on the LV-T45 with the Spider above it; if it fires the Spider first and the burn fails, say so on #842.
2. **The log.** `KSP.log`'s `ENTRYFINISH` line reads `engines, keeping everything: ... on LV-T45 ... (stage 2, the highest that can stop the fall: stage 3 gives 2 kN at the surface, needing <n> kN)`.
3. **It passes:** `touchdown_speed` under 6 and `powered_dv` over 20, with the chute never armed. Before the fix it would have lit the Spider alone and hit the ground with the LV-T45 unlit.
4. **The Spider is a spare.** It stays dark through the braking burn unless the LV-T45 flames out.
