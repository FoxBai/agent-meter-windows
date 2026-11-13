"""Small Windows-facing adapter over the upstream read-only collectors.

Outputs only normalized metrics; never emits session content or credentials.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import time
from concurrent.futures import ThreadPoolExecutor

from agent_meter.cli_sessions import claude_tokens_metric, codex_tokens_metric, collect_gemini_sessions, mcode_tokens_metric
from agent_meter.codex import normalize_limits
from agent_meter.model import SourceError, metric
from agent_meter.transport import CodexRPC
from agent_meter.workbuddy_tokens import collect_workbuddy_tokens
from agent_meter.zcode_local import zcode_tokens_metric
from agent_meter.dsh_tokens import collect_dsh_sessions
from agent_meter.record_cache import records_cache
from agent_meter.kimi_tokens import collect_kimi_tokens
from agent_meter.kimi_legacy_tokens import collect_kimi_legacy_tokens
from agent_meter.opencode_tokens import collect_opencode_tokens, readonly_database as opencode_database
from agent_meter.mimocode_tokens import collect_mimocode_tokens, collect_deveco_tokens
from agent_meter.qoder_tokens import collect_qoder_credits


def codex_binary():
    # Prefer a real executable: .cmd wrappers need a shell and must not be
    # interpolated into a command line. Codex Desktop also ships its own exe.
    found = shutil.which("codex.exe")
    if found:
        return found
    base = Path(os.environ.get("LOCALAPPDATA", "")) / "OpenAI" / "Codex" / "bin"
    candidates = list(base.glob("*/codex.exe")) if base.is_dir() else []
    return str(max(candidates, key=lambda p: p.stat().st_mtime)) if candidates else None


def quota(binary, now):
    with CodexRPC(binary, timeout=25) as rpc:
        result = rpc.call("account/rateLimits/read", {"excludeResetCreditDetails": False, "supportsLunaReserve": False})
    source = normalize_limits(result, now)
    return {"status": source["status"], "quota": source["metrics"]["quota"],
            "credits": source["metrics"]["credits"], "reset_cards": source["metrics"]["reset_cards"]}


def histories(home, now):
    codex = Path(os.environ.get("CODEX_HOME") or home / ".codex")
    claude = Path(os.environ.get("CLAUDE_CONFIG_DIR") or home / ".claude")
    kimi_root = Path(os.environ.get("KIMI_CODE_HOME") or home / ".kimi-code")
    xdg = os.environ.get("XDG_DATA_HOME")
    data_root = Path(xdg) if xdg and Path(xdg).is_absolute() else home / ".local" / "share"
    opencode_override = os.environ.get("OPENCODE_DB")
    opencode_path = (data_root / "opencode" / "opencode.db" if not opencode_override else
                     Path(opencode_override) if Path(opencode_override).is_absolute() else data_root / "opencode" / opencode_override)
    def opencode():
        if opencode_override == ":memory:":
            raise SourceError("opencode_memory_database")
        result = collect_opencode_tokens(opencode_path, now)["metrics"]["tokens"]
        # Newer OpenCode releases can store messages in a second schema that
        # the upstream v1 parser does not read. Do not present v1 as complete.
        with opencode_database(opencode_path) as connection:
            has_v2 = connection.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='session_message'").fetchone()
            if has_v2 and connection.execute("SELECT 1 FROM session_message LIMIT 1").fetchone():
                result["status"] = "partial"
                result["reason"] = "opencode_v2_unread"
        return result
    return {
        "Codex": lambda: codex_tokens_metric(codex / "sessions", codex / "archived_sessions", now),
        "Claude Code": lambda: claude_tokens_metric(claude / "projects", now),
        "Gemini CLI": lambda: collect_gemini_sessions(home / ".gemini", now)["metrics"]["tokens"],
        "WorkBuddy": lambda: collect_workbuddy_tokens(home / ".workbuddy" / "projects", now),
        "ZCode": lambda: zcode_tokens_metric(home / ".zcode" / "cli" / "db" / "db.sqlite", now),
        "DeepSeek Harness": lambda: collect_dsh_sessions(Path(os.environ.get("DSH_HOME") or home / ".dsh") / "sessions", now)["metrics"]["tokens"],
        "Kimi Code": lambda: collect_kimi_tokens(home, now, sessions_root=kimi_root / "sessions"),
        "Kimi CLI (旧版)": lambda: collect_kimi_legacy_tokens(home / ".kimi" / "sessions", now)["metrics"]["tokens"],
        "OpenCode": opencode,
        "MiniMax Code": lambda: mcode_tokens_metric(home / ".minimax" / "v2" / "sqlite" / "runtime-state.sqlite", now),
        "MiMo Code": lambda: collect_mimocode_tokens(data_root / "mimocode" / "mimocode.db", now)["metrics"]["tokens"],
        "DevECo": lambda: collect_deveco_tokens(data_root / "deveco" / "deveco-stable.db", now)["metrics"]["tokens"],
    }


def guarded(fn):
    try:
        return fn()
    except SourceError as exc:
        return {"status": "unavailable", "reason": exc.code, "value": None}
    except (OSError, ValueError, TypeError, KeyError, OverflowError):
        return {"status": "error", "reason": "read_failed", "value": None}


def collect(local_only=False, home=None, account_only=False):
    now = time.time()
    home = Path(home) if home else Path.home()
    if account_only:
        return {"version": 1, "observed_at": int(now),
                "account": guarded(lambda: quota(codex_binary(), now)), "history": {}}
    jobs = histories(home, now)
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = {name: executor.submit(guarded, fn) for name, fn in jobs.items()}
        qoder = executor.submit(guarded, lambda: metric(collect_qoder_credits(home / ".qoder-cn" / "projects", now)))
        account = None if local_only else executor.submit(guarded, lambda: quota(codex_binary(), now))
        return {"version": 1, "observed_at": int(now),
                "account": {"status": "disabled", "reason": "local_only"} if account is None else account.result(),
                "history": {name: future.result() for name, future in futures.items()},
                "local_credits": {"Qoder CN": qoder.result()}}


def compact_history(result):
    """Send only fields the tray renders, keeping large per-model series local."""
    compact = {"version": result["version"], "observed_at": result["observed_at"],
               "account": result["account"], "history": {}}
    for name, item in result["history"].items():
        value = item.get("value")
        periods = value.get("periods", {}) if isinstance(value, dict) else {}
        all_period = periods.get("all", {})
        all_totals = all_period.get("totals", {}) if isinstance(all_period, dict) else {}
        # An existing but empty history directory is not evidence of zero use.
        if isinstance(all_totals, dict) and all_totals.get("requests") == 0:
            partial = item.get("status") == "partial"
            compact["history"][name] = {"status": "partial" if partial else "not_provided",
                                        "reason": item.get("reason") if partial else "history_no_usage_records", "value": None}
            continue
        selected = {}
        for period in ("today", "7d", "30d", "all"):
            bucket = periods.get(period)
            totals = bucket.get("totals") if isinstance(bucket, dict) else None
            if isinstance(totals, dict) and "total_tokens" in totals:
                selected[period] = {"totals": {"total_tokens": totals["total_tokens"]}}
        compact["history"][name] = {"status": item.get("status"), "reason": item.get("reason"),
                                    "value": {"periods": selected} if selected else None}
    compact["local_credits"] = {}
    for name, item in result.get("local_credits", {}).items():
        value = item.get("value")
        periods = value.get("periods", {}) if isinstance(value, dict) else {}
        all_totals = periods.get("all", {}).get("totals", {})
        selected = {}
        if all_totals.get("requests", 0) > 0:
            for period in ("today", "7d", "30d", "all"):
                totals = periods.get(period, {}).get("totals", {})
                if isinstance(totals.get("credits"), str):
                    selected[period] = {"totals": {"credits": totals["credits"],
                                                   "billable_credits": totals.get("billable_credits")}}
        status = item.get("status")
        compact["local_credits"][name] = {"status": status if selected or status in {"error", "unavailable", "partial"} else "not_provided",
                                          "reason": item.get("reason") or (None if selected else "history_no_usage_records"),
                                          "value": {"periods": selected} if selected else None}
    return compact


def history_worker(home=None):
    """Keep the upstream parsed-usage cache in memory between refreshes.

    No transcripts or credentials are persisted. File metadata invalidates a
    cached item when its source changes; upstream applies its normal eviction
    and completeness rules. The tiny stdin protocol exposes no path or code
    execution interface.
    """
    records_cache.max_bytes = 128 * 1024 * 1024
    records_cache.max_entries = 50000
    for line in sys.stdin:
        if len(line) > 1024:
            print('{"error":"invalid_request"}', flush=True)
            continue
        try:
            request = json.loads(line.lstrip("\ufeff"))
            if request != {"command": "refresh"}:
                raise ValueError("invalid request")
            result = compact_history(collect(local_only=True, home=home))
            print(json.dumps(result, ensure_ascii=False, separators=(",", ":")), flush=True)
        except (ValueError, TypeError, KeyError):
            print('{"error":"invalid_request"}', flush=True)
        except Exception:
            # Never return exception text, paths, source data, or credentials.
            print('{"error":"history_scan_failed"}', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--local-only", action="store_true")
    parser.add_argument("--account-only", action="store_true")
    parser.add_argument("--history-worker", action="store_true")
    parser.add_argument("--home", type=Path, help="Override home for isolated testing")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if args.history_worker:
        sys.stdin.reconfigure(encoding="utf-8")
        history_worker(args.home)
        return
    print(json.dumps(collect(args.local_only, args.home, args.account_only), ensure_ascii=False, separators=(",", ":")))


if __name__ == "__main__":
    main()
