"""Windows 适配层测试：只使用模拟数据，不访问用户账户。"""
import json
import os
import sqlite3
import subprocess
import sys
import time
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

try:
    import zstandard
except ImportError:
    zstandard = None
from windows import backend
from agent_meter.model import SourceError
from agent_meter.kimi import process_exists


class WindowsBackendTests(unittest.TestCase):
    def test_kimi_process_probe_does_not_terminate_process(self):
        child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])
        try:
            self.assertTrue(process_exists(child.pid))
            self.assertIsNone(child.poll())
        finally:
            child.terminate(); child.wait(timeout=10)

    def test_kimi_existing_local_server_quota(self):
        token = 'synthetic-local-token'
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_GET(self):
                if self.path != '/api/v1/oauth/usage' or self.headers.get('Authorization') != 'Bearer ' + token:
                    self.send_error(401); return
                payload = {'code': 0, 'data': {'kind': 'ok', 'quota': {'usages': {
                    'limit5h': {'usedRatio': .25, 'resetAt': None}},
                    'extraUsage': {'balanceCents': 1234, 'currency': 'CNY'}}}}
                body = json.dumps(payload).encode()
                self.send_response(200); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', {'KIMI_CODE_HOME': ''}):
                kimi_root = Path(root) / '.kimi-code'
                instances = kimi_root / 'server' / 'instances'; instances.mkdir(parents=True)
                (kimi_root / 'server.token').write_text(token, encoding='utf-8')
                (instances / 'test.json').write_text(json.dumps({'pid': os.getpid(), 'host': '127.0.0.1',
                                                                 'port': server.server_port}), encoding='utf-8')
                result = backend.kimi_quota(Path(root), time.time())
            self.assertEqual(result['quota']['value'][0]['remaining_percent'], 75)
            self.assertEqual(result['credits']['value']['amount'], '12.34')
            self.assertNotIn(token, json.dumps(result))
        finally:
            server.shutdown(); server.server_close(); thread.join(timeout=5)

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
        self.assertEqual(result['value'], {'periods': {'today': {'totals': {'total_tokens': 12, 'cache_read': 8}},
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

    def test_model_groups_are_bounded_sorted_and_exclude_other_fields(self):
        rows = [{'model': f'model-{n}', 'total_tokens': n, 'private_detail': '不应发送'} for n in range(25)]
        rows.extend([{'model': 'bad', 'total_tokens': -1}, {'model': 'bad', 'total_tokens': True}])
        source = {'version': 1, 'observed_at': 1, 'account': {}, 'history': {'测试': {
            'status': 'available', 'value': {'periods': {'7d': {'totals': {'total_tokens': 300}, 'groups': rows}}}}}}
        period = backend.compact_history(source)['history']['测试']['value']['periods']['7d']
        self.assertEqual(len(period['groups']), 20)
        self.assertEqual(period['groups'][0], {'model': 'model-24', 'total_tokens': 24})
        self.assertTrue(period['groups_truncated'])

    def test_daily_trend_uses_beijing_dates_and_marks_partial_sources(self):
        now = datetime(2026, 11, 14, 16, 30, tzinfo=timezone.utc).timestamp()
        source = {'version': 1, 'observed_at': now, 'account': {}, 'history': {
            '甲': {'status': 'available', 'value': {'periods': {}, 'daily_buckets': [
                {'date': '2026-11-14', 'tokens': 15}, {'date': '2026-11-15', 'tokens': 7},
                {'date': '2026-11-08', 'tokens': 500}, {'date': '2026-11-16', 'tokens': 500}]}},
            '乙': {'status': 'partial', 'value': {'periods': {}, 'daily_buckets': [
                {'date': '2026-11-15', 'tokens': 3}]}}}}
        trend = backend.compact_history(source)['daily_trend']
        self.assertEqual(trend['days'][0], {'date': '2026-11-09', 'tokens': 0})
        self.assertEqual(trend['days'][-2:], [{'date': '2026-11-14', 'tokens': 15},
                                              {'date': '2026-11-15', 'tokens': 10}])
        self.assertEqual(trend['sources'], 2)
        self.assertTrue(trend['incomplete'])

    def test_account_only_does_not_start_history_scan(self):
        with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', {'KIMI_CODE_HOME': ''}), \
             patch.object(backend, 'histories', side_effect=AssertionError('history scan called')), \
             patch.object(backend, 'codex_binary', return_value='codex.exe'), \
             patch.object(backend, 'quota', return_value={'status': 'available'}):
            result = backend.collect(account_only=True, home=root)
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
        event = {'timestamp': (datetime.now(timezone.utc) - timedelta(seconds=5)).isoformat(), 'type': 'event_msg',
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
