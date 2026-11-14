# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments`.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Make an issue a sub-issue of a parent**: `gh issue create --parent <parent> ...`, or `gh issue edit <parent> --add-sub-issue <child>` afterwards (`gh` 2.94+). Older `gh`: `gh api --method POST repos/<owner>/<repo>/issues/<parent>/sub_issues -F sub_issue_id=<child-db-id>` (database id, as in **Blocking** below). Without sub-issues, put `Part of #<parent>` at the top of the child body.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

Infer the repo from `git remote -v`; `gh` does this automatically when run inside a clone.
- **Who the Artisan is** (triage compares ticket authors to this): `gh api user --jq .login`, which was `arathianevasion` at setup.
- **Tickets filed by agents** carry the `needs-triage` label (the string `docs/agents/triage-labels.md` maps it to). Tickets the Artisan writes or approves skip triage.

## The Project: every issue gets an Area

All issues live on the GitHub Project **Claudia Kerman**: <https://github.com/users/arathianevasion/projects/1>, project number 1, owner `arathianevasion`.

- **Labels are the category and the state.** `bug`/`enhancement` and `needs-triage`/`needs-info`/`ready-for-agent`/`ready-for-human`/`wontfix` (see `triage-labels.md`). Every open issue carries exactly one of each.
- **The Project carries what labels don't:** Area, Priority and Verified in. Leave the built-in Status field alone.

| Field | Values | Set by |
|---|---|---|
| **Area** | Launch & Ascent · Orbit & Maneuvers · Transfer & Encounter · Rendezvous & Docking · Descent & Landing · Surface Ops & Ascent · Return & Reentry · UI · LLM Cost Reduction · Claudia's Judgment · kOS Runtime & Performance · Mod Core & Persistence · Test & Tooling | whoever files the issue |
| **Priority** | P0 (loses a Mission, a craft or money) · P1 (costs a retry or a replan) · P2 (polish) | triage |
| **Verified in** | Not yet · KSP_spike · KSP_stable | whoever merges, then the player's in-game check |

**Choosing an Area.**
- **Pick the one the fix lives in, not the one where the symptom showed up.** For example, a Script Check lint goes under Claudia's Judgment, even though it fired on a landing plan.
- **The flight phases:**
  - Launch & Ascent: liftoff to the parking orbit, including circularize and plan_ascent;
  - Orbit & Maneuvers: solve_burn, change_orbit, exec_node, warp and RCS;
  - Transfer & Encounter: ejections, corrections, Lambert and DeltaVMap;
  - Rendezvous & Docking: matching and docking with another vessel;
  - Descent & Landing: land, find_landing_site and braking;
  - Surface Ops & Ascent: science, EVA, and ascend from a body;
  - Return & Reentry: return, deorbit, reentry and parachutes.
- **The areas that cut across phases:**
  - UI: windows and the plan bar;
  - LLM Cost Reduction: tokens, cache, effort, compaction, the Spending Cap and charges;
  - Claudia's Judgment: the prompt, Planning Tools, Script Check and lints, Debriefs, Alerts, Telemetry Checks and the Craft Check;
  - kOS Runtime & Performance: library instruction budgets, loop stalls, staging and drops;
  - Mod Core & Persistence: saves, settings, the Game Snapshot, the runtime gate, threading and licensing;
  - Test & Tooling: tests, the harness, fixtures, baseline tools and in-game checklists.

**Commands.** `gh` needs a token with the `project` scope: use `export GH_TOKEN=$(gh auth token --user arathianevasion)`.

- **Add a new issue to the Project.** Do this straight after `gh issue create`: `gh project item-add 1 --owner arathianevasion --url <issue url> --format json --jq .id` returns the item id.
- **Set a field:**

  ```sh
  gh project item-edit --id <item id> --project-id <project id> --field-id <field id> --single-select-option-id <option id>
  ```

  Look the ids up with `gh project view 1 --owner arathianevasion --format json --jq .id` (the project id) and `gh project field-list 1 --owner arathianevasion --format json` (field and option ids).
- **Read an issue's fields:** `gh project item-list 1 --owner arathianevasion --format json --limit 500` lists every item with its fields.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents:

- **Read a PR**: `gh pr view <number> --comments` and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh api --paginate 'repos/{owner}/{repo}/pulls?state=open' --jq '.[] | select(.author_association | IN("OWNER","MEMBER","COLLABORATOR") | not) | {number, title, author: .user.login, author_association, labels: [.labels[].name]}'`.
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either: resolve with `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `gh issue create --label wayfinder:map`.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (see **Make an issue a sub-issue of a parent**). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies**, the canonical, UI-visible representation. Add an edge with `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). GitHub reports `issue_dependencies_summary.blocked_by` (open blockers only, the live gate). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open children (`gh issue list --state open`, scoped to the map's sub-issues / task list), drop any with an open blocker (`issue_dependencies_summary.blocked_by > 0`, or an open issue in the `Blocked by` line) or an assignee; first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far.
