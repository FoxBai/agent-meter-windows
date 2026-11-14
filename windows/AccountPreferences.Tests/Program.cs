using AgentMeter.Windows;
using System.Drawing;

string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AgentMeterPreferencesTests"));
string directory = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
if (!directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("测试目录不在预期位置");

try
{
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
    Console.WriteLine("账户开关、面板位置、主题对比度和价格设置检查通过");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}
