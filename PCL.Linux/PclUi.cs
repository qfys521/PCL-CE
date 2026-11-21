using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// PCL CE 风格的通用控件工厂。所有尺寸/颜色对应原版 WPF 控件度量：
/// 卡片圆角 5、标题带 40、菜单项 36、按钮 35/圆角 3、输入框 28、标签胶囊 27/13.5、大按钮 52/21。
/// </summary>
internal static class PclUi
{
    public static IBrush Brush(string key) => PclColors.GetBrush(key);
    public static IBrush Accent => Brush("ColorBrush3");
    public static IBrush Text => Brush("ColorBrush1");
    public static IBrush Muted => Brush("ColorBrushGray2");

    public static T Class<T>(T control, params string[] classes) where T : Control
    {
        foreach (var name in classes) control.Classes.Add(name);
        return control;
    }

    public static SvgIcon Icon(string name, double size = 20, IBrush? brush = null)
    {
        var icon = new SvgIcon
        {
            Icon = name,
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (brush is not null) icon.IconBrush = brush;
        else icon.Bind(SvgIcon.IconBrushProperty, icon.GetResourceObservable("ColorBrush1"));
        return icon;
    }

    /// <summary>PCL 卡片：圆角 5、40 像素标题带、可选折叠箭头、内容边距 25,40,25,15。</summary>
    public sealed class PclCard : Border
    {
        private readonly Grid _root = new();
        private readonly Border _contentHost = new();
        private readonly Avalonia.Controls.Shapes.Path _chevron = new();
        private readonly TextBlock _titleText = new();
        private bool _swapped;

        public PclCard(string? title = null, bool canSwap = false)
        {
            Classes.Add("card");
            Padding = new Thickness(0);
            // 用动态资源绑定背景，主题/深浅色切换时卡片自动跟随（PclCard 是 Border 子类，样式选择器不会匹配）。
            this.Bind(BackgroundProperty, this.GetResourceObservable("ColorBrushTransparentBackground"));
            CornerRadius = new CornerRadius(5);
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 5, OffsetY = 1, Color = Color.Parse("#20343D4A") });
            Child = _root;
            _root.RowDefinitions = new RowDefinitions("40,*");
            var header = new Grid { Height = 40 };
            header.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            _titleText.Classes.Add("title");
            _titleText.Margin = new Thickness(15, 12, 0, 0);
            _titleText.VerticalAlignment = VerticalAlignment.Top;
            _titleText.Text = title ?? "";
            _titleText.IsVisible = !string.IsNullOrEmpty(title);
            header.Children.Add(_titleText);

            _chevron.Data = Geometry.Parse("M2,4 l-2,2 10,10 10,-10 -2,-2 -8,8 -8,-8 z");
            _chevron.Width = 10;
            _chevron.Height = 6;
            _chevron.Stretch = Stretch.Fill;
            _chevron.Fill = Brush("ColorBrush1");
            _chevron.HorizontalAlignment = HorizontalAlignment.Right;
            _chevron.VerticalAlignment = VerticalAlignment.Top;
            _chevron.Margin = new Thickness(0, 17, 16, 0);
            _chevron.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _chevron.RenderTransform = new RotateTransform(180);
            _chevron.IsVisible = canSwap;
            Grid.SetColumn(_chevron, 1);
            header.Children.Add(_chevron);

            _root.Children.Add(header);

            _contentHost.Margin = new Thickness(25, 0, 25, 15);
            Grid.SetRow(_contentHost, 1);
            _root.Children.Add(_contentHost);

            if (canSwap)
            {
                header.Cursor = new Cursor(StandardCursorType.Hand);
                header.PointerPressed += (_, e) =>
                {
                    var point = e.GetPosition(header);
                    if (point.Y > 34) return;
                    SetSwapped(!_swapped);
                    e.Handled = true;
                };
            }

            HeaderHost = header;
        }

        public Grid HeaderHost { get; }

        public Border ContentHost => _contentHost;

        public string? Title
        {
            get => _titleText.Text;
            set { _titleText.Text = value; _titleText.IsVisible = !string.IsNullOrEmpty(value); }
        }

        public PclCard Add(Control control)
        {
            if (_contentHost.Child is StackPanel panel) panel.Children.Add(control);
            else _contentHost.Child = new StackPanel { Children = { control } };
            return this;
        }

        public PclCard Add(params Control[] controls)
        {
            foreach (var control in controls) Add(control);
            return this;
        }

        public PclCard SetContent(Control content)
        {
            _contentHost.Child = content;
            return this;
        }

        /// <summary>把额外控件放到标题带右侧（例如刷新按钮、状态文本）。</summary>
        public PclCard SetHeaderRight(Control control)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(0, 0, canSwapMargin(), 0);
            Grid.SetColumn(control, 1);
            HeaderHost.Children.Add(control);
            return this;
        }

        private double canSwapMargin() => _chevron.IsVisible ? 38 : 15;

        public PclCard SetSwapped(bool swapped)
        {
            _swapped = swapped;
            _root.RowDefinitions = new RowDefinitions(swapped ? "40,0" : "40,*");
            _contentHost.IsVisible = !swapped;
            _chevron.RenderTransform = new RotateTransform(swapped ? 270 : 180);
            return this;
        }

        /// <summary>松开内容高度限制（例如长列表卡片）。</summary>
        public PclCard AutoHeight()
        {
            _root.RowDefinitions = new RowDefinitions("40,Auto");
            return this;
        }
    }

    public static PclCard Card(string title, params Control[] controls) => new PclCard(title).Add(controls);

    public static PclCard Section(string title) => new PclCard(title);

    /// <summary>左侧菜单项（MyListItem：高 36、图标 20、文字 14、选中蓝字+左侧 5×20 指示条）。</summary>
    public static Button MenuItem(string title, string icon, bool selected, Action click, params Button[] hoverButtons)
    {
        var check = new Border
        {
            Width = 5, Height = 20, CornerRadius = new CornerRadius(2),
            Background = Brush("ColorBrush3"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-1, 0, 0, 0),
            IsVisible = selected
        };
        check.Classes.Add("menucheck");
        var menuIcon = new SvgIcon
        {
            Icon = icon, Width = 20, Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var iconHost = new Border { Width = 34, Height = 36, Child = menuIcon };
        iconHost.Classes.Add("menuicon");
        var text = new TextBlock
        {
            Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        text.Classes.Add("menutext");
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Height = 36
        };
        content.Children.Add(check);
        Grid.SetColumn(iconHost, 1); content.Children.Add(iconHost);
        Grid.SetColumn(text, 2); content.Children.Add(text);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0), IsVisible = false
        };
        buttons.Classes.Add("menubuttons");
        foreach (var button in hoverButtons) buttons.Children.Add(button);
        Grid.SetColumn(buttons, 3); content.Children.Add(buttons);

        var item = new Button
        {
            Height = 36, MinHeight = 36, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Content = content,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        item.Classes.Add("pcl");
        item.Classes.Add("menuitem");
        item.Classes.Set("selected", selected);
        item.Click += (_, _) => click();
        item.PointerEntered += (_, _) => buttons.IsVisible = hoverButtons.Length > 0;
        item.PointerExited += (_, _) => buttons.IsVisible = false;
        return item;
    }

    public static Button IconButton(string icon, double size = 28, bool white = false, string? tip = null)
    {
        var button = new Button
        {
            Content = new SvgIcon { Icon = icon, Width = size - 8, Height = size - 8, IconBrush = white ? Brushes.White : Text },
            Width = size, Height = size, MinWidth = 0, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(1000), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Classes.Add("pcl");
        button.Classes.Add("icon");
        if (white) button.Classes.Add("white");
        if (tip is not null) ToolTip.SetTip(button, tip);
        return button;
    }

    public static Button SmallIconButton(string icon, double size = 25, string? tip = null) => IconButton(icon, size, false, tip);

    public static Button TextButton(string text, Action click, string? css = null, string? icon = null)
    {
        Control content;
        if (icon is null) content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        else
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new SvgIcon { Icon = icon, Width = 14, Height = 14 });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = panel;
        }
        var button = new Button
        {
            Content = content, Height = 35, Padding = new Thickness(13, 0),
            CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Classes.Add("pcl");
        if (css is not null) foreach (var name in css.Split(' ', StringSplitOptions.RemoveEmptyEntries)) button.Classes.Add(name);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button LargeButton(string text, Action click, string? icon = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon is not null) panel.Children.Add(new SvgIcon { Icon = icon, Width = 16, Height = 16, IconBrush = Brushes.White });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = panel, Height = 52, MinWidth = 0, Padding = new Thickness(30, 0),
            CornerRadius = new CornerRadius(21), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Classes.Add("pcl");
        button.Classes.Add("large");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button TabButton(string text, string icon, bool selected, Action click)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var iconControl = new SvgIcon { Icon = icon, Width = 16, Height = 16 };
        iconControl.Classes.Add("tabicon");
        panel.Children.Add(iconControl);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        label.Classes.Add("tabtext");
        panel.Children.Add(label);
        var button = new Button
        {
            Content = panel, Height = 27, MinHeight = 27, MaxHeight = 27, MinWidth = 0,
            Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(13.5),
            BorderThickness = new Thickness(0), Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Classes.Add("pcl");
        button.Classes.Add("tab");
        button.Classes.Set("selected", selected);
        button.Click += (_, _) => click();
        return button;
    }

    public static Control SectionHeader(string text, bool download = false) => new TextBlock
    {
        Text = text, FontSize = 12, Opacity = 0.6,
        Margin = download ? new Thickness(13, 10, 5, 4) : new Thickness(13, 5, 5, 3)
    };

    /// <summary>表单行：标签 Auto（右边距 25）+ 控件拉伸；行高 28，间隔由外层 StackPanel 控制。</summary>
    public static Grid FormRow(string label, Control control, double labelMarginRight = 25, double height = 28)
    {
        var labelColumn = new ColumnDefinition(1, GridUnitType.Auto) { SharedSizeGroup = "PclFormLabel" };
        var row = new Grid
        {
            Height = height,
            ColumnDefinitions = new ColumnDefinitions { labelColumn, new ColumnDefinition(1, GridUnitType.Star) },
            Margin = new Thickness(0, 0, 0, 9)
        };
        var text = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, labelMarginRight, 0)
        };
        row.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(control);
        return row;
    }

    /// <summary>带附加控件（浏览按钮等）的表单行。</summary>
    public static Grid FormRow(string label, Control control, params Control[] trailing)
    {
        var columns = "Auto,*" + string.Concat(trailing.Select(_ => ",Auto"));
        var row = new Grid
        {
            Height = 28,
            ColumnDefinitions = new ColumnDefinitions(columns),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 9)
        };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 25, 0) });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(control);
        for (var i = 0; i < trailing.Length; i++)
        {
            Grid.SetColumn(trailing[i], i + 2);
            trailing[i].VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(trailing[i]);
        }
        return row;
    }

    /// <summary>提示条（MyHint：3 像素左边框、圆角 2、13 号字）。</summary>
    public static Border Hint(string text, string kind = "info")
    {
        var hue = kind switch { "error" or "red" => 355d, "warn" or "yellow" => 40d, _ => 210d };
        var dark = PclColors.IsDark;
        var background = PclColors.Hsl(hue, 0.9, dark ? 0.225 : 0.965);
        var foreground = PclColors.Hsl(hue, 0.85, dark ? 0.75 : 0.5);
        var border = new Border
        {
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(foreground),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(12, 9),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = text, FontSize = 13, LineHeight = 16, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(foreground)
            }
        };
        return border;
    }

    /// <summary>PCL 搜索框（高 40，左侧放大镜，13.5 字体）。</summary>
    public static Border SearchBox(TextBox textBox)
    {
        var border = new Border { Classes = { "search" }, Child = textBox };
        textBox.Height = 40;
        textBox.MinHeight = 40;
        textBox.BorderThickness = new Thickness(0);
        textBox.Background = Brushes.Transparent;
        textBox.FontSize = 13.5;
        textBox.Padding = new Thickness(32, 0, 40, 0);
        var host = new Grid();
        host.Children.Add(border);
        var icon = Icon("search", 14, Brush("ColorBrushGray3"));
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(14, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.IsHitTestVisible = false;
        host.Children.Add(icon);
        return new Border { Child = host };
    }

    /// <summary>Minecraft 头像：从原版皮肤纹理裁剪头部与帽子层。</summary>
    public static Control Head(Bitmap skin, double size = 120, double radius = 6)
    {
        var head = new CroppedBitmap(skin, new PixelRect(8, 8, 8, 8));
        var hat = new CroppedBitmap(skin, new PixelRect(40, 8, 8, 8));
        var grid = new Grid { Width = size, Height = size };
        foreach (var bitmap in new[] { head, hat })
        {
            var image = new Image
            {
                Source = bitmap, Stretch = Stretch.Fill,
                Width = size, Height = size
            };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
            grid.Children.Add(image);
        }
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            ClipToBounds = true, Child = grid
        };
    }

    public static Bitmap? LoadSkin(string? name = null)
    {
        try
        {
            var file = string.IsNullOrWhiteSpace(name) ? "Steve.png" : name + ".png";
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "Images", "Skins", file),
                         Path.Combine(AppContext.BaseDirectory, "Images", "Skins", "Steve.png")
                     })
                if (File.Exists(candidate)) return new Bitmap(candidate);
        }
        catch (Exception ex) { AppLog.Warn("加载皮肤失败：" + ex.Message); }
        return null;
    }

    /// <summary>小标签（TagPanelHelper 风格：11 号字、3,1 内边距、圆角 3、#11000000 背景）。</summary>
    public static Border Tag(string text) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#11000000")),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(3, 1),
        Margin = new Thickness(0, 0, 3, 0),
        Child = new TextBlock
        {
            Text = text, FontSize = 11,
            Foreground = new SolidColorBrush(PclColors.IsDark ? Color.Parse("#B0B6C0") : Color.Parse("#868686"))
        }
    };

    /// <summary>水平按钮行：按钮 35 高、间距 20。</summary>
    public static WrapPanel ButtonRow(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 20, LineSpacing = 10 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    /// <summary>页面外层滚动区（右栏）：内边距按原版 25,25,25,10 / 25,10 等常用值。</summary>
    public static ScrollViewer PageScroll(Control content, Thickness? margin = null)
    {
        var stack = new StackPanel
        {
            Margin = margin ?? new Thickness(25, 25, 25, 25),
            Children = { content },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetIsSharedSizeScope(stack, true);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Content = stack,
            Background = Brushes.Transparent
        };
    }

    /// <summary>把若干控件放入一个竖向堆叠的页面（第一张卡片上边距 15 由调用方控制）。</summary>
    public static StackPanel Stack(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 15 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    /// <summary>卡片之间使用 15 的间隔：StackPanel 已设置 Spacing，卡片本身不再加 margin。</summary>
    public static Control Page(params Control[] controls) => PageScroll(Stack(controls));

    public static TextBlock Paragraph(string text, bool muted = false)
    {
        var block = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = muted ? 12 : 13,
            Opacity = muted ? 0.6 : 1
        };
        return block;
    }
}
