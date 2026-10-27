using System.Windows;
using System.Windows.Media;

namespace MemoryKeeper.Modules.Shared;

/// <summary>
/// 应用图标圆角 tile：底色取图标主色的淡彩色（无图标时按名称哈希取柔和色板），
/// 无图标应用回退为名称首字母。尺寸/圆角可配。
/// </summary>
public partial class IconTile : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(IconTile),
            new PropertyMetadata(null, OnVisualChanged));
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty IconPathProperty =
        DependencyProperty.Register(nameof(IconPath), typeof(string), typeof(IconTile),
            new PropertyMetadata(null, OnVisualChanged));
    public string? IconPath
    {
        get => (string?)GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public static readonly DependencyProperty DisplayNameProperty =
        DependencyProperty.Register(nameof(DisplayName), typeof(string), typeof(IconTile),
            new PropertyMetadata(null, OnVisualChanged));
    public string? DisplayName
    {
        get => (string?)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public static readonly DependencyProperty TileSizeProperty =
        DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(IconTile),
            new PropertyMetadata(44.0));
    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(IconTile),
            new PropertyMetadata(26.0));
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty TileRadiusProperty =
        DependencyProperty.Register(nameof(TileRadius), typeof(double), typeof(IconTile),
            new PropertyMetadata(11.0));
    public double TileRadius
    {
        get => (double)GetValue(TileRadiusProperty);
        set => SetValue(TileRadiusProperty, value);
    }

    public static readonly DependencyProperty LetterSizeProperty =
        DependencyProperty.Register(nameof(LetterSize), typeof(double), typeof(IconTile),
            new PropertyMetadata(17.0));
    public double LetterSize
    {
        get => (double)GetValue(LetterSizeProperty);
        set => SetValue(LetterSizeProperty, value);
    }

    public IconTile()
    {
        InitializeComponent();
        Update();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((IconTile)d).Update();

    private void Update()
    {
        var has = Source is not null;
        PART_Image.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        PART_Letter.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        var name = DisplayName;
        PART_Letter.Text = string.IsNullOrEmpty(name) ? "·" : name.Trim()[0].ToString();
    }
}
