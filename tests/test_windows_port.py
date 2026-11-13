"""Windows adapter tests use synthetic data and do not access user accounts."""
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

import zstandard
from windows import backend
from agent_meter.model import SourceError


class WindowsBackendTests(unittest.TestCase):
    def test_account_only_does_not_start_history_scan(self):
        with patch.object(backend, 'histories', side_effect=AssertionError('history scan called')), \
             patch.object(backend, 'codex_binary', return_value='codex.exe'), \
             patch.object(backend, 'quota', return_value={'status': 'available'}):
            result = backend.collect(account_only=True)
        self.assertEqual(result['account']['status'], 'available')
        self.assertEqual(result['history'], {})

    def test_missing_histories_are_not_zero(self):
        with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', {'CODEX_HOME': '', 'CLAUDE_CONFIG_DIR': '', 'DSH_HOME': ''}):
            result = backend.collect(local_only=True, home=root)
        self.assertEqual(result['account']['status'], 'disabled')
        for item in result['history'].values():
            self.assertEqual(item['status'], 'unavailable')
            self.assertIsNone(item['value'])

    def test_compressed_codex_usage(self):
        event = {'timestamp': datetime.now(timezone.utc).isoformat(), 'type': 'event_msg',
                 'payload': {'type': 'token_count', 'info': {'total_token_usage':
                     {'input_tokens': 100, 'cached_input_tokens': 40, 'output_tokens': 20, 'reasoning_output_tokens': 0}}}}
        with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', {'CODEX_HOME': '', 'CLAUDE_CONFIG_DIR': '', 'DSH_HOME': ''}):
            folder = Path(root) / '.codex' / 'sessions'
            folder.mkdir(parents=True)
            (folder / 'synthetic.jsonl.zst').write_bytes(zstandard.ZstdCompressor().compress((json.dumps(event) + '\n').encode()))
            result = backend.collect(local_only=True, home=root)
        codex = result['history']['Codex']
        self.assertEqual(codex['status'], 'available')
        totals = codex['value']['periods']['all']['totals']
        self.assertEqual((totals['fresh_input'], totals['cache_read'], totals['output']), (60, 40, 20))

    def test_quota_read_is_limited_to_read_only_rpc(self):
        class FakeRPC:
            def __init__(self, binary, timeout): pass
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def call(self, method, params):
                self.method = method
                self.params = params
                self.assertion()
                return {'rateLimits': {'primary': {'usedPercent': 25, 'windowDurationMins': 300, 'resetsAt': None}}}
            def assertion(self):
                assert self.method == 'account/rateLimits/read'
        with patch.object(backend, 'CodexRPC', FakeRPC):
            result = backend.quota('unused', 1794600000)
        self.assertEqual(result['quota']['value'][0]['remaining_percent'], 75)

    def test_provider_errors_are_sanitized(self):
        def fail(): raise SourceError('rpc_timeout')
        self.assertEqual(backend.guarded(fail), {'status': 'unavailable', 'reason': 'rpc_timeout', 'value': None})


if __name__ == '__main__':
    unittest.main()
