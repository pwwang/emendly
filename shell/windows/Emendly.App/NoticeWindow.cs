using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Emendly.Capture;

namespace Emendly.App;

public sealed class NoticeWindow : Window
{
    private readonly DispatcherTimer _timer;

    private NoticeWindow(string title, string message, CaretLocation anchor)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 360;

        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var bodyBlock = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel();
        stack.Children.Add(titleBlock);
        stack.Children.Add(bodyBlock);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Child = stack,
        };

        PositionNearAnchor(anchor);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
    }

    public static NoticeWindow ShowNotice(string appName, string reason, CaretLocation anchor)
    {
        var label = string.IsNullOrWhiteSpace(appName) ? "Emendly" : $"Emendly · {appName}";
        var window = new NoticeWindow(label, reason, anchor);
        window.Show();
        window._timer.Start();
        return window;
    }

    private void PositionNearAnchor(CaretLocation anchor)
    {
        var monitor = CaretLocator.GetMonitorWorkArea(anchor.Bounds.Left, anchor.Bounds.Bottom);
        var scaleX = monitor.DpiScaleX > 0 ? monitor.DpiScaleX : 1.0;
        var scaleY = monitor.DpiScaleY > 0 ? monitor.DpiScaleY : 1.0;

        var workLeft = monitor.WorkArea.Left / scaleX;
        var workTop = monitor.WorkArea.Top / scaleY;
        var workRight = monitor.WorkArea.Right / scaleX;
        var workBottom = monitor.WorkArea.Bottom / scaleY;

        const double estimatedWidth = 300;
        const double estimatedHeight = 64;

        var desiredLeft = anchor.Bounds.Left / scaleX + 8;
        var desiredTop = anchor.Bounds.Bottom / scaleY + 10;

        if (desiredTop + estimatedHeight > workBottom)
        {
            desiredTop = anchor.Bounds.Top / scaleY - estimatedHeight - 10;
        }

        Left = Math.Clamp(desiredLeft, workLeft + 8, Math.Max(workLeft + 8, workRight - estimatedWidth - 8));
        Top = Math.Clamp(desiredTop, workTop + 8, Math.Max(workTop + 8, workBottom - estimatedHeight - 8));
    }
}
