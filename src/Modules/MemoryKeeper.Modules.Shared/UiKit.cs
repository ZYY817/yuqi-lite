using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MemoryKeeper.Modules.Shared;

/// <summary>从 exe 提取 48x48 高清内嵌图标（过滤无图标 exe 的系统默认白框图标），带缓存。</summary>
public static class IconExtractor
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(
        string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static bool HasEmbeddedIcon(string? path)
    {
        if (path is null || !File.Exists(path)) return false;
        return Get(path) is not null;
    }

    public static ImageSource? Get(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var count = ExtractIconEx(path, -1, null, null, 0);
            if (count == 0)
            {
                Cache[path] = null;
                return null;
            }
            var hIcons = new IntPtr[1];
            var ids = new uint[1];
            // 直接提取 48x48 高分辨率图标组资源，避免 32x32 缩放模糊
            var extracted = PrivateExtractIconsW(path, 0, 48, 48, hIcons, ids, 1, 0);
            if (extracted == 0 || hIcons[0] == IntPtr.Zero)
            {
                ExtractIconEx(path, 0, hIcons, null, 1);
            }
            if (hIcons[0] == IntPtr.Zero)
            {
                Cache[path] = null;
                return null;
            }
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Cache[path] = source;
                return source;
            }
            finally
            {
                DestroyIcon(hIcons[0]);
            }
        }
        catch
        {
            Cache[path] = null;
            return null;
        }
    }
}

public sealed class PathToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => IconExtractor.Get(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>按应用图标主色生成淡彩 tile 底色；无图标时按名称哈希取柔和色板色。</summary>
public static class IconTint
{
    private static readonly Dictionary<string, (Brush Tint, Brush Accent)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush Tint(string? path, string? name) => Resolve(path, name).Tint;
    public static Brush Accent(string? path, string? name) => Resolve(path, name).Accent;

    private static (Brush Tint, Brush Accent) Resolve(string? path, string? name)
    {
        var key = (path ?? "") + "|" + (name ?? "");
        if (Cache.TryGetValue(key, out var cached)) return cached;

        // 有真实图标的按图标主色出淡彩底；无图标的字母 tile 统一中性灰，更克制
        var dominant = path is not null ? DominantColor(IconExtractor.Get(path)) : (Color?)null;
        var pair = dominant is null
            ? (Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0xF1, 0xF5))),
               Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))))
            : (Freeze(Solid(dominant.Value, 0.84)),
               Freeze(Solid(dominant.Value, 0.30, towardWhite: false)));
        Cache[key] = pair;
        return pair;
    }

    private static SolidColorBrush Solid(Color c, double amount, bool towardWhite = true)
    {
        byte Blend(byte v) => (byte)Math.Clamp(
            (int)Math.Round(towardWhite ? v + (255 - v) * amount : v * (1 - amount)), 0, 255);
        return new SolidColorBrush(Color.FromRgb(Blend(c.R), Blend(c.G), Blend(c.B)));
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static Color? DominantColor(ImageSource? icon)
    {
        if (icon is not BitmapSource bmp) return null;
        try
        {
            const int n = 10;
            var conv = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var scaled = new TransformedBitmap(conv,
                new ScaleTransform(n / (double)conv.PixelWidth, n / (double)conv.PixelHeight));
            var px = new byte[n * n * 4];
            scaled.CopyPixels(px, n * 4, 0);
            double r = 0, g = 0, b = 0, w = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                var a = px[i + 3];
                if (a < 40) continue;
                var weight = a / 255.0;
                b += px[i] * weight; g += px[i + 1] * weight; r += px[i + 2] * weight; w += weight;
            }
            if (w < 1) return null;
            var cr = (byte)(r / w); var cg = (byte)(g / w); var cb = (byte)(b / w);
            // 平均色过灰（接近黑白图标）时按中性灰蓝处理，避免 tile 发脏
            var max = Math.Max(cr, Math.Max(cg, cb)); var min = Math.Min(cr, Math.Min(cg, cb));
            return max - min < 18 ? Color.FromRgb(110, 124, 158) : Color.FromRgb(cr, cg, cb);
        }
        catch { return null; }
    }
}

/// <summary>(IconPath, DisplayName) → 图标主色的淡彩 tile 底色画刷。</summary>
public sealed class IconTintConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => IconTint.Tint(values.ElementAtOrDefault(0) as string, values.ElementAtOrDefault(1) as string);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>(IconPath, DisplayName) → 图标主色加深的字母回退画刷。</summary>
public sealed class IconAccentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => IconTint.Accent(values.ElementAtOrDefault(0) as string, values.ElementAtOrDefault(1) as string);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible ? false : true;
}

public sealed class StringNotEmptyToVis : IValueConverter
{
    public static readonly StringNotEmptyToVis Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVis : IValueConverter
{
    public static readonly CountToVis Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NotBoolConverter : IValueConverter
{
    public static readonly NotBoolConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class BoolToOpacity : IValueConverter
{
    public static readonly BoolToOpacity Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.35;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Path mini-language 字符串 → Geometry（用于矢量图标数据绑定）。</summary>
public sealed class StringToGeometryConverter : IValueConverter
{
    public static readonly StringToGeometryConverter Instance = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return null;
        try { return Geometry.Parse(s); } catch { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
