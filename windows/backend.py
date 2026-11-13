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

from agent_meter.cli_sessions import claude_tokens_metric, codex_tokens_metric, collect_gemini_sessions
from agent_meter.codex import normalize_limits
from agent_meter.model import SourceError
from agent_meter.transport import CodexRPC
from agent_meter.workbuddy_tokens import collect_workbuddy_tokens
from agent_meter.zcode_local import zcode_tokens_metric
from agent_meter.dsh_tokens import collect_dsh_sessions
from agent_meter.record_cache import records_cache


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
    return {
        "Codex": lambda: codex_tokens_metric(codex / "sessions", codex / "archived_sessions", now),
        "Claude Code": lambda: claude_tokens_metric(claude / "projects", now),
        "Gemini CLI": lambda: collect_gemini_sessions(home / ".gemini", now)["metrics"]["tokens"],
        "WorkBuddy": lambda: collect_workbuddy_tokens(home / ".workbuddy" / "projects", now),
        "ZCode": lambda: zcode_tokens_metric(home / ".zcode" / "cli" / "db" / "db.sqlite", now),
        "DeepSeek Harness": lambda: collect_dsh_sessions(Path(os.environ.get("DSH_HOME") or home / ".dsh") / "sessions", now)["metrics"]["tokens"],
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
        account = None if local_only else executor.submit(guarded, lambda: quota(codex_binary(), now))
        return {"version": 1, "observed_at": int(now),
                "account": {"status": "disabled", "reason": "local_only"} if account is None else account.result(),
                "history": {name: future.result() for name, future in futures.items()}}


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
        for period in ("today", "all"):
            bucket = periods.get(period)
            totals = bucket.get("totals") if isinstance(bucket, dict) else None
            if isinstance(totals, dict) and "total_tokens" in totals:
                selected[period] = {"totals": {"total_tokens": totals["total_tokens"]}}
        compact["history"][name] = {"status": item.get("status"), "reason": item.get("reason"),
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
