"""Windows adapter tests use synthetic data and do not access user accounts."""
import json
import os
import sqlite3
import subprocess
import sys
import time
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
    def test_minimax_reads_committed_wal_updates_on_windows(self):
        with tempfile.TemporaryDirectory() as root:
            db = Path(root) / '.minimax' / 'v2' / 'sqlite' / 'runtime-state.sqlite'
            db.parent.mkdir(parents=True)
            now = time.time()
            writer = sqlite3.connect(db)
            try:
                writer.execute('PRAGMA journal_mode=WAL')
                writer.execute('PRAGMA wal_autocheckpoint=0')
                writer.execute('CREATE TABLE local_runtime_token_usage (id INTEGER, session_id TEXT, turn_id TEXT, model TEXT, ts INTEGER, input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER)')
                row = (1, 's', 't', 'demo', int(now*1000)-1000, 10, 5, 0, 0, 0)
                writer.execute('INSERT INTO local_runtime_token_usage VALUES (?,?,?,?,?,?,?,?,?,?)', row)
                writer.commit()
                read = backend.histories(Path(root), now)['MiniMax Code']
                self.assertEqual(read()['value']['periods']['all']['totals']['total_tokens'], 15)
                writer.execute('INSERT INTO local_runtime_token_usage VALUES (?,?,?,?,?,?,?,?,?,?)', (2, 's', 'u', 'demo', row[4], 20, 3, 0, 0, 0))
                writer.commit()
                self.assertEqual(read()['value']['periods']['all']['totals']['total_tokens'], 38)
            finally:
                writer.close()

    def test_custom_kimi_and_opencode_locations(self):
        with tempfile.TemporaryDirectory() as root:
            home = Path(root) / 'home'
            xdg = Path(root) / 'data'
            kimi = Path(root) / 'kimi-custom'
            variables = {'KIMI_CODE_HOME': str(kimi), 'XDG_DATA_HOME': str(xdg), 'OPENCODE_DB': 'custom.db'}
            with patch.dict('os.environ', variables), \
                 patch.object(backend, 'collect_kimi_tokens', return_value={'status': 'available'}) as kimi_read, \
                 patch.object(backend, 'collect_opencode_tokens', return_value={'metrics': {'tokens': {'status': 'available'}}}) as opencode_read, \
                 patch.object(backend, 'opencode_database') as opencode_db:
                opencode_db.return_value.__enter__.return_value.execute.return_value.fetchone.return_value = None
                jobs = backend.histories(home, 123)
                jobs['Kimi Code'](); jobs['OpenCode']()
                kimi_read.assert_called_once_with(home, 123, sessions_root=kimi / 'sessions')
                opencode_read.assert_called_once_with(xdg / 'opencode' / 'custom.db', 123)
            with patch.dict('os.environ', {'OPENCODE_DB': ':memory:'}):
                result = backend.guarded(backend.histories(home, 123)['OpenCode'])
            self.assertEqual(result['reason'], 'opencode_memory_database')

    def test_opencode_v2_rows_mark_history_partial(self):
        with tempfile.TemporaryDirectory() as root:
            db = Path(root) / 'opencode.db'
            now = time.time()
            message = {'role': 'assistant', 'modelID': 'test', 'tokens':
                       {'input': 10, 'output': 4, 'reasoning': 0, 'cache': {'read': 0, 'write': 0}}}
            conn = sqlite3.connect(db)
            conn.execute('CREATE TABLE message(time_created INTEGER, data TEXT)')
            conn.execute('INSERT INTO message VALUES (?, ?)', (int(now * 1000) - 1000, json.dumps(message)))
            conn.execute('CREATE TABLE session_message(id TEXT)')
            conn.execute("INSERT INTO session_message VALUES ('new-schema')")
            conn.commit(); conn.close()
            with patch.dict('os.environ', {'OPENCODE_DB': str(db)}):
                result = backend.histories(Path(root), now)['OpenCode']()
            self.assertEqual((result['status'], result['reason']), ('partial', 'opencode_v2_unread'))
            self.assertEqual(result['value']['periods']['all']['totals']['total_tokens'], 14)

    def test_qoder_credits_are_separate_and_keep_decimal_strings(self):
        source = {'version': 1, 'observed_at': 1, 'account': {'status': 'disabled'}, 'history': {},
                  'local_credits': {'Qoder CN': {'status': 'available', 'value': {'periods': {
                      'today': {'totals': {'requests': 2, 'credits': '1.250', 'billable_credits': '0.500'}},
                      'all': {'totals': {'requests': 2, 'credits': '1.250', 'billable_credits': '0.500'}}}}}}}
        result = backend.compact_history(source)
        self.assertEqual(result['history'], {})
        self.assertEqual(result['local_credits']['Qoder CN']['value']['periods']['all']['totals'],
                         {'credits': '1.250', 'billable_credits': '0.500'})
        source['local_credits']['Qoder CN'] = {'status': 'error', 'reason': 'read_failed', 'value': None}
        self.assertEqual(backend.compact_history(source)['local_credits']['Qoder CN'],
                         {'status': 'error', 'reason': 'read_failed', 'value': None})

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
                         '7d': {'totals': {'total_tokens': 20}}, '30d': {'totals': {'total_tokens': 35}},
                         'all': {'totals': {'total_tokens': 60}}}}}}}
        result = backend.compact_history(source)['history']['Codex']
        self.assertEqual(result['status'], 'partial')
        self.assertEqual(result['reason'], 'some_skipped')
        self.assertEqual(result['value'], {'periods': {'today': {'totals': {'total_tokens': 12}},
                                                       '7d': {'totals': {'total_tokens': 20}},
                                                       '30d': {'totals': {'total_tokens': 35}},
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
            env = dict(os.environ, CODEX_HOME='', CLAUDE_CONFIG_DIR='', DSH_HOME='', KIMI_CODE_HOME='', XDG_DATA_HOME='', OPENCODE_DB='')
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
        with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', {'CODEX_HOME': '', 'CLAUDE_CONFIG_DIR': '', 'DSH_HOME': '', 'KIMI_CODE_HOME': '', 'XDG_DATA_HOME': '', 'OPENCODE_DB': ''}):
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
        with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', {'CODEX_HOME': '', 'CLAUDE_CONFIG_DIR': '', 'DSH_HOME': '', 'KIMI_CODE_HOME': '', 'XDG_DATA_HOME': '', 'OPENCODE_DB': ''}):
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
