# Schema probe

Asks the API whether it compiles a structured-output schema (#199). It sends one Messages request through the mod's own `ClaudeClient`: `max_tokens` 16, a one-line user message, `output_config.format` set to the schema, and no retries. It prints the schema's union count, optional-parameter count and size (`SchemaComplexity.Size`: property definitions plus enum values), then the HTTP status and the API's error message. It never prints the key or any request header. Each run costs a few tokens, so run it by hand and never in CI.

```sh
export CLAUDIA_PROBE_KEY_FILE=/path/to/a/file/holding/the/key
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-planning   # as the mod sends it
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-flight     # as the mod sends it
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply:answer,sequence_plan,debrief   # any set of kinds
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- craft-check              # CraftCheckRules.SchemaJson()
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- tools/ClaudiaKermin.SchemaProbe/schemas/round2-flight-plus-one.json
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-flight --count   # counts only, sends nothing
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-flight --print   # the schema JSON, sends nothing
```

A Mission request sends one of two schemas. The planning schema (answer, mission_plan, sequence_plan) goes with Mission Plan, Mission Settings, typed reply and change requests, and with questions before a Mission Plan is approved. The flight schema (answer, sequence_plan, debrief, alert) goes with everything else. `mission-reply-all` is every kind in one schema, which the API refuses.

## #534 probes: one Mission schema, propose_mission_plan, cache

```sh
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-one --count      # the one Mission schema: flight plus mission_plan in kind, no part
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-grammar --count  # its counts and the Mission tool list, sends nothing
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-grammar          # sends it with a non-strict propose_mission_plan, then a control without the tool
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- cache-probe --count      # the four cache steps, sends nothing
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- cache-probe              # sends them (claude-sonnet-5-5); prints usage only
```

`mission-grammar` tells whether a non-strict tool adds to the grammar budget: both requests compile, or only the control. `cache-probe` sends one prompt of about 10k tokens (cache_control as the mod sets it, a fresh nonce each run) four times: a baseline, the baseline again, only `effort` changed (medium to high), only `tool_choice` changed (auto to none, as #509's last request does). It prints `cache_creation_input_tokens`, `cache_read_input_tokens` and `input_tokens` per request. A request that reads about the baseline's creation count kept the cache. All requests use `max_tokens` 16. `--print` shows the request bodies. `schemas/534-mission.json` is the one Mission schema, frozen.

`--model <id>` overrides the default model (`claude-haiku-5-5`, the cheapest model with structured outputs). Exit code 0 means the schema compiled (2xx), or was counted or printed. 1 means the API refused it or the request failed. 2 means bad usage.

`schemas/` holds frozen schemas for comparison:

| File | Unions | Size | What |
|---|---|---|---|
| `mission-reply-pre-65.json` | 11 | 75 | `36f6463^`, the Child Vessels branch before #65: compiled |
| `mission-reply-before-child-vessels-merge.json` | 12 | 89 | `6ea3805^1`, main just before the Child Vessels merge: worked in play |
| `mission-reply-main-34ccd14.json` | 14 | 95 | main when #199 was filed: refused |
| `mission-reply-fixed-plus-one-nullable.json` | 5 | 98 | every part with sentinels, plus one nullable field: refused |
| `round2-planning.json` | 2 | 65 | the planning schema as shipped |
| `round2-flight.json` | 3 | 78 | the flight schema as shipped |
| `round2-flight-plus-one.json` | 4 | 79 | the flight schema (the larger) plus one nullable number in `sequence_plan`, to check headroom |
| `round2-mission-plan-flat-child-vessels.json` | 2 | 64 | the planning schema with `child_vessels` as three parallel arrays, a fallback if planning is refused |

Before a schema grows, probe the bigger schema plus one more nullable field. Raise the budgets in `MissionReplySchemaTests` only after that probe compiles.
