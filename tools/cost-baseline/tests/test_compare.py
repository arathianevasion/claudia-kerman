"""Tests for `baseline.py --compare` (#550). The Missions are hand-made and tiny; none comes from a real flight."""
import contextlib
import io
import json
import os
import tempfile
import unittest

import baseline
from tests import fixtures

NOTICES = {
    'first': 'The craft is in Flight. Propose the Mission Plan: reply with kind "mission_plan".',
    'seq': 'Plan the next leg: reply with kind "sequence_plan".',
    'debrief': 'The sequence ended. Reply with kind "debrief".',
    'revised': 'The Debrief was Off-Nominal. Revise the Mission Plan: reply with kind "mission_plan".',
}


def usage(read, write):
    return {'input_tokens': 2, 'cache_creation_input_tokens': write,
            'cache_creation': {'ephemeral_1h_input_tokens': write, 'ephemeral_5m_input_tokens': 0},
            'cache_read_input_tokens': read, 'output_tokens': 100}


def sse(text, use):
    ev = [{'type': 'message_start', 'message': {'model': 'claude-sonnet-5-5'}},
          {'type': 'content_block_delta', 'delta': {'type': 'text_delta', 'text': text}},
          {'type': 'message_delta', 'delta': {'stop_reason': 'end_turn'}, 'usage': use}]
    return ': attempt 1\n' + ''.join('event: x\ndata: %s\n\n' % json.dumps(e) for e in ev)


def sequence(name):
    return {'kind': 'sequence_plan', 'say': 'ok', 'sequence_plan': {'name': name}}


def make(saves, mid, turns, paid=0.5):
    """turns: (notice key, reply, cache read, cache write). One committed response each, in order."""
    folder = os.path.join(saves, 'Save', 'ClaudiaKerman', 'missions', mid)
    messages = []
    for n, (key, reply, read, write) in enumerate(turns, 1):
        text = json.dumps(reply)
        messages.append({'role': 'system', 'content': NOTICES[key]})
        messages.append({'role': 'assistant', 'content': [{'type': 'text', 'text': text}]})
        fixtures.write(os.path.join(folder, 'stream-dumps', 'request-20261030-100000-%d.sse' % n), sse(text, usage(read, write)))
    fixtures.write(os.path.join(folder, 'transcript.json'), json.dumps({'system': [], 'messages': messages}))
    fixtures.write(os.path.join(folder, 'cost-log.jsonl'), json.dumps(
        {'event': 'paid', 'request': 1, 'amount': paid, 'at': '2026-10-30T10:00:00Z'}) + '\n')
    return folder


FLOWN = [('first', fixtures.plan('a'), 0, 1000), ('seq', sequence('leg 1'), 1000, 200),
         ('debrief', fixtures.debrief('Off-Nominal'), 1200, 100)]


class CompareTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        # Two installs, each with its own saves folder.
        self.stable = os.path.join(self._tmp.name, 'KSP_stable', 'saves')
        self.spike = os.path.join(self._tmp.name, 'KSP_spike', 'saves')
        make(self.stable, 'm-ref', FLOWN + [('revised', fixtures.plan('b'), 300, 900), ('seq', sequence('leg 2'), 1200, 100)])
        make(self.spike, 'm-new', FLOWN + [('revised', fixtures.plan('b'), 320, 900), ('seq', sequence('leg 2'), 1220, 100),
                                           ('debrief', fixtures.debrief('Off-Nominal'), 1320, 100),
                                           ('revised', fixtures.plan('c'), 320, 1100)])

    def _run(self, *argv):
        out = io.StringIO()
        old = baseline.sys.argv
        baseline.sys.argv = ['baseline.py'] + list(argv)
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                baseline.sys.stdout = out
                baseline.main()
        finally:
            baseline.sys.argv = old
            baseline.sys.stdout = baseline.sys.__stdout__
        return out.getvalue()

    def _compare(self, *extra):
        return self._run(self.spike, self.stable, '--reference', 'm-ref', '--compare', 'm-new', *extra)

    def _row(self, text, mission):
        return next(l for l in text.splitlines() if l.startswith('| ' + mission) or l.startswith('| **' + mission))

    def test_prints_a_row_per_mission_and_then_the_reference_with_the_install_each_came_from(self):
        text = self._compare()
        new, ref = self._row(text, 'm-new'), self._row(text, 'm-ref')
        self.assertLess(text.index(new), text.index(ref))
        self.assertIn('| KSP_spike |', new)
        self.assertIn('| KSP_stable |', ref)

    def test_counts_legs_revised_plans_and_the_prefix_the_cache_read_stopped_at(self):
        text = self._compare()
        new, ref = [c.strip() for c in self._row(text, 'm-new').split('|')], [c.strip() for c in self._row(text, 'm-ref').split('|')]
        # | Mission | Install | Published | Mod logged | Legs | Per leg | Revised | Compactions | Misses | Prefix | ...
        self.assertEqual('2', new[5])
        self.assertEqual('2', ref[5])
        self.assertTrue(ref[7].startswith('1, '))
        self.assertTrue(new[7].startswith('2, '))
        self.assertEqual('300', ref[10])
        self.assertEqual('320', new[10])

    def test_shows_the_mods_logged_total_beside_the_published_one(self):
        ref = [c.strip() for c in self._row(self._compare(), 'm-ref').split('|')]
        self.assertEqual('$0.50', ref[4])

    def test_shows_the_change_against_the_reference_and_the_distance_to_the_goal(self):
        text = self._compare('--goal', '0.01')
        self.assertIn('Prefix vs reference', text)
        self.assertIn('+20 (+7%)', text)
        self.assertIn('$0.01', text)

    def test_a_mission_under_the_goal_shows_a_negative_amount_with_the_sign_before_the_dollar(self):
        text = self._run(self.spike, '--reference', 'm-new', '--compare', 'm-new', '--goal', '5')
        self.assertRegex(text, r'\| -\$\d+\.\d\d \(0\.\dx\) \|')

    def test_a_mission_named_twice_gets_one_row(self):
        text = self._run(self.spike, '--reference', 'm-gone', '--compare', 'm-new', 'm-new')
        self.assertEqual(1, sum(1 for l in text.splitlines() if l.startswith('| m-new | KSP_spike')))

    def test_the_prefix_is_blank_when_no_request_read_only_the_prefix(self):
        make(self.spike, 'm-flat', FLOWN)
        text = self._run(self.spike, '--reference', 'm-flat', '--compare', 'm-flat')
        cells = [c.strip() for c in self._row(text, 'm-flat').split('|')]
        self.assertEqual('', cells[10])

    def test_a_named_mission_that_is_in_no_folder_is_an_error(self):
        with self.assertRaises(SystemExit) as e:
            self._run(self.spike, '--compare', 'm-nope')
        self.assertIn('m-nope', str(e.exception))

    def test_a_missing_reference_is_said_not_guessed(self):
        text = self._run(self.spike, '--reference', 'm-gone', '--compare', 'm-new')
        self.assertIn('reference Mission was not found', text)

    def test_nothing_is_written_under_a_saves_folder(self):
        def listing():
            return sorted((d, tuple(sorted(f))) for root in (self.stable, self.spike) for d, _, f in os.walk(root))
        before = listing()
        mtimes = {p: os.path.getmtime(p) for root in (self.stable, self.spike) for d, _, fs in os.walk(root)
                  for p in [os.path.join(d, f) for f in fs]}
        self._compare()
        self._run(self.spike, self.stable)
        self.assertEqual(before, listing())
        self.assertEqual(mtimes, {p: os.path.getmtime(p) for p in mtimes})

    def test_the_full_report_reads_every_folder_and_names_each_missions_install(self):
        text = self._run(self.spike, self.stable, '--reference', 'm-ref')
        self.assertIn('KSP_spike, Save', text)
        self.assertIn('KSP_stable, Save', text)
        self.assertIn('Reference Mun Mission m-ref (KSP_stable', text)


class InstallTests(unittest.TestCase):
    def test_an_install_is_named_for_the_folder_above_saves(self):
        self.assertEqual('KSP_stable', baseline.install_of('/home/me/KSP_stable/saves'))
        self.assertEqual('elsewhere', baseline.install_of('/tmp/elsewhere'))


if __name__ == '__main__':
    unittest.main()
