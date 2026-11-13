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
    readonly System.Windows.Forms.Timer timer = new();
    readonly CancellationTokenSource shutdown = new();
    readonly HistoryWorker historyWorker = new();
    readonly bool demo;
    bool busy;

    public MeterWindow(bool demo, bool windowed)
    {
        this.demo = demo; Text = "Agent Meter"; Font = new Font("Microsoft YaHei UI", 10F); BackColor = Background; ForeColor = Color.White;
        ClientSize = new Size(500, 760); MinimumSize = new Size(420, 480); StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = demo || windowed; FormBorderStyle = demo || windowed ? FormBorderStyle.Sizable : FormBorderStyle.SizableToolWindow;
        var header = new Panel { Dock = DockStyle.Top, Height = 100 };
        var title = new Label { Text = "◈  AGENT METER", Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), AutoSize = true, Location = new Point(22, 16), ForeColor = Color.White };
        var subtitle = new Label { Text = "你的 AI 用量，一目了然", AutoSize = true, Location = new Point(25, 55), ForeColor = Muted };
        refresh.Text = "刷新"; refresh.Size = new Size(66, 32); refresh.Location = new Point(0, 23);
        refresh.FlatStyle = FlatStyle.Flat; refresh.FlatAppearance.BorderColor = Color.FromArgb(76, 96, 111); refresh.ForeColor = Accent; refresh.BackColor = Card;
        refresh.Click += async (_, _) => await RefreshData(); header.Controls.AddRange([title, subtitle, refresh]);
        header.Resize += (_, _) => refresh.Left = header.ClientSize.Width - refresh.Width - 22;
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

    public async Task RefreshData()
    {
        if (busy) return;
        busy = true; refresh.Enabled = false; status.Text = "正在刷新，本机记录较多时可能需要一些时间…";
        try
        {
            if (demo) { using var preview = JsonDocument.Parse(DemoData); Render(preview.RootElement); status.Text = "演示数据 · 不代表真实账户"; }
            else
            {
                bool partialFailure = false;
                var accountTask = RunCollector("--account-only", shutdown.Token);
                var historyTask = historyWorker.RefreshAsync(shutdown.Token);
                JsonObject combined;
                try { combined = JsonNode.Parse(await accountTask)!.AsObject(); }
                catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    partialFailure = true;
                    combined = new JsonObject { ["account"] = new JsonObject { ["status"] = "error", ["reason"] = "account_read_failed" } };
                }
                combined["history"] = new JsonObject { ["历史统计"] = new JsonObject { ["status"] = "loading", ["reason"] = "history_loading" } };
                using (var first = JsonDocument.Parse(combined.ToJsonString())) Render(first.RootElement);
                status.Text = "账户读取完成 · 正在扫描本机会话记录…";
                try
                {
                    var local = JsonNode.Parse(await historyTask)!.AsObject();
                    combined["history"] = local["history"]?.DeepClone();
                    combined["local_credits"] = local["local_credits"]?.DeepClone();
                }
                catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    partialFailure = true;
                    combined["history"] = new JsonObject { ["历史统计"] = new JsonObject { ["status"] = "error", ["reason"] = "history_scan_failed" } };
                }
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
        finally { busy = false; if (!IsDisposed && !Disposing) refresh.Enabled = true; }
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
        content.SuspendLayout();
        while (content.Controls.Count > 0) content.Controls[0].Dispose();
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
        AddHeading("本机 Token", "只统计本设备可读取的会话记录");
        var history = GetValue(root, "history");
        if (history.ValueKind == JsonValueKind.Object)
        {
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
        content.ResumeLayout(); ResizeCards();
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
    void AddCard(string name, string value, string detail, double? progress, string? extra = null)
    {
        var box = new Panel { Height = progress.HasValue ? 112 : extra is null ? 95 : 120, Margin = new Padding(0, 0, 0, 9), BackColor = Card };
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
    void ResizeCards() { foreach (Control c in content.Controls) c.Width = Math.Max(300, content.ClientSize.Width - 52); }
    const string DemoData = """
    {"account":{"status":"available","quota":{"value":[{"bucket":"codex","window":"primary","window_minutes":300,"remaining_percent":73,"resets_at":{"epoch_seconds":1794654000}},{"bucket":"codex","window":"secondary","window_minutes":10080,"remaining_percent":46,"resets_at":{"epoch_seconds":1794913200}}]}},"history":{"Codex":{"status":"available","value":{"periods":{"today":{"totals":{"total_tokens":1426000}},"7d":{"totals":{"total_tokens":7600000}},"30d":{"totals":{"total_tokens":25800000}},"all":{"totals":{"total_tokens":102000000}}}}},"Claude Code":{"status":"available","value":{"periods":{"today":{"totals":{"total_tokens":341000}},"7d":{"totals":{"total_tokens":900000}},"30d":{"totals":{"total_tokens":2400000}},"all":{"totals":{"total_tokens":3800000}}}}},"Gemini CLI":{"status":"unavailable","reason":"gemini_chats_not_found"}},"local_credits":{"Qoder CN":{"status":"available","value":{"periods":{"today":{"totals":{"credits":"12.5"}},"7d":{"totals":{"credits":"75"}},"30d":{"totals":{"credits":"248"}},"all":{"totals":{"credits":"560","billable_credits":"420"}}}}}}}
    """;
}
internal sealed class MeterBar : Control
{
    public double Value { get; set; }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Color.FromArgb(53, 67, 79)); using var brush = new SolidBrush(Value < 20 ? Color.FromArgb(244, 136, 116) : Color.FromArgb(117, 220, 194)); e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * Value / 100), Height); }
}

/// <summary>One local, read-only worker preserves parsed usage in memory between refreshes.</summary>
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
