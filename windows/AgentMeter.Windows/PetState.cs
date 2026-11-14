using System.Text.Json;

namespace AgentMeter.Windows;

internal sealed record PetMood(string Animation, string Description);

/// <summary>只依据可用额度和最近记录更新选择状态，不把缺失数据当作耗尽。</summary>
internal static class PetState
{
    public static PetMood Select(JsonElement snapshot, long now)
    {
        var observed = Number(snapshot, "observed_at");
        bool freshAccount = observed.HasValue && observed.Value <= now && now - observed.Value <= 20 * 60;
        if (freshAccount)
        {
            if (Low(Field(snapshot, "account"), now)) return new PetMood("alarm", "账户额度偏低");
            var others = Field(snapshot, "other_accounts");
            if (others.ValueKind == JsonValueKind.Object)
                foreach (var entry in others.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var account in entry.Value.EnumerateArray())
                            if (Low(account, now)) return new PetMood("alarm", "账户额度偏低");
                    }
                    else if (Low(entry.Value, now)) return new PetMood("alarm", "账户额度偏低");
                }
        }
        var activity = Field(snapshot, "activity");
        var apps = Field(activity, "apps");
        if (Text(activity, "status") is "available" or "partial" && apps.ValueKind == JsonValueKind.Array)
            foreach (var app in apps.EnumerateArray())
            {
                var updated = Number(app, "last_updated");
                if (updated.HasValue && updated.Value <= now && now - updated.Value <= 15 * 60)
                    return new PetMood("working", "近期本机记录有更新");
            }
        return new PetMood("idle", "待机");
    }

    static bool Low(JsonElement account, long now)
    {
        if (Text(account, "status") is not ("available" or "partial")) return false;
        var quotas = Field(Field(account, "quota"), "value");
        if (quotas.ValueKind != JsonValueKind.Array) return false;
        foreach (var quota in quotas.EnumerateArray())
        {
            var remaining = Number(quota, "remaining_percent");
            var reset = Number(Field(quota, "resets_at"), "epoch_seconds");
            if (remaining is >= 0 and < 20 && (!reset.HasValue || reset.Value > now)) return true;
        }
        return false;
    }
    static JsonElement Field(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    static string Text(JsonElement e, string key) { var value = Field(e, key); return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; }
    static double? Number(JsonElement e, string key)
    {
        var value = Field(e, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n) ? n : null;
    }
}
