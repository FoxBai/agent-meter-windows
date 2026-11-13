"""Windows adapter tests use synthetic data and do not access user accounts."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

try:
    import zstandard
except ImportError:
    zstandard = None
from windows import backend
from agent_meter.model import SourceError


class WindowsBackendTests(unittest.TestCase):
    def test_empty_history_directory_is_not_reported_as_zero_usage(self):
        metric = {'version': 1, 'observed_at': 1, 'account': {'status': 'disabled'}, 'history':
                  {'Codex': {'status': 'available', 'value': {'periods': {'all': {'totals':
                      {'requests': 0, 'total_tokens': 0}}}}}}}
        item = backend.compact_history(metric)['history']['Codex']
        self.assertEqual(item, {'status': 'not_provided', 'reason': 'history_no_usage_records', 'value': None})
        metric['history']['Codex'].update(status='partial', reason='codex_rollout_unreadable')
        item = backend.compact_history(metric)['history']['Codex']
        self.assertEqual(item, {'status': 'partial', 'reason': 'codex_rollout_unreadable', 'value': None})

    def test_worker_output_only_contains_panel_fields(self):
        source = {'version': 1, 'observed_at': 1, 'account': {'status': 'disabled'}, 'history':
                  {'Codex': {'status': 'partial', 'reason': 'some_skipped', 'value':
                     {'daily_buckets': [{'private_detail': 'not sent'}], 'periods': {
                         'today': {'totals': {'total_tokens': 12, 'cache_read': 8}, 'groups': [{'model': 'example'}]},
                         'all': {'totals': {'total_tokens': 60}}}}}}}
        result = backend.compact_history(source)['history']['Codex']
        self.assertEqual(result['status'], 'partial')
        self.assertEqual(result['reason'], 'some_skipped')
        self.assertEqual(result['value'], {'periods': {'today': {'totals': {'total_tokens': 12}},
                                                       'all': {'totals': {'total_tokens': 60}}}})

    def test_worker_refreshes_changed_session_and_rejects_unknown_commands(self):
        stamp = datetime.now(timezone.utc).isoformat()
        def event(n):
            return {'timestamp': stamp, 'type': 'event_msg', 'payload': {'type': 'token_count', 'info':
                    {'total_token_usage': {'input_tokens': n, 'cached_input_tokens': 0,
                                           'output_tokens': 0, 'reasoning_output_tokens': 0}}}}
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / '.codex' / 'sessions' / 'sample.jsonl'
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps(event(100)) + '\n', encoding='utf-8')
            env = dict(os.environ, CODEX_HOME='', CLAUDE_CONFIG_DIR='', DSH_HOME='')
            worker = subprocess.Popen([sys.executable, '-m', 'windows.backend', '--history-worker', '--home', root],
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                      text=True, encoding='utf-8', env=env)
            try:
                def refresh(prefix=''):
                    worker.stdin.write(prefix + '{"command":"refresh"}\n'); worker.stdin.flush()
                    return json.loads(worker.stdout.readline())['history']['Codex']['value']['periods']['all']['totals']['total_tokens']
                self.assertEqual(refresh('\ufeff'), 100)
                with path.open('a', encoding='utf-8') as f:
                    f.write(json.dumps(event(230)) + '\n')
                self.assertEqual(refresh(), 230)
                worker.stdin.write('{"command":"delete"}\n'); worker.stdin.flush()
                self.assertEqual(json.loads(worker.stdout.readline()), {'error': 'invalid_request'})
                worker.stdin.close()
                self.assertEqual(worker.wait(timeout=10), 0)
            finally:
                if worker.poll() is None:
                    worker.kill(); worker.wait(timeout=10)
                if not worker.stdin.closed:
                    worker.stdin.close()
                worker.stdout.close(); worker.stderr.close()

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

    @unittest.skipUnless(zstandard is not None, 'python-zstandard is not installed')
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
