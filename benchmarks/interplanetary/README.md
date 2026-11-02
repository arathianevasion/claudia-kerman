# Interplanetary benchmark

The reference flight for interplanetary Missions, kept so later builds can be compared against it. The Mission folder is a copy from
KSP_spike, laid out as `saves/<install>/ClaudiaKermin/missions/<id>/` so `tools/cost-baseline/baseline.py` reads it directly. It shows
up as install `interplanetary`, save `duna`. It's untrusted data, as every Mission folder is: read it, never run anything from it.

Every file is byte-for-byte as the mod wrote it, except the 55 request bodies (`stream-dumps/request-*.json`, 12 MB). Those are packed
into `stream-dumps/requests.tar.xz` (0.2 MB). baseline.py doesn't need them: it reads the `.sse` responses, which stay unpacked. To read
a request body, unpack the archive into a scratch folder:

```sh
mkdir -p /tmp/duna-requests && xz -dc benchmarks/interplanetary/saves/duna/ClaudiaKermin/missions/m20261101-160346-2997/stream-dumps/requests.tar.xz | tar xf - -C /tmp/duna-requests
```

## The flight

| | |
|---|---|
| Mission | `m20261101-160346-2997`, "Fly to Duna and land anywhere" |
| Model, build | Sonnet 5.5, `d240233` (installed 2026-11-01 09:26), Auto Mode on, `debug_tools` on |
| Outcome | **Lost** on the landing leg, about 3,260 s after its script started |
| Cost | **$3.00** at published prices (mod logged $3.00), 57 requests |
| Legs | 11 Sequence Plans, 11 distinct legs; 0 Off-Nominal Debriefs from Claudia, 2 mod-written Pre-burn Refusals (#579) |
| Compaction | 1, at 152,178 tokens and 112 messages ($0.35) |
| Max context | 151,436 tokens |
| Snapshot tokens per turn | 665 (median) |

### Where the money went

| Reply kind | Requests | Cost |
|---|---:|---:|
| Mission Plan (and 1 revised) | 4 | $0.23 |
| Sequence Plan | 42 | $2.04 |
| Debrief | 9 | $0.63 |
| Craft Check | 2 | $0.09 |

- **Thinking was $1.19 (41% of spend):** 118,803 of 145,515 output tokens.
- **Two cut-offs** (`max_tokens`) cost $0.67. Both were on Sequence Plans: the ejection (leg 3) and the landing (leg 11).
- **Three legs were 50% of the bill:**
  - Leg 3, the ejection: $0.61, 49k thinking tokens, 3 `solve_burn` calls, and a cut-off.
  - Leg 5, the phasing burn: $0.45, with 11 `solve_burn` calls.
  - Leg 11, the landing: $0.44, 37k thinking tokens, and a cut-off.
- **The interplanetary path is hand-built.** It has no `transfer_to` for planets yet (#459), so Claudia planned the ejection, the phasing burn and the intercept with `exec_node` and repeated `solve_burn` calls.

### Legs

1. Launch to Kerbin orbit
2. Extend solar panels
3. Duna transfer: the ejection burn
4. Coast out of Kerbin's sphere of influence
5. Phasing burn at the Sun-orbit periapsis
6. Duna intercept burn
7. Duna mid-course and approach. Pre-burn refusal: periapsis −15,472 m, against 100 ± 20 km.
8. Mid-course retry. Pre-burn refusal: periapsis 38,275 m, against 110 ± 40 km.
9. Late mid-course
10. Duna capture
11. Duna landing: **Lost**

### The loss

Leg 11 was a hand-written suicide-burn loop instead of a landing Building Block:
- `tune_pe` to a 20 km periapsis;
- a `coast` to periapsis − 500 s;
- then a custom `until LANDED` loop with `lock steering to srfretrograde`, a stopping-distance estimate from total speed, legs, and a throttled touchdown.

Claudia flagged its weak points as risks: "Burn may start early because the suicide-burn estimate uses total speed", "Unknown terrain slope", "Daylight is not checked". The craft carried one Mk16 parachute (`parachuteSingle`) and the script never used it; there is no atmosphere handling: Duna has an atmosphere.

## Compare a later flight against it

```sh
python3 -I tools/cost-baseline/baseline.py benchmarks/interplanetary/saves ~/KSP_spike/saves \
  --reference m20261101-160346-2997 --compare <new mission id>
```

The `--reference` applies to the change table; without it, the reference is the Mun baseline b5f3. Things to compare:
- cost, and cost per leg;
- legs and pre-burn refusals;
- compactions, and the context size at the first compaction;
- thinking share, and cut-offs;
- whether the landing leg used a Building Block.
