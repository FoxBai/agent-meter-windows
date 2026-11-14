using System.Drawing;

namespace AgentMeter.Windows;

/// <summary>按触发位置和任务栏方向定位面板，并限制在屏幕工作区域内。</summary>
internal static class PanelPlacement
{
    public static Size Fit(Size preferred, Rectangle work)
    {
        int width = Math.Max(1, work.Width - 24), height = Math.Max(1, work.Height - 24);
        return new Size(Math.Clamp(preferred.Width, 1, width), Math.Clamp(preferred.Height, 1, height));
    }

    public static Point Find(Rectangle screen, Rectangle work, Size window, Point cursor, bool anchorToCursor)
    {
        const int gap = 12;
        int minX = work.Left, maxX = Math.Max(minX, work.Right - window.Width);
        int minY = work.Top, maxY = Math.Max(minY, work.Bottom - window.Height);
        int left = Math.Max(0, work.Left - screen.Left), right = Math.Max(0, screen.Right - work.Right);
        int top = Math.Max(0, work.Top - screen.Top), bottom = Math.Max(0, screen.Bottom - work.Bottom);
        int largest = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        int padMinX = Math.Min(maxX, minX + gap), padMaxX = Math.Max(padMinX, maxX - gap);
        int padMinY = Math.Min(maxY, minY + gap), padMaxY = Math.Max(padMinY, maxY - gap);
        int alongX = anchorToCursor ? Math.Clamp(cursor.X - window.Width / 2, padMinX, padMaxX) : padMaxX;
        int alongY = anchorToCursor ? Math.Clamp(cursor.Y - window.Height / 2, padMinY, padMaxY) : padMaxY;
        if (largest > 0 && left == largest) return new Point(Math.Min(maxX, minX + gap), alongY);
        if (largest > 0 && right == largest) return new Point(Math.Max(minX, maxX - gap), alongY);
        if (largest > 0 && top == largest) return new Point(alongX, Math.Min(maxY, minY + gap));
        return new Point(alongX, Math.Max(minY, maxY - gap));
    }
}
