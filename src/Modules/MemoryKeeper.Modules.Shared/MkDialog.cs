using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace MemoryKeeper.Modules.Shared;

/// <summary>
/// 轻量应用内确认弹窗：替代系统 MessageBox，跟随主窗口视觉语言
/// （白卡 12px 圆角、柔和投影、黑色主按钮、描边次按钮，入场 150ms 淡入上移）。
/// </summary>
public static class MkDialog
{
    /// <summary>显示确认弹窗，返回 true 表示用户点了确认按钮。</summary>
    public static bool Confirm(
        Window? owner, string title, string message,
        string okText = "确定", string cancelText = "取消")
        => Show(owner, title, message, okText, cancelText, hasCancel: true);

    /// <summary>显示仅含单个"知道了"按钮的提示弹窗。</summary>
    public static void Alert(Window? owner, string title, string message, string okText = "知道了")
        => Show(owner, title, message, okText, null, hasCancel: false);

    private static bool Show(
        Window? owner, string title, string message,
        string? okText, string? cancelText, bool hasCancel)
    {
        var ok = MakeButton(okText ?? "确定", isPrimary: true);
        var cancel = MakeButton(cancelText ?? "取消", isPrimary: false);
        ok.Margin = new Thickness(8, 0, 0, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        if (hasCancel) buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 18),
            Children =
            {
                new TextBlock
                {
                    Text = title, FontSize = 14.5, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1D)),
                },
                new TextBlock
                {
                    Text = message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x57, 0x61)),
                    Margin = new Thickness(0, 8, 0, 0), LineHeight = 19,
                },
                buttons,
            },
        };

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEC)),
            BorderThickness = new Thickness(1),
            Child = body,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 26, Opacity = 0.16, ShadowDepth = 0 },
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, 8),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var win = new Window
        {
            Width = 368,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Content = card,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };
        if (owner is not null) win.Owner = owner;
        WindowChrome.SetWindowChrome(win, new WindowChrome { CornerRadius = new CornerRadius(12), GlassFrameThickness = new Thickness(0), CaptionHeight = 0 });

        var result = false;
        void Close(bool value) { result = value; win.Close(); }
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close(false);
            else if (e.Key == System.Windows.Input.Key.Enter) Close(true);
        };
        win.MouseLeftButtonDown += (_, e) =>
        {
            // 空白处按下可拖动窗口
            if (e.OriginalSource is not DependencyObject d || !IsIn<System.Windows.Controls.Button>(d)) win.DragMove();
        };
        win.Loaded += (_, _) =>
        {
            card.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            card.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        win.ShowDialog();
        return result;
    }

    private static bool IsIn<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    private static Button MakeButton(string text, bool isPrimary) => new()
    {
        Content = text,
        MinWidth = 78, Height = 34, Padding = new Thickness(14, 0, 14, 0),
        FontSize = 12.5, FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand,
        Foreground = isPrimary ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)),
        Template = MakeTemplate(isPrimary),
    };

    private static ControlTemplate MakeTemplate(bool isPrimary)
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "b" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, isPrimary
            ? new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1D)) : Brushes.White);
        border.SetValue(Border.BorderBrushProperty, isPrimary
            ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0xE0, 0xE3, 0xEA)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            isPrimary ? new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)) : new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5)),
            "b"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            isPrimary ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)) : new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEC)),
            "b"));
        template.Triggers.Add(pressed);
        return template;
    }
}
