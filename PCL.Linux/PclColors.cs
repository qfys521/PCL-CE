using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.Linux;

/// <summary>PCL CE 配色主题（与原版 ConfigEnums.ColorTheme 对应）。</summary>
public enum PclThemeColor
{
    SkyBlue = 0,
    CatBlue = 1,
    DeathBlue = 2,
    HmclBlue = 3
}

/// <summary>
/// 原版 PCL CE 的 OKLCH 配色系统移植：根据 ToneProfile 与主题色生成 ColorBrush1..8、
/// 灰度色、半透明白色与「彩色背景」渐变，供 Avalonia 资源字典使用，保证配色与原版一致。
/// 颜色公式来自 PCL.Core/UI/Theme/ThemeService 与 ToneProfileConfig。
/// </summary>
public static class PclColors
{
    public static bool IsDark { get; private set; }
    public static PclThemeColor Theme { get; private set; } = PclThemeColor.CatBlue;

    private record Tone(
        double L1 = 0.35, double L2 = 0.5, double L3 = 0.575, double L4 = 0.65,
        double L5 = 0.8, double L6 = 0.92, double L7 = 0.94, double L8 = 0.96,
        double LWhite = 1, double LForeground = 0, double LBackground = 0.995,
        double C1 = 0.025, double C2 = 0.188, double C3 = 0.213, double C4 = 0.168,
        double C5 = 0.093, double C6 = 0.036, double C7 = 0.028, double C8 = 0.018,
        double ASemiWhite = 0.733, double AHalfWhite = 0.333, double ASemiTransparent = 0.004,
        double AHalfTransparent = 0.5, double ATransparent = 0, double ABackground = 0.824,
        double AToolTip = 0.9);

    private static readonly Tone LightTone = new();
    private static readonly Tone DarkTone = new(
        L1: 0.96, L2: 0.75, L3: 0.6, L4: 0.65,
        L5: 0.45, L6: 0.25, L7: 0.225, L8: 0.2,
        LBackground: 0.3, LForeground: 1, LWhite: 0.275);

    private static (int Hue, double LightAdjust, double ChromaAdjust) ThemeArgs(PclThemeColor theme) => theme switch
    {
        PclThemeColor.SkyBlue => (235, 0.36, 0.2),
        PclThemeColor.CatBlue => (255, 0, -0.2),
        PclThemeColor.DeathBlue => (268, -0.05, -0.1),
        PclThemeColor.HmclBlue => (275, -0.03, -0.35),
        _ => (255, 0, -0.2)
    };

    /// <summary>应用配色到 Avalonia 应用资源（ColorBrush*/ColorObject*）。</summary>
    public static void Apply(Application application, bool dark, PclThemeColor theme)
    {
        IsDark = dark;
        Theme = theme;
        var tone = dark ? DarkTone : LightTone;
        var (hue, lightAdjust, chromaAdjust) = ThemeArgs(theme);
        var resources = application.Resources;

        // 灰度
        SetColor(resources, "ColorObjectGray1", Oklch(tone.L1));
        SetColor(resources, "ColorObjectGray2", Oklch(tone.L2));
        SetColor(resources, "ColorObjectGray3", Oklch(tone.L3));
        SetColor(resources, "ColorObjectGray4", Oklch(tone.L4));
        SetColor(resources, "ColorObjectGray5", Oklch(tone.L5));
        SetColor(resources, "ColorObjectGray6", Oklch(tone.L6));
        SetColor(resources, "ColorObjectGray7", Oklch(tone.L7));
        SetColor(resources, "ColorObjectGray8", Oklch(tone.L8));
        for (var i = 1; i <= 8; i++)
            SetBrush(resources, "ColorBrushGray" + i, Oklch(tone.L1 + (tone.L8 - tone.L1) * (i - 1) / 7.0));
        SetBrush(resources, "ColorBrushHalfWhite", Oklch(tone.LWhite, alpha: tone.AHalfWhite));
        SetBrush(resources, "ColorBrushSemiWhite", Oklch(tone.LWhite, alpha: tone.ASemiWhite));
        SetBrush(resources, "ColorBrushWhite", Oklch(tone.LWhite));
        SetBrush(resources, "ColorBrushTransparent", Oklch(tone.LWhite, alpha: tone.ATransparent));
        SetBrush(resources, "ColorBrushTransparentBackground", Oklch(tone.LBackground, alpha: dark ? 0.9 : 0.88));
        SetBrush(resources, "ColorBrushBackground", Oklch(tone.LBackground));
        SetBrush(resources, "ColorBrushToolTip", Oklch(tone.LBackground, alpha: tone.AToolTip));
        SetBrush(resources, "ColorBrushRedBack", Oklch(tone.L7, 0.25, 30, tone.AHalfTransparent));
        SetBrush(resources, "ColorBrushMemory", Oklch(tone.LForeground));

        // 主色
        var c1 = AdjustLinear(tone.C1, chromaAdjust * 0.25);
        SetBrush(resources, "ColorBrush1", Oklch(AdjustLinear(tone.L1, lightAdjust * 0.1), c1, hue));
        SetBrush(resources, "ColorBrush2", Oklch(AdjustLinear(tone.L2, lightAdjust), AdjustLinear(tone.C2, chromaAdjust), hue));
        SetBrush(resources, "ColorBrush3", Oklch(AdjustLinear(tone.L3, lightAdjust), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetBrush(resources, "ColorBrush4", Oklch(AdjustLinear(tone.L4, lightAdjust), AdjustLinear(tone.C4, chromaAdjust), hue));
        SetBrush(resources, "ColorBrush5", Oklch(AdjustLinear(tone.L5, lightAdjust), AdjustLinear(tone.C5, chromaAdjust), hue));
        SetBrush(resources, "ColorBrush6", Oklch(AdjustLinear(tone.L6, lightAdjust), AdjustLinear(tone.C6, chromaAdjust), hue));
        SetBrush(resources, "ColorBrush7", Oklch(AdjustLinear(tone.L7, lightAdjust), AdjustLinear(tone.C7, chromaAdjust), hue));
        SetBrush(resources, "ColorBrush8", Oklch(AdjustLinear(tone.L8, lightAdjust), AdjustLinear(tone.C8, chromaAdjust), hue));
        SetBrush(resources, "ColorBrushSemiTransparent",
            Oklch(AdjustLinear(tone.L8, lightAdjust), AdjustLinear(tone.C8, chromaAdjust), hue, tone.ASemiTransparent));
        SetBrush(resources, "ColorBrushBg0", Oklch(AdjustLinear(tone.L5, lightAdjust), AdjustLinear(tone.C5, chromaAdjust), hue));
        SetBrush(resources, "ColorBrushBg1",
            Oklch(AdjustLinear(tone.L7, lightAdjust), AdjustLinear(tone.C7, chromaAdjust), hue, tone.ASemiWhite));

        // 状态色（与原版 Application.xaml 相同）
        SetBrush(resources, "ColorBrushRedLight", Color.Parse("#FF4C4C"));
        SetBrush(resources, "ColorBrushRedDark", Color.Parse("#CE2111"));
        SetBrush(resources, "ColorBrushFatal", Color.Parse("#C23616"));
        SetBrush(resources, "ColorBrushError", Color.Parse("#E74C3C"));
        SetBrush(resources, "ColorBrushWarn", Color.Parse("#F39C12"));
        SetBrush(resources, "ColorBrushInfoDark", Color.Parse("#FFFFFF"));
        SetBrush(resources, "ColorBrushInfo", Color.Parse("#000000"));
        SetBrush(resources, "ColorBrushDebug", Color.Parse("#95A5A6"));

        // Fluent 基础控件（复选框、下拉选中项等）跟随 PCL 主题色
        var accent = Oklch(AdjustLinear(tone.L3, lightAdjust), AdjustLinear(tone.C3, chromaAdjust), hue);
        SetColor(resources, "SystemAccentColor", accent);
        SetColor(resources, "SystemAccentColorLight1", Oklch(Math.Min(1d, AdjustLinear(tone.L3, lightAdjust) + 0.1), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetColor(resources, "SystemAccentColorLight2", Oklch(Math.Min(1d, AdjustLinear(tone.L3, lightAdjust) + 0.18), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetColor(resources, "SystemAccentColorLight3", Oklch(Math.Min(1d, AdjustLinear(tone.L3, lightAdjust) + 0.26), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetColor(resources, "SystemAccentColorDark1", Oklch(Math.Max(0d, AdjustLinear(tone.L3, lightAdjust) - 0.08), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetColor(resources, "SystemAccentColorDark2", Oklch(Math.Max(0d, AdjustLinear(tone.L3, lightAdjust) - 0.16), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetColor(resources, "SystemAccentColorDark3", Oklch(Math.Max(0d, AdjustLinear(tone.L3, lightAdjust) - 0.24), AdjustLinear(tone.C3, chromaAdjust), hue));

        // 文字/背景便捷别名（布局代码使用）
        SetBrush(resources, "PclText", Oklch(tone.L1));
        SetBrush(resources, "PclTextSecondary", Oklch(tone.L1, alpha: 0.6));
        SetBrush(resources, "PclAccent", Oklch(AdjustLinear(tone.L3, lightAdjust), AdjustLinear(tone.C3, chromaAdjust), hue));
        SetBrush(resources, "PclWindowBackground", Oklch(tone.LBackground));
        SetBrush(resources, "PclCardBackground", Oklch(tone.LBackground, alpha: tone.ABackground));
        SetBrush(resources, "PclSidebarBackground", Oklch(tone.LBackground, alpha: dark ? 0.55 : 0.82));
    }

    /// <summary>原版「彩色背景」渐变（ThemeManager.RefreshBackground）。</summary>
    public static LinearGradientBrush BackgroundGradient()
    {
        var tone = IsDark ? DarkTone : LightTone;
        var (hue, _, _) = ThemeArgs(Theme);
        var darkLight = IsDark ? 0.2 : 1d;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.9, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.1, 1, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(Oklch(0.84 * darkLight, tone.C5, hue - 15), -0.1));
        brush.GradientStops.Add(new GradientStop(Oklch(0.96 * darkLight, tone.C7, hue), 0.4));
        brush.GradientStops.Add(new GradientStop(Oklch(0.84 * darkLight, tone.C5, hue + 15), 1.1));
        return brush;
    }

    /// <summary>取当前主题的静态画刷（供不支持 DynamicResource 的构造代码使用）。</summary>
    public static IBrush GetBrush(string key) =>
        Application.Current?.TryGetResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    /// <summary>读取当前主题下的颜色资源（不存在时返回透明色）。</summary>
    public static Color GetColor(string key) =>
        Application.Current?.TryGetResource(key, out var value) == true && value is Color color
            ? color
            : Application.Current?.TryGetResource(key, out var brush) == true && brush is ISolidColorBrush solid
                ? solid.Color
                : Colors.Transparent;

    private static void SetBrush(IResourceDictionary resources, string key, Color color) =>
        resources[key] = new SolidColorBrush(color);

    private static void SetColor(IResourceDictionary resources, string key, Color color) =>
        resources[key] = color;

    private static double AdjustLinear(double value, double adjustment)
    {
        if (adjustment == 0) return value;
        value = Math.Clamp(value, 0d, 1d);
        adjustment = Math.Clamp(adjustment, -1d, 1d);
        return adjustment > 0 ? value + (1d - value) * adjustment : value + value * adjustment;
    }

    /// <summary>HSL → sRGB（PCL MyHint 使用固定色相 + 90% 饱和度 + ToneProfile 明度）。</summary>
    public static Color Hsl(double hue, double saturation, double lightness, double alpha = 1d)
    {
        hue = (hue % 360d + 360d) % 360d;
        saturation = Math.Clamp(saturation, 0d, 1d);
        lightness = Math.Clamp(lightness, 0d, 1d);
        var c = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        var x = c * (1d - Math.Abs(hue / 60d % 2d - 1d));
        var m = lightness - c / 2d;
        double r, g, b;
        if (hue < 60) (r, g, b) = (c, x, 0);
        else if (hue < 120) (r, g, b) = (x, c, 0);
        else if (hue < 180) (r, g, b) = (0, c, x);
        else if (hue < 240) (r, g, b) = (0, x, c);
        else if (hue < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        byte Byte(double value) => (byte)Math.Round(Math.Clamp(value + m, 0d, 1d) * 255d);
        return Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0d, 1d) * 255d), Byte(r), Byte(g), Byte(b));
    }

    /// <summary>OKLCH → sRGB（OKLab 转换公式，来自 Björn Ottosson 的 OKLab 定义）。</summary>
    public static Color Oklch(double l, double c = 0d, double h = 0d, double alpha = 1d)
    {
        var radians = h * Math.PI / 180d;
        var a = c * Math.Cos(radians);
        var b = c * Math.Sin(radians);
        var l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        var l3 = l_ * l_ * l_;
        var m3 = m_ * m_ * m_;
        var s3 = s_ * s_ * s_;
        var r = 4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3;
        var g = -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3;
        var bl = -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3;
        return Color.FromArgb(
            (byte)Math.Round(Math.Clamp(alpha, 0d, 1d) * 255d),
            ToSrgbByte(r), ToSrgbByte(g), ToSrgbByte(bl));
    }

    private static byte ToSrgbByte(double linear)
    {
        var value = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(Math.Max(0d, linear), 1d / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(value, 0d, 1d) * 255d);
    }

    public static string Hex(Color color) => "#" + color.ToString().Substring(3);
}
