using System.Drawing.Drawing2D;

namespace AgentMeter.Windows;

/// <summary>以颜色和缩写区分服务；保留账户别名，不使用未经授权的商标图案。</summary>
internal sealed record ProviderStyle(string DisplayName, string Initial, Color Color)
{
    public static ProviderStyle For(string provider)
    {
        var choices = new (string Prefix, string Display, string Initial, Color Color)[]
        {
            ("CODEX", "Codex", "C", Color.FromArgb(18, 164, 129)),
            ("CLAUDE CODE", "Claude Code", "✦", Color.FromArgb(202, 105, 73)),
            ("KIMI CODE", "Kimi Code", "K", Color.FromArgb(70, 121, 229)),
            ("MINIMAX CODE", "MiniMax Code", "M", Color.FromArgb(212, 72, 132)),
            ("WORKBUDDY", "WorkBuddy", "W", Color.FromArgb(71, 145, 215)),
            ("ZCODE", "ZCode", "Z", Color.FromArgb(72, 111, 229)),
            ("GEMINI CLI", "Gemini CLI", "G", Color.FromArgb(73, 128, 215)),
            ("DEEPSEEK HARNESS", "DeepSeek Harness", "D", Color.FromArgb(76, 122, 225)),
            ("OPENCODE", "OpenCode", "O", Color.FromArgb(126, 100, 215)),
            ("MIMO CODE", "MiMo Code", "M", Color.FromArgb(223, 133, 48)),
            ("DEVECO", "DevECo", "D", Color.FromArgb(100, 138, 180)),
        };
        foreach (var choice in choices)
            if (provider.StartsWith(choice.Prefix, StringComparison.OrdinalIgnoreCase))
                return new ProviderStyle(choice.Display + provider[choice.Prefix.Length..], choice.Initial, choice.Color);
        return new ProviderStyle(provider, "•", Color.FromArgb(104, 126, 176));
    }
}

/// <summary>账户分组标记，同时保留可访问的文字名称。</summary>
internal sealed class ProviderMark : Control
{
    readonly ProviderStyle provider;
    public ProviderMark(ProviderStyle provider)
    {
        this.provider = provider;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Graphic; AccessibleName = provider.DisplayName;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        int r = Math.Min(12, Math.Min(bounds.Width, bounds.Height));
        using var shape = new GraphicsPath();
        shape.AddArc(bounds.Left, bounds.Top, r, r, 180, 90);
        shape.AddArc(bounds.Right - r, bounds.Top, r, r, 270, 90);
        shape.AddArc(bounds.Right - r, bounds.Bottom - r, r, r, 0, 90);
        shape.AddArc(bounds.Left, bounds.Bottom - r, r, r, 90, 90);
        shape.CloseFigure();
        using var fill = new LinearGradientBrush(bounds, provider.Color,
            Color.FromArgb((int)(provider.Color.R * .7), (int)(provider.Color.G * .7), (int)(provider.Color.B * .7)), 90F);
        e.Graphics.FillPath(fill, shape);
        using var font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, provider.Initial, font, bounds, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
