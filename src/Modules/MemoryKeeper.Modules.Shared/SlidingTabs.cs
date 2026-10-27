using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace MemoryKeeper.Modules.Shared;

/// <summary>
/// 滑动式分段切换控件：浅灰轨道 + 白色滑块随选中项动画平移。
/// 通过代码构建可视化树，无需 Generic.xaml；支持双向绑定 SelectedIndex。
/// </summary>
public sealed class SlidingTabs : System.Windows.Controls.ContentControl
{
    private readonly Border _pill;
    private readonly TranslateTransform _pillTransform = new();
    private readonly StackPanel _itemsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly List<TextBlock> _itemLabels = [];

    private static readonly System.Windows.Media.Brush TrackBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEE));
    private static readonly System.Windows.Media.Brush TextNormal = new SolidColorBrush(Color.FromRgb(0x73, 0x79, 0x85));
    private static readonly System.Windows.Media.Brush TextActive = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1D));
    private static readonly System.Windows.Media.Brush TextHover = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SlidingTabs),
        new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SlidingTabs),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>用户点击切换后触发（参数为新索引）。</summary>
    public event EventHandler<int>? SelectionChanged;

    public SlidingTabs()
    {
        _pill = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = _pillTransform,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x1A, 0x1D, 0x24),
                Opacity = 0.16,
                BlurRadius = 10,
                ShadowDepth = 1,
            },
            Visibility = Visibility.Collapsed,
        };

        var inner = new Grid();
        inner.Children.Add(_pill);
        inner.Children.Add(_itemsPanel);

        var track = new Border
        {
            Background = TrackBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Child = inner,
        };
        Content = track;

        SizeChanged += (_, _) => UpdatePill(animate: false);
        IsVisibleChanged += (_, _) => UpdatePill(animate: false);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SlidingTabs)d).RebuildItems();

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SlidingTabs)d;
        self.UpdateItemStyles();
        self.UpdatePill(animate: true);
    }

    private void RebuildItems()
    {
        _itemsPanel.Children.Clear();
        _itemLabels.Clear();

        var items = ItemsSource?.Cast<object>().ToList() ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var label = new TextBlock
            {
                Text = items[i]?.ToString() ?? "",
                FontSize = 12.5,
                Foreground = TextNormal,
                Padding = new Thickness(14, 6, 14, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Tag = i,
            };
            var idx = i;
            // 用按下而非抬起：父级若有 DragMove 等鼠标捕获逻辑，抬起事件到不了 label
            label.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SelectedIndex = idx;
                UpdateItemStyles();
                UpdatePill(animate: true);
                SelectionChanged?.Invoke(this, idx);
            };
            label.MouseEnter += (_, _) => { if (idx != SelectedIndex) label.Foreground = TextHover; };
            label.MouseLeave += (_, _) => UpdateItemStyles();
            _itemsPanel.Children.Add(label);
            _itemLabels.Add(label);
        }

        if (SelectedIndex >= items.Count) SelectedIndex = Math.Max(0, items.Count - 1);
        UpdateItemStyles();
        // 等待测量完成后再定位滑块
        Dispatcher.InvokeAsync(() => UpdatePill(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateItemStyles()
    {
        for (var i = 0; i < _itemLabels.Count; i++)
        {
            var active = i == SelectedIndex;
            // 只改颜色不改字重：SemiBold 会改变文字宽度，导致相邻项位移跳动
            _itemLabels[i].Foreground = active ? TextActive : TextNormal;
        }
    }

    private void UpdatePill(bool animate)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _itemLabels.Count || _itemLabels.Count == 0)
        {
            _pill.Visibility = Visibility.Collapsed;
            return;
        }

        var target = _itemLabels[SelectedIndex];
        if (target.ActualWidth <= 0)
        {
            _pill.Visibility = Visibility.Collapsed;
            return;
        }

        var pos = target.TranslatePoint(new Point(0, 0), _itemsPanel);
        var duration = animate ? TimeSpan.FromMilliseconds(230) : TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        _pill.Visibility = Visibility.Visible;
        _pill.BeginAnimation(WidthProperty, animate
            ? new DoubleAnimation(target.ActualWidth, duration) { EasingFunction = ease }
            : null);
        _pill.Width = target.ActualWidth;
        _pillTransform.BeginAnimation(TranslateTransform.XProperty, animate
            ? new DoubleAnimation(pos.X, duration) { EasingFunction = ease }
            : null);
        _pillTransform.X = pos.X;
    }
}
