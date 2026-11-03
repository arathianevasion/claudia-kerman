# claudia-kermin

## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues on arathianevasion/claudia-kermin (via `gh`). See `docs/agents/issue-tracker.md`.

Every new issue goes on the **Claudia Kermin** Project (`gh project item-add 1 --owner arathianevasion`), with an **Area** set when it's filed. Triage sets **Priority**. Labels stay the category and the state. The Areas and commands are in `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## Building Blocks

When a ticket reworks a Building Block's flight logic, move that logic into C# in `ClaudiaKermin.Core` behind the block's existing interface, following the guardrails in `docs/adr/0014-block-flight-logic-moves-to-c-as-it-changes.md`. Steering, throttle, staging and warp stay in Kerboscript. Don't grow `kerboscript/library`: `tools/gates/kerboscript-loc.sh` enforces a line limit, and a block's `//|` docs are pinned by `BlockDocsSnapshotTests`. A brief that touches `kerboscript/library/` carries a Block migration line (`docs/agents/block-migration.md`).
