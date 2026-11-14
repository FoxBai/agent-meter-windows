"""Windows 适配层：复用上游只读采集器，仅输出指标，不输出会话内容或凭据。"""
import argparse
import json
import math
import os
from pathlib import Path
import shutil
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import date, datetime, timedelta
from decimal import Decimal, InvalidOperation

from agent_meter.cli_sessions import claude_tokens_metric, codex_tokens_metric, collect_gemini_sessions, mcode_tokens_metric
from agent_meter.claude_code import collect_claude
from agent_meter.codex import normalize_limits
from agent_meter.model import DISPLAY_ZONE, SourceError, metric
from agent_meter.transport import CodexRPC
from agent_meter.workbuddy_tokens import collect_workbuddy_tokens
from agent_meter.zcode_local import zcode_tokens_metric
from agent_meter.dsh_tokens import collect_dsh_sessions
from agent_meter.record_cache import records_cache
from agent_meter.prices import PriceTable, estimate_source_cost, normalize_model
from agent_meter.kimi_tokens import collect_kimi_tokens
from agent_meter.kimi import collect_kimi
from agent_meter.kimi_legacy_tokens import collect_kimi_legacy_tokens
from agent_meter.opencode_tokens import collect_opencode_tokens, readonly_database as opencode_database
from agent_meter.mimocode_tokens import collect_mimocode_tokens, collect_deveco_tokens
from agent_meter.minimax import collect_minimax
from agent_meter.qoder_tokens import collect_qoder_credits


def codex_binary():
    # 优先使用真实可执行文件，避免把 .cmd 包装器拼入 shell 命令。
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


def kimi_quota(home, now):
    root = Path(os.environ.get("KIMI_CODE_HOME") or home / ".kimi-code")
    # 只连接已运行的本地服务，不启动辅助进程、不发送提示词。
    source = collect_kimi(home, None, now, timeout=3, start_server=False, server_root=root)
    return {"status": source["status"], "quota": source["metrics"]["quota"],
            "credits": source["metrics"]["credits"]}


def claude_quota(home, now):
    root = Path(os.environ.get("CLAUDE_CONFIG_DIR") or home / ".claude")
    credentials = root / ".credentials.json"
    # Windows 只尝试 Claude Code 已有的本地凭据文件，不调用 macOS 钥匙串，也不刷新令牌。
    if not credentials.is_file():
        raise SourceError("claude_subscription_login_missing")
    source = collect_claude(home, now, timeout=12, credential_path=credentials)
    return {"status": source["status"], "quota": source["metrics"]["quota"]}


def minimax_quota(home, now):
    # 仅在用户启用时读取已有环境变量；不保存密钥，也不执行外部脚本。
    key = os.environ.get("MINIMAX_TOKEN_PLAN_KEY")
    if not key:
        raise SourceError("minimax_credential_not_connected")
    source = collect_minimax(home, now, timeout=12, app_key=key)
    return {"status": source["status"], "quota": source["metrics"]["quota"]}


def account_sources(home, now, disabled_accounts=frozenset(), enable_minimax=False):
    with ThreadPoolExecutor(max_workers=4) as executor:
        jobs = {"codex": lambda: quota(codex_binary(), now),
                "kimi": lambda: kimi_quota(home, now), "claude": lambda: claude_quota(home, now),
                "minimax": lambda: minimax_quota(home, now)}
        futures = {name: executor.submit(guarded, job) for name, job in jobs.items()
                   if name not in disabled_accounts and (name != "minimax" or enable_minimax)}
        def result(name):
            return futures[name].result() if name in futures else {"status": "disabled", "reason": "disabled_by_user"}
        return result("codex"), {"Kimi Code": result("kimi"), "Claude Code": result("claude"), "MiniMax Code": result("minimax")}


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
        # 上游解析器尚不读取 OpenCode v2 消息；发现新版记录时标记不完整。
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


def collect(local_only=False, home=None, account_only=False, disabled_accounts=frozenset(), enable_minimax=False):
    now = time.time()
    home = Path(home) if home else Path.home()
    if account_only:
        codex, other_accounts = account_sources(home, now, disabled_accounts, enable_minimax)
        return {"version": 1, "observed_at": int(now),
                "account": codex, "other_accounts": other_accounts, "history": {}}
    jobs = histories(home, now)
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = {name: executor.submit(guarded, fn) for name, fn in jobs.items()}
        qoder = executor.submit(guarded, lambda: metric(collect_qoder_credits(home / ".qoder-cn" / "projects", now)))
        account = None if local_only else executor.submit(account_sources, home, now, disabled_accounts, enable_minimax)
        account_values = None if account is None else account.result()
        return {"version": 1, "observed_at": int(now),
                "account": {"status": "disabled", "reason": "local_only"} if account is None else account_values[0],
                "other_accounts": {} if account_values is None else account_values[1],
                "history": {name: future.result() for name, future in futures.items()},
                "local_credits": {"Qoder CN": qoder.result()}}


def recent_daily_trend(result):
    """汇总北京时间最近七个自然日；缺失日只对可读取的账本视作零。"""
    today = datetime.fromtimestamp(result["observed_at"], DISPLAY_ZONE).date()
    days = [(today - timedelta(days=offset)).isoformat() for offset in range(6, -1, -1)]
    totals = dict.fromkeys(days, 0)
    sources = 0
    incomplete = False
    for item in result["history"].values():
        status = item.get("status")
        reason = item.get("reason") or ""
        if status in {"error", "partial"} or (status == "unavailable" and reason and not reason.endswith("_not_found")):
            incomplete = True
        value = item.get("value")
        if not isinstance(value, dict):
            continue
        periods = value.get("periods", {})
        all_totals = periods.get("all", {}).get("totals", {})
        if all_totals.get("requests") == 0:
            continue
        rows = value.get("daily_buckets")
        if not isinstance(rows, list):
            incomplete = True
            continue
        sources += 1
        for row in rows:
            if not isinstance(row, dict):
                incomplete = True
                continue
            day, count = row.get("date"), row.get("tokens")
            if not isinstance(day, str) or not isinstance(count, int) or isinstance(count, bool) or count < 0:
                incomplete = True
                continue
            if day in totals:
                totals[day] += count
    return {"days": [{"date": day, "tokens": totals[day]} for day in days],
            "sources": sources, "incomplete": incomplete} if sources else None


def calendar_report(result):
    """汇总北京时间自然周和自然月；当前周期为截至采集时的部分周期。"""
    today = datetime.fromtimestamp(result["observed_at"], DISPLAY_ZONE).date()
    week = today - timedelta(days=today.weekday())
    month = today.replace(day=1)
    last_month = (month - timedelta(days=1)).replace(day=1)
    bounds = {"week": (week - timedelta(days=7), week), "month": (last_month, month)}
    out = {kind: {"previous_start": previous.isoformat(), "current_start": current.isoformat(),
                  "current_end": today.isoformat(), "previous_tokens": 0, "current_tokens": 0}
           for kind, (previous, current) in bounds.items()}
    sources = 0
    incomplete = False
    for item in result["history"].values():
        status, reason = item.get("status"), item.get("reason") or ""
        if status in {"error", "partial"} or (status == "unavailable" and reason and not reason.endswith("_not_found")):
            incomplete = True
        value = item.get("value")
        if not isinstance(value, dict):
            continue
        all_totals = value.get("periods", {}).get("all", {}).get("totals", {})
        if all_totals.get("requests") == 0:
            continue
        rows = value.get("daily_buckets")
        if not isinstance(rows, list):
            incomplete = True
            continue
        sources += 1
        for row in rows:
            if not isinstance(row, dict):
                incomplete = True
                continue
            try:
                day = date.fromisoformat(row.get("date", ""))
            except (TypeError, ValueError):
                incomplete = True
                continue
            count = row.get("tokens")
            if isinstance(count, bool) or not isinstance(count, int) or count < 0:
                incomplete = True
                continue
            for kind, (previous, current) in bounds.items():
                if current <= day <= today:
                    out[kind]["current_tokens"] += count
                elif previous <= day < current:
                    out[kind]["previous_tokens"] += count
    return {"periods": out, "sources": sources, "incomplete": incomplete, "timezone": "Asia/Shanghai"} if sources else None


_DEFAULT_PRICE_TABLE = object()


def load_price_table(directory):
    """读取可选的美元自定义价格；损坏时不显示估算，绝不回退成看似有效的值。"""
    if directory is None:
        return PriceTable("USD", {}, set())
    path = Path(directory) / "prices.json"
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate price field")
            result[key] = value
        return result
    try:
        if path.stat().st_size > 1024 * 1024:
            return None
        data = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        rows = data.get("models") if isinstance(data, dict) and set(data) == {"currency", "models"} and data.get("currency") == "USD" else None
        if not isinstance(rows, dict) or len(rows) > 200:
            return None
        models, seen = {}, set()
        for pattern, values in rows.items():
            if (not isinstance(pattern, str) or not 0 < len(pattern.strip()) <= 120 or not isinstance(values, dict)
                    or set(values) != {"input", "cache_read", "cache_write", "output"}):
                return None
            normalized = normalize_model(pattern)
            if not normalized or normalized in seen:
                return None
            seen.add(normalized)
            rates = []
            for key in ("input", "cache_read", "cache_write", "output"):
                rate = values.get(key)
                if isinstance(rate, bool) or not isinstance(rate, (int, float)) or not math.isfinite(rate) or not 0 <= rate <= 1_000_000:
                    return None
                rates.append(rate)
            models[pattern] = tuple(rates)
        return PriceTable("USD", models, set())
    except FileNotFoundError:
        return PriceTable("USD", {}, set())
    except (OSError, ValueError, TypeError, OverflowError):
        return None


def compact_history(result, price_table=_DEFAULT_PRICE_TABLE):
    """仅向面板传递显示字段；模型明细按用量排序并限制数量。"""
    compact = {"version": result["version"], "observed_at": result["observed_at"],
               "account": result["account"], "history": {}}
    # 使用上游内置美元价格和固定回退汇率，不联网更新价格或把估算当作账单。
    table = PriceTable("USD", {}, set()) if price_table is _DEFAULT_PRICE_TABLE else price_table
    compact["price_status"] = "available" if table is not None else "invalid"
    for name, item in result["history"].items():
        value = item.get("value")
        periods = value.get("periods", {}) if isinstance(value, dict) else {}
        all_period = periods.get("all", {})
        all_totals = all_period.get("totals", {}) if isinstance(all_period, dict) else {}
        # 目录存在但没有用量记录，不能据此认定实际消耗为零。
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
                keys = ("total_tokens", "requests", "fresh_input", "output", "reasoning", "cache_read", "cache_write", "unclassified")
                selected[period] = {"totals": {key: totals[key] for key in keys if key in totals}}
                if period == "7d" and isinstance(bucket.get("groups"), list):
                    groups = []
                    for row in bucket["groups"]:
                        if not isinstance(row, dict):
                            continue
                        model, count = row.get("model"), row.get("total_tokens")
                        if (isinstance(model, str) and 0 < len(model) < 120 and
                                isinstance(count, int) and not isinstance(count, bool) and count >= 0):
                            groups.append({"model": model, "total_tokens": count})
                    groups.sort(key=lambda row: row["total_tokens"], reverse=True)
                    selected[period]["groups"] = groups[:20]
                    selected[period]["groups_truncated"] = len(groups) > 20
        compact["history"][name] = {"status": item.get("status"), "reason": item.get("reason"),
                                    "value": {"periods": selected} if selected else None}
        try:
            estimated = estimate_source_cost(item, table) if table is not None else None
            cost = (estimated or {}).get("value", {}).get("periods", {}).get("today", {})
            today = periods.get("today", {})
            today_total = today.get("totals", {}).get("total_tokens") if isinstance(today, dict) else None
            groups = today.get("groups") if isinstance(today, dict) else None
            amount = Decimal(str(cost.get("amount")))
            if (isinstance(today_total, int) and not isinstance(today_total, bool) and today_total > 0
                    and isinstance(groups, list) and groups and amount.is_finite() and amount >= 0):
                grouped = sum(row.get("total_tokens", 0) for row in groups if isinstance(row, dict)
                              and isinstance(row.get("total_tokens"), int) and not isinstance(row.get("total_tokens"), bool))
                peak_missing = any(table.peak_multipliers.get(table.match(model), 1) != 1
                                   for model in cost.get("unsplit_models", []))
                compact["history"][name]["estimate"] = {"amount_usd": format(amount, "f"),
                    "partial": bool(item.get("status") == "partial" or cost.get("unmatched_models") or
                                    grouped != today_total or peak_missing)}
        except (TypeError, ValueError, KeyError, InvalidOperation, OverflowError):
            # 价格估算失败不影响本机用量显示，也不把未知价格当作零。
            pass
    compact["daily_trend"] = recent_daily_trend(result)
    compact["calendar_report"] = calendar_report(result)
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


def history_worker(home=None, prices_dir=None):
    """在刷新之间保留内存缓存；源文件变化时失效，不落盘会话或凭据。"""
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
            result = compact_history(collect(local_only=True, home=home), load_price_table(prices_dir))
            print(json.dumps(result, ensure_ascii=False, separators=(",", ":")), flush=True)
        except (ValueError, TypeError, KeyError):
            print('{"error":"invalid_request"}', flush=True)
        except Exception:
            # 不返回异常原文、路径、原始数据或凭据。
            print('{"error":"history_scan_failed"}', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--local-only", action="store_true")
    parser.add_argument("--account-only", action="store_true")
    parser.add_argument("--history-worker", action="store_true")
    parser.add_argument("--prices-dir", type=Path, help="本地价格配置目录")
    for provider in ("codex", "kimi", "claude"):
        parser.add_argument("--disable-" + provider + "-account", action="store_true")
    parser.add_argument("--enable-minimax-account", action="store_true")
    parser.add_argument("--home", type=Path, help="Override home for isolated testing")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if args.history_worker:
        sys.stdin.reconfigure(encoding="utf-8")
        history_worker(args.home, args.prices_dir)
        return
    disabled = frozenset(provider for provider in ("codex", "kimi", "claude")
                         if getattr(args, "disable_" + provider + "_account"))
    print(json.dumps(collect(args.local_only, args.home, args.account_only, disabled, args.enable_minimax_account), ensure_ascii=False, separators=(",", ":")))


if __name__ == "__main__":
    main()
