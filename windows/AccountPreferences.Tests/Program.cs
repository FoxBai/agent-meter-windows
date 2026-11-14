using AgentMeter.Windows;

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
    Console.WriteLine("账户开关设置检查通过");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}
