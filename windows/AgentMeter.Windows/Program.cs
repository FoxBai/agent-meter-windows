using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMeter.Windows;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool demo = args.Contains("--demo");
        using var mutex = new Mutex(true, "Local\\AgentMeterWindows", out bool first);
        if (!first && !demo) return;
        Application.Run(new MeterContext(demo, args.Contains("--window")));
    }
}

internal sealed class MeterContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Icon icon;
    readonly MeterWindow window;
    bool quitting;
    public MeterContext(bool demo, bool windowed)
    {
        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("AgentMeter.Icon") ?? throw new InvalidOperationException("icon_missing");
        icon = new Icon(iconStream);
        window = new MeterWindow(demo, windowed) { Icon = icon };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 Agent Meter", null, (_, _) => ShowPanel());
        menu.Items.Add("刷新数据", null, async (_, _) => await window.RefreshData());
        if (!demo)
        {
            var accounts = new ToolStripMenuItem("账户查询");
            foreach (var provider in new[] { "Codex", "Kimi", "Claude" })
            {
                var item = new ToolStripMenuItem(provider + " 账户额度") { Checked = window.AccountEnabled(provider) };
                item.Click += (_, _) => { if (window.SetAccountEnabled(provider, !item.Checked)) item.Checked = !item.Checked; };
                accounts.DropDownItems.Add(item);
            }
            menu.Items.Add(accounts);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Text = "Agent Meter · AI 用量", Icon = icon, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (window.Visible) window.Hide(); else ShowPanel(); } };
        window.FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; window.Hide(); } };
        window.FormClosed += (_, _) => { if (!quitting) ExitThread(); };
        ShowPanel();
        _ = window.RefreshData();
    }
    void ShowPanel() { window.PlaceAtTray(); window.Show(); window.Activate(); }
    protected override void ExitThreadCore()
    {
        quitting = true; tray.Visible = false; tray.Dispose(); if (!window.IsDisposed) { window.Close(); window.Dispose(); } icon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class MeterWindow : Form
{
    static readonly Color Background = Color.FromArgb(15, 19, 30), Card = Color.FromArgb(26, 32, 47), Muted = Color.FromArgb(158, 170, 190), Accent = Color.FromArgb(117, 220, 194);
    readonly FlowLayoutPanel content = new();
    readonly Label status = new();
    readonly Button refresh = new();
    readonly Button accountTab = new(), usageTab = new();
    readonly FlowLayoutPanel gauges = new();
    readonly System.Windows.Forms.Timer timer = new();
    readonly CancellationTokenSource shutdown = new();
    readonly HistoryWorker historyWorker = new();
    readonly bool demo;
    AccountPreferences preferences;
    CancellationTokenSource? accountCancellation;
    bool busy, refreshQueued;
    bool showAccounts = true;
    string? lastSnapshot;

    public MeterWindow(bool demo, bool windowed)
    {
        this.demo = demo; preferences = demo ? AccountPreferences.Enabled : AccountPreferences.Load(AccountPreferences.DefaultDirectory);
        Text = "Agent Meter"; Font = new Font("Microsoft YaHei UI", 10F); BackColor = Background; ForeColor = Color.White;
        ClientSize = new Size(500, 760); MinimumSize = new Size(420, 480); StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = demo || windowed; FormBorderStyle = demo || windowed ? FormBorderStyle.Sizable : FormBorderStyle.SizableToolWindow;
        var header = new Panel { Dock = DockStyle.Top, Height = 270 };
        var title = new Label { Text = "◈  Agent 用量", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = true, Location = new Point(22, 16), ForeColor = Color.FromArgb(216, 207, 255) };
        var subtitle = new Label { Text = "账户额度与本机消耗", AutoSize = true, Location = new Point(25, 55), ForeColor = Muted };
        refresh.Text = "刷新"; refresh.Size = new Size(66, 32); refresh.Location = new Point(0, 23);
        refresh.FlatStyle = FlatStyle.Flat; refresh.FlatAppearance.BorderColor = Color.FromArgb(76, 96, 111); refresh.ForeColor = Accent; refresh.BackColor = Card;
        refresh.Click += async (_, _) => await RefreshData(); header.Controls.AddRange([title, subtitle, refresh]);
        header.Resize += (_, _) => { refresh.Left = header.ClientSize.Width - refresh.Width - 22; gauges.Width = Math.Max(0, header.ClientSize.Width - 40); };
        foreach (var tab in new[] { accountTab, usageTab })
        {
            tab.FlatStyle = FlatStyle.Flat; tab.FlatAppearance.BorderSize = 0; tab.ForeColor = Color.White;
            tab.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold); tab.Size = new Size(132, 36);
        }
        gauges.Location = new Point(20, 82); gauges.Size = new Size(450, 120);
        gauges.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        gauges.WrapContents = false; gauges.AutoScroll = true; gauges.BackColor = Background;
        header.Controls.Add(gauges);
        accountTab.Text = "账户额度"; accountTab.Location = new Point(22, 217);
        usageTab.Text = "本机消耗"; usageTab.Location = new Point(160, 217);
        accountTab.Click += (_, _) => SetView(true); usageTab.Click += (_, _) => SetView(false);
        header.Controls.AddRange([accountTab, usageTab]); UpdateTabs();
        status.Dock = DockStyle.Bottom; status.Height = 39; status.Padding = new Padding(22, 8, 0, 0); status.ForeColor = Muted; status.Text = "正在准备…";
        content.Dock = DockStyle.Fill; content.FlowDirection = FlowDirection.TopDown; content.WrapContents = false; content.AutoScroll = true;
        content.Padding = new Padding(18, 0, 18, 14); content.BackColor = Background; content.SizeChanged += (_, _) => ResizeCards();
        Controls.Add(content); Controls.Add(status); Controls.Add(header);
        timer.Interval = 15 * 60 * 1000; timer.Tick += async (_, _) => await RefreshData(); timer.Start();
    }
    public void PlaceAtTray()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
    }
    protected override void Dispose(bool disposing) { if (disposing) { shutdown.Cancel(); historyWorker.Dispose(); timer.Dispose(); } base.Dispose(disposing); }
    void UpdateTabs() { accountTab.BackColor = showAccounts ? Color.FromArgb(115, 76, 219) : Card; usageTab.BackColor = showAccounts ? Card : Color.FromArgb(115, 76, 219); }
    void SetView(bool account)
    {
        showAccounts = account; UpdateTabs();
        if (lastSnapshot is not null) { using var snapshot = JsonDocument.Parse(lastSnapshot); Render(snapshot.RootElement); }
    }

    public bool AccountEnabled(string provider) => provider switch
    {
        "Codex" => preferences.Codex, "Kimi" => preferences.Kimi, "Claude" => preferences.Claude, _ => false
    };
    public bool SetAccountEnabled(string provider, bool enabled)
    {
        var next = provider switch
        {
            "Codex" => preferences with { Codex = enabled }, "Kimi" => preferences with { Kimi = enabled },
            "Claude" => preferences with { Claude = enabled }, _ => preferences
        };
        try { next.Save(AccountPreferences.DefaultDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            status.Text = "无法保存账户开关，请检查设置目录权限";
            return false;
        }
        preferences = next;
        accountCancellation?.Cancel();
        if (lastSnapshot is not null)
        {
            var snapshot = JsonNode.Parse(lastSnapshot)!.AsObject(); ApplyAccountPreferences(snapshot);
            using var doc = JsonDocument.Parse(snapshot.ToJsonString()); Render(doc.RootElement);
        }
        if (busy) { refreshQueued = true; status.Text = "账户设置已保存，当前刷新结束后生效"; }
        else _ = RefreshData();
        return true;
    }

    void ApplyAccountPreferences(JsonObject snapshot)
    {
        static JsonObject Disabled() => new() { ["status"] = "disabled", ["reason"] = "disabled_by_user" };
        if (!preferences.Codex) snapshot["account"] = Disabled();
        var others = snapshot["other_accounts"] as JsonObject;
        if (others is null) { others = new JsonObject(); snapshot["other_accounts"] = others; }
        if (!preferences.Kimi) others["Kimi Code"] = Disabled();
        if (!preferences.Claude) others["Claude Code"] = Disabled();
    }

    public async Task RefreshData()
    {
        if (busy) { refreshQueued = true; return; }
        busy = true; refresh.Enabled = false; status.Text = "正在刷新，本机记录较多时可能需要一些时间…";
        try
        {
            if (demo) { using var preview = JsonDocument.Parse(DemoData); Render(preview.RootElement); status.Text = "演示数据 · 不代表真实账户"; }
            else
            {
                bool partialFailure = false;
                accountCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                string arguments = "--account-only" + (preferences.Codex ? "" : " --disable-codex-account") +
                    (preferences.Kimi ? "" : " --disable-kimi-account") + (preferences.Claude ? "" : " --disable-claude-account");
                var accountTask = RunCollector(arguments, accountCancellation.Token);
                var historyTask = historyWorker.RefreshAsync(shutdown.Token);
                JsonObject combined;
                try { combined = JsonNode.Parse(await accountTask)!.AsObject(); }
                catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    partialFailure = true;
                    combined = new JsonObject { ["account"] = new JsonObject { ["status"] = "error", ["reason"] = "account_read_failed" } };
                }
                ApplyAccountPreferences(combined);
                combined["history"] = new JsonObject { ["历史统计"] = new JsonObject { ["status"] = "loading", ["reason"] = "history_loading" } };
                using (var first = JsonDocument.Parse(combined.ToJsonString())) Render(first.RootElement);
                status.Text = "账户读取完成 · 正在扫描本机会话记录…";
                try
                {
                    var local = JsonNode.Parse(await historyTask)!.AsObject();
                    combined["history"] = local["history"]?.DeepClone();
                    combined["local_credits"] = local["local_credits"]?.DeepClone();
                    combined["daily_trend"] = local["daily_trend"]?.DeepClone();
                }
                catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    partialFailure = true;
                    combined["history"] = new JsonObject { ["历史统计"] = new JsonObject { ["status"] = "error", ["reason"] = "history_scan_failed" } };
                }
                ApplyAccountPreferences(combined);
                using var final = JsonDocument.Parse(combined.ToJsonString()); Render(final.RootElement);
                status.Text = partialFailure ? "部分数据读取失败 · 请稍后刷新" : $"更新于 {DateTime.Now:HH:mm:ss}  ·  本机历史 ≠ 账户总用量";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException or ObjectDisposedException)
        {
            if (IsDisposed || Disposing) return;
            content.Controls.Clear(); AddHeading("暂时无法读取数据", "请确认应用文件完整后重试。");
            status.Text = "刷新失败 · " + (ex is OperationCanceledException ? "读取超时" : "无法启动或解析采集程序");
        }
        finally
        {
            accountCancellation?.Dispose(); accountCancellation = null; busy = false;
            if (!IsDisposed && !Disposing)
            {
                refresh.Enabled = true;
                if (refreshQueued) { refreshQueued = false; _ = RefreshData(); }
            }
        }
    }
    static async Task<string> RunCollector(string mode, CancellationToken shutdown)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "backend", "AgentMeterBackend.exe");
        if (!File.Exists(path)) throw new IOException("backend_missing");
        using var process = new Process { StartInfo = new ProcessStartInfo(path, mode) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 } };
        if (!process.Start()) throw new InvalidOperationException("backend_start_failed");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("backend_failed");
        return await output;
    }
    void Render(JsonElement root)
    {
        lastSnapshot = root.GetRawText();
        UpdateGauges(root);
        content.SuspendLayout();
        while (content.Controls.Count > 0) content.Controls[0].Dispose();
        if (showAccounts)
        {
        AddHeading("账户额度", "来自账户接口 · 与本机 Token 分开统计");
        var account = GetValue(root, "account"); var quotas = GetValue(GetValue(account, "quota"), "value");
        if (quotas.ValueKind == JsonValueKind.Array && quotas.GetArrayLength() > 0)
        {
            foreach (var q in quotas.EnumerateArray())
            {
                string bucket = GetString(q, "bucket", "Codex"), window = GetString(q, "window", "");
                var minutes = GetNumber(q, "window_minutes");
                string period = minutes == 300 ? "5 小时" : minutes == 10080 ? "7 天" : minutes.HasValue ? $"{minutes:0} 分钟" : window;
                double? remaining = GetNumber(q, "remaining_percent");
                double? epoch = GetNumber(GetValue(q, "resets_at"), "epoch_seconds");
                string detail = epoch.HasValue ? "重置于 " + DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).LocalDateTime.ToString("MM-dd HH:mm") : "重置时间未提供";
                AddCard(bucket.ToUpperInvariant() + "  ·  " + period, remaining.HasValue ? $"{remaining:0.#}% 剩余" : "额度未提供", detail, remaining);
            }
        }
        else if (GetString(account, "status", "") == "disabled") AddCard("CODEX", "账户查询已关闭", "可在托盘菜单重新开启", null);
        else AddCard("CODEX", "暂时无法获取额度", Reason(account), null);
        var credits = GetValue(GetValue(account, "credits"), "value");
        if (credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in credits.EnumerateArray())
            {
                var balance = GetValue(credit, "balance");
                var unlimited = GetValue(credit, "unlimited");
                if (balance.ValueKind != JsonValueKind.String && unlimited.ValueKind != JsonValueKind.True) continue;
                string amount = unlimited.ValueKind == JsonValueKind.True ? "无限额" : balance.GetString() ?? "未提供";
                AddCard("CODEX · 积分", amount, "供应商返回的账户积分", null);
            }
        }
        var resetCards = GetValue(GetValue(account, "reset_cards"), "value");
        var cardCount = GetNumber(resetCards, "available_count");
        if (cardCount.HasValue)
        {
            double? expires = GetNumber(GetValue(resetCards, "next_known_expiry"), "epoch_seconds");
            string note = expires.HasValue ? "最近到期 " + DateTimeOffset.FromUnixTimeSeconds((long)expires.Value).LocalDateTime.ToString("MM-dd HH:mm") : "到期时间未提供";
            AddCard("CODEX · 重置卡", $"{cardCount:0} 张可用", note, null);
        }
        var otherAccounts = GetValue(root, "other_accounts");
        var kimiAccount = GetValue(otherAccounts, "Kimi Code");
        var kimiStatus = GetString(kimiAccount, "status", "");
        if (kimiAccount.ValueKind == JsonValueKind.Object && GetString(kimiAccount, "reason", "") != "kimi_server_token_missing")
        {
            var kimiQuotas = GetValue(GetValue(kimiAccount, "quota"), "value");
            if (kimiQuotas.ValueKind == JsonValueKind.Array && kimiQuotas.GetArrayLength() > 0)
            {
                foreach (var q in kimiQuotas.EnumerateArray())
                {
                    string bucket = GetString(q, "bucket", "");
                    var window = GetValue(q, "window");
                    double? duration = GetNumber(window, "duration");
                    string unit = GetString(window, "unit", "");
                    string label = bucket switch { "limit5h" => "5 小时", "limit7d" => "7 天", "monthTotal" => "月度总额", "monthCode" => "月度代码", _ => bucket };
                    if (duration.HasValue) label = $"{duration:0.##} " + (unit switch { "hour" => "小时", "day" => "天", "week" => "周", "month" => "月", "minute" => "分钟", "second" => "秒", _ => unit });
                    double? remaining = GetNumber(q, "remaining_percent");
                    double? reset = GetNumber(GetValue(q, "resets_at"), "epoch_seconds");
                    string note = reset.HasValue ? "重置于 " + DateTimeOffset.FromUnixTimeSeconds((long)reset.Value).LocalDateTime.ToString("MM-dd HH:mm") : "重置时间未提供";
                    AddCard("KIMI CODE · " + label, remaining.HasValue ? $"{remaining:0.#}% 剩余" : "额度未提供", note, remaining);
                }
            }
            else if (kimiStatus == "disabled") AddCard("KIMI CODE", "账户查询已关闭", "可在托盘菜单重新开启", null);
            else if (kimiStatus is "unavailable" or "error") AddCard("KIMI CODE", "暂时无法获取额度", Reason(kimiAccount), null);
            var wallet = GetValue(GetValue(kimiAccount, "credits"), "value");
            string amount = GetString(wallet, "amount", ""), currency = GetString(wallet, "unit", "");
            if (amount != "" && currency != "") AddCard("KIMI CODE · 额外用量钱包", amount + " " + currency, "账户服务提供的余额", null);
        }
        var claudeAccount = GetValue(otherAccounts, "Claude Code");
        var claudeQuotas = GetValue(GetValue(claudeAccount, "quota"), "value");
        if (claudeQuotas.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in claudeQuotas.EnumerateArray())
            {
                string bucket = GetString(q, "bucket", "");
                string label = bucket switch { "five_hour" => "5 小时", "seven_day" => "7 天", "seven_day_opus" => "7 天 · Opus", "seven_day_sonnet" => "7 天 · Sonnet", "seven_day_oauth_apps" => "7 天 · OAuth 应用", _ => bucket };
                double? remaining = GetNumber(q, "remaining_percent");
                double? reset = GetNumber(GetValue(q, "resets_at"), "epoch_seconds");
                string note = reset.HasValue ? "重置于 " + DateTimeOffset.FromUnixTimeSeconds((long)reset.Value).LocalDateTime.ToString("MM-dd HH:mm") : "重置时间未提供";
                AddCard("CLAUDE CODE · " + label, remaining.HasValue ? $"{remaining:0.#}% 剩余" : "额度未提供", note, remaining);
            }
        }
        else if (GetString(claudeAccount, "status", "") == "disabled") AddCard("CLAUDE CODE", "账户查询已关闭", "可在托盘菜单重新开启", null);
        else if (GetString(claudeAccount, "reason", "") is not ("" or "claude_subscription_login_missing"))
            AddCard("CLAUDE CODE", "暂时无法获取额度", Reason(claudeAccount), null);
        }
        else
        {
        AddHeading("本机 Token", "只统计本设备可读取的会话记录");
        var history = GetValue(root, "history");
        if (history.ValueKind == JsonValueKind.Object)
        {
            AddUsageSummary(history);
            AddDailyTrend(GetValue(root, "daily_trend"));
            int visible = 0;
            foreach (var item in history.EnumerateObject())
            {
                if (IsAbsent(item.Value)) continue;
                visible++;
                var periods = GetValue(GetValue(item.Value, "value"), "periods");
                var today = GetValue(GetValue(periods, "today"), "totals"); var all = GetValue(GetValue(periods, "all"), "totals");
                var week = GetValue(GetValue(periods, "7d"), "totals"); var month = GetValue(GetValue(periods, "30d"), "totals");
                double? dayTokens = GetNumber(today, "total_tokens"), allTokens = GetNumber(all, "total_tokens");
                double? weekTokens = GetNumber(week, "total_tokens"), monthTokens = GetNumber(month, "total_tokens");
                var note = allTokens.HasValue && weekTokens.HasValue && monthTokens.HasValue
                    ? $"近 7 天 {FormatNumber(weekTokens.Value)}  ·  近 30 天 {FormatNumber(monthTokens.Value)}"
                    : allTokens.HasValue ? "累计 " + FormatNumber(allTokens.Value) + " tokens" : Reason(item.Value);
                string? extra = allTokens.HasValue && weekTokens.HasValue && monthTokens.HasValue ? "累计 " + FormatNumber(allTokens.Value) + " tokens" : null;
                if (GetString(item.Value, "status", "") == "partial")
                {
                    string detail = GetString(item.Value, "reason", "") == "opencode_v2_unread" ? "新版记录未计入" : "部分记录未读取";
                    if (extra is null) note += " · " + detail; else extra += " · " + detail;
                }
                AddCard(item.Name, dayTokens.HasValue ? FormatNumber(dayTokens.Value) + " 今日" : "暂无数据", note, null, extra);
            }
            if (visible == 0) AddCard("本机记录", "暂无数据", "未发现支持的会话记录", null);
            AddModelRanking(history);
        }
        var creditsHistory = GetValue(root, "local_credits");
        if (creditsHistory.ValueKind == JsonValueKind.Object)
        {
            bool headingShown = false;
            foreach (var item in creditsHistory.EnumerateObject())
            {
                if (IsAbsent(item.Value) || GetString(item.Value, "reason", "") == "history_no_usage_records") continue;
                if (!headingShown) { AddHeading("本机积分", "独立计量，不等于账户余额"); headingShown = true; }
                var periods = GetValue(GetValue(item.Value, "value"), "periods");
                var today = GetValue(GetValue(periods, "today"), "totals");
                var all = GetValue(GetValue(periods, "all"), "totals");
                var week = GetValue(GetValue(periods, "7d"), "totals");
                var month = GetValue(GetValue(periods, "30d"), "totals");
                string todayCredits = GetString(today, "credits", "");
                string allCredits = GetString(all, "credits", "");
                string billed = GetString(all, "billable_credits", "");
                string weekly = GetString(week, "credits", "—"), monthly = GetString(month, "credits", "—");
                AddCard(item.Name + " · 本机积分消耗", todayCredits == "" ? "暂无数据" : todayCredits + " 今日",
                        allCredits == "" ? Reason(item.Value) : $"近 7 天 {weekly} · 近 30 天 {monthly}", null,
                        allCredits == "" ? null : $"累计 {allCredits} · 计费 {billed}");
            }
        }
        }
        content.ResumeLayout(); ResizeCards();
    }
    void UpdateGauges(JsonElement root)
    {
        while (gauges.Controls.Count > 0) gauges.Controls[0].Dispose();
        AddGauge("Codex", GetValue(root, "account"));
        AddGauge("Kimi", GetValue(GetValue(root, "other_accounts"), "Kimi Code"));
        AddGauge("Claude", GetValue(GetValue(root, "other_accounts"), "Claude Code"));
        if (gauges.Controls.Count == 0)
            gauges.Controls.Add(new Label { Text = "暂无可用账户额度", ForeColor = Muted, AutoSize = true, Margin = new Padding(5, 32, 0, 0) });
    }
    void AddGauge(string provider, JsonElement account)
    {
        var quotas = GetValue(GetValue(account, "quota"), "value");
        if (quotas.ValueKind != JsonValueKind.Array) return;
        foreach (var row in quotas.EnumerateArray())
        {
            var remaining = GetNumber(row, "remaining_percent");
            if (!remaining.HasValue) continue;
            var minutes = GetNumber(row, "window_minutes");
            var bucket = GetString(row, "bucket", "");
            var window = GetValue(row, "window");
            var duration = GetNumber(window, "duration");
            var unit = GetString(window, "unit", "");
            string label = minutes == 300 || bucket == "limit5h" ? "5 小时" : minutes == 10080 || bucket == "limit7d" ? "7 天" :
                duration.HasValue ? $"{duration:0.#}{(unit == "hour" ? "小时" : unit == "week" ? "周" : unit)}" : GetString(row, "window", bucket);
            gauges.Controls.Add(new QuotaGauge(provider, label, Math.Clamp(remaining.Value, 0, 100)));
            return;
        }
    }
    static string FormatNumber(double n) => n >= 1_000_000_000 ? $"{n / 1_000_000_000:0.##}B" : n >= 1_000_000 ? $"{n / 1_000_000:0.##}M" : n >= 1_000 ? $"{n / 1_000:0.##}K" : $"{n:0}";
    static JsonElement GetValue(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    static string GetString(JsonElement e, string key, string fallback) { var v = GetValue(e, key); return v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback; }
    static double? GetNumber(JsonElement e, string key) { var v = GetValue(e, key); return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null; }
    static bool IsAbsent(JsonElement e) => (GetString(e, "status", "") is "unavailable" or "not_provided") && GetString(e, "reason", "") is
        "codex_sessions_not_found" or "claude_projects_not_found" or "gemini_chats_not_found" or "workbuddy_projects_not_found" or
        "zcode_db_not_found" or "dsh_sessions_not_found" or "kimi_sessions_not_found" or "kimi_legacy_sessions_not_found" or
        "opencode_db_not_found" or "opencode_memory_database" or "mcode_db_not_found" or "mimocode_db_not_found" or "deveco_db_not_found" or "qoder_sessions_not_found";
    static string Reason(JsonElement e) => GetString(e, "reason", "") switch
    {
        "cli_not_available" => "未找到 Codex；请安装并登录后刷新",
        "rpc_timeout" => "账户查询超时，请稍后重试",
        "rpc_disconnected" => "Codex 服务未连接，请检查登录状态",
        "kimi_server_not_running" => "未发现运行中的 Kimi 本地服务",
        "kimi_server_token_invalid" => "Kimi 本地服务凭据不可用",
        "claude_token_expired" => "Claude Code 登录已过期，请在 Claude Code 中重新登录",
        "claude_not_logged_in" or "claude_subscription_login_missing" => "未找到 Claude Code 订阅登录",
        "history_loading" => "后台扫描中，请稍候",
        "history_no_usage_records" => "没有可统计的用量记录",
        "codex_sessions_not_found" or "claude_projects_not_found" or "gemini_chats_not_found" or "workbuddy_projects_not_found" or "zcode_db_not_found" or "dsh_sessions_not_found" => "未找到本机会话记录",
        "zstd_unavailable" => "部分压缩会话无法读取",
        "unsupported_qoder_usage_schema" => "当前 Qoder 记录格式暂不支持",
        "opencode_v2_unread" => "检测到新版记录，暂未计入",
        "unsupported_opencode_schema" => "当前 OpenCode 数据格式暂不支持",
        _ => "未连接、未提供数据或读取失败"
    };
    void AddHeading(string title, string subtitle)
    {
        var box = new Panel { Height = 60, Margin = new Padding(0, 3, 0, 1), BackColor = Background };
        box.Controls.Add(new Label { Text = title, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), AutoSize = true, Location = new Point(3, 5), ForeColor = Color.White });
        box.Controls.Add(new Label { Text = subtitle, AutoSize = true, Location = new Point(3, 32), ForeColor = Muted }); content.Controls.Add(box);
    }
    void AddUsageSummary(JsonElement history)
    {
        double total = 0; int sources = 0; bool complete = true; bool partial = false;
        double[] parts = new double[5];
        foreach (var item in history.EnumerateObject())
        {
            string status = GetString(item.Value, "status", "");
            if (status is "error" or "partial" || (status == "unavailable" && !IsAbsent(item.Value))) partial = true;
            var totals = GetValue(GetValue(GetValue(GetValue(item.Value, "value"), "periods"), "today"), "totals");
            var amount = GetNumber(totals, "total_tokens");
            if (!amount.HasValue || amount.Value < 0) continue;
            total += amount.Value; sources++;
            double[] values = [GetNumber(totals, "fresh_input") ?? 0,
                (GetNumber(totals, "output") ?? 0) + (GetNumber(totals, "reasoning") ?? 0),
                GetNumber(totals, "cache_read") ?? 0, GetNumber(totals, "cache_write") ?? 0,
                GetNumber(totals, "unclassified") ?? 0];
            if (Math.Abs(values.Sum() - amount.Value) > 0.5) complete = false;
            for (int i = 0; i < parts.Length; i++) parts[i] += values[i];
        }
        if (sources == 0) return;
        var box = new RoundedCard { Height = 190, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = "今日 Token 总量", AutoSize = true, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Muted, Location = new Point(15, 10) });
        box.Controls.Add(new Label { Text = FormatNumber(total), AutoSize = true, Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), ForeColor = Color.White, Location = new Point(14, 31) });
        box.Controls.Add(new Label { Text = $"{sources} 个本机账本相加" + (partial ? " · 含不完整来源" : ""), AutoSize = true, ForeColor = Muted, Location = new Point(16, 82) });
        if (complete && total > 0)
        {
            var bar = new BreakdownBar(parts) { Location = new Point(16, 116), Height = 9 };
            box.Resize += (_, _) => bar.Width = Math.Max(0, box.ClientSize.Width - 32); box.Controls.Add(bar);
            var legend = new FlowLayoutPanel { Location = new Point(16, 137), Height = 48, WrapContents = true };
            string[] labels = ["输入", "输出", "缓存读取", "缓存写入", "未拆分"];
            for (int i = 0; i < labels.Length; i++)
                legend.Controls.Add(new Label { Text = "● " + labels[i], AutoSize = true, Font = new Font(Font.FontFamily, 8F), ForeColor = BreakdownBar.Colors[i], Margin = new Padding(0, 0, 8, 0) });
            box.Resize += (_, _) => legend.Width = Math.Max(0, box.ClientSize.Width - 32); box.Controls.Add(legend);
        }
        else box.Controls.Add(new Label { Text = "部分来源未提供完整拆分", AutoSize = true, ForeColor = Muted, Location = new Point(16, 125) });
        content.Controls.Add(box);
    }
    void AddDailyTrend(JsonElement trend)
    {
        var days = GetValue(trend, "days");
        if (days.ValueKind != JsonValueKind.Array) return;
        var points = new List<(string Date, double Tokens)>();
        foreach (var day in days.EnumerateArray())
        {
            string date = GetString(day, "date", ""); var tokens = GetNumber(day, "tokens");
            if (date.Length != 10 || !tokens.HasValue || tokens.Value < 0) return;
            points.Add((date, tokens.Value));
        }
        if (points.Count != 7) return;
        var box = new RoundedCard { Height = 218, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = "最近 7 个自然日", AutoSize = true, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Color.White, Location = new Point(15, 10) });
        bool incomplete = GetValue(trend, "incomplete").ValueKind == JsonValueKind.True;
        box.Controls.Add(new Label { Text = incomplete ? "北京时间 · 部分记录不完整" : "北京时间 · 本机账本相加", AutoSize = true, ForeColor = Muted, Location = new Point(16, 38) });
        var chart = new DailyChart(points) { Location = new Point(14, 70), Height = 132 };
        box.Resize += (_, _) => chart.Width = Math.Max(0, box.ClientSize.Width - 28); box.Controls.Add(chart);
        content.Controls.Add(box);
    }
    void AddCard(string name, string value, string detail, double? progress, string? extra = null)
    {
        var box = new RoundedCard { Height = progress.HasValue ? 112 : extra is null ? 95 : 120, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = name, AutoSize = true, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Muted, Location = new Point(15, 11) });
        box.Controls.Add(new Label { Text = value, AutoSize = true, Font = new Font(Font.FontFamily, 14F, FontStyle.Bold), ForeColor = Color.White, Location = new Point(14, 31) });
        box.Controls.Add(new Label { Text = detail, AutoSize = true, ForeColor = Muted, Location = new Point(16, progress.HasValue ? 84 : 67) });
        if (extra is not null) box.Controls.Add(new Label { Text = extra, AutoSize = true, ForeColor = Muted, Location = new Point(16, 91) });
        if (progress.HasValue)
        {
            var bar = new MeterBar { Value = Math.Clamp(progress.Value, 0, 100), Location = new Point(16, 70), Height = 5 };
            box.Resize += (_, _) => bar.Width = Math.Max(0, box.ClientSize.Width - 32);
            box.Controls.Add(bar);
        }
        content.Controls.Add(box);
    }
    void AddModelRanking(JsonElement history)
    {
        var models = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool incomplete = false;
        foreach (var item in history.EnumerateObject())
        {
            string status = GetString(item.Value, "status", "");
            if (status is "error" or "partial" || (status == "unavailable" && !IsAbsent(item.Value))) incomplete = true;
            var week = GetValue(GetValue(GetValue(item.Value, "value"), "periods"), "7d");
            var total = GetNumber(GetValue(week, "totals"), "total_tokens");
            if (!total.HasValue || total.Value <= 0) continue;
            var groups = GetValue(week, "groups");
            if (groups.ValueKind != JsonValueKind.Array) { incomplete = true; continue; }
            double grouped = 0;
            foreach (var row in groups.EnumerateArray())
            {
                string model = GetString(row, "model", ""); var amount = GetNumber(row, "total_tokens");
                if (model.Length == 0 || !amount.HasValue || amount.Value < 0) { incomplete = true; continue; }
                grouped += amount.Value;
                models[model] = models.GetValueOrDefault(model) + amount.Value;
            }
            if (Math.Abs(grouped - total.Value) > 0.5 || GetValue(week, "groups_truncated").ValueKind == JsonValueKind.True) incomplete = true;
        }
        if (models.Count == 0) return;
        AddHeading("近 7 天模型排行", incomplete ? "本机账本相加 · 部分模型记录不完整" : "本机账本相加 · 按 Token 排序");
        int rank = 0;
        foreach (var entry in models.OrderByDescending(pair => pair.Value).Take(5))
        {
            string label = new(entry.Key.Where(c => !char.IsControl(c)).Take(48).ToArray());
            if (label.Length == 0 || label.Equals("unknown", StringComparison.OrdinalIgnoreCase)) label = "未识别模型";
            AddCard($"{++rank}. {label}", FormatNumber(entry.Value) + " tokens", "近 7 天 · 按本机记录统计", null);
        }
    }
    void ResizeCards() { foreach (Control c in content.Controls) c.Width = Math.Max(300, content.ClientSize.Width - 52); }
    const string DemoData = """
    {"account":{"status":"available","quota":{"value":[{"bucket":"codex","window":"primary","window_minutes":300,"remaining_percent":73,"resets_at":{"epoch_seconds":1794654000}},{"bucket":"codex","window":"secondary","window_minutes":10080,"remaining_percent":46,"resets_at":{"epoch_seconds":1794913200}}]}},"other_accounts":{"Kimi Code":{"status":"available","quota":{"value":[{"bucket":"limit5h","remaining_percent":64,"resets_at":{"epoch_seconds":1794654000}}]},"credits":{"value":{"amount":"12.34","unit":"CNY"}}},"Claude Code":{"status":"available","quota":{"value":[{"bucket":"five_hour","window_minutes":300,"remaining_percent":82,"resets_at":{"epoch_seconds":1794654000}},{"bucket":"seven_day","window_minutes":10080,"remaining_percent":53,"resets_at":{"epoch_seconds":1794913200}}]}}},"history":{"Codex":{"status":"available","value":{"periods":{"today":{"totals":{"total_tokens":1426000,"fresh_input":300000,"output":126000,"reasoning":0,"cache_read":1000000,"cache_write":0}},"7d":{"totals":{"total_tokens":7600000},"groups":[{"model":"gpt-5.2-codex","total_tokens":6200000},{"model":"gpt-5.1-codex","total_tokens":1400000}]},"30d":{"totals":{"total_tokens":25800000}},"all":{"totals":{"total_tokens":102000000}}}}},"Claude Code":{"status":"available","value":{"periods":{"today":{"totals":{"total_tokens":341000,"fresh_input":100000,"output":41000,"reasoning":0,"cache_read":200000,"cache_write":0}},"7d":{"totals":{"total_tokens":900000},"groups":[{"model":"claude-sonnet-4","total_tokens":900000}]},"30d":{"totals":{"total_tokens":2400000}},"all":{"totals":{"total_tokens":3800000}}}}},"Gemini CLI":{"status":"unavailable","reason":"gemini_chats_not_found"}},"local_credits":{"Qoder CN":{"status":"available","value":{"periods":{"today":{"totals":{"credits":"12.5"}},"7d":{"totals":{"credits":"75"}},"30d":{"totals":{"credits":"248"}},"all":{"totals":{"credits":"560","billable_credits":"420"}}}}}},"daily_trend":{"days":[{"date":"2026-11-08","tokens":340000},{"date":"2026-11-09","tokens":780000},{"date":"2026-11-10","tokens":1120000},{"date":"2026-11-11","tokens":550000},{"date":"2026-11-12","tokens":970000},{"date":"2026-11-13","tokens":1300000},{"date":"2026-11-14","tokens":1767000}],"sources":2,"incomplete":false}}
    """;
}
internal sealed class RoundedCard : Panel
{
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 2 || Height < 2) return;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int r = 20, w = Width - 1, h = Height - 1;
        path.AddArc(0, 0, r, r, 180, 90); path.AddArc(w - r, 0, r, r, 270, 90);
        path.AddArc(w - r, h - r, r, r, 0, 90); path.AddArc(0, h - r, r, r, 90, 90); path.CloseFigure();
        Region?.Dispose(); Region = new Region(path);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var border = new Pen(Color.FromArgb(65, 74, 100));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}
internal sealed class MeterBar : Control
{
    public double Value { get; set; }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Color.FromArgb(53, 67, 79)); using var brush = new SolidBrush(Value < 20 ? Color.FromArgb(244, 136, 116) : Color.FromArgb(117, 220, 194)); e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * Value / 100), Height); }
}
internal sealed class QuotaGauge : Control
{
    readonly string provider, period;
    readonly double remaining;
    public QuotaGauge(string provider, string period, double remaining)
    {
        this.provider = provider; this.period = period; this.remaining = remaining;
        Size = new Size(110, 110); Margin = new Padding(0, 0, 9, 0);
        AccessibleRole = AccessibleRole.Graphic; AccessibleName = $"{provider} {period}，剩余 {remaining:0.#}%";
        DoubleBuffered = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var ring = new Rectangle(25, 4, 52, 52);
        using var track = new Pen(Color.FromArgb(48, 52, 68), 7);
        using var fill = new Pen(remaining < 20 ? Color.FromArgb(255, 98, 135) : remaining < 50 ? Color.FromArgb(255, 169, 58) : Color.FromArgb(61, 218, 175), 7);
        fill.StartCap = fill.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        e.Graphics.DrawArc(track, ring, -90, 359); if (remaining > 0) e.Graphics.DrawArc(fill, ring, -90, (float)(3.59 * remaining));
        using var valueFont = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, $"{remaining:0.#}%", valueFont, ring, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, provider, Font, new Rectangle(0, 61, Width, 23), Color.White, TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(e.Graphics, period, Font, new Rectangle(0, 84, Width, 23), Color.FromArgb(158, 170, 190), TextFormatFlags.HorizontalCenter);
    }
}
internal sealed class DailyChart : Control
{
    readonly IReadOnlyList<(string Date, double Tokens)> points;
    public DailyChart(IReadOnlyList<(string Date, double Tokens)> points)
    {
        this.points = points; DoubleBuffered = true; AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "最近七个自然日 Token：" + string.Join("，", points.Select(p => $"{p.Date} {p.Tokens:0}"));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        double maximum = Math.Max(1, points.Max(p => p.Tokens));
        int slot = Math.Max(1, Width / points.Count), baseline = Height - 28, top = 22;
        using var guide = new Pen(Color.FromArgb(65, 74, 100));
        using var fill = new SolidBrush(Color.FromArgb(117, 220, 194));
        using var small = new Font(Font.FontFamily, 8F);
        string peak = maximum >= 1_000_000 ? $"{maximum / 1_000_000:0.##}M" : maximum >= 1_000 ? $"{maximum / 1_000:0.##}K" : $"{(points.Max(p => p.Tokens)):0}";
        TextRenderer.DrawText(e.Graphics, "单日最高 " + peak, small, new Point(0, 0), Color.FromArgb(158, 170, 190));
        e.Graphics.DrawLine(guide, 0, baseline, Width, baseline);
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i]; int barHeight = (int)Math.Round((baseline - top) * point.Tokens / maximum);
            int barWidth = Math.Max(4, Math.Min(24, slot / 2)), x = i * slot + (slot - barWidth) / 2;
            if (barHeight > 0) e.Graphics.FillRectangle(fill, x, baseline - barHeight, barWidth, barHeight);
            var labelArea = new Rectangle(i * slot, baseline + 5, slot, 20);
            TextRenderer.DrawText(e.Graphics, point.Date[5..].Replace('-', '/'), small, labelArea,
                Color.FromArgb(158, 170, 190), TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
internal sealed class BreakdownBar : Control
{
    readonly double[] values;
    internal static readonly Color[] Colors = [Color.FromArgb(95, 164, 255), Color.FromArgb(166, 112, 255), Color.FromArgb(69, 208, 185), Color.FromArgb(255, 176, 73), Color.FromArgb(151, 154, 168)];
    public BreakdownBar(double[] values) { this.values = values; DoubleBuffered = true; AccessibleName = "今日用量构成：输入、输出、缓存读取、缓存写入和未拆分"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); double total = values.Sum(), start = 0;
        if (total <= 0) return;
        for (int i = 0; i < values.Length; i++)
        {
            float left = (float)(Width * start / total), right = (float)(Width * (start + values[i]) / total);
            using var brush = new SolidBrush(Colors[i]); e.Graphics.FillRectangle(brush, left, 0, Math.Max(0, right - left), Height); start += values[i];
        }
    }
}

/// <summary>本地只读工作进程在刷新之间保留已解析的用量缓存。</summary>
internal sealed class HistoryWorker : IDisposable
{
    Process? process;
    public async Task<string> RefreshAsync(CancellationToken shutdown)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            if (process is null || process.HasExited)
            {
                Stop();
                var path = Path.Combine(AppContext.BaseDirectory, "backend", "AgentMeterBackend.exe");
                if (!File.Exists(path)) throw new IOException("backend_missing");
                process = new Process { StartInfo = new ProcessStartInfo(path, "--history-worker")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8
                } };
                if (!process.Start()) throw new InvalidOperationException("backend_start_failed");
                _ = process.StandardError.ReadToEndAsync();
            }
            await process.StandardInput.WriteLineAsync("{\"command\":\"refresh\"}".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            string? response = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (response is null) throw new IOException("worker_disconnected");
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("error", out _)) throw new IOException("worker_failed");
            return response;
        }
        catch
        {
            Stop();
            throw;
        }
    }
    void Stop()
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose(); process = null;
    }
    public void Dispose() => Stop();
}
