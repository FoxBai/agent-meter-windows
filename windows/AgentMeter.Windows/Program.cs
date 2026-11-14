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
        // 构建机使用纯演示数据生成视觉检查图，不启动采集器或读取用户设置。
        if (args.Length == 2 && args[0] == "--render-demo")
        {
            using var preview = new MeterWindow(demo: true, windowed: true);
            preview.RenderDemoImages(args[1]);
            return;
        }
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
    readonly ToolStripMenuItem petVisible = new("显示桌宠");
    readonly ToolStripMenuItem petMochi = new("麻薯"), petBit = new("比特");
    PetPreferences petPreferences;
    PetWindow? pet;
    bool quitting;
    public MeterContext(bool demo, bool windowed)
    {
        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("AgentMeter.Icon") ?? throw new InvalidOperationException("icon_missing");
        icon = new Icon(iconStream);
        window = new MeterWindow(demo, windowed) { Icon = icon };
        petPreferences = demo ? PetPreferences.Default : PetPreferences.Load(AccountPreferences.DefaultDirectory);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 Agent Meter", null, (_, _) => ShowPanel(true));
        menu.Items.Add("刷新数据", null, async (_, _) => await window.RefreshData());
        if (!demo)
        {
            var accounts = new ToolStripMenuItem("账户查询");
            foreach (var provider in new[] { "Codex", "Kimi", "Claude", "MiniMax" })
            {
                var item = new ToolStripMenuItem(provider + " 账户额度") { Checked = window.AccountEnabled(provider) };
                item.Click += (_, _) => { if (window.SetAccountEnabled(provider, !item.Checked)) item.Checked = !item.Checked; };
                accounts.DropDownItems.Add(item);
            }
            menu.Items.Add(accounts);
            menu.Items.Add("自定义模型价格", null, async (_, _) =>
            {
                ShowPanel(true);
                using var editor = new PriceEditor(AccountPreferences.DefaultDirectory);
                if (editor.ShowDialog(window) == DialogResult.OK) await window.RefreshData();
            });
            menu.Items.Add("管理 MiniMax 账户", null, async (_, _) =>
            {
                ShowPanel(true);
                using var editor = new MiniMaxAccountsDialog(AccountPreferences.DefaultDirectory);
                editor.ShowDialog(window);
                if (editor.Changed) await window.RefreshAfterAccountChange();
            });
            var alerts = new ToolStripMenuItem("低额度提醒（低于 20%）") { Checked = window.AlertsEnabled };
            alerts.Click += (_, _) => { if (window.SetAlertsEnabled(!alerts.Checked)) alerts.Checked = !alerts.Checked; };
            menu.Items.Add(alerts);
            var forecast = new ToolStripMenuItem("第三方重置预测（联网）") { Checked = window.ForecastEnabled };
            forecast.Click += (_, _) => { if (window.SetForecastEnabled(!forecast.Checked)) forecast.Checked = !forecast.Checked; };
            menu.Items.Add(forecast);
        }
        var appearance = new ToolStripMenuItem("界面风格");
        foreach (var themeOption in VisualThemes.All)
        {
            var item = new ToolStripMenuItem(themeOption.Name) { Checked = window.ThemeId == themeOption.Id };
            item.Click += (_, _) =>
            {
                if (!window.SetTheme(themeOption)) return;
                foreach (ToolStripMenuItem other in appearance.DropDownItems) other.Checked = other == item;
            };
            appearance.DropDownItems.Add(item);
        }
        menu.Items.Add(appearance);
        if (!demo)
        {
            var pets = new ToolStripMenuItem("桌宠");
            petVisible.Checked = petPreferences.Enabled;
            petVisible.Click += (_, _) => ChangePet(petPreferences with { Enabled = !petPreferences.Enabled });
            petMochi.Checked = petPreferences.Pack == "mochi"; petBit.Checked = petPreferences.Pack == "bit";
            petMochi.Click += (_, _) => ChangePet(petPreferences with { Pack = "mochi" });
            petBit.Click += (_, _) => ChangePet(petPreferences with { Pack = "bit" });
            pets.DropDownItems.AddRange([petVisible, new ToolStripSeparator(), petMochi, petBit]);
            menu.Items.Add(pets);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Text = "Agent Meter · AI 用量", Icon = icon, ContextMenuStrip = menu, Visible = true };
        window.QuotaLow += messages =>
        {
            string summary = string.Join("\n", messages.Take(3)) + (messages.Count > 3 ? $"\n另有 {messages.Count - 3} 项，请打开面板查看。" : "");
            if (summary.Length > 240) summary = summary[..239] + "…";
            try { tray.ShowBalloonTip(8000, "Agent 额度提醒", summary, ToolTipIcon.Warning); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { }
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (window.Visible) window.Hide(); else ShowPanel(true); } };
        window.FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; window.Hide(); } };
        window.FormClosed += (_, _) => { if (!quitting) ExitThread(); };
        window.SnapshotUpdated += snapshot => pet?.SetSnapshot(snapshot);
        ShowPanel();
        if (petPreferences.Enabled) StartPet();
        _ = window.RefreshData();
    }
    void ChangePet(PetPreferences next)
    {
        try { next.Save(AccountPreferences.DefaultDirectory); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { window.ShowNotice("无法保存桌宠设置"); return; }
        bool restart = next.Enabled && (!petPreferences.Enabled || next.Pack != petPreferences.Pack);
        petPreferences = next; petVisible.Checked = next.Enabled; petMochi.Checked = next.Pack == "mochi"; petBit.Checked = next.Pack == "bit";
        if (!next.Enabled) { pet?.Dispose(); pet = null; }
        else if (restart) StartPet();
    }
    void StartPet()
    {
        pet?.Dispose(); pet = null;
        try
        {
            var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Point.Empty);
            Point location;
            if (petPreferences.X.HasValue && petPreferences.Y.HasValue)
            {
                var saved = new Rectangle(petPreferences.X.Value, petPreferences.Y.Value, 240, 240);
                var matching = Screen.AllScreens.FirstOrDefault(item => item.WorkingArea.IntersectsWith(saved));
                if (matching is not null)
                {
                    var area = matching.WorkingArea;
                    location = new Point(Math.Clamp(saved.X, area.Left, Math.Max(area.Left, area.Right - 240)),
                                         Math.Clamp(saved.Y, area.Top, Math.Max(area.Top, area.Bottom - 240)));
                }
                else location = PanelPlacement.Find(screen.Bounds, screen.WorkingArea, new Size(240, 240), Point.Empty, false);
            }
            else location = PanelPlacement.Find(screen.Bounds, screen.WorkingArea, new Size(240, 240), Point.Empty, false);
            var instance = new PetWindow(petPreferences.Pack, location);
            instance.OpenRequested += () => ShowPanel(true);
            instance.RefreshRequested += async () => await window.RefreshData();
            instance.HideRequested += () => ChangePet(petPreferences with { Enabled = false });
            instance.PositionChanged += position =>
            {
                var next = petPreferences with { X = position.X, Y = position.Y };
                try { next.Save(AccountPreferences.DefaultDirectory); petPreferences = next; }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { window.ShowNotice("无法保存桌宠位置"); }
            };
            instance.RenderingFailed += () =>
            {
                petPreferences = petPreferences with { Enabled = false }; petVisible.Checked = false;
                try { petPreferences.Save(AccountPreferences.DefaultDirectory); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                window.ShowNotice("桌宠暂时无法显示");
            };
            pet = instance;
            if (window.CurrentSnapshot is not null) instance.SetSnapshot(window.CurrentSnapshot);
            instance.Show();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            pet?.Dispose(); pet = null; petPreferences = petPreferences with { Enabled = false }; petVisible.Checked = false;
            try { petPreferences.Save(AccountPreferences.DefaultDirectory); }
            catch (Exception saveError) when (saveError is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            window.ShowNotice("桌宠暂时无法显示");
        }
    }
    void ShowPanel(bool atCursor = false) { window.PlaceAtTray(atCursor); window.Show(); window.Activate(); }
    protected override void ExitThreadCore()
    {
        quitting = true; pet?.Dispose(); pet = null; tray.Visible = false; tray.Dispose(); if (!window.IsDisposed) { window.Close(); window.Dispose(); } icon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class MeterWindow : Form
{
    enum View { Accounts, Applications, Usage, Report, Forecast, Collection }
    VisualTheme theme;
    Color Background => theme.Background;
    Color Card => theme.Card;
    Color Muted => theme.Secondary;
    Color Accent => theme.Accent;
    Color Primary => theme.Primary;
    readonly FlowLayoutPanel content = new();
    readonly Label status = new();
    readonly Button refresh = new();
    readonly Panel header = new();
    readonly Label title = new(), subtitle = new();
    readonly Button accountTab = new(), applicationsTab = new(), usageTab = new(), reportTab = new(), forecastTab = new(), collectionTab = new();
    readonly Font tabRegular = new("Microsoft YaHei UI", 9F, FontStyle.Bold), tabCompact = new("Microsoft YaHei UI", 8F, FontStyle.Bold);
    readonly FlowLayoutPanel gauges = new();
    readonly System.Windows.Forms.Timer timer = new();
    readonly CancellationTokenSource shutdown = new();
    readonly HistoryWorker historyWorker = new();
    readonly QuotaAlerts quotaAlerts = new();
    readonly bool demo;
    AccountPreferences preferences;
    CancellationTokenSource? accountCancellation;
    bool busy, refreshQueued, positioning, alertsEnabled, forecastEnabled;
    Size preferredSize;
    View view = View.Accounts;
    string? lastSnapshot;
    public string? CurrentSnapshot => lastSnapshot;
    public event Action<string>? SnapshotUpdated;
    public void ShowNotice(string message) { if (!IsDisposed && !Disposing) status.Text = message; }

    public MeterWindow(bool demo, bool windowed)
    {
        this.demo = demo; preferences = demo ? AccountPreferences.Enabled : AccountPreferences.Load(AccountPreferences.DefaultDirectory);
        alertsEnabled = !demo && AlertPreferences.Load(AccountPreferences.DefaultDirectory);
        forecastEnabled = !demo && ForecastPreferences.Load(AccountPreferences.DefaultDirectory);
        theme = demo ? VisualThemes.Find("aurora") : VisualThemes.Load(AccountPreferences.DefaultDirectory);
        Text = "Agent Meter"; Font = new Font("Microsoft YaHei UI", 10F); BackColor = Background; ForeColor = Primary;
        ClientSize = new Size(500, 760); MinimumSize = new Size(420, 480); StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = demo || windowed; FormBorderStyle = demo || windowed ? FormBorderStyle.Sizable : FormBorderStyle.SizableToolWindow;
        preferredSize = Size;
        Resize += (_, _) => { if (!positioning && WindowState == FormWindowState.Normal) preferredSize = Size; };
        header.Dock = DockStyle.Top; header.Height = 280; header.BackColor = Background;
        title.Text = "◈  Agent 用量"; title.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold); title.AutoSize = true; title.Location = new Point(22, 16); title.ForeColor = Accent;
        subtitle.Text = "账户额度与本机消耗"; subtitle.AutoSize = true; subtitle.Location = new Point(25, 55); subtitle.ForeColor = Muted;
        refresh.Text = "刷新"; refresh.Size = new Size(66, 32); refresh.Location = new Point(0, 23);
        refresh.FlatStyle = FlatStyle.Flat; refresh.FlatAppearance.BorderColor = theme.Border; refresh.ForeColor = Accent; refresh.BackColor = Card;
        refresh.Click += async (_, _) => await RefreshData(); header.Controls.AddRange([title, subtitle, refresh]);
        header.Resize += (_, _) => { refresh.Left = header.ClientSize.Width - refresh.Width - 22; gauges.Width = Math.Max(0, header.ClientSize.Width - 40); LayoutTabs(); };
        foreach (var tab in new[] { accountTab, applicationsTab, usageTab, reportTab, forecastTab, collectionTab })
        {
            tab.FlatStyle = FlatStyle.Flat; tab.FlatAppearance.BorderSize = 0; tab.ForeColor = Primary;
            tab.Font = tabRegular; tab.Size = new Size(86, 36);
        }
        gauges.Location = new Point(20, 82); gauges.Size = new Size(450, 144);
        gauges.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        gauges.WrapContents = false; gauges.AutoScroll = true; gauges.BackColor = Background;
        gauges.Resize += (_, _) => LayoutGauges();
        header.Controls.Add(gauges);
        accountTab.Text = "账户额度"; applicationsTab.Text = "应用";
        usageTab.Text = "本机消耗"; reportTab.Text = "用量报告"; forecastTab.Text = "重置预测"; collectionTab.Text = "收集";
        accountTab.Click += (_, _) => SetView(View.Accounts); applicationsTab.Click += (_, _) => SetView(View.Applications);
        usageTab.Click += (_, _) => SetView(View.Usage);
        reportTab.Click += (_, _) => SetView(View.Report);
        forecastTab.Click += (_, _) => SetView(View.Forecast);
        collectionTab.Click += (_, _) => SetView(View.Collection);
        header.Controls.AddRange([accountTab, applicationsTab, usageTab, reportTab, forecastTab, collectionTab]); LayoutTabs(); UpdateTabs();
        status.Dock = DockStyle.Bottom; status.Height = 39; status.Padding = new Padding(22, 8, 0, 0); status.ForeColor = Muted; status.BackColor = Background; status.Text = "正在准备…";
        content.Dock = DockStyle.Fill; content.FlowDirection = FlowDirection.TopDown; content.WrapContents = false; content.AutoScroll = true;
        content.Padding = new Padding(18, 0, 18, 14); content.BackColor = Background; content.SizeChanged += (_, _) => ResizeCards();
        Controls.Add(content); Controls.Add(status); Controls.Add(header);
        timer.Interval = 15 * 60 * 1000; timer.Tick += async (_, _) => await RefreshData(); timer.Start();
    }
    public void PlaceAtTray(bool atCursor)
    {
        Point cursor = Cursor.Position;
        var screen = atCursor ? Screen.FromPoint(cursor) : Screen.PrimaryScreen ?? Screen.FromPoint(cursor);
        positioning = true;
        try
        {
            var fit = PanelPlacement.Fit(preferredSize, screen.WorkingArea);
            MinimumSize = new Size(Math.Min(420, fit.Width), Math.Min(480, fit.Height));
            Size = fit;
            Location = PanelPlacement.Find(screen.Bounds, screen.WorkingArea, Size, cursor, atCursor);
        }
        finally { positioning = false; }
    }
    internal void RenderDemoImages(string directory)
    {
        if (!demo) throw new InvalidOperationException("demo_required");
        Directory.CreateDirectory(directory);
        Location = new Point(-32000, -32000);
        Show();
        RefreshData().GetAwaiter().GetResult();
        void SavePreview(string name)
        {
            Application.DoEvents();
            using var image = new Bitmap(Width, Height);
            DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(directory, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        foreach (var option in VisualThemes.All)
        {
            SetTheme(option);
            foreach (var (tab, name) in new[] { (View.Accounts, "accounts"), (View.Applications, "applications"),
                                               (View.Usage, "usage"), (View.Report, "report"), (View.Forecast, "forecast"),
                                               (View.Collection, "collection") })
            {
                SetView(tab);
                SavePreview(option.Id + "-" + name);
            }
        }
        Size = new Size(420, 760);
        SetTheme(VisualThemes.Find("aurora"));
        SetView(View.Accounts); SavePreview("aurora-accounts-narrow");
        SetView(View.Collection); SavePreview("aurora-collection-narrow");
        // 多账户需要横向滚动；额外预览用于检查滚动条是否挤压仪表。
        var many = JsonNode.Parse(lastSnapshot!)!.AsObject();
        var accounts = new JsonArray();
        for (int index = 1; index <= 5; index++)
            accounts.Add(new JsonObject { ["label"] = "演示" + index, ["status"] = "available",
                ["quota"] = JsonNode.Parse("""{"value":[{"bucket":"5h","remaining_percent":55}]}""") });
        many["other_accounts"]!["MiniMax Code"] = accounts;
        using (var snapshot = JsonDocument.Parse(many.ToJsonString())) Render(snapshot.RootElement);
        SetView(View.Accounts); SavePreview("aurora-many-accounts-narrow");
        Hide();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { shutdown.Cancel(); historyWorker.Dispose(); timer.Dispose(); }
        base.Dispose(disposing);
        if (disposing) { tabRegular.Dispose(); tabCompact.Dispose(); }
    }
    void UpdateTabs()
    {
        foreach (var (button, tab) in new[] { (accountTab, View.Accounts), (applicationsTab, View.Applications), (usageTab, View.Usage), (reportTab, View.Report), (forecastTab, View.Forecast), (collectionTab, View.Collection) })
        {
            button.BackColor = view == tab ? theme.Selected : Card;
            button.ForeColor = view == tab ? Color.White : Primary;
        }
    }
    void LayoutTabs()
    {
        var tabs = new[] { accountTab, applicationsTab, usageTab, reportTab, forecastTab, collectionTab };
        string[] full = ["账户额度", "应用", "本机消耗", "用量报告", "重置预测", "收集"];
        string[] compact = ["账户", "应用", "用量", "报告", "预测", "收集"];
        int width = Math.Max(1, (header.ClientSize.Width - 44 - 4 * (tabs.Length - 1)) / tabs.Length);
        for (int index = 0; index < tabs.Length; index++)
        {
            tabs[index].Size = new Size(width, 36);
            tabs[index].Location = new Point(22 + index * (width + 4), 228);
            tabs[index].Font = width < 70 ? tabCompact : tabRegular;
            tabs[index].Text = width < 70 ? compact[index] : full[index];
        }
    }
    public string ThemeId => theme.Id;
    public bool SetTheme(VisualTheme value)
    {
        try { if (!demo) VisualThemes.Save(AccountPreferences.DefaultDirectory, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Text = "无法保存界面风格，请检查设置目录权限"; return false; }
        theme = value;
        BackColor = header.BackColor = content.BackColor = gauges.BackColor = status.BackColor = Background;
        ForeColor = Primary; title.ForeColor = Accent; subtitle.ForeColor = status.ForeColor = Muted;
        refresh.ForeColor = Accent; refresh.BackColor = Card; refresh.FlatAppearance.BorderColor = theme.Border;
        UpdateTabs();
        if (lastSnapshot is not null) { using var snapshot = JsonDocument.Parse(lastSnapshot); Render(snapshot.RootElement); }
        return true;
    }
    void SetView(View selected)
    {
        view = selected; UpdateTabs();
        if (lastSnapshot is not null) { using var snapshot = JsonDocument.Parse(lastSnapshot); Render(snapshot.RootElement); }
    }

    public bool AccountEnabled(string provider) => provider switch
    {
        "Codex" => preferences.Codex, "Kimi" => preferences.Kimi, "Claude" => preferences.Claude, "MiniMax" => preferences.MiniMax, _ => false
    };
    public Task RefreshAfterAccountChange()
    {
        quotaAlerts.Clear(); accountCancellation?.Cancel();
        return RefreshData();
    }
    public bool AlertsEnabled => alertsEnabled;
    public bool ForecastEnabled => forecastEnabled;
    public bool SetForecastEnabled(bool enabled)
    {
        try { ForecastPreferences.Save(AccountPreferences.DefaultDirectory, enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Text = "无法保存预测设置，请检查设置目录权限"; return false; }
        forecastEnabled = enabled;
        accountCancellation?.Cancel();
        if (lastSnapshot is not null)
        {
            var snapshot = JsonNode.Parse(lastSnapshot)!.AsObject();
            snapshot["forecast"] = new JsonObject { ["status"] = enabled ? "loading" : "disabled" };
            using var doc = JsonDocument.Parse(snapshot.ToJsonString()); Render(doc.RootElement);
        }
        if (busy) { refreshQueued = true; status.Text = "预测设置已保存，当前刷新结束后生效"; }
        else _ = RefreshData();
        return true;
    }
    public event Action<IReadOnlyList<string>>? QuotaLow;
    public bool SetAlertsEnabled(bool enabled)
    {
        try { AlertPreferences.Save(AccountPreferences.DefaultDirectory, enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Text = "无法保存提醒设置，请检查设置目录权限"; return false; }
        alertsEnabled = enabled; quotaAlerts.Clear();
        if (enabled && lastSnapshot is not null)
        {
            using var snapshot = JsonDocument.Parse(lastSnapshot);
            NotifyLowQuota(snapshot.RootElement);
        }
        return true;
    }
    void NotifyLowQuota(JsonElement snapshot)
    {
        if (!alertsEnabled) return;
        var messages = quotaAlerts.Evaluate(snapshot);
        if (messages.Count > 0) QuotaLow?.Invoke(messages);
    }
    public bool SetAccountEnabled(string provider, bool enabled)
    {
        var next = provider switch
        {
            "Codex" => preferences with { Codex = enabled }, "Kimi" => preferences with { Kimi = enabled },
            "Claude" => preferences with { Claude = enabled }, "MiniMax" => preferences with { MiniMax = enabled }, _ => preferences
        };
        try { next.Save(AccountPreferences.DefaultDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            status.Text = "无法保存账户开关，请检查设置目录权限";
            return false;
        }
        preferences = next;
        quotaAlerts.Clear();
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
        if (!preferences.MiniMax) others["MiniMax Code"] = Disabled();
        if (!forecastEnabled) snapshot["forecast"] = Disabled();
    }

    public async Task RefreshData()
    {
        if (busy) { refreshQueued = true; return; }
        busy = true; refresh.Enabled = false; status.Text = "正在刷新，本机记录较多时可能需要一些时间…";
        try
        {
            if (demo)
            {
                var sample = JsonNode.Parse(DemoData)!.AsObject();
                sample["forecast"] = JsonNode.Parse("""{"status":"partial","cache_state":"fresh","sources":[{"name":"WillReset","status":"available","24h":35,"24h_basis":"published_answer","48h":55,"48h_basis":"cadence_model_fallback"},{"name":"Tibo Radar","status":"available","24h":28,"24h_basis":"published_answer","48h":49,"48h_basis":"published_answer"},{"name":"Reset Monitor","status":"unavailable"}]}""");
                sample["local_medals"] = JsonNode.Parse("""{"sources":2,"incomplete":false,"earned":1,"total":3,"active_days":12,"medals":[{"name":"亿级玩家","category":"day_peak","category_name":"单日用量","target":100000000,"unit":"tokens","value":120000000,"progress":1,"earned":true,"earned_on":"2026-11-12"},{"name":"五亿狂飙","category":"day_peak","category_name":"单日用量","target":500000000,"unit":"tokens","value":120000000,"progress":0.24,"earned":false},{"name":"一周不停","category":"streak","category_name":"连续使用","target":7,"unit":"days","value":4,"progress":0.57,"earned":false}]}""");
                using var preview = JsonDocument.Parse(sample.ToJsonString()); Render(preview.RootElement); status.Text = "演示数据 · 不代表真实账户";
            }
            else
            {
                bool partialFailure = false;
                accountCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                string? accountInput = null; bool profilesInvalid = false;
                if (preferences.MiniMax)
                {
                    try { accountInput = MiniMaxAccountInput.Build(AccountPreferences.DefaultDirectory, new WindowsMiniMaxVault()); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
                    { profilesInvalid = true; }
                }
                string arguments = "--account-only" + (preferences.Codex ? "" : " --disable-codex-account") +
                    (preferences.Kimi ? "" : " --disable-kimi-account") + (preferences.Claude ? "" : " --disable-claude-account") +
                    (preferences.MiniMax && !profilesInvalid ? " --enable-minimax-account" : "") +
                    (accountInput is null ? "" : " --account-input");
                var accountTask = RunCollector(arguments, accountCancellation.Token, accountInput);
                accountInput = null;
                var historyTask = historyWorker.RefreshAsync(shutdown.Token);
                var forecastTask = forecastEnabled ? RunCollector("--forecast-only", accountCancellation.Token) : null;
                JsonObject combined;
                try { combined = JsonNode.Parse(await accountTask)!.AsObject(); }
                catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    partialFailure = true;
                    combined = new JsonObject { ["account"] = new JsonObject { ["status"] = "error", ["reason"] = "account_read_failed" } };
                }
                if (profilesInvalid)
                {
                    if (combined["other_accounts"] is not JsonObject) combined["other_accounts"] = new JsonObject();
                    combined["other_accounts"]!["MiniMax Code"] = new JsonObject { ["status"] = "error", ["reason"] = "minimax_profiles_invalid" };
                }
                ApplyAccountPreferences(combined);
                combined["history"] = new JsonObject { ["历史统计"] = new JsonObject { ["status"] = "loading", ["reason"] = "history_loading" } };
                combined["forecast"] = new JsonObject { ["status"] = forecastEnabled ? "loading" : "disabled" };
                using (var first = JsonDocument.Parse(combined.ToJsonString())) { Render(first.RootElement); NotifyLowQuota(first.RootElement); }
                status.Text = "账户读取完成 · 正在扫描本机会话记录…";
                try
                {
                    var local = JsonNode.Parse(await historyTask)!.AsObject();
                    combined["history"] = local["history"]?.DeepClone();
                    combined["local_credits"] = local["local_credits"]?.DeepClone();
                    combined["daily_trend"] = local["daily_trend"]?.DeepClone();
                    combined["calendar_report"] = local["calendar_report"]?.DeepClone();
                    combined["local_medals"] = local["local_medals"]?.DeepClone();
                    combined["activity"] = local["activity"]?.DeepClone();
                    combined["price_status"] = local["price_status"]?.DeepClone();
                }
                catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    partialFailure = true;
                    combined["history"] = new JsonObject { ["历史统计"] = new JsonObject { ["status"] = "error", ["reason"] = "history_scan_failed" } };
                }
                ApplyAccountPreferences(combined);
                using (var localSnapshot = JsonDocument.Parse(combined.ToJsonString())) Render(localSnapshot.RootElement);
                status.Text = partialFailure ? "部分数据读取失败 · 请稍后刷新" : $"更新于 {DateTime.Now:HH:mm:ss}  ·  本机历史 ≠ 账户总用量";
                if (forecastTask is not null)
                {
                    try { combined["forecast"] = JsonNode.Parse(await forecastTask); }
                    catch (Exception ex) when (!shutdown.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
                    { combined["forecast"] = new JsonObject { ["status"] = "error" }; }
                    ApplyAccountPreferences(combined);
                    using var final = JsonDocument.Parse(combined.ToJsonString()); Render(final.RootElement);
                }
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
    static async Task<string> RunCollector(string mode, CancellationToken shutdown, string? accountInput = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "backend", "AgentMeterBackend.exe");
        if (!File.Exists(path)) throw new IOException("backend_missing");
        using var process = new Process { StartInfo = new ProcessStartInfo(path, mode) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 } };
        if (accountInput is not null) { process.StartInfo.RedirectStandardInput = true; process.StartInfo.StandardInputEncoding = new System.Text.UTF8Encoding(false); }
        if (!process.Start()) throw new InvalidOperationException("backend_start_failed");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            if (accountInput is not null)
            {
                await process.StandardInput.WriteAsync(accountInput.AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                process.StandardInput.Close();
                accountInput = null;
            }
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            try { process.Kill(true); }
            catch (Exception stopError) when (stopError is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            throw;
        }
        await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("backend_failed");
        return await output;
    }
    void Render(JsonElement root)
    {
        lastSnapshot = root.GetRawText();
        SnapshotUpdated?.Invoke(lastSnapshot);
        UpdateGauges(root);
        content.SuspendLayout();
        while (content.Controls.Count > 0) content.Controls[0].Dispose();
        if (view == View.Accounts)
        {
        AddHeading("账户额度", "来自账户接口 · 与本机 Token 分开统计");
        var account = GetValue(root, "account"); var quotas = GetValue(GetValue(account, "quota"), "value");
        if (quotas.ValueKind == JsonValueKind.Array && quotas.GetArrayLength() > 0)
        {
            var rows = new List<QuotaRow>();
            foreach (var q in quotas.EnumerateArray())
            {
                string bucket = GetString(q, "bucket", "Codex"), window = GetString(q, "window", "");
                var minutes = GetNumber(q, "window_minutes");
                string period = minutes == 300 ? "5 小时" : minutes == 10080 ? "7 天" : minutes.HasValue ? $"{minutes:0} 分钟" : window;
                double? remaining = GetNumber(q, "remaining_percent");
                string prefix = bucket.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "" : bucket.ToUpperInvariant() + " · ";
                rows.Add(new QuotaRow(prefix + period, remaining, ResetNote(q)));
            }
            AddQuotaGroup("CODEX", rows);
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
                var rows = new List<QuotaRow>();
                foreach (var q in kimiQuotas.EnumerateArray())
                {
                    string bucket = GetString(q, "bucket", "");
                    var window = GetValue(q, "window");
                    double? duration = GetNumber(window, "duration");
                    string unit = GetString(window, "unit", "");
                    string label = bucket switch { "limit5h" => "5 小时", "limit7d" => "7 天", "monthTotal" => "月度总额", "monthCode" => "月度代码", _ => bucket };
                    if (duration.HasValue) label = $"{duration:0.##} " + (unit switch { "hour" => "小时", "day" => "天", "week" => "周", "month" => "月", "minute" => "分钟", "second" => "秒", _ => unit });
                    double? remaining = GetNumber(q, "remaining_percent");
                    rows.Add(new QuotaRow(label, remaining, ResetNote(q)));
                }
                AddQuotaGroup("KIMI CODE", rows);
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
            var rows = new List<QuotaRow>();
            foreach (var q in claudeQuotas.EnumerateArray())
            {
                string bucket = GetString(q, "bucket", "");
                string label = bucket switch { "five_hour" => "5 小时", "seven_day" => "7 天", "seven_day_opus" => "7 天 · Opus", "seven_day_sonnet" => "7 天 · Sonnet", "seven_day_oauth_apps" => "7 天 · OAuth 应用", _ => bucket };
                double? remaining = GetNumber(q, "remaining_percent");
                rows.Add(new QuotaRow(label, remaining, ResetNote(q)));
            }
            if (rows.Count > 0) AddQuotaGroup("CLAUDE CODE", rows);
        }
        else if (GetString(claudeAccount, "status", "") == "disabled") AddCard("CLAUDE CODE", "账户查询已关闭", "可在托盘菜单重新开启", null);
        else if (GetString(claudeAccount, "reason", "") is not ("" or "claude_subscription_login_missing"))
            AddCard("CLAUDE CODE", "暂时无法获取额度", Reason(claudeAccount), null);
        var minimaxAccount = GetValue(otherAccounts, "MiniMax Code");
        if (minimaxAccount.ValueKind == JsonValueKind.Array)
        {
            foreach (var accountRow in minimaxAccount.EnumerateArray())
                AddMiniMaxAccount(accountRow, GetString(accountRow, "label", "账户"));
        }
        else AddMiniMaxAccount(minimaxAccount, "");
        }
        else if (view == View.Applications) AddApplications(GetValue(root, "activity"));
        else if (view == View.Usage)
        {
        AddHeading("本机 Token", "只统计本设备可读取的会话记录");
        var history = GetValue(root, "history");
        if (history.ValueKind == JsonValueKind.Object)
        {
            AddUsageSummary(history);
            AddDailyTrend(GetValue(root, "daily_trend"));
            AddHeading("本机账本", GetString(root, "price_status", "") == "invalid"
                ? "自定义价格无法读取，已隐藏金额" : "按价格快照及自定义价格估算，不代表账单");
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
                var estimate = GetValue(item.Value, "estimate");
                string rawAmount = GetString(estimate, "amount_usd", "");
                string? costNote = null;
                if (decimal.TryParse(rawAmount, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount >= 0)
                {
                    string display = amount is > 0 and < 0.0001m ? "<$0.0001" : $"${amount:0.####}";
                    costNote = $"今日 API 等价估算 {display} · 非账单" + (GetValue(estimate, "partial").ValueKind == JsonValueKind.True ? " · 不完整" : "");
                }
                AddCard(item.Name, dayTokens.HasValue ? FormatNumber(dayTokens.Value) + " 今日" : "暂无数据", note, null, extra, costNote);
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
        else if (view == View.Report) AddCalendarReport(GetValue(root, "calendar_report"));
        else if (view == View.Forecast) AddForecast(GetValue(root, "forecast"));
        else AddCollection(GetValue(root, "local_medals"));
        content.ResumeLayout(); ResizeCards();
    }
    void AddForecast(JsonElement forecast)
    {
        AddHeading("重置预测", "第三方公开预测 · 不代表账户自身的重置时间");
        string state = GetString(forecast, "status", "disabled");
        if (state == "disabled")
        {
            AddCard("第三方预测", "默认关闭", "在托盘菜单开启后访问三个第三方网站", null,
                    "WillReset · Tibo Radar · Reset Monitor");
            return;
        }
        if (state == "loading") { AddCard("第三方预测", "正在读取", "不会发送账户凭据或本机会话", null); return; }
        var rows = GetValue(forecast, "sources");
        if (state is "error" or "unavailable" || rows.ValueKind != JsonValueKind.Array)
        {
            AddCard("第三方预测", "暂时无法获取", "请检查网络或稍后刷新", null);
            return;
        }
        string cache = GetString(forecast, "cache_state", "");
        if (cache == "stale") AddCard("数据状态", "使用旧缓存", "最新查询失败，预测可能已过时", null);
        else if (state == "partial") AddCard("数据状态", "部分来源不可用", "以下数值由各站独立发布或推算", null);
        foreach (var row in rows.EnumerateArray())
        {
            string name = GetString(row, "name", "第三方");
            if (GetString(row, "status", "") is not ("available" or "stale"))
            { AddCard(name, "暂时不可用", "此来源未返回可用预测", null); continue; }
            var day = GetNumber(row, "24h"); var twoDays = GetNumber(row, "48h");
            string first = day.HasValue ? $"24 小时 {day:0.#}%" : "24 小时未提供";
            string second = twoDays.HasValue ? $"48 小时 {twoDays:0.#}%" : "48 小时未提供";
            string basis = !day.HasValue && !twoDays.HasValue ? "该站未提供可用数值" :
                GetString(row, "24h_basis", "") == "cadence_model_fallback" || GetString(row, "48h_basis", "") == "cadence_model_fallback"
                    ? "含站点模型推算值" : "站点公布值";
            double? updated = GetNumber(row, "updated_at");
            string updatedNote = "站点未提供更新时间";
            if (updated.HasValue && double.IsFinite(updated.Value) && updated.Value >= 0 && updated.Value <= 253402300799)
            {
                try
                {
                    var stamp = DateTimeOffset.FromUnixTimeSeconds((long)updated.Value);
                    updatedNote = "站点更新 " + stamp.LocalDateTime.ToString("MM-dd HH:mm") +
                        (DateTimeOffset.UtcNow - stamp > TimeSpan.FromHours(48) ? " · 已超过 48 小时" : "");
                }
                catch (ArgumentException) { }
            }
            AddCard(name, first, second, null, basis, updatedNote);
        }
        AddCard("统计说明", "仅供参考", "第三方预测并非官方承诺，也不是个人额度周期", null);
    }
    void AddCollection(JsonElement collection)
    {
        AddHeading("收集册", "基于本机逐日账本 · 不同记录可能重叠");
        var medals = GetValue(collection, "medals");
        if (medals.ValueKind != JsonValueKind.Array || medals.GetArrayLength() == 0)
        {
            string note = GetValue(collection, "incomplete").ValueKind == JsonValueKind.True
                ? "部分账本未能读取，请稍后刷新" : "需要可读取的本机逐日用量账本";
            AddCard("本机徽章", "暂无可用记录", note, null);
            return;
        }
        var earned = GetNumber(collection, "earned"); var total = GetNumber(collection, "total");
        var sources = GetNumber(collection, "sources");
        AddCard("本机徽章", earned.HasValue && total.HasValue ? $"{earned:0} / {total:0} 已获得" : "正在计算",
                sources.HasValue ? $"基于 {sources:0} 个可读取账本" : "基于可读取账本", null);
        if (GetValue(collection, "incomplete").ValueKind == JsonValueKind.True)
            AddCard("读取范围", "部分记录不完整", "徽章只反映已读取的数据", null);
        foreach (var (category, title) in new[] { ("day_peak", "单日巅峰"), ("lifetime", "累计里程"),
            ("streak", "连续出勤"), ("apps", "多端并用"), ("record", "个人纪录") })
        {
            var rows = medals.EnumerateArray().Where(row => GetString(row, "category", "") == category).ToArray();
            if (rows.Length == 0) continue;
            AddHeading(title, category == "streak" ? "当前连续天数与历史达成分别计算" : "按已读取的本机记录计算");
            foreach (var row in rows)
            {
                string name = GetString(row, "name", "徽章");
                double? value = GetNumber(row, "value"), target = GetNumber(row, "target"), progress = GetNumber(row, "progress");
                if (!value.HasValue || !target.HasValue || !progress.HasValue || !double.IsFinite(value.Value) ||
                    !double.IsFinite(target.Value) || !double.IsFinite(progress.Value) || value < 0 || target <= 0) continue;
                string unit = GetString(row, "unit", "") switch { "tokens" => "tokens", "days" => "天", "apps" => "个应用", "times" => "次", _ => "" };
                bool hasMedal = GetValue(row, "earned").ValueKind == JsonValueKind.True;
                string date = GetString(row, "earned_on", "");
                string detail = $"当前 {FormatNumber(value.Value)} / {FormatNumber(target.Value)} {unit}" +
                    (hasMedal && date.Length == 10 ? " · 达成于 " + date : "");
                AddCard(name, hasMedal ? "已获得" : "待解锁", detail, Math.Clamp(progress.Value * 100, 0, 100));
            }
        }
        AddCard("尚在移植", "更多卡片与徽章", "签到、图片、估算花费等暂未计算", null);
    }

    void AddApplications(JsonElement activity)
    {
        AddHeading("应用活动", "近 24 小时的本机记录 · 不代表进程状态");
        var apps = GetValue(activity, "apps");
        if (apps.ValueKind != JsonValueKind.Array || apps.GetArrayLength() == 0)
        {
            string note = GetString(activity, "status", "") switch { "error" => "活动记录读取失败，请稍后刷新",
                "partial" => "部分来源读取不完整，暂无可展示记录", _ => "未发现可展示的活动记录，或来源暂不支持" };
            AddCard("近期活动", "暂无数据", note, null);
            return;
        }
        if (GetString(activity, "status", "") == "partial")
            AddCard("读取范围", "部分来源不完整", "不同客户端的活动记录覆盖范围不同", null);
        foreach (var app in apps.EnumerateArray())
        {
            string name = GetString(app, "name", "应用");
            var sessions = GetNumber(app, "sessions"); var recent = GetNumber(app, "recent_updates");
            var last = GetNumber(app, "last_updated");
            if (!sessions.HasValue || !recent.HasValue || !last.HasValue || last.Value < 0 || last.Value > 253402300799) continue;
            string when = DateTimeOffset.FromUnixTimeSeconds((long)last.Value).LocalDateTime.ToString("MM-dd HH:mm");
            AddCard(name, $"{sessions:0} 条活动记录", $"采集时 15 分钟内更新：{recent:0} 条", null, "最后更新 " + when + "（本机时间）");
        }
    }
    void AddMiniMaxAccount(JsonElement account, string accountLabel)
    {
        string prefix = "MINIMAX CODE" + (accountLabel.Length > 0 ? " · " + accountLabel : "");
        var quotas = GetValue(GetValue(account, "quota"), "value");
        if (quotas.ValueKind == JsonValueKind.Array && quotas.GetArrayLength() > 0)
        {
            var rows = new List<QuotaRow>();
            foreach (var quota in quotas.EnumerateArray())
            {
                string label = GetString(quota, "bucket", "") switch { "5h" => "5 小时", "week" => "7 天", var other => other };
                double? remaining = GetNumber(quota, "remaining_percent");
                rows.Add(new QuotaRow(label, remaining, ResetNote(quota)));
            }
            AddQuotaGroup(prefix, rows);
        }
        else if (GetString(account, "status", "") is "unavailable" or "error")
            AddCard(prefix, "暂时无法获取额度", Reason(account), null);
        else if (GetString(account, "status", "") is "available" or "partial")
            AddCard(prefix, "额度未提供", "账户服务未返回可展示的额度窗口", null);
    }
    void AddCalendarReport(JsonElement report)
    {
        AddHeading("用量报告", "北京时间 · 本机账本相加，可能存在重叠");
        var periods = GetValue(report, "periods");
        if (periods.ValueKind != JsonValueKind.Object)
        {
            AddCard("自然周与自然月", "暂无可用报告", "需要可读取的本机逐日用量记录", null);
            return;
        }
        bool incomplete = GetValue(report, "incomplete").ValueKind == JsonValueKind.True;
        foreach (var (key, title, previous) in new[] { ("week", "本周", "上周"), ("month", "本月", "上月") })
        {
            var period = GetValue(periods, key);
            var current = GetNumber(period, "current_tokens"); var before = GetNumber(period, "previous_tokens");
            if (!current.HasValue || !before.HasValue) continue;
            string start = GetString(period, "current_start", ""), end = GetString(period, "current_end", "");
            string note = $"{previous} {FormatNumber(before.Value)} · 本期截至 {end}";
            string extra = $"本期开始 {start}" + (incomplete ? " · 部分来源不完整" : " · 当前周期未结束");
            AddCard(title, FormatNumber(current.Value) + " tokens", note, null, extra);
        }
        AddCard("统计说明", "自然周期比较", "当前周期尚未结束；上期为完整周期", null,
                "仅覆盖本机可读取记录，不等于账户总量");
        var export = new Button { Text = "导出 Markdown 报告", Height = 42, Margin = new Padding(0, 0, 0, 12),
            BackColor = Card, ForeColor = Primary, FlatStyle = FlatStyle.Flat };
        export.FlatAppearance.BorderColor = theme.Border;
        export.Click += (_, _) => ExportCalendarReport();
        content.Controls.Add(export);
        var imageExport = new Button { Text = "导出报告分享图", Height = 42, Margin = new Padding(0, 0, 0, 12),
            BackColor = Card, ForeColor = Primary, FlatStyle = FlatStyle.Flat };
        imageExport.FlatAppearance.BorderColor = theme.Border;
        imageExport.Click += (_, _) => ExportShareImage();
        content.Controls.Add(imageExport);
    }
    void ExportCalendarReport()
    {
        try
        {
            if (lastSnapshot is null) throw new InvalidDataException("没有可导出的报告");
            using var snapshot = JsonDocument.Parse(lastSnapshot);
            string report = CalendarReportExport.Create(snapshot.RootElement, demo);
            using var dialog = new SaveFileDialog { Title = "导出本机用量报告", Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
                FileName = "AgentMeter-报告-" + DateTime.Now.ToString("yyyy-MM-dd") + ".md", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dialog.FileName, report, new System.Text.UTF8Encoding(false));
            status.Text = "报告已导出到所选位置";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            MessageBox.Show(this, ex is InvalidDataException ? ex.Message : "无法导出报告，请检查目标位置。",
                "导出未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    void ExportShareImage()
    {
        try
        {
            if (lastSnapshot is null) throw new InvalidDataException("没有可导出的报告");
            using var snapshot = JsonDocument.Parse(lastSnapshot);
            using var image = ShareCardRenderer.Render(snapshot.RootElement, theme, demo);
            using var dialog = new SaveFileDialog { Title = "导出报告分享图", Filter = "PNG 图片 (*.png)|*.png",
                FileName = "AgentMeter-分享图-" + DateTime.Now.ToString("yyyy-MM-dd") + ".png", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            image.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            status.Text = "报告分享图已导出到所选位置";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException or System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, ex is InvalidDataException ? ex.Message : "无法导出图片，请检查目标位置。",
                "导出未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    void UpdateGauges(JsonElement root)
    {
        while (gauges.Controls.Count > 0) gauges.Controls[0].Dispose();
        AddGauge("Codex", GetValue(root, "account"));
        AddGauge("Kimi", GetValue(GetValue(root, "other_accounts"), "Kimi Code"));
        AddGauge("Claude", GetValue(GetValue(root, "other_accounts"), "Claude Code"));
        var minimax = GetValue(GetValue(root, "other_accounts"), "MiniMax Code");
        if (minimax.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in minimax.EnumerateArray()) AddGauge("MiniMax · " + GetString(account, "label", "账户"), account);
        }
        else AddGauge("MiniMax", minimax);
        if (gauges.Controls.Count == 0)
            gauges.Controls.Add(new Label { Text = "暂无可用账户额度", ForeColor = Muted, AutoSize = true, Margin = new Padding(5, 32, 0, 0) });
        LayoutGauges();
    }
    void LayoutGauges()
    {
        var items = gauges.Controls.OfType<QuotaGauge>().ToArray();
        bool scroll = items.Length > 4;
        if (gauges.AutoScroll != scroll) gauges.AutoScroll = scroll;
        if (items.Length == 0) return;
        int width = items.Length <= 4 ? Math.Clamp(gauges.ClientSize.Width / items.Length - 7, 70, 95) : 95;
        foreach (var item in items) item.Width = width;
    }
    void AddGauge(string provider, JsonElement account)
    {
        var windows = QuotaWindows.Select(account);
        if (windows is not null) gauges.Controls.Add(new QuotaGauge(provider, windows, theme));
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
        "minimax_credential_not_connected" => "请设置 MINIMAX_TOKEN_PLAN_KEY 并重启",
        "minimax_vault_credential_missing" => "凭据缺失或无法读取，请在账户管理中更新密钥",
        "minimax_profiles_invalid" => "账户列表无法读取，请备份并检查本地文件",
        "minimax_no_saved_accounts" => "尚未添加账户，请在托盘菜单管理 MiniMax 账户",
        "invalid_minimax_region" => "MiniMax 区域设置不正确（cn 或 global）",
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
        box.Controls.Add(new Label { Text = title, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), AutoSize = true, Location = new Point(3, 5), ForeColor = Primary });
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
        var box = new RoundedCard(theme) { Height = 190, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = "今日 Token 总量", AutoSize = true, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Muted, Location = new Point(15, 10) });
        box.Controls.Add(new Label { Text = FormatNumber(total), AutoSize = true, Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), ForeColor = Primary, Location = new Point(14, 31) });
        box.Controls.Add(new Label { Text = $"{sources} 个本机账本相加" + (partial ? " · 含不完整来源" : ""), AutoSize = true, ForeColor = Muted, Location = new Point(16, 82) });
        if (complete && total > 0)
        {
            var bar = new BreakdownBar(parts, theme) { Location = new Point(16, 116), Height = 9 };
            box.Resize += (_, _) => bar.Width = Math.Max(0, box.ClientSize.Width - 32); box.Controls.Add(bar);
            var legend = new FlowLayoutPanel { Location = new Point(16, 137), Height = 48, WrapContents = true };
            string[] labels = ["输入", "输出", "缓存读取", "缓存写入", "未拆分"];
            for (int i = 0; i < labels.Length; i++)
                legend.Controls.Add(new Label { Text = "● " + labels[i], AutoSize = true, Font = new Font(Font.FontFamily, 8F), ForeColor = BreakdownBar.ColorsFor(theme)[i], Margin = new Padding(0, 0, 8, 0) });
            box.Resize += (_, _) => legend.Width = Math.Max(0, box.ClientSize.Width - 32); box.Controls.Add(legend);
        }
        else box.Controls.Add(new Label { Text = "部分来源未提供完整拆分", AutoSize = true, ForeColor = Muted, Location = new Point(16, 125) });
        StyleCard(box); content.Controls.Add(box);
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
        var box = new RoundedCard(theme) { Height = 218, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = "最近 7 个自然日", AutoSize = true, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Primary, Location = new Point(15, 10) });
        bool incomplete = GetValue(trend, "incomplete").ValueKind == JsonValueKind.True;
        box.Controls.Add(new Label { Text = incomplete ? "北京时间 · 部分记录不完整" : "北京时间 · 本机账本相加", AutoSize = true, ForeColor = Muted, Location = new Point(16, 38) });
        var chart = new DailyChart(points, theme) { Location = new Point(14, 70), Height = 132 };
        box.Resize += (_, _) => chart.Width = Math.Max(0, box.ClientSize.Width - 28); box.Controls.Add(chart);
        StyleCard(box); content.Controls.Add(box);
    }
    sealed record QuotaRow(string Name, double? Remaining, string Reset);

    static string ResetNote(JsonElement quota)
    {
        double? epoch = GetNumber(GetValue(quota, "resets_at"), "epoch_seconds");
        if (!epoch.HasValue || !double.IsFinite(epoch.Value) || epoch.Value < 0 || epoch.Value > 253402300799)
            return "重置时间未提供";
        try { return "重置于 " + DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).LocalDateTime.ToString("MM-dd HH:mm"); }
        catch (ArgumentException) { return "重置时间未提供"; }
    }

    void AddQuotaGroup(string provider, IReadOnlyList<QuotaRow> rows)
    {
        int shown = Math.Min(rows.Count, 40);
        if (shown == 0) return;
        var box = new RoundedCard(theme) { Height = 45 + shown * 70, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = provider, AutoSize = true, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), ForeColor = Primary, Location = new Point(15, 11) });
        for (int index = 0; index < shown; index++)
        {
            var row = rows[index];
            int top = 43 + index * 70;
            string label = new string(row.Name.Where(c => !char.IsControl(c)).Take(80).ToArray());
            if (label.Length == 0) label = "额度窗口";
            var name = new Label { Text = label, AutoEllipsis = true, Height = 21, ForeColor = Muted, Location = new Point(16, top) };
            double? remaining = row.Remaining is double number && double.IsFinite(number) && number >= 0 && number <= 100 ? number : null;
            var amount = new Label { Text = remaining.HasValue ? $"{remaining:0.#}% 剩余" : "额度未提供", AutoSize = true,
                Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Primary, Location = new Point(0, top) };
            var note = new Label { Text = row.Reset, AutoSize = true, Font = new Font(Font.FontFamily, 9F), ForeColor = Muted, Location = new Point(16, top + 40) };
            var bar = new MeterBar(theme) { Value = remaining ?? 0, Visible = remaining.HasValue, Location = new Point(16, top + 27), Height = 5 };
            box.Controls.AddRange([name, amount, note, bar]);
            box.Resize += (_, _) =>
            {
                amount.Left = Math.Max(16, box.ClientSize.Width - amount.Width - 16);
                name.Width = Math.Max(0, amount.Left - name.Left - 8);
                bar.Width = Math.Max(0, box.ClientSize.Width - 32);
            };
        }
        StyleCard(box); content.Controls.Add(box);
        if (rows.Count > shown) AddCard(provider, "部分额度未显示", $"仅展示前 {shown} 个窗口", null);
    }

    void AddCard(string name, string value, string detail, double? progress, string? extra = null, string? cost = null)
    {
        var box = new RoundedCard(theme) { Height = progress.HasValue ? 112 : cost is not null ? (extra is null ? 120 : 146) : extra is null ? 95 : 120, Margin = new Padding(0, 0, 0, 12), BackColor = Card };
        box.Controls.Add(new Label { Text = name, AutoSize = true, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), ForeColor = Muted, Location = new Point(15, 11) });
        box.Controls.Add(new Label { Text = value, AutoSize = true, Font = new Font(Font.FontFamily, 14F, FontStyle.Bold), ForeColor = Primary, Location = new Point(14, 31) });
        box.Controls.Add(new Label { Text = detail, AutoSize = true, ForeColor = Muted, Location = new Point(16, progress.HasValue ? 84 : 67) });
        if (extra is not null) box.Controls.Add(new Label { Text = extra, AutoSize = true, ForeColor = Muted, Location = new Point(16, 91) });
        if (cost is not null) box.Controls.Add(new Label { Text = cost, AutoSize = true, ForeColor = Accent, Location = new Point(16, extra is null ? 91 : 115) });
        if (progress.HasValue)
        {
            var bar = new MeterBar(theme) { Value = Math.Clamp(progress.Value, 0, 100), Location = new Point(16, 70), Height = 5 };
            box.Resize += (_, _) => bar.Width = Math.Max(0, box.ClientSize.Width - 32);
            box.Controls.Add(bar);
        }
        StyleCard(box); content.Controls.Add(box);
    }
    static void StyleCard(RoundedCard box)
    {
        foreach (Control child in box.Controls)
            if (child is Label) child.BackColor = Color.Transparent;
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
    {"account":{"status":"available","quota":{"value":[{"bucket":"codex","window":"primary","window_minutes":300,"remaining_percent":73,"resets_at":{"epoch_seconds":1794654000}},{"bucket":"codex","window":"secondary","window_minutes":10080,"remaining_percent":46,"resets_at":{"epoch_seconds":1794913200}}]}},"other_accounts":{"Kimi Code":{"status":"available","quota":{"value":[{"bucket":"limit5h","remaining_percent":64,"resets_at":{"epoch_seconds":1794654000}}]},"credits":{"value":{"amount":"12.34","unit":"CNY"}}},"Claude Code":{"status":"available","quota":{"value":[{"bucket":"five_hour","window_minutes":300,"remaining_percent":82,"resets_at":{"epoch_seconds":1794654000}},{"bucket":"seven_day","window_minutes":10080,"remaining_percent":53,"resets_at":{"epoch_seconds":1794913200}}]}},"MiniMax Code":{"status":"available","quota":{"value":[{"bucket":"5h","remaining_percent":55,"resets_at":{"epoch_seconds":1794654000}}]}}},"history":{"Codex":{"status":"available","value":{"periods":{"today":{"totals":{"total_tokens":1426000,"fresh_input":300000,"output":126000,"reasoning":0,"cache_read":1000000,"cache_write":0}},"7d":{"totals":{"total_tokens":7600000},"groups":[{"model":"gpt-5.3-codex","total_tokens":6200000},{"model":"gpt-5.1-codex","total_tokens":1400000}]},"30d":{"totals":{"total_tokens":25800000}},"all":{"totals":{"total_tokens":102000000}}}},"estimate":{"amount_usd":"2.464","partial":false}},"Claude Code":{"status":"available","value":{"periods":{"today":{"totals":{"total_tokens":341000,"fresh_input":100000,"output":41000,"reasoning":0,"cache_read":200000,"cache_write":0}},"7d":{"totals":{"total_tokens":900000},"groups":[{"model":"claude-sonnet-4","total_tokens":900000}]},"30d":{"totals":{"total_tokens":2400000}},"all":{"totals":{"total_tokens":3800000}}}}},"Gemini CLI":{"status":"unavailable","reason":"gemini_chats_not_found"}},"local_credits":{"Qoder CN":{"status":"available","value":{"periods":{"today":{"totals":{"credits":"12.5"}},"7d":{"totals":{"credits":"75"}},"30d":{"totals":{"credits":"248"}},"all":{"totals":{"credits":"560","billable_credits":"420"}}}}}},"daily_trend":{"days":[{"date":"2026-11-08","tokens":340000},{"date":"2026-11-09","tokens":780000},{"date":"2026-11-10","tokens":1120000},{"date":"2026-11-11","tokens":550000},{"date":"2026-11-12","tokens":970000},{"date":"2026-11-13","tokens":1300000},{"date":"2026-11-14","tokens":1767000}],"sources":2,"incomplete":false},"calendar_report":{"periods":{"week":{"previous_start":"2026-11-02","current_start":"2026-11-09","current_end":"2026-11-14","previous_tokens":4200000,"current_tokens":6367000},"month":{"previous_start":"2026-10-01","current_start":"2026-11-01","current_end":"2026-11-14","previous_tokens":14200000,"current_tokens":8200000}},"sources":2,"incomplete":false,"timezone":"Asia/Shanghai"},"activity":{"status":"available","window_hours":24,"idle_minutes":15,"apps":[{"name":"Codex","sessions":5,"recent_updates":2,"last_updated":1794650400},{"name":"Claude Code","sessions":2,"recent_updates":0,"last_updated":1794643200}]}}
    """;
}
internal sealed class RoundedCard : Panel
{
    readonly VisualTheme theme;
    public RoundedCard(VisualTheme theme) { this.theme = theme; DoubleBuffered = true; }
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
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0) return;
        using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, theme.Card, theme.CardEnd, 90F);
        e.Graphics.FillRectangle(fill, ClientRectangle);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (theme.Minimal || Width < 22 || Height < 22) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var border = new Pen(theme.Border);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int r = 20, w = Width - 1, h = Height - 1;
        path.AddArc(0, 0, r, r, 180, 90); path.AddArc(w - r, 0, r, r, 270, 90);
        path.AddArc(w - r, h - r, r, r, 0, 90); path.AddArc(0, h - r, r, r, 90, 90); path.CloseFigure();
        e.Graphics.DrawPath(border, path);
    }
}
internal sealed class MeterBar : Control
{
    readonly VisualTheme theme;
    public MeterBar(VisualTheme theme) { this.theme = theme; }
    public double Value { get; set; }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(theme.Border); using var brush = new SolidBrush(Value < 20 ? (theme.Light ? Color.FromArgb(181, 51, 71) : Color.FromArgb(244, 136, 116)) : theme.Accent); e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * Value / 100), Height); }
}
internal sealed class QuotaGauge : Control
{
    readonly string provider;
    readonly QuotaWindowPair windows;
    readonly VisualTheme theme;
    public QuotaGauge(string provider, QuotaWindowPair windows, VisualTheme theme)
    {
        this.provider = provider; this.windows = windows; this.theme = theme;
        Size = new Size(95, 126); Margin = new Padding(0, 0, 7, 0);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = $"{provider} {windows.Primary.Label}，剩余 {windows.Primary.Remaining:0.#}%" +
            (windows.Secondary is null ? "" : $"；{windows.Secondary.Label}，剩余 {windows.Secondary.Remaining:0.#}%");
        DoubleBuffered = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        void Ring(Rectangle rect, double remaining, Color normal)
        {
            using var track = new Pen(theme.Border, 5);
            using var fill = new Pen(remaining < 20 ? (theme.Light ? Color.FromArgb(181, 51, 71) : Color.FromArgb(255, 98, 135)) :
                remaining < 50 ? (theme.Light ? Color.FromArgb(160, 96, 0) : Color.FromArgb(255, 169, 58)) : normal, 5);
            fill.StartCap = fill.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            e.Graphics.DrawArc(track, rect, -90, 359);
            if (remaining > 0) e.Graphics.DrawArc(fill, rect, -90, (float)(3.59 * remaining));
        }
        int left = Math.Max(0, (Width - 60) / 2);
        var outer = new Rectangle(left, 4, 60, 60); Ring(outer, windows.Primary.Remaining, theme.Accent);
        if (windows.Secondary is not null)
            Ring(new Rectangle(left + 8, 12, 44, 44), windows.Secondary.Remaining,
                theme.Light ? Color.FromArgb(103, 75, 159) : Color.FromArgb(178, 145, 255));
        using var valueFont = new Font(Font.FontFamily, 8F, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, 8F);
        TextRenderer.DrawText(e.Graphics, $"{windows.Primary.Remaining:0.#}%", valueFont, new Rectangle(left + 12, 16, 36, 36), theme.Primary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, provider, Font, new Rectangle(0, 67, Width, 20), theme.Primary, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, $"{windows.Primary.Label} {windows.Primary.Remaining:0.#}%", small, new Rectangle(0, 88, Width, 19), theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        if (windows.Secondary is not null)
            TextRenderer.DrawText(e.Graphics, $"{windows.Secondary.Label} {windows.Secondary.Remaining:0.#}%", small, new Rectangle(0, 107, Width, 19), theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }
}
internal sealed class DailyChart : Control
{
    readonly IReadOnlyList<(string Date, double Tokens)> points;
    readonly VisualTheme theme;
    public DailyChart(IReadOnlyList<(string Date, double Tokens)> points, VisualTheme theme)
    {
        this.points = points; this.theme = theme; DoubleBuffered = true; AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "最近七个自然日 Token：" + string.Join("，", points.Select(p => $"{p.Date} {p.Tokens:0}"));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        double maximum = Math.Max(1, points.Max(p => p.Tokens));
        int slot = Math.Max(1, Width / points.Count), baseline = Height - 28, top = 22;
        using var guide = new Pen(theme.Border);
        using var fill = new SolidBrush(theme.Accent);
        using var small = new Font(Font.FontFamily, 8F);
        string peak = maximum >= 1_000_000 ? $"{maximum / 1_000_000:0.##}M" : maximum >= 1_000 ? $"{maximum / 1_000:0.##}K" : $"{(points.Max(p => p.Tokens)):0}";
        TextRenderer.DrawText(e.Graphics, "单日最高 " + peak, small, new Point(0, 0), theme.Secondary);
        e.Graphics.DrawLine(guide, 0, baseline, Width, baseline);
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i]; int barHeight = (int)Math.Round((baseline - top) * point.Tokens / maximum);
            int barWidth = Math.Max(4, Math.Min(24, slot / 2)), x = i * slot + (slot - barWidth) / 2;
            if (barHeight > 0) e.Graphics.FillRectangle(fill, x, baseline - barHeight, barWidth, barHeight);
            var labelArea = new Rectangle(i * slot, baseline + 5, slot, 20);
            TextRenderer.DrawText(e.Graphics, point.Date[5..].Replace('-', '/'), small, labelArea,
                theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
internal sealed class BreakdownBar : Control
{
    readonly double[] values;
    readonly Color[] colors;
    static readonly Color[] DarkColors = [Color.FromArgb(95, 164, 255), Color.FromArgb(166, 112, 255), Color.FromArgb(69, 208, 185), Color.FromArgb(255, 176, 73), Color.FromArgb(151, 154, 168)];
    static readonly Color[] LightColors = [Color.FromArgb(35, 94, 180), Color.FromArgb(105, 55, 160), Color.FromArgb(15, 111, 97), Color.FromArgb(149, 84, 0), Color.FromArgb(89, 95, 112)];
    public static Color[] ColorsFor(VisualTheme theme) => theme.Light ? LightColors : DarkColors;
    public BreakdownBar(double[] values, VisualTheme theme) { this.values = values; colors = ColorsFor(theme); DoubleBuffered = true; AccessibleName = "今日用量构成：输入、输出、缓存读取、缓存写入和未拆分"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); double total = values.Sum(), start = 0;
        if (total <= 0) return;
        for (int i = 0; i < values.Length; i++)
        {
            float left = (float)(Width * start / total), right = (float)(Width * (start + values[i]) / total);
            using var brush = new SolidBrush(colors[i]); e.Graphics.FillRectangle(brush, left, 0, Math.Max(0, right - left), Height); start += values[i];
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
                process = new Process { StartInfo = new ProcessStartInfo(path)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8
                } };
                process.StartInfo.ArgumentList.Add("--history-worker");
                process.StartInfo.ArgumentList.Add("--prices-dir");
                process.StartInfo.ArgumentList.Add(AccountPreferences.DefaultDirectory);
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
