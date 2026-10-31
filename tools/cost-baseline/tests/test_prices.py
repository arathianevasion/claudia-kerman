"""Tests for the price table in `baseline.py` (#566): Haiku 5.5's two tiers, chosen by the request's prompt size."""
import unittest

import baseline


def usage(input_tokens=0, w1h=0, read=0, output=0):
    return {'input_tokens': input_tokens, 'cache_creation_input_tokens': w1h,
            'cache_creation': {'ephemeral_1h_input_tokens': w1h, 'ephemeral_5m_input_tokens': 0},
            'cache_read_input_tokens': read, 'output_tokens': output}


class HaikuTierTests(unittest.TestCase):
    def cost(self, use):
        rows = baseline.iteration_rows(use, 'claude-haiku-5-5')
        self.assertEqual(1, len(rows))
        return rows[0]['cost']

    def test_a_prompt_up_to_100k_is_priced_at_the_short_tier(self):
        # 10k uncached, 20k written, 70k read (exactly 100,000), 1k out.
        c = self.cost(usage(10000, 20000, 70000, 1000))
        self.assertAlmostEqual((10000 * 0.10 + 20000 * 0.20 + 70000 * 0.01 + 1000 * 0.50) / 1e6, c)

    def test_a_prompt_over_100k_is_priced_at_the_long_tier(self):
        c = self.cost(usage(10000, 20000, 70001, 1000))
        self.assertAlmostEqual((10000 * 0.50 + 20000 * 1.00 + 70001 * 0.05 + 1000 * 2.50) / 1e6, c)

    def test_the_prompt_counts_cache_reads_and_writes_not_only_uncached_input(self):
        self.assertAlmostEqual((150000 * 0.05 + 100 * 2.50) / 1e6, self.cost(usage(0, 0, 150000, 100)))

    def test_a_model_with_one_tier_ignores_the_prompt_size(self):
        rows = baseline.iteration_rows(usage(0, 0, 500000, 100), 'claude-sonnet-5-5')
        self.assertAlmostEqual((500000 * 0.10 + 100 * 10.0) / 1e6, rows[0]['cost'])


if __name__ == '__main__':
    unittest.main()
