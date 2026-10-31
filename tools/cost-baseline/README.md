# cost-baseline

Measures what each reply kind costs, from the mod's debug stream dumps. Written for #244 so a re-flight can be
measured the same way.

## Rerun

Fly with `debug_tools` on, so each Mission folder gets `stream-dumps/`. Then, from the repo root:

```sh
python3 -I tools/cost-baseline/baseline.py ~/KSP_spike/saves --out docs/research/cost-baseline-<date>.md
```

- `saves` takes one or more folders, one per install, each read in place. It defaults to `~/KSP_spike/saves` and
  `~/KSP_stable/saves`. Each install keeps its own logs and the script never copies, merges or writes into a save folder;
  every Mission row names the install it came from (the folder above `saves`). Without `--out` the report goes to stdout.
- `--reference <mission id>` names the Mission reported on its own and compared against (default `m20261029-194229-b5f3`,
  the baseline Mun flight).
- `--goal <usd>` is what a full Mun flight should cost (default 1.00).
- Run it with `-I` and keep it outside the save folders: Mission folders are untrusted data. The script only reads them.

## Baseline and goal

The baseline is **b5f3** (`m20261029-194229-b5f3`, KSP_stable): a full Mun flight with return that cost **$2.20** at
published prices. The goal is **under $1.00** for a full Mun flight. Anything dearer than b5f3 is a regression to explain;
getting under the goal is the work (#534, #549 and #257 are the known levers, none of them in this tool).

## Comparing Missions (`--compare`, #550)

```sh
python3 -I tools/cost-baseline/baseline.py --compare m20261031-022433-3dc5
python3 -I tools/cost-baseline/baseline.py ~/KSP_spike/saves ~/KSP_stable/saves --compare <mission id> [<mission id> ...]
```

Prints, instead of the full report, one row per named Mission and then the same row for the reference. Put the saves
folders before `--compare`, which takes every id after it.

| Column | Meaning |
|---|---|
| Install | the install the Mission's folder was read from |
| Published | cost at the published prices in `PRICES`, Craft Checks included at the mod's own amounts |
| Mod logged | the mod's own total (every `paid` and `kept` line of `cost-log.jsonl`, Craft Checks too), at its `PriceTable`. It runs above Published because the mod prices cache reads too high (#257); the column stays until that lands, so the gap stays visible |
| Legs, Per leg | Sequence Plans committed, and the dumped cost (Craft Checks left out) divided by them |
| Revised Mission Plans | count and cost of every request that asked for a revised Mission Plan (#534: each one rewrites the cache) |
| Compactions | count and cost of the compaction iterations (#549) |
| Full cache misses | requests after the second whose writes exceed their reads, with what rewriting the lost prefix cost over the read price |
| Prefix (tokens) | the system prompt and tools as the API cached them: the cache read of the first revised Mission Plan (else Sequence Plan) request whose read stopped short of what the request before had cached. Blank when no request shows that |
| Per Sequence Plan request, Per Debrief request | dumped cost of those reply kinds divided by their request count |

A second table gives each named Mission's change against the reference (published total, cost per leg, prefix) and its
distance to `--goal`. An unknown Mission id is an error; an unknown reference is said, with no change shown. On
2026-10-30 the two flights read: b5f3 $2.20, 6 legs, 1 revised plan for $0.42, no compaction, prefix 25,569; 3dc5
$4.21, 10 legs, 3 revised plans for $1.04, 1 compaction for $0.66, prefix 26,865.

The prefix also has a test: `MissionPrefixBudgetTests` in `tests/ClaudiaKermin.Core.Tests` builds a Mission request as
`MissionControl` does and holds the system prompt and tool definitions under a budget about 5% above today's figure
(chars / 3, the mod's own estimate; it runs lower than the API's count, so compare it only with itself).

## After an in-game check flight

When a build's in-game check includes a Mun flight, add this step to the check: on the flown Mission's install, run

```sh
python3 -I tools/cost-baseline/baseline.py --compare <the check flight's mission id>
```

and paste its row, the reference row and the change line into the check's ticket. Flag any rise in per-leg cost,
revised plans, compactions, full cache misses or prefix size against the reference. The flight needs `debug_tools` on
so its Mission folder has `stream-dumps/`.

## How it counts

- Each `request-*.sse` is one request. Its last attempt with a `message_delta` gives the final usage. Cost is summed
  over every `usage.iterations` entry (message, compaction, fallback_message), and each cache write is priced at the
  TTL the API reported for it.
- A response is committed when its streamed `text_delta` length equals an assistant turn's text in `transcript.json`.
  The reply kind is the one the mod asked for in the system notice before that turn. A response that never committed
  (cut off, off-schema, refused, or abandoned) takes the kind of the next turn still to commit, or of
  `unanswered-turn.json`.
- Reply time is `pending` to `paid` (or `kept`/`released`) in `cost-log.jsonl`. Main requests pair with the dumps in
  order. Craft Checks and margin rechecks are not dumped, so only the mod's own `paid` amount is reported for them.
- Prices are in `PRICES` at the top of the script. Check them against
  <https://platform.claude.com/docs/en/about-claude/pricing> before each run.

## Per-plan log (`plans.jsonl`, #286)

The mod writes one JSON line per Sequence Plan offered to `plans.jsonl` in the Mission folder, and `baseline.py` adds a
"Per Sequence Plan" section from it: a row per plan, then totals for all plans, Custom Scripts, Building Blocks only and plans
with lints. The log is rewritten, not appended to: when that sequence's Debrief commits, the latest plan's line gains a
`debrief` object (`outcome`, and the Debrief turn's own `effort`, `requests`, `cost`, `second_look`, ...). The rewrite is atomic.
A plan whose Debrief has not come shows "not yet".

Each Mission Plan turn (first or revised, #514) also writes a line with `"event":"mission_plan"`: `purpose`, `tools`, `tool_calls`,
`refused_calls`, `repeat_calls` (zeros included), `tool_steps`, `discarded` (true for a tool loop dropped before the plan came),
and the turn's figures as for a plan. `baseline.py` reads only the `plan` lines, so these do not change its report.

Fields of a line: `plan` (1, 2, ... in the file), `name`, `purpose` (the turn that made it), `manual`, `tools` and `tool_calls`
(Planning Tools called, with counts), `blocks` (Building Blocks the script calls, with counts), `custom_script`, `custom_lines`
(lines of code, not blank or comment), `lints`, `effort`, `requests`, `output_tokens`, `thinking_tokens`, `reply_seconds`,
`cost` (what the plan's requests added to the Mission's spend), `second_look` (the reason, or null), `cut_off_retry`,
`script_rewrite`, `at`. The figures cover every request of the turn that made the plan, including a discarded first reply, tool
steps, and a Script Check rewrite; they restart from zero if the game is loaded mid-turn. `thinking_tokens` is an estimate: the
API reports output tokens only, so it is the output less the visible reply at three characters a token.

## Replay screen for effort cuts (`replay.py`)

Proves an effort cut on recorded requests (#269, spec #265). Developer tooling outside the mod; stdlib only.

```sh
python3 -I tools/cost-baseline/replay.py ~/KSP_spike/saves --dry-run
python3 -I tools/cost-baseline/replay.py ~/KSP_spike/saves --kinds debrief --new-effort medium --out replay-debrief.md
```

- **Input.** Mission folders, or a saves folder holding them. Per request it uses the saved body
  (`stream-dumps/request-*.json`, written with `debug_tools` on) as it was sent. Without one it rebuilds the request from
  `transcript.json`: the Mission system layers and the history up to the reply. Kinds are told apart by the mod's notice
  before the reply: `debrief`, `change`, `reply`, `settings`. The Craft Check and the Margin recheck are in no transcript
  and the mod saves no request body for them yet, so they run only from `craft-checks/*.json` case files
  (`{"kind": "craft_check" | "margin_recheck", "body": {...}, "reply": "<text>", "input_tokens": n}`).
- **Rebuild check.** The cost log holds dollars, not tokens, so the check is against the input tokens in the matching
  stream dump. With no tokenizer, a rebuilt request must have characters per recorded token within `--tolerance`
  (default 15%) of its Mission's median over its recorded requests (at least 3 are needed). Anything else is listed as
  unusable with the reason and left out.
- **Replay.** About `--per-kind` (20) usable requests per kind, each `--repeats` (1) times at the old effort (the one
  recorded, or `--old-effort`) and at `--new-effort` (medium). Each goes through Claude Code print mode on the
  subscription: `claude -p` with model, `--effort`, the Mission system prompt, `--json-schema` (the saved body's, else a
  reduced one), `--tools ""`, the history flattened into the prompt on stdin, and `--max-budget-usd` (1.00 a call, set
  with `--max-budget-usd` here) as the safety net. Requests rebuilt from a transcript, which holds no model, use `--model`.
  The flags were checked against the installed CLI's `--help` (#567); no real call has been made yet. The fixes: added
  `--safe-mode` (no CLAUDE.md, hooks, MCP servers, skills or memory reach the model, so only the Mission prompt does) and
  `--no-session-persistence` (no transcripts left on disk). `--tools ""` is documented as "disable all tools" and
  `--system-prompt` replaces the default prompt. The reply is read from `structured_output` of the `json` result, else
  `result`; that field name is unverified until the first real run.
- **Agreement** is on parsed fields, not wording: Debrief `outcome`; the sets of Blockers and of Warnings for Craft Checks
  and Margin rechecks (Blocker codes are `BLOCKERS`, which a test checks against `CraftCheckRules.cs`); for Change, Reply
  and Settings, whether a revision (a Mission Plan or Sequence Plan) came back and the names of the Maneuver Sequences
  in it. A failed call or a reply that is not JSON is a disagreement.
- **Pass or fail** per kind: agreement at the new effort no more than 1 in 20 below the old effort's, and for Craft
  Checks and Margin rechecks no Blocker missed. A kind whose old effort never reproduces an original fails as having no
  yardstick. The exit code is 0 only when every kind passes. The pass is advice: the report prints every disagreement
  (case, effort, original and replay fields, `unsure_because`, errors) for a person to read, and the final call is theirs.
- **`unsure_because`** (Second look ticket): for replies that have the field, per kind and effort, how many said they were
  unsure, how many disagreed with the original, and how many of those disagreements were flagged unsure.
- **`sequence_plan`** (#567) is a kind too: the notice asks for `kind "sequence_plan"` (a pending-plan change or reply
  stays `change` or `reply`). Agreement is the set of Building Blocks the script calls (as `PlanLog.BlocksCalled` counts
  them; the names are the files in `kerboscript/library`) plus `plan_step`.
- **Model screen** (#567). `--model claude-haiku-5-5` replays each chosen request once on that model, overriding the model
  in a saved body, at the effort it was recorded at (`--old-effort` overrides it), and compares with the recorded
  replies. A kind passes at 95% of its replays (19 of 20, so a kind with fewer than 20 usable requests must agree on all; no Blocker
  missed for Craft Checks). `--model` now means this screen; the plain effort screen is run without it. The report is a per-kind table plus every
  disagreement with both replies' parsed fields. `--new-effort` is not used. Then confirm a passing kind with
  `batch_confirm.py`.

  ```sh
  python3 -I tools/cost-baseline/replay.py ~/KSP_spike/saves ~/KSP_stable/saves --model claude-haiku-5-5 --dry-run
  python3 -I tools/cost-baseline/replay.py ~/KSP_spike/saves ~/KSP_stable/saves --model claude-haiku-5-5 --per-kind 20 --out replay-haiku-5-5.md
  ```
- **`--dry-run`** reads and checks the requests, prints how many calls it would make, and sends nothing.
- **Testing.** The runner is injectable (`replay.main(argv, runner=...)`, `replay.run(cases, runner)`), so the tests use
  a fake and never call `claude`.

## Batch API confirmation (`batch_confirm.py`)

Confirms a kind that passed the screen on about 5 exact requests through the Batch API, at the new effort.

```sh
python3 -I tools/cost-baseline/batch_confirm.py ~/KSP_spike/saves --kinds debrief,reply --dry-run
ANTHROPIC_API_KEY=... python3 -I tools/cost-baseline/batch_confirm.py ~/KSP_spike/saves --kinds debrief --approve debrief
```

- Only requests with a saved body are exact; rebuilt ones are skipped and counted. The body is sent as saved, without
  `stream`, with the turn's effort message set to `--new-effort`.
- For every kind in `--kinds` it first prints the cost estimate: the recorded input tokens at the full input rate (no
  cache credit) plus the original replies' output tokens, at the Batch API's 50% discount, and a worst case with
  `max_tokens` of output. Prices are `PRICES` in `baseline.py`.
- It sends a kind only when that kind has its own `--approve <kind>` flag (repeatable; there is no approve-all, and a kind
  not in `--kinds` is an error). With none it exits 2 having sent nothing. `--dry-run` never sends.
- Results are judged on the same parsed fields. It prints each disagreement and whether the kind is CONFIRMED (every
  request agrees and no Blocker missed) or NOT CONFIRMED.
- Only `api_submit` touches the network (stdlib `urllib`, key from `ANTHROPIC_API_KEY`, never printed). Tests pass a fake
  `submit`. The Batch request shape (the saved body, including `fallbacks`, as `params`) is unverified against the live
  API.

## Tests

```sh
python3 -I -m unittest discover -s tools/cost-baseline/tests -t tools/cost-baseline
```

The fixture Missions are written by `tests/fixtures.py`, hand-made and tiny; no real Mission folder is in the repo.
