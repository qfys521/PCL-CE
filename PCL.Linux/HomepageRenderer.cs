using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PCL.Linux;

/// <summary>Renders declarative PCL homepage controls without executing CLR objects or WPF code.</summary>
public static class HomepageRenderer
{
    public static Control Load(string path, Action<string, string> action)
    {
        var text = File.ReadAllText(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return ReadJson(JsonNode.Parse(text)!, action);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*<\?xml[^>]*\?>", "");
        using var reader = XmlReader.Create(new StringReader("<Homepage xmlns:local=\"clr-namespace:PCL\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + text + "</Homepage>"), settings);
        return Render(XElement.Load(reader), directory, action, 0);
    }

    private static Control ReadJson(JsonNode json, Action<string, string> action)
    {
        var stack = new StackPanel { Spacing = 15 };
        foreach (var card in (json as JsonArray ?? json["cards"]?.AsArray() ?? []))
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = card!["title"]?.ToString() ?? "", FontWeight = FontWeight.SemiBold, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = card["text"]?.ToString() ?? "", TextWrapping = TextWrapping.Wrap });
            foreach (var button in card["buttons"]?.AsArray() ?? [])
            {
                var item = new Button { Content = button!["text"]?.ToString() ?? "打开" }; item.Classes.Add("pcl");
                item.Click += (_, _) => action(button["type"]?.ToString() ?? "打开网页", button["data"]?.ToString() ?? button["url"]?.ToString() ?? "");
                content.Children.Add(item);
            }
            var border = new Border { Child = content }; border.Classes.Add("card"); stack.Children.Add(border);
        }
        return stack;
    }

    private static Control Render(XElement element, string directory, Action<string, string> action, int depth)
    {
        if (depth > 48) throw new InvalidDataException("主页控件嵌套过深。");
        string? Attribute(string name) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;
        var children = element.Elements().Where(child => !child.Name.LocalName.Contains('.')).ToArray();
        Control Child(XElement child) => Render(child, directory, action, depth + 1);
        Control control;
        switch (element.Name.LocalName)
        {
            case "Homepage": case "StackPanel": case "MyStackPanel":
                var stack = new StackPanel { Orientation = Attribute("Orientation") == "Horizontal" ? Orientation.Horizontal : Orientation.Vertical };
                foreach (var child in children) stack.Children.Add(Child(child));
                control = stack; break;
            case "WrapPanel":
                var wrap = new WrapPanel(); foreach (var child in children) wrap.Children.Add(Child(child)); control = wrap; break;
            case "Grid":
                var grid = new Grid();
                var rows = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Grid.RowDefinitions");
                var columns = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Grid.ColumnDefinitions");
                if (rows is not null) grid.RowDefinitions = new RowDefinitions(string.Join(',', rows.Elements().Select(row => row.Attribute("Height")?.Value ?? "*")));
                if (columns is not null) grid.ColumnDefinitions = new ColumnDefinitions(string.Join(',', columns.Elements().Select(column => column.Attribute("Width")?.Value ?? "*")));
                foreach (var child in children) grid.Children.Add(Child(child)); control = grid; break;
            case "TextBlock": case "Label":
                var text = new TextBlock { Text = Attribute("Text") ?? Attribute("Content") ?? element.Value, TextWrapping = TextWrapping.Wrap };
                if (Attribute("Foreground") is { } foreground && !foreground.StartsWith('{')) text.Foreground = Brush.Parse(foreground);
                if (Attribute("FontWeight") is "Bold" or "SemiBold") text.FontWeight = FontWeight.SemiBold;
                if (double.TryParse(Attribute("FontSize"), CultureInfo.InvariantCulture, out var size)) text.FontSize = size;
                control = text; break;
            case "MyCard":
                var body = new StackPanel { Spacing = 12 };
                if (Attribute("Title") is { } title) body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
                foreach (var child in children) body.Children.Add(Child(child));
                var card = new Border { Child = body }; card.Classes.Add("card"); control = card; break;
            case "MyButton": case "MyTextButton": case "MyListItem": case "Button":
                var button = new Button { Content = Attribute("Text") ?? Attribute("Title") ?? Attribute("Content") ?? element.Value };
                button.Classes.Add("pcl");
                var eventType = Attribute("EventType") ?? "";
                var eventData = Attribute("EventData") ?? "";
                button.Click += (_, _) => action(eventType, eventData);
                if (Attribute("ColorType") == "Highlight") button.Classes.Add("primary");
                control = button; break;
            case "MyHint":
                control = new Border { Background = new SolidColorBrush(Color.Parse("#153B9BE8")), Padding = new Thickness(12), CornerRadius = new CornerRadius(3), Child = new TextBlock { Text = Attribute("Text") ?? element.Value, TextWrapping = TextWrapping.Wrap } };
                break;
            case "Border":
                var border = new Border { Child = children.Length == 1 ? Child(children[0]) : null };
                if (children.Length > 1) { var panel = new StackPanel(); foreach (var child in children) panel.Children.Add(Child(child)); border.Child = panel; }
                if (Attribute("CornerRadius") is { } radius) border.CornerRadius = CornerRadius.Parse(radius);
                if (Attribute("Padding") is { } padding) border.Padding = Thickness.Parse(padding);
                if (Attribute("Background") is { } background && !background.StartsWith('{')) border.Background = Brush.Parse(background);
                control = border; break;
            case "MyImage": case "Image":
                var image = new Image { Stretch = Stretch.Uniform };
                if (Attribute("Source") is { } source)
                {
                    var resource = source.Contains(";component/", StringComparison.Ordinal) ? source[(source.IndexOf(";component/", StringComparison.Ordinal) + 11)..] : source;
                    var candidate = Path.IsPathRooted(resource) ? resource : Path.Combine(directory, resource);
                    if (!File.Exists(candidate)) candidate = Path.Combine(AppContext.BaseDirectory, resource.TrimStart('/'));
                    if (!File.Exists(candidate)) throw new InvalidDataException($"找不到主页图片：{source}");
                    image.Source = new Bitmap(candidate);
                }
                control = image; break;
            case "ScrollViewer": case "MyScrollViewer":
                control = new ScrollViewer { Content = children.Length > 0 ? Child(children[0]) : null }; break;
            case "Separator": control = new Separator(); break;
            default: throw new InvalidDataException($"主页控件 {element.Name.LocalName} 尚未兼容，请改用 MyCard、MyButton、MyHint 或基础布局控件。");
        }
        if (Attribute("Margin") is { } margin) control.Margin = Thickness.Parse(margin);
        if (double.TryParse(Attribute("Width"), CultureInfo.InvariantCulture, out var width)) control.Width = width;
        if (double.TryParse(Attribute("Height"), CultureInfo.InvariantCulture, out var height)) control.Height = height;
        if (double.TryParse(Attribute("Opacity"), CultureInfo.InvariantCulture, out var opacity)) control.Opacity = opacity;
        if (Enum.TryParse<HorizontalAlignment>(Attribute("HorizontalAlignment"), out var horizontal)) control.HorizontalAlignment = horizontal;
        if (Enum.TryParse<VerticalAlignment>(Attribute("VerticalAlignment"), out var vertical)) control.VerticalAlignment = vertical;
        if (int.TryParse(Attribute("Grid.Row"), out var rowIndex)) Grid.SetRow(control, rowIndex);
        if (int.TryParse(Attribute("Grid.Column"), out var columnIndex)) Grid.SetColumn(control, columnIndex);
        if (int.TryParse(Attribute("Grid.RowSpan"), out var rowSpan)) Grid.SetRowSpan(control, rowSpan);
        if (int.TryParse(Attribute("Grid.ColumnSpan"), out var columnSpan)) Grid.SetColumnSpan(control, columnSpan);
        if (Attribute("Visibility") is "Collapsed" or "Hidden") control.IsVisible = false;
        if (Attribute("ToolTip") is { } tooltip) ToolTip.SetTip(control, tooltip);
        return control;
    }
}
