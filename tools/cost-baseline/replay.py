#!/usr/bin/env python3
"""Replay screen for an effort cut (#269, from the #265 spec): would a reply kind do as well at a lower effort?

Reads Mission folders (untrusted data: this script only reads them), rebuilds each recorded request, replays about 20
per reply kind at the old effort and at the new one through Claude Code print mode on the subscription (no tools, the
history flattened into the prompt, spend capped per call), compares the parsed fields with the originals, and reports
pass or fail per kind. It never calls anything itself in --dry-run, and the runner is injectable (see `run`).

Request source, per Mission folder:
  stream-dumps/request-<session>-<id>.json  the saved request body (#266), used as is when present
  stream-dumps/request-<session>-<id>.sse   the response: its usage is the recorded input-token count
  transcript.json                           the conversation: requests without a saved body are rebuilt from it
  craft-checks/*.json                       {"kind": "craft_check"|"margin_recheck", "body": {...}, "reply": "<text>",
                                            "input_tokens": n}: the mod keeps no other record of those two kinds

Model screen (#567): with --model, each request is replayed once on that model at the effort it was recorded at, and
the replies are compared with the recorded ones; a kind passes at 19 in 20 (--old-effort/--new-effort do not apply).

A Mission found in more than one folder (one id, identical contents) counts once; --since YYYY-MM-DD leaves out Missions
started before that date, read from the Mission id (#586).

Usage: python3 -I tools/cost-baseline/replay.py MISSION_OR_SAVES_DIR... [--new-effort medium] [--kinds debrief,...]
       [--per-kind 20] [--repeats 1] [--since YYYY-MM-DD] [--dry-run] [--out FILE.md]
       python3 -I tools/cost-baseline/replay.py MISSION_OR_SAVES_DIR... --model claude-haiku-5-5 [--kinds ...] [--dry-run]
"""
import argparse
import datetime
import fractions
import glob
import hashlib
import json
import os
import re
import statistics
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python3 -I leaves the script's folder off the path
import baseline  # noqa: E402

KINDS = ['debrief', 'craft_check', 'margin_recheck', 'change', 'reply', 'settings', 'sequence_plan']
FINDING_KINDS = ('craft_check', 'margin_recheck')

# Craft Check codes that are Blockers (CraftCheckRules.cs); every other code is a Warning. A unittest checks this
# against the C# list.
BLOCKERS = frozenset([
    'no_kos_processor', 'no_seat_for_claudia', 'no_probe_core_or_antenna', 'delta_v_short', 'twr_below_one',
    'no_parachutes_for_return', 'required_experiments_missing', 'child_vessel_problem',
])

# A rebuilt request is usable when its characters per recorded input token are within this fraction of the Mission's
# median. There is no tokenizer here, so the Mission's own recorded requests are the ruler.
TOLERANCE = 0.15
MIN_CALIBRATION = 3
# "Within 1 in 20 of the old effort's agreement."
PASS_MARGIN = fractions.Fraction(1, 20)
# A model screen (#567) passes a kind at 19 of 20.
MODEL_PASS = fractions.Fraction(19, 20)
DEFAULT_MODEL = 'claude-sonnet-5-5'
# Building Block names are the file names in kerboscript/library (CoreLibrary.cs); claudia_lib is the shared helpers.
LIBRARY = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'kerboscript', 'library')
CLOSING = '\n\nReply now as Claudia to the last message above, as one JSON object that fits the schema.'


# ---- Reading a Mission ----

def classify_notice(text):
    """The reply kind a mod notice asks for, or None when it is not one of the screened kinds."""
    t = text or ''
    if 'kind "debrief"' in t:
        return 'debrief'
    if 'changed the Mission Settings' in t:
        return 'settings'
    if 'wants a change' in t:
        return 'change'
    if 'typed a reply' in t:
        return 'reply'
    if 'kind "sequence_plan"' in t or 'pending Sequence Plan' in t:
        return 'sequence_plan'
    return None


def block_names():
    return sorted(os.path.basename(p)[:-3] for p in glob.glob(os.path.join(LIBRARY, '*.ks'))
                  if os.path.basename(p) != 'claudia_lib.ks')


def blocks_called(script, names=None):
    """Building Blocks a script calls, as PlanLog.BlocksCalled counts them (comments and strings dropped, a definition
    is not a call)."""
    code = re.sub(r'"(?:[^"]|"")*"|//[^\n]*', lambda m: ' ' if m.group(0)[0] == '"' else '', script or '')
    called = set()
    for name in names if names is not None else block_names():
        for m in re.finditer(r'(?<![:\w])' + re.escape(name) + r'\s*\(', code, re.I):
            if not re.search(r'\bfunction\s+$', code[:m.start()], re.I):
                called.add(name)
                break
    return sorted(called)


def blocks_text(content):
    if isinstance(content, str):
        return content
    return ''.join(b.get('text', '') for b in content or [] if isinstance(b, dict) and b.get('type') == 'text')


def layers_text(system):
    if isinstance(system, str):
        return [system]
    return [(l.get('text') if isinstance(l, dict) else str(l)) or '' for l in system or []]


def effort_of(messages):
    """The effort the conversation last set, or None."""
    eff = None
    for m in messages:
        if m.get('role') == 'system' and isinstance(m.get('output_config'), dict):
            eff = m['output_config'].get('effort') or eff
    return eff


def chars_of(system, messages):
    n = sum(len(t) for t in system)
    for m in messages:
        if m.get('role') == 'system' and 'output_config' in m:
            continue
        n += len(blocks_text(m.get('content')))
    return n


def input_tokens(usage):
    """Tokens in the request's last message iteration (compaction iterations are other requests), or None."""
    if not usage:
        return None
    its = [i for i in usage.get('iterations') or [] if i.get('type') != 'compaction'] or [usage]
    it = its[-1]
    total = (it.get('input_tokens', 0) + it.get('cache_creation_input_tokens', 0)
             + it.get('cache_read_input_tokens', 0))
    return total or None


def request_key(path):
    m = re.match(r'request-(\d{8}-\d{6})-(\d+)\.sse$', os.path.basename(path))
    return (m.group(1), int(m.group(2))) if m else (os.path.basename(path), 0)


def schema_of(body):
    try:
        return body['output_config']['format']['schema']
    except (KeyError, TypeError):
        return None


def case_from_body(cid, kind, mission, body, original_text, tokens, output=None):
    messages = body.get('messages') or []
    system = layers_text(body.get('system'))
    return dict(id=cid, mission=mission, kind=kind, source='body', model=body.get('model'), system=system,
                messages=messages, schema=schema_of(body), original_text=original_text,
                effort=effort_of(messages) or (body.get('output_config') or {}).get('effort'),
                recorded_tokens=tokens, recorded_output=output, chars=chars_of(system, messages), body=body)


def load_cases(folder, label=None):
    """Every replayable request of one Mission folder, usable or not (see `check_rebuilds`). `label` stands in for the
    folder name in request ids (a second, different folder with the same Mission id gets its own)."""
    mission = label or os.path.basename(folder.rstrip(os.sep))
    cases = []
    tr = baseline.load_json(os.path.join(folder, 'transcript.json')) or {}
    messages = tr.get('messages') or []
    system = layers_text(tr.get('system'))
    turns = []
    notice_kind = None
    for i, m in enumerate(messages):
        role = m.get('role')
        if role == 'system' and 'output_config' not in m:
            notice_kind = classify_notice(blocks_text(m.get('content'))) or notice_kind
        elif role == 'assistant':
            text = blocks_text(m.get('content'))
            turns.append(dict(index=i, kind=notice_kind, text=text, matched=False))
            notice_kind = None
    pos = 0
    dumps = sorted(glob.glob(os.path.join(folder, 'stream-dumps', 'request-*.sse')), key=request_key)
    for p in dumps:
        finals = [a for a in baseline.parse_dump(p) if a['usage'] is not None]
        if not finals:
            continue
        a = finals[-1]
        for j in range(pos, len(turns)):
            if not turns[j]['matched'] and len(turns[j]['text']) == a['text_len'] and a['text_len'] > 0:
                t = turns[j]
                t.update(matched=True, tokens=input_tokens(a['usage']), output=a['usage'].get('output_tokens'), dump=p)
                pos = j + 1
                break
    for t in turns:
        if t['kind'] is None:
            continue
        cid = '%s/turn-%d' % (mission, t['index'])
        body = baseline.load_json(t['dump'][:-4] + '.json') if t.get('dump') else None
        if isinstance(body, dict):
            cases.append(case_from_body(cid, t['kind'], mission, body, t['text'], t.get('tokens'), t.get('output')))
            continue
        history = messages[:t['index']]
        cases.append(dict(id=cid, mission=mission, kind=t['kind'], source='transcript', model=None, system=system,
                          messages=history, schema=None, original_text=t['text'], effort=effort_of(history),
                          recorded_tokens=t.get('tokens'), recorded_output=t.get('output'), chars=chars_of(system, history),
                          body=None))
    for p in sorted(glob.glob(os.path.join(folder, 'craft-checks', '*.json'))):
        d = baseline.load_json(p)
        if isinstance(d, dict) and d.get('kind') in FINDING_KINDS and isinstance(d.get('body'), dict):
            cases.append(case_from_body('%s/%s' % (mission, os.path.basename(p)[:-5]), d['kind'], mission, d['body'],
                                        d.get('reply') or '', d.get('input_tokens'), d.get('output_tokens')))
    return cases


def find_missions(paths):
    """Every Mission folder under `paths`, copies and all (batch_confirm.py takes these as they are)."""
    out = []
    for p in paths:
        if os.path.exists(os.path.join(p, 'transcript.json')) or os.path.isdir(os.path.join(p, 'craft-checks')):
            out.append(p)
        else:
            out.extend(sorted(glob.glob(os.path.join(p, '*', 'ClaudiaKermin', 'missions', '*', ''))))
            out.extend(sorted(d for d in glob.glob(os.path.join(p, '*', '')) if os.path.exists(os.path.join(d, 'transcript.json'))))
    return out


def folder_digest(folder):
    """A hash of every file's relative path and bytes under the folder (links are not followed)."""
    h = hashlib.sha256()
    for root, dirs, files in os.walk(folder):
        dirs.sort()
        for name in sorted(files):
            path = os.path.join(root, name)
            if os.path.islink(path):
                continue
            h.update(os.path.relpath(path, folder).encode('utf-8', 'surrogateescape') + b'\0')
            with open(path, 'rb') as f:
                h.update(f.read())
            h.update(b'\0')
    return h.hexdigest()


def mission_date(mission_id):
    """The date a Mission id starts with (m20261024-182114-096f), or None."""
    m = re.match(r'm(\d{4})(\d{2})(\d{2})-', mission_id)
    try:
        return datetime.date(int(m.group(1)), int(m.group(2)), int(m.group(3))) if m else None
    except ValueError:
        return None


def discover_missions(paths, since=None):
    """The Mission folders under `paths`, as a dict: `folders` (path, label) pairs; `copies` (kept, skipped) pairs for a
    folder with the id and the contents of one already kept; `conflicts` (kept, other) pairs for the same id with
    different contents (both are kept, the later labelled `id~2`); `before` how many Missions `since` left out;
    `undated` how many kept ones have no date in their id (the filter keeps them)."""
    seen = {}   # Mission id -> [[folder, digest or None], ...] of the folders kept
    found = dict(folders=[], copies=[], conflicts=[], before=0, undated=0, since=since)
    for folder in find_missions(paths):
        mid = os.path.basename(folder.rstrip(os.sep))
        kept = seen.setdefault(mid, [])
        label = mid
        if kept:
            digest = folder_digest(folder)
            for k in kept:
                k[1] = k[1] or folder_digest(k[0])
            twin = next((k[0] for k in kept if k[1] == digest), None)
            if twin is not None:
                found['copies'].append((twin, folder))
                continue
            found['conflicts'].append((kept[0][0], folder))
            label = '%s~%d' % (mid, len(kept) + 1)
            kept.append([folder, digest])
        else:
            kept.append([folder, None])
        date = mission_date(mid)
        if since and date and date < since:
            found['before'] += 1
            continue
        found['undated'] += 0 if date else 1
        found['folders'].append((folder, label))
    return found


def found_lines(found, shown=3):
    """What discovery skipped and the filter used, for the dry run and the report."""
    w = ['Missions: %d read; filter --since %s%s.' % (
        len(found['folders']), found['since'] or 'none (all Missions)',
        ' (%d earlier Mission(s) left out)' % found['before'] if found['since'] else '')]
    if found['since'] and found['undated']:
        w[0] += ' %d Mission(s) with no date in their id are kept.' % found['undated']
    if found['copies']:
        w.append('Skipped %d identical copy(ies) of a Mission already read; the first %d:' % (
            len(found['copies']), min(shown, len(found['copies']))))
        w.extend('- kept %s; skipped %s' % c for c in found['copies'][:shown])
    for kept, other in found['conflicts']:
        w.append('Two folders share a Mission id but differ in content, both kept: %s and %s' % (kept, other))
    return w


def check_rebuilds(cases, tolerance=TOLERANCE):
    """Marks each case usable or not. A case with a saved body is exact. A rebuilt one needs a recorded input-token
    count, and its characters per token must sit within `tolerance` of the Mission's median; the Mission's own recorded
    requests are the ruler, and at least MIN_CALIBRATION of them must exist."""
    by_mission = {}
    for c in cases:
        by_mission.setdefault(c['mission'], []).append(c)
    for group in by_mission.values():
        ratios = [c['chars'] / float(c['recorded_tokens']) for c in group if c['recorded_tokens'] and c['chars']]
        median = statistics.median(ratios) if len(ratios) >= MIN_CALIBRATION else None
        for c in group:
            c['usable'], c['reason'] = True, ''
            if not c['original_text'] or parse_json(c['original_text']) is None:
                c['usable'], c['reason'] = False, 'the original reply is missing or not JSON'
            elif c['source'] == 'body':
                if c['recorded_tokens'] and median:
                    c['drift'] = c['chars'] / float(c['recorded_tokens']) / median - 1
            elif not c['recorded_tokens']:
                c['usable'], c['reason'] = False, 'rebuilt, with no recorded input tokens to check it against'
            elif median is None:
                c['usable'], c['reason'] = False, 'rebuilt, and too few recorded requests (%d of %d) to calibrate' % (
                    len(ratios), MIN_CALIBRATION)
            else:
                c['drift'] = c['chars'] / float(c['recorded_tokens']) / median - 1
                if abs(c['drift']) > tolerance:
                    c['usable'], c['reason'] = False, 'rebuilt request is %+.0f%% off the recorded input tokens (tolerance %.0f%%)' % (
                        100 * c['drift'], 100 * tolerance)
    return cases


# ---- Comparing replies ----

def parse_json(text):
    try:
        v = json.loads(text)
    except (TypeError, ValueError):
        return None
    return v if isinstance(v, dict) else None


def signature(kind, reply):
    """The parsed fields a kind is compared on; wording never counts. None when the reply is not usable for the kind."""
    if not isinstance(reply, dict):
        return None
    if kind == 'debrief':
        d = reply.get('debrief')
        return {'outcome': d.get('outcome')} if isinstance(d, dict) else None
    if kind in FINDING_KINDS:
        found = reply.get('findings')
        if not isinstance(found, list):
            return None
        codes = [f.get('code') for f in found if isinstance(f, dict)]
        return {'blockers': sorted(set(c for c in codes if c in BLOCKERS)),
                'warnings': sorted(set(c for c in codes if c not in BLOCKERS))}
    mp, sp = reply.get('mission_plan'), reply.get('sequence_plan')
    if kind == 'sequence_plan':
        if not isinstance(sp, dict):
            return None
        return {'blocks': blocks_called(sp.get('script')), 'plan_step': sp.get('plan_step')}
    names = []
    if isinstance(mp, dict):
        names = [s.get('name') for s in mp.get('sequences') or [] if isinstance(s, dict)]
    elif isinstance(sp, dict):
        names = [sp.get('name')]
    return {'revised': bool(isinstance(mp, dict) or isinstance(sp, dict)), 'sequences': sorted(set(n for n in names if n))}


def agrees(original, replay):
    """Whether a replay's fields match the recorded ones. A recording with no `plan_step` (made before it existed) is
    compared on the Building Blocks alone."""
    if replay is None:
        return False
    if isinstance(original, dict) and 'plan_step' in original and original['plan_step'] is None:
        original, replay = dict(original), dict(replay)
        original.pop('plan_step'), replay.pop('plan_step', None)
    return replay == original


def missed_blockers(original, replay):
    return sorted(set(original.get('blockers', [])) - set(replay.get('blockers', [])))


# ---- Running ----

def flatten(messages):
    """The history as one prompt: the committed turns in order, the effort messages dropped (effort is a flag now)."""
    parts = []
    for m in messages:
        role = m.get('role')
        if role == 'system' and 'output_config' in m:
            continue
        text = blocks_text(m.get('content'))
        if text:
            parts.append('[%s]\n%s' % ({'system': 'mod notice', 'user': 'user', 'assistant': 'Claudia'}.get(role, role), text))
    return '\n\n'.join(parts) + CLOSING


def fallback_schema(kind):
    """When no saved body carries the real reply schema: just the fields the screen compares."""
    if kind == 'debrief':
        return {'type': 'object', 'additionalProperties': False, 'required': ['kind', 'debrief'], 'properties': {
            'kind': {'type': 'string', 'enum': ['debrief']},
            'debrief': {'type': 'object', 'additionalProperties': False, 'required': ['outcome'], 'properties': {
                'outcome': {'type': 'string', 'enum': ['Nominal', 'Off-Nominal', 'Failed']}}}}}
    if kind == 'sequence_plan':
        return {'type': 'object', 'required': ['kind', 'sequence_plan'], 'properties': {
            'kind': {'type': 'string', 'enum': ['sequence_plan']},
            'sequence_plan': {'type': 'object', 'required': ['name', 'plan_step', 'script'], 'properties': {
                'name': {'type': 'string'}, 'plan_step': {'type': 'integer'}, 'script': {'type': 'string'}}}}}
    return {'type': 'object'}


def claude_print_runner(req):
    """Claude Code print mode on the subscription. Not run by the tests or by --dry-run."""
    # Checked against `claude --help` (#567). --safe-mode leaves out CLAUDE.md, hooks, MCP servers, skills and memory, so
    # only the Mission prompt reaches the model; --system-prompt replaces the default one; --tools "" turns every tool off.
    cmd = ['claude', '-p', '--safe-mode', '--no-session-persistence', '--model', req['model'], '--effort', req['effort'],
           '--system-prompt', req['system'], '--json-schema', json.dumps(req['schema']), '--tools', '',
           '--output-format', 'json', '--max-budget-usd', str(req['max_budget_usd'])]
    out = subprocess.run(cmd, input=req['prompt'], capture_output=True, text=True, check=True, timeout=900).stdout
    d = json.loads(out)
    if isinstance(d.get('structured_output'), dict):
        return json.dumps(d['structured_output'])
    return d.get('result') or ''


def pick(cases, per_kind):
    """Up to `per_kind` usable cases per kind, spread evenly through the Missions."""
    chosen = []
    for k in KINDS:
        ok = [c for c in cases if c['kind'] == k and c['usable']]
        if len(ok) <= per_kind:
            chosen.extend(ok)
        else:
            chosen.extend(ok[int(i * len(ok) / per_kind)] for i in range(per_kind))
    return chosen


def request_for(case, effort, model, budget, force_model=False):
    return dict(model=model if force_model else case['model'] or model, effort=effort, system='\n\n'.join(case['system']),
                prompt=flatten(case['messages']), schema=case['schema'] or fallback_schema(case['kind']),
                max_budget_usd=budget)


def run(cases, runner, new_effort='medium', old_effort=None, per_kind=20, repeats=1, model=DEFAULT_MODEL,
        budget=1.0, model_screen=False):
    """Replays the chosen cases at both efforts. `runner(request dict) -> reply text` is the injectable seam. In a model
    screen (#567) each goes once per repeat to `model` at its recorded effort, labelled 'model'."""
    results = []
    for case in pick(cases, per_kind):
        original = signature(case['kind'], parse_json(case['original_text']))
        recorded = old_effort or case['effort'] or 'high'
        for label, effort in ((('model', recorded),) if model_screen else (('old', recorded), ('new', new_effort))):
            for n in range(repeats):
                rec = dict(case=case['id'], kind=case['kind'], which=label, effort=effort, run=n, original=original,
                           reply=None, signature=None, error=None, unsure=None)
                try:
                    reply = parse_json(runner(request_for(case, effort, model, budget, model_screen)))
                except Exception as e:  # a failed call is a disagreement, reported; the screen goes on
                    rec['error'] = '%s: %s' % (type(e).__name__, e)
                    reply = None
                if reply is not None:
                    rec['reply'] = reply
                    rec['signature'] = signature(case['kind'], reply)
                    if 'unsure_because' in reply:
                        rec['unsure'] = str(reply['unsure_because'] or '')
                elif rec['error'] is None:
                    rec['error'] = 'reply was not a JSON object'
                rec['agree'] = agrees(original, rec['signature'])
                if case['kind'] in FINDING_KINDS and original:
                    # A call that failed or came back unusable has found none of the Blockers.
                    rec['missed'] = missed_blockers(original, rec['signature'] or {})
                else:
                    rec['missed'] = []
                results.append(rec)
    return results


def summarize(results):
    """Per kind: runs, agreement and verdict at each effort, by the rule in the spec."""
    out = {}
    for k in KINDS:
        rs = [r for r in results if r['kind'] == k]
        if not rs:
            continue
        S = {'cases': len(set(r['case'] for r in rs))}
        for w in ('model',) if rs[0]['which'] == 'model' else ('old', 'new'):
            ws = [r for r in rs if r['which'] == w]
            dis = [r for r in ws if not r['agree']]
            flagged = [r for r in ws if r['unsure'] is not None]
            S[w] = dict(effort=ws[0]['effort'] if ws else None, runs=len(ws), agree=len(ws) - len(dis),
                        requests=len(set(r['case'] for r in ws)),
                        requests_agree=len(set(r['case'] for r in ws)) - len(set(r['case'] for r in dis)),
                        missed=sum(1 for r in ws if r['missed']), disagreements=dis,
                        with_unsure=len(flagged), unsure_set=sum(1 for r in flagged if r['unsure']),
                        dis_flagged=sum(1 for r in dis if r['unsure']),
                        dis_with_unsure=sum(1 for r in dis if r['unsure'] is not None))
        if 'model' in S:
            m = S['model']
            reasons = []
            if fractions.Fraction(m['agree'], m['runs']) < MODEL_PASS:
                reasons.append('agreement is under 19 in 20')
            if k in FINDING_KINDS and m['missed']:
                reasons.append('%d run(s) missed a Blocker' % m['missed'])
            S['pass'], S['reasons'] = not reasons, reasons
            out[k] = S
            continue
        o, n = S['old'], S['new']
        reasons = []
        if not o['runs'] or not n['runs']:
            reasons.append('no runs')
        else:
            if not o['agree']:
                reasons.append('the old effort never reproduced the originals, so it is no yardstick')
            if fractions.Fraction(n['agree'], n['runs']) < fractions.Fraction(o['agree'], o['runs']) - PASS_MARGIN:
                reasons.append('agreement fell by more than 1 in 20')
            if k in FINDING_KINDS and n['missed']:
                reasons.append('%d run(s) missed a Blocker' % n['missed'])
        S['pass'], S['reasons'] = not reasons, reasons
        out[k] = S
    return out


def rate(a, b):
    return '%d/%d (%.0f%%)' % (a, b, 100.0 * a / b) if b else '-'


def by_request(results):
    """The disagreeing runs, grouped per (request, effort) in the order first met, with the runs each had."""
    groups = {}
    for r in results:
        g = groups.setdefault((r['case'], r['which']), dict(runs=0, bad=[]))
        g['runs'] += 1
        if not r['agree']:
            g['bad'].append(r)
    return [(k, g) for k, g in groups.items() if g['bad']]


def runs_note(results):
    runs = sorted(set(sum(1 for r in results if r['case'] == c and r['which'] == w)
                      for c, w in set((r['case'], r['which']) for r in results)))
    return ('Each request is replayed %s time(s) per effort; the Agree columns count runs.' % (
        '/'.join(str(n) for n in runs))) if runs else ''


def model_report(cases, results, summary, model, found=None):
    w = ['# Model screen: %s against the recorded replies' % model, '']
    if found:
        w.extend(found_lines(found))
        w.append('')
    w.append('Requests found: %d; usable: %d. Each request is replayed at the effort it was recorded at; a kind passes at '
             '19 in 20 agreeing with the recorded reply on its parsed fields. %s' % (
                 len(cases), sum(1 for c in cases if c['usable']), runs_note(results)))
    w.append('')
    w.append('| Kind | Requests | Effort | Agree | Blockers missed | Verdict |')
    w.append('|---|---:|---|---:|---:|---|')
    for k, S in summary.items():
        m = S['model']
        w.append('| %s | %d | %s | %s | %d | **%s**%s |' % (
            k, S['cases'], m['effort'], rate(m['agree'], m['runs']), m['missed'], 'PASS' if S['pass'] else 'FAIL',
            '' if S['pass'] else ' (' + '; '.join(S['reasons']) + ')'))
    w.append('')
    w.append('## Disagreements')
    w.append('')
    shown = by_request(results)
    for (case, _), g in shown:
        first = g['bad'][0]
        w.append('- %s, %s (%d of %d run(s) disagreed): recorded %s' % (
            case, first['kind'], len(g['bad']), g['runs'], json.dumps(first['original'], sort_keys=True)))
        for r in g['bad']:
            w[-1] += '; run %d: replay %s%s%s' % (
                r['run'], json.dumps(r['signature'], sort_keys=True),
                ('; MISSED BLOCKERS ' + ', '.join(r['missed'])) if r['missed'] else '',
                ('; error: ' + json.dumps(r['error'])) if r['error'] else '')
    if not shown:
        w.append('None.')
    w.append('')
    return '\n'.join(w)


def report(cases, results, summary, new_effort, tolerance=TOLERANCE, found=None):
    w = []
    w.append('# Replay screen: effort cut to %s' % new_effort)
    w.append('')
    if found:
        w.extend(found_lines(found))
        w.append('')
    usable = [c for c in cases if c['usable']]
    w.append('Requests found: %d; usable: %d. A rebuilt request is usable when its characters per recorded input token are '
             'within %.0f%% of its Mission\'s median (at least %d recorded requests); a saved request body always is.' % (
                 len(cases), len(usable), 100 * tolerance, MIN_CALIBRATION))
    w.append('')
    unusable = [c for c in cases if not c['usable']]
    if unusable:
        w.append('## Unusable requests')
        w.append('')
        for c in unusable:
            w.append('- %s (%s): %s' % (c['id'], c['kind'], c['reason']))
        w.append('')
    w.append('## Agreement with the originals, per kind')
    w.append('')
    if runs_note(results):
        w.append(runs_note(results))
        w.append('')
    w.append('| Kind | Requests | Old effort | Agree | New effort | Agree | Blockers missed at new | Verdict |')
    w.append('|---|---:|---|---:|---|---:|---:|---|')
    for k, S in summary.items():
        o, n = S['old'], S['new']
        w.append('| %s | %d | %s | %s | %s | %s | %d | **%s**%s |' % (
            k, S['cases'], o['effort'], rate(o['agree'], o['runs']), n['effort'], rate(n['agree'], n['runs']), n['missed'],
            'PASS' if S['pass'] else 'FAIL', '' if S['pass'] else ' (' + '; '.join(S['reasons']) + ')'))
    w.append('')
    w.append('Pass: agreement at the new effort within 1 in 20 of the old effort\'s, and no Blocker missed in a Craft Check '
             'or Margin recheck. The final call on a cut is a person\'s: read the disagreements below.')
    w.append('')
    w.append('## `unsure_because` against the disagreements')
    w.append('')
    w.append('| Kind | Effort | Replies with the field | Said unsure | Disagreements | ...of which flagged unsure |')
    w.append('|---|---|---:|---:|---:|---:|')
    for k, S in summary.items():
        for wh in ('old', 'new'):
            s = S[wh]
            w.append('| %s | %s | %d | %d | %d | %s |' % (k, s['effort'], s['with_unsure'], s['unsure_set'],
                                                         s['dis_with_unsure'], rate(s['dis_flagged'], s['dis_with_unsure'])))
    w.append('')
    w.append('Replies without the field (before the Second look ships) are left out of these columns.')
    w.append('')
    w.append('## Disagreements')
    w.append('')
    shown = by_request(results)
    for (case, _), g in shown:
        first = g['bad'][0]
        w.append('- %s, %s effort %s (%d of %d run(s) disagreed): original %s' % (
            case, first['kind'], first['effort'], len(g['bad']), g['runs'], json.dumps(first['original'], sort_keys=True)))
        for r in g['bad']:
            w[-1] += '; run %d: replay %s%s%s' % (
                r['run'], json.dumps(r['signature'], sort_keys=True),
                ('; MISSED BLOCKERS ' + ', '.join(r['missed'])) if r['missed'] else '',
                ('; unsure_because: ' + json.dumps(r['unsure'])) if r['unsure'] is not None else '')
            w[-1] += ('; error: ' + json.dumps(r['error'])) if r['error'] else ''
    if not shown:
        w.append('None.')
    w.append('')
    return '\n'.join(w)


def dry_run_text(cases, chosen, repeats, new_effort, model=None, old_effort=None, found=None):
    w = ['Dry run: nothing is sent.', '']
    if found:
        w.extend(found_lines(found))
    w.append('Requests found %d, usable %d.' % (len(cases), sum(1 for c in cases if c['usable'])))
    for k in KINDS:
        n = sum(1 for c in chosen if c['kind'] == k)
        if n and model:
            efforts = sorted(set(old_effort or c['effort'] or 'high' for c in chosen if c['kind'] == k))
            w.append('- %s: %d request(s), %d replay(s) on %s at the recorded effort (%s)' % (
                k, n, n * repeats, model, ', '.join(efforts)))
        elif n:
            w.append('- %s: %d request(s), %d replay(s) at the old effort and %d at %s' % (k, n, n * repeats, n * repeats, new_effort))
    for c in cases:
        if not c['usable']:
            w.append('Unusable: %s (%s): %s' % (c['id'], c['kind'], c['reason']))
    w.append('Total Claude Code calls it would make: %d' % ((1 if model else 2) * repeats * len(chosen)))
    return '\n'.join(w) + '\n'


def main(argv=None, runner=None, stdout=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('paths', nargs='+', help='Mission folders, or a saves folder holding them')
    ap.add_argument('--new-effort', default='medium', choices=['low', 'medium', 'high'])
    ap.add_argument('--old-effort', choices=['low', 'medium', 'high'], help='default: the effort each request was recorded at')
    ap.add_argument('--kinds', default=','.join(KINDS))
    ap.add_argument('--per-kind', type=int, default=20)
    ap.add_argument('--repeats', type=int, default=1, help='replays of each request at each effort')
    ap.add_argument('--since', type=lambda t: datetime.datetime.strptime(t, '%Y-%m-%d').date(), metavar='YYYY-MM-DD',
                    help='leave out Missions started before this date, read from the Mission id (default: all Missions)')
    ap.add_argument('--tolerance', type=float, default=TOLERANCE)
    ap.add_argument('--model', help='a model screen (#567): replay every request on this model at its recorded effort and '
                    'compare with the recorded replies (without it, requests rebuilt from a transcript use %s)' % DEFAULT_MODEL)
    ap.add_argument('--max-budget-usd', type=float, default=1.0, help='spend cap per call, a safety net')
    ap.add_argument('--dry-run', action='store_true', help='read and check the requests, send nothing')
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    stdout = stdout or sys.stdout
    kinds = [k for k in a.kinds.split(',') if k]
    bad = [k for k in kinds if k not in KINDS]
    if bad:
        ap.error('unknown kind %s; kinds are %s' % (', '.join(bad), ', '.join(KINDS)))
    cases = []
    found = discover_missions(a.paths, a.since)
    for folder, label in found['folders']:
        cases.extend(load_cases(folder, label))
    # Calibrate on every request of a Mission, whatever the kinds asked for.
    cases = [c for c in check_rebuilds(cases, a.tolerance) if c['kind'] in kinds]
    if a.dry_run:
        stdout.write(dry_run_text(cases, pick(cases, a.per_kind), a.repeats, a.new_effort, a.model, a.old_effort, found))
        return 0
    results = run(cases, runner or claude_print_runner, a.new_effort, a.old_effort, a.per_kind, a.repeats,
                  a.model or DEFAULT_MODEL, a.max_budget_usd, model_screen=bool(a.model))
    summary = summarize(results)
    text = (model_report(cases, results, summary, a.model, found) if a.model
            else report(cases, results, summary, a.new_effort, a.tolerance, found))
    if a.out:
        with open(a.out, 'w', encoding='utf-8') as f:
            f.write(text)
    else:
        stdout.write(text)
    return 0 if summary and all(S['pass'] for S in summary.values()) else 1


if __name__ == '__main__':
    sys.exit(main())
