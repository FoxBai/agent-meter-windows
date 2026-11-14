using AgentMeter.Windows;
using System.Drawing;
using System.Text.Json;

string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AgentMeterPreferencesTests"));
string directory = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
if (!directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("测试目录不在预期位置");

try
{
    Guid credentialId = Guid.NewGuid();
    try
    {
        if (CredentialVault.Read("test", credentialId) is not null) throw new InvalidOperationException("随机测试凭据不应存在");
        CredentialVault.Write("test", credentialId, "模拟密钥-A");
        if (CredentialVault.Read("test", credentialId) != "模拟密钥-A") throw new InvalidOperationException("凭据读取失败");
        CredentialVault.Write("test", credentialId, "模拟密钥-B");
        if (CredentialVault.Read("test", credentialId) != "模拟密钥-B") throw new InvalidOperationException("凭据更新失败");
        CredentialVault.Delete("test", credentialId);
        if (CredentialVault.Read("test", credentialId) is not null) throw new InvalidOperationException("凭据删除失败");
    }
    finally { CredentialVault.Delete("test", credentialId); }
    if (AccountPreferences.Load(directory) != AccountPreferences.Enabled)
        throw new InvalidOperationException("首次运行应启用账户查询");
    var changed = new AccountPreferences(false, true, false);
    changed.Save(directory);
    if (AccountPreferences.Load(directory) != changed)
        throw new InvalidOperationException("账户开关未正确保存或读取");
    File.WriteAllText(Path.Combine(directory, "preferences.json"), "not-json");
    if (AccountPreferences.Load(directory) != AccountPreferences.Disabled)
        throw new InvalidOperationException("损坏的设置应关闭账户查询");
    AccountPreferences.Enabled.Save(directory);
    if (AccountPreferences.Load(directory) != AccountPreferences.Enabled)
        throw new InvalidOperationException("账户开关未正确覆盖");
    File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"codex\":true,\"kimi\":false,\"claude\":true}");
    var oldPreferences = AccountPreferences.Load(directory);
    if (!oldPreferences.Codex || oldPreferences.Kimi || !oldPreferences.Claude || oldPreferences.MiniMax)
        throw new InvalidOperationException("旧设置兼容或新增来源默认关闭失败");
    var fakeVault = new FakeMiniMaxVault();
    if (MiniMaxAccountInput.Build(directory, fakeVault) is not null)
        throw new InvalidOperationException("没有账户列表时应保留环境变量模式");
    var account = MiniMaxProfiles.Add(directory, "测试账户", "cn", "synthetic-secret", fakeVault);
    if (MiniMaxProfiles.Load(directory).Single() != account || fakeVault.Read(account.Id) != "synthetic-secret" ||
        File.ReadAllText(Path.Combine(directory, "minimax-accounts.json")).Contains("synthetic-secret"))
        throw new InvalidOperationException("账户信息与凭据未正确分离");
    using (var input = JsonDocument.Parse(MiniMaxAccountInput.Build(directory, fakeVault)!))
    {
        var item = input.RootElement.GetProperty("minimax_accounts")[0];
        if (item.GetProperty("label").GetString() != "测试账户" || item.GetProperty("key").GetString() != "synthetic-secret")
            throw new InvalidOperationException("账户标准输入内容不正确");
    }
    fakeVault.Write(account.Id, "bad\nvalue");
    using (var input = JsonDocument.Parse(MiniMaxAccountInput.Build(directory, fakeVault)!))
        if (input.RootElement.GetProperty("minimax_accounts")[0].GetProperty("key").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("损坏的凭据不应传递给后端");
    fakeVault.Write(account.Id, "synthetic-secret");
    fakeVault.FailRead = true;
    using (var input = JsonDocument.Parse(MiniMaxAccountInput.Build(directory, fakeVault)!))
        if (input.RootElement.GetProperty("minimax_accounts")[0].GetProperty("key").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("凭据读取失败应标为缺失");
    fakeVault.FailRead = false;
    try { MiniMaxProfiles.Add(directory, "测试账户", "cn", "synthetic-duplicate", fakeVault); throw new InvalidOperationException("重复账户名称应被拒绝"); }
    catch (InvalidDataException) { }
    fakeVault.FailWrite = true;
    try { MiniMaxProfiles.Add(directory, "另一个账户", "global", "synthetic-other", fakeVault); throw new InvalidOperationException("模拟保存失败未生效"); }
    catch (IOException) { }
    if (MiniMaxProfiles.Load(directory).Count != 1) throw new InvalidOperationException("凭据写入失败后应回滚账户列表");
    fakeVault.FailWrite = false; fakeVault.FailDelete = true;
    try { MiniMaxProfiles.Remove(directory, account.Id, fakeVault); throw new InvalidOperationException("模拟删除失败未生效"); }
    catch (IOException) { }
    if (MiniMaxProfiles.Load(directory).Count != 1) throw new InvalidOperationException("凭据删除失败时不能隐藏账户");
    fakeVault.FailDelete = false; MiniMaxProfiles.Remove(directory, account.Id, fakeVault);
    if (MiniMaxProfiles.Load(directory).Count != 0 || fakeVault.Read(account.Id) is not null)
        throw new InvalidOperationException("账户和凭据应同时删除");
    using (var input = JsonDocument.Parse(MiniMaxAccountInput.Build(directory, fakeVault)!))
        if (input.RootElement.GetProperty("minimax_accounts").GetArrayLength() != 0)
            throw new InvalidOperationException("清空账户后不应回退到环境变量");
    var firstAccount = MiniMaxProfiles.Add(directory, "甲", "cn", "synthetic-a", fakeVault);
    var secondAccount = MiniMaxProfiles.Add(directory, "乙", "global", "synthetic-b", fakeVault);
    using (var input = JsonDocument.Parse(MiniMaxAccountInput.Build(directory, fakeVault)!))
        if (input.RootElement.GetProperty("minimax_accounts").GetArrayLength() != 2)
            throw new InvalidOperationException("多个账户未正确隔离");
    MiniMaxProfiles.Remove(directory, firstAccount.Id, fakeVault);
    MiniMaxProfiles.Remove(directory, secondAccount.Id, fakeVault);
    if (VisualThemes.All.Count != 7 || VisualThemes.All.Select(t => t.Id).Distinct().Count() != 7)
        throw new InvalidOperationException("界面风格列表不完整");
    VisualThemes.Save(directory, VisualThemes.Find("porcelain"));
    if (VisualThemes.Load(directory).Id != "porcelain") throw new InvalidOperationException("界面风格未正确保存");
    File.WriteAllText(Path.Combine(directory, "appearance.json"), "not-json");
    if (VisualThemes.Load(directory).Id != "aurora") throw new InvalidOperationException("损坏的外观设置应回退默认风格");
    static double Luminance(Color color)
    {
        static double Linear(int channel) { double v = channel / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
    static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
    foreach (var theme in VisualThemes.All)
    {
        foreach (var background in new[] { theme.Background, theme.Card, theme.CardEnd })
            if (Contrast(theme.Primary, background) < 4.5 || Contrast(theme.Secondary, background) < 4.5)
                throw new InvalidOperationException($"{theme.Name} 的文字对比度不足");
        if (Contrast(Color.White, theme.Selected) < 4.5) throw new InvalidOperationException($"{theme.Name} 的选中标签对比度不足");
        if (Contrast(theme.Accent, theme.Background) < 4.5 || Contrast(theme.Accent, theme.Card) < 4.5)
            throw new InvalidOperationException($"{theme.Name} 的强调文字对比度不足");
    }
    var prices = new[] { new PriceRow("sample-model", 1.25m, 0.2m, 0m, 3m) };
    PriceOverrides.Save(directory, prices);
    if (PriceOverrides.Load(directory).Single() != prices[0]) throw new InvalidOperationException("价格保存或读取失败");
    string oldPrices = File.ReadAllText(Path.Combine(directory, "prices.json"));
    try
    {
        PriceOverrides.Save(directory, new[] { prices[0], prices[0] with { Model = "SAMPLE.MODEL" } });
        throw new InvalidOperationException("不应接受重复的模型名称");
    }
    catch (InvalidDataException) { }
    if (File.ReadAllText(Path.Combine(directory, "prices.json")) != oldPrices)
        throw new InvalidOperationException("无效价格不能覆盖已有设置");
    File.WriteAllText(Path.Combine(directory, "prices.json"), "broken");
    try { PriceOverrides.Load(directory); throw new InvalidOperationException("损坏价格文件不应被当作空设置"); }
    catch (InvalidDataException) { }
    File.WriteAllText(Path.Combine(directory, "prices.json"), "{\"currency\":\"USD\",\"models\":{},\"future\":true}");
    try { PriceOverrides.Load(directory); throw new InvalidOperationException("未知字段不能被静默覆盖"); }
    catch (InvalidDataException) { }
    if (AlertPreferences.Load(directory)) throw new InvalidOperationException("额度提醒首次应默认关闭");
    AlertPreferences.Save(directory, true);
    if (!AlertPreferences.Load(directory)) throw new InvalidOperationException("额度提醒设置未保存");
    var alerts = new QuotaAlerts();
    JsonElement Snapshot(double remaining, long reset, string status = "available") => JsonSerializer.SerializeToElement(new
    {
        account = new { status, quota = new { value = new[] { new { bucket = "primary", window_minutes = 300,
            remaining_percent = remaining, resets_at = new { epoch_seconds = reset } } } } }
    });
    var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    if (alerts.Evaluate(Snapshot(20, 100), now).Count != 0 || alerts.Evaluate(Snapshot(19, 100), now).Count != 1 ||
        alerts.Evaluate(Snapshot(15, 100), now).Count != 0 || alerts.Evaluate(Snapshot(15, 101), now.AddMinutes(1)).Count != 0 ||
        alerts.Evaluate(Snapshot(15, 101), now.AddMinutes(16)).Count != 1)
        throw new InvalidOperationException("低额度提醒阈值、去重或重置周期错误");
    if (alerts.Evaluate(Snapshot(35, 101), now.AddMinutes(17)).Count != 0 || alerts.Evaluate(Snapshot(10, 101), now.AddMinutes(17)).Count != 1 ||
        alerts.Evaluate(Snapshot(10, 102, "disabled"), now.AddMinutes(17)).Count != 0)
        throw new InvalidOperationException("额度恢复或关闭来源后的提醒错误");
    var kimi = JsonSerializer.SerializeToElement(new { other_accounts = new Dictionary<string, object> {
        ["Kimi Code"] = new { status = "available", quota = new { value = new[] { new { bucket = "monthTotal", remaining_percent = 10 } } } }
    } });
    if (!new QuotaAlerts().Evaluate(kimi, now).Single().Contains("月度总额"))
        throw new InvalidOperationException("Kimi 月度额度提醒应显示中文名称");
    var minimax = JsonSerializer.SerializeToElement(new { other_accounts = new Dictionary<string, object> {
        ["MiniMax Code"] = new { status = "available", quota = new { value = new[] { new { bucket = "5h", remaining_percent = 10 } } } }
    } });
    if (!new QuotaAlerts().Evaluate(minimax, now).Single().Contains("5 小时"))
        throw new InvalidOperationException("MiniMax 额度提醒应显示中文周期");
    var multiMiniMax = JsonSerializer.SerializeToElement(new { other_accounts = new Dictionary<string, object> {
        ["MiniMax Code"] = new[] {
            new { label = "甲", status = "available", quota = new { value = new[] { new { bucket = "5h", remaining_percent = 10 } } } },
            new { label = "乙", status = "available", quota = new { value = new[] { new { bucket = "5h", remaining_percent = 15 } } } }
        }
    } });
    if (new QuotaAlerts().Evaluate(multiMiniMax, now).Count != 2)
        throw new InvalidOperationException("不同 MiniMax 账户的提醒应独立计数");
    using var quotaData = JsonDocument.Parse("""
    {"quota":{"value":[{"bucket":"seven_day_opus","window_minutes":10080,"remaining_percent":5},
      {"bucket":"seven_day","window_minutes":10080,"remaining_percent":46},
      {"bucket":"five_hour","window_minutes":300,"remaining_percent":73}]}}
    """);
    var pair = QuotaWindows.Select(quotaData.RootElement);
    if (pair?.Primary.Remaining != 73 || pair.Secondary?.Remaining != 46 || pair.Secondary.Label != "7 天")
        throw new InvalidOperationException("额度仪表应配对通用短周期和七天周期");
    using var singleQuota = JsonDocument.Parse("""
    {"quota":{"value":[{"bucket":"seven_day_opus","window_minutes":10080,"remaining_percent":15},
      {"bucket":"bad","remaining_percent":120}]}}
    """);
    var single = QuotaWindows.Select(singleQuota.RootElement);
    if (single?.Primary.Label != "7 天 Opus" || single.Secondary is not null)
        throw new InvalidOperationException("单独额度或异常百分比处理错误");
    using var kimiPair = JsonDocument.Parse("""
    {"quota":{"value":[{"bucket":"monthTotal","remaining_percent":80},
      {"bucket":"limit7d","remaining_percent":42}, {"bucket":"limit5h","remaining_percent":64}]}}
    """);
    var kimiWindows = QuotaWindows.Select(kimiPair.RootElement);
    if (kimiWindows?.Primary.Remaining != 64 || kimiWindows.Secondary?.Remaining != 42)
        throw new InvalidOperationException("Kimi 月度额度不应代替短周期或七天周期");
    using var reportData = JsonDocument.Parse("""
    {"account":{"credential":"synthetic-secret"},"history":{"message":"synthetic-private-text"},"calendar_report":{"incomplete":true,"periods":{
      "week":{"previous_start":"2025-12-22","current_start":"2025-12-29","current_end":"2026-01-01","previous_tokens":50,"current_tokens":300},
      "month":{"previous_start":"2025-12-01","current_start":"2026-01-01","current_end":"2026-01-01","previous_tokens":180,"current_tokens":200}}}}
    """);
    string exported = CalendarReportExport.Create(reportData.RootElement, demo: true);
    if (!exported.Contains("虚构演示数据") || !exported.Contains("2025-12-28") || !exported.Contains("300 Token") ||
        !exported.Contains("部分来源") || exported.Contains("synthetic-secret") || exported.Contains("synthetic-private-text"))
        throw new InvalidOperationException("报告导出内容或隐私边界不正确");
    var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
        if (!CalendarReportExport.Create(reportData.RootElement).Contains("2025-12-28"))
            throw new InvalidOperationException("报告日期不应随系统区域设置改变");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
    using var share = ShareCardRenderer.Render(reportData.RootElement, VisualThemes.Find("aurora"), demo: true);
    using var lightShare = ShareCardRenderer.Render(reportData.RootElement, VisualThemes.Find("porcelain"), demo: true);
    if (share.Width != 1200 || share.Height != 860 || share.GetPixel(5, 5) == lightShare.GetPixel(5, 5))
        throw new InvalidOperationException("分享图尺寸或主题配色不正确");
    if (args.Length == 2 && args[0] == "--render-demo")
        share.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
    if (args.Length == 2 && args[0] == "--render-demo-light")
        lightShare.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
    var screen = new Rectangle(0, 0, 1920, 1080);
    var panel = new Size(500, 700);
    void Position(Rectangle bounds, Rectangle work, Point cursor, Point expected)
    {
        Point actual = PanelPlacement.Find(bounds, work, panel, cursor, true);
        if (actual != expected) throw new InvalidOperationException($"面板位置错误：{actual}，应为 {expected}");
    }
    Position(screen, new Rectangle(0, 0, 1920, 1040), new Point(1900, 1060), new Point(1408, 328));
    Position(screen, new Rectangle(0, 40, 1920, 1040), new Point(100, 5), new Point(12, 52));
    Position(screen, new Rectangle(40, 0, 1880, 1080), new Point(5, 500), new Point(52, 150));
    Position(screen, new Rectangle(0, 0, 1880, 1080), new Point(1910, 500), new Point(1368, 150));
    Position(new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1040),
             new Point(-10, 1060), new Point(-512, 328));
    Point oversized = PanelPlacement.Find(screen, new Rectangle(0, 0, 300, 200), new Size(500, 700), Point.Empty, true);
    if (oversized != Point.Empty) throw new InvalidOperationException("超大面板应从工作区左上角开始");
    if (PanelPlacement.Fit(new Size(500, 760), new Rectangle(0, 0, 300, 200)) != new Size(276, 176))
        throw new InvalidOperationException("面板应适应较小工作区");
    if (PanelPlacement.Fit(new Size(500, 760), new Rectangle(0, 0, 1920, 1040)) != new Size(500, 760))
        throw new InvalidOperationException("足够大的工作区不应改变面板尺寸");
    Console.WriteLine("账户开关、面板位置、主题、价格、提醒和报告导出检查通过");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}

internal sealed class FakeMiniMaxVault : IMiniMaxVault
{
    readonly Dictionary<Guid, string> values = new();
    public bool FailWrite { get; set; }
    public bool FailDelete { get; set; }
    public bool FailRead { get; set; }
    public void Write(Guid id, string secret) { if (FailWrite) throw new IOException("synthetic write failure"); values[id] = secret; }
    public string? Read(Guid id) { if (FailRead) throw new IOException("synthetic read failure"); return values.GetValueOrDefault(id); }
    public void Delete(Guid id) { if (FailDelete) throw new IOException("synthetic delete failure"); values.Remove(id); }
}
