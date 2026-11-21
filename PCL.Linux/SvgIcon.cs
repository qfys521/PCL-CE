using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.Linux;

/// <summary>
/// 轻量 SVG 图标控件：直接使用仓库中 PCL CE 自带的 lucide 图标资源
/// （<c>PCL.Core.Wpf/UI/Assets/IconPacks/lucide</c>），以保证 Linux 版与原版图标完全一致。
/// 仅支持 lucide 使用的元素类型：path / line / circle / rect / polyline / polygon。
/// </summary>
public sealed class SvgIcon : Control
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<SvgIcon, string?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<SvgIcon, IBrush?>(nameof(IconBrush), Brushes.White);

    public static readonly StyledProperty<double> IconScaleProperty =
        AvaloniaProperty.Register<SvgIcon, double>(nameof(IconScale), 1d);

    static SvgIcon()
    {
        AffectsRender<SvgIcon>(IconProperty, IconBrushProperty, IconScaleProperty);
        AffectsMeasure<SvgIcon>(IconProperty);
    }

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    /// <summary>与 PCL WPF 的 LogoScale 对应：图标外框缩放，1 表示 1:1（16/24 DIP 视觉尺寸视使用处而定）。</summary>
    public double IconScale
    {
        get => GetValue(IconScaleProperty);
        set => SetValue(IconScaleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var geometry = LucideIcons.Get(Icon);
        if (geometry is null) return;
        var brush = IconBrush ?? Brushes.White;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // 注意：横线类图标（如 lucide/minus）的几何高度为 0，不能按几何边界判断是否绘制。
        var scale = Math.Min(Bounds.Width / 24d, Bounds.Height / 24d) * Math.Max(0.01, IconScale);
        var offsetX = (Bounds.Width - 24d * scale) / 2d;
        var offsetY = (Bounds.Height - 24d * scale) / 2d;
        var pen = new Pen(brush, 2d, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            // 图标原始 viewBox 为 24×24，lucide 线宽 2。
            using (context.PushTransform(Matrix.CreateTranslation(offsetX / scale, offsetY / scale)))
                context.DrawGeometry(null, pen, geometry);
        }
    }
}

/// <summary>lucide 图标几何缓存与 SVG 解析。</summary>
public static class LucideIcons
{
    private static readonly Dictionary<string, Geometry?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Geometry? Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.StartsWith("lucide/", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            Geometry? geometry = null;
            try
            {
                var xml = Load(name);
                if (xml is not null) geometry = Parse(xml);
            }
            catch (Exception ex)
            {
                PCL.Portable.AppLog.Warn($"加载图标失败（{name}）：{ex.Message}");
            }
            Cache[name] = geometry;
            return geometry;
        }
    }

    private static string? Load(string name)
    {
        var assembly = typeof(LucideIcons).Assembly;
        var stream = assembly.GetManifestResourceStream("PCL.Icons." + name + ".svg");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Geometry Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var group = new GeometryGroup { FillRule = FillRule.NonZero };
        var root = document.Root ?? throw new InvalidDataException("SVG 为空");
        foreach (var element in root.Elements())
        {
            var name = element.Name.LocalName.ToLowerInvariant();
            Geometry? geometry = name switch
            {
                "path" => ParsePath(element),
                "line" => new LineGeometry(Point(element, "x1", "y1"), Point(element, "x2", "y2")),
                "circle" => new EllipseGeometry(new Rect(
                    Number(element, "cx") - Number(element, "r"), Number(element, "cy") - Number(element, "r"),
                    Number(element, "r") * 2, Number(element, "r") * 2)),
                "rect" => new RectangleGeometry(new Rect(Number(element, "x"), Number(element, "y"),
                    Number(element, "width"), Number(element, "height")), Number(element, "rx", 0), Number(element, "ry", 0)),
                "polyline" => Points(element, false),
                "polygon" => Points(element, true),
                _ => null
            };
            if (geometry is not null) group.Children.Add(geometry);
        }
        return group.Children.Count == 1 ? group.Children[0] : group;
    }

    private static Geometry? ParsePath(XElement element)
    {
        var data = element.Attribute("d")?.Value;
        return string.IsNullOrWhiteSpace(data) ? null : StreamGeometry.Parse(data);
    }

    private static double Number(XElement element, string name, double fallback = 0d) =>
        double.TryParse(element.Attribute(name)?.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static Point Point(XElement element, string x, string y) => new(Number(element, x), Number(element, y));

    private static Geometry? Points(XElement element, bool closed)
    {
        var raw = element.Attribute("points")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var values = raw.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(text => double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0d).ToArray();
        if (values.Length < 4) return null;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(values[0], values[1]), false);
            for (var i = 2; i + 1 < values.Length; i += 2) context.LineTo(new Point(values[i], values[i + 1]));
            context.EndFigure(closed);
        }
        return geometry;
    }
}
