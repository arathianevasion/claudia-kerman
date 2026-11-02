# tune_pe succeeding on a short burn

`tune_pe` judges its burn by the periapsis it leaves, against its own `tolerance` (1000 m by default). Unlike `exec_node` and `circularize` since #577, it does not report `success` with `finished_short` when the periapsis lands near, but outside, that tolerance.

## Why this is out of scope

A periapsis is often a safety number. Examples are a reentry corridor at Kerbin, the periapsis over a moon before capture, and a periapsis kept clear of terrain. A burn a few percent short of a 100 km change can leave the periapsis kilometres off, and that miss can be the difference between aerobraking and skipping out, or between orbit and terrain. Reporting it as a success hides the case that matters most.

The block already has a knob for a target that needn't be exact. Claudia sets `tolerance` wider in the Sequence Plan. A short burn then succeeds on that plan's terms, which are explicit, rather than on a hidden fraction.

The failure also isn't seen in practice. `tune_pe` flew in 9 of the first 79 logged Missions and never once failed on "periapsis … is over tolerance". The short burns #577 fixed were `circularize`'s.

```kerboscript
// tune_pe.ks: exec_node's residual is ignored; the periapsis decides.
set burn_opts:max_residual to 1000000.
...
if extra:pe_actual < 0 or abs(extra:pe_actual - opt:value) > opt:tolerance { ... fail ... }
```

## Prior requests

- #593: "tune_pe finishes a short burn: finished_short when pe is within a fraction of its target"
