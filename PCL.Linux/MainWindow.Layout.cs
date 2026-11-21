using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// 主窗口骨架与页面导航。视觉度量对齐原版 PCL CE：
/// 窗口 850×500（最小 810×470）、外边距 10、圆角 6、标题栏 48；
/// 左侧栏宽度：启动 300 / 下载、设置、工具 152；卡片圆角 5、标题带 40。
/// </summary>
internal sealed partial class MainWindow
{
    private const double TitleBarHeight = 48;
    private const string DefaultLaunchPage = "launch";
    private const string DefaultDownloadPage = "download";
    private const string DefaultSetupPage = "settings";
    private const string DefaultToolsPage = "more";

    // =====================================================================
    // 结构
    // =====================================================================
    private void InitializeLayout()
    {
        Title = string.IsNullOrWhiteSpace(_settings.TitleLogoText) ? "PCL-CE-Linux" : _settings.TitleLogoText;
        Width = _settings.WindowWidth is >= 810 and <= 10000 ? _settings.WindowWidth : 850;
        Height = _settings.WindowHeight is >= 470 and <= 10000 ? _settings.WindowHeight : 500;
        MinWidth = 810;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Icon = LoadWindowIcon();
        // 先应用配色资源，后续用 PclUi 构建控件时 PclColors.GetBrush 才能取到正确颜色。
        ApplyThemeColors();
        InitializeMotion();

        _jvmArguments.Text = _settings.JvmArguments;
        _gameArguments.Text = _settings.GameArguments;
        _curseKey.Text = _settings.CurseForgeKey;
        _homepage.Text = _settings.HomepagePath;
        _background.Text = _settings.BackgroundPath;
        _accent.Text = _settings.AccentColor;
        _theme.SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _downloadSource.SelectedIndex = _settings.DownloadSource switch { "official" => 1, "mirror" => 2, _ => 0 };
        _interfaceFont.Text = _settings.InterfaceFont;
        _colorfulBackground.IsChecked = _settings.ColorfulBackground;
        _logoType.SelectedIndex = _settings.TitleLogoType switch { 2 => 1, 0 => 2, _ => 0 };
        _logoText.Text = _settings.TitleLogoText;
        _themeColor.SelectedIndex = _settings.ThemeColor switch { "SkyBlue" => 1, "DeathBlue" => 2, "HmclBlue" => 3, _ => 0 };
        _systemTitlebar.IsChecked = _settings.UseSystemTitlebar;
        _relays.Text = _settings.RelayNodes;
        _authlibServer.Text = _settings.AuthlibServer;
        _authlibUsername.Text = _settings.AuthlibUsername;
        _authlib.IsChecked = _settings.UseAuthlibAccount;
        _authlib.IsCheckedChanged += (_, _) => { if (_authlib.IsChecked == true) _microsoft.IsChecked = false; RefreshAccountHeader(); };
        _microsoft.IsCheckedChanged += (_, _) => { if (_microsoft.IsChecked == true) _authlib.IsChecked = false; RefreshAccountHeader(); };
        _directory.Text = _settings.GameDirectory;

        // ---------- 外层：透明窗口 + 10 像素边距 + 圆角 6 + 阴影 ----------
        _windowFrame = new Border
        {
            Margin = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = BuildFormBackground()
        };
        _windowFrame.Classes.Add("windowframe");
        _windowFrame.BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.Parse("#30000000") });

        var root = new Grid();
        _panRoot = root;
        root.Children.Add(_windowFrame);

        _panBack = new Border { Margin = new Thickness(10) };
        // 外层阴影与圆角裁切由 _windowFrame 完成，_panBack 保持透明。
        _panForm = new Grid { RowDefinitions = new RowDefinitions($"{TitleBarHeight},*") };

        // ---------- 标题栏 ----------
        _panTitle = new Border { Height = TitleBarHeight, ClipToBounds = true };
        _panTitle.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(PclColors.GetColor("ColorBrush2"), 0),
                new GradientStop(PclColors.GetColor("ColorBrush3"), 0.55),
                new GradientStop(PclColors.GetColor("ColorBrush2"), 1)
            }
        };
        Grid.SetRow(_panTitle, 0);
        _panForm.Children.Add(_panTitle);

        _panTitleMain = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Height = TitleBarHeight
        };
        _panTitleLeft = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_panTitleLeft, 0);
        _panTitleMain.Children.Add(_panTitleLeft);
        RebuildTitleBrand();

        _panTitleSelect = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 27,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(13, 0, 0, 0)
        };
        var (launchTab, downloadTab, setupTab, toolsTab) = (
            (Id: "launch", Label: "启动", Icon: "play"),
            (Id: "download", Label: "下载", Icon: "download"),
            (Id: "settings", Label: "设置", Icon: "settings"),
            (Id: "more", Label: "工具", Icon: "wrench"));
        foreach (var (id, label, icon) in new[] { launchTab, downloadTab, setupTab, toolsTab })
        {
            var tab = PclUi.TabButton(label, icon, id == "launch", () => Navigate(DefaultPageOf(id)));
            _tabs[id] = tab;
            _panTitleSelect.Children.Add(tab);
        }
        Grid.SetColumn(_panTitleSelect, 1);
        _panTitleMain.Children.Add(_panTitleSelect);

        // 右侧窗口按钮（原版只有最小化与关闭；双击标题栏切换最大化）
        _btnTitleMin = PclUi.IconButton("minus", 28, white: true, tip: "最小化");
        _btnTitleMin.Margin = new Thickness(0, 0, 44, 0);
        _btnTitleMin.HorizontalAlignment = HorizontalAlignment.Right;
        _btnTitleMin.VerticalAlignment = VerticalAlignment.Center;
        _btnTitleMin.Click += (_, _) => WindowState = WindowState.Minimized;
        _btnTitleClose = PclUi.IconButton("x", 28, white: true, tip: "关闭");
        _btnTitleClose.Margin = new Thickness(0, 0, 12, 0);
        _btnTitleClose.HorizontalAlignment = HorizontalAlignment.Right;
        _btnTitleClose.VerticalAlignment = VerticalAlignment.Center;
        _btnTitleClose.Click += (_, _) => Close();
        _panTitleButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _panTitleButtons.Children.Add(_btnTitleMin);
        _panTitleButtons.Children.Add(_btnTitleClose);

        // 子页面标题（返回箭头 + 标题）
        _panTitleInner = new Grid { Height = TitleBarHeight, IsVisible = false };
        var back = PclUi.IconButton("arrow-left", 28, white: true, tip: "返回");
        back.Margin = new Thickness(12, 0, 0, 0);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.VerticalAlignment = VerticalAlignment.Center;
        back.Click += (_, _) => GoBack();
        _panTitleInner.Children.Add(back);
        _labTitleInner = new TextBlock
        {
            FontSize = 15,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(47, 1, 60, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _panTitleInner.Children.Add(_labTitleInner);
        var titleHost = new Grid();
        titleHost.Children.Add(_panTitleMain);
        titleHost.Children.Add(_panTitleInner);
        titleHost.Children.Add(_panTitleButtons);
        _panTitle.Child = titleHost;

        _panTitle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual source && (source.GetVisualAncestors().OfType<Button>().Any() || source is Button)) return;
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else
                BeginMoveDrag(e);
        };

        // ---------- 左侧栏 + 右侧页面 ----------
        _form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_form, 1);
        _panForm.Children.Add(_form);

        _left.Bind(Border.BackgroundProperty, this.GetResourceObservable("ColorBrushTransparentBackground"));
        _left.HorizontalAlignment = HorizontalAlignment.Left;
        _left.Width = 152;
        _form.Children.Add(_left);
        var leftShadow = new Border
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(152, 0, 0, 0),
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#0A000000"), 0),
                    new GradientStop(Color.Parse("#00000000"), 1)
                }
            }
        };
        leftShadow.Classes.Add("leftshadow");
        _form.Children.Add(leftShadow);
        Grid.SetColumn(_right, 1);
        _right.HorizontalAlignment = HorizontalAlignment.Stretch;
        _right.VerticalAlignment = VerticalAlignment.Stretch;
        _right.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _right.VerticalContentAlignment = VerticalAlignment.Stretch;
        _form.Children.Add(_right);

        // ---------- 底部状态栏（保持与原版接近的极简样式） ----------
        var statusBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(18, 0, 14, 2),
            Height = 22,
            Background = Brushes.Transparent,
            IsVisible = false
        };
        _statusBar = statusBar;
        _status.FontSize = 11;
        _status.Opacity = 0.6;
        _status.VerticalAlignment = VerticalAlignment.Center;
        statusBar.Children.Add(_status);
        _activity.IsVisible = false;
        Grid.SetColumn(_activity, 1);
        statusBar.Children.Add(_activity);
        _cancel.Classes.Add("pcl");
        _cancel.Classes.Add("pill");
        _cancel.Padding = new Thickness(12, 0);
        _cancel.FontSize = 11;
        _cancel.Height = 22;
        _cancel.IsVisible = false;
        _cancel.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_cancel, 2);
        statusBar.Children.Add(_cancel);
        Grid.SetRow(statusBar, 1);
        Grid.SetColumn(statusBar, 0);
        Grid.SetColumnSpan(statusBar, 2);
        _form.Children.Add(statusBar);

        // ---------- 悬浮播放按钮（原版右下角圆形 Play） ----------
        _floatingPlay = new Button
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 22, 22),
            Background = PclUi.Accent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = new SvgIcon { Icon = "play", Width = 26, Height = 26, IconBrush = Brushes.White },
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _floatingPlay.Bind(BackgroundProperty, this.GetResourceObservable("ColorBrush3"));
        ToolTip.SetTip(_floatingPlay, "启动当前版本");
        _floatingPlay.Click += async (_, _) => await RunAsync(LaunchAsync);
        var floatHost = new Border
        {
            Child = _floatingPlay,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 22, 22),
            CornerRadius = new CornerRadius(28),
            IsVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 2, Color = Color.Parse("#401370F3") })
        };
        _floatingPlay.Margin = new Thickness(0);
        Grid.SetRowSpan(floatHost, 2);
        Grid.SetColumn(floatHost, 1);
        _form.Children.Add(floatHost);
        _floatingHost = floatHost;

        _windowFrame.Child = _panForm;
        Content = root;

        AddResizeGrips(root);
        ApplyAppearance();
        ApplyInterfaceFont();
        ApplyWindowDecorations();
        _releaseList.SelectionChanged += (_, _) =>
        {
            if (_releaseList.SelectedItem is GameRelease release) _releases.SelectedItem = release;
        };
        _installedList.SelectionChanged += (_, _) =>
        {
            if (_installedList.SelectedItem is string version)
            {
                _installed.SelectedItem = version;
                UpdateSelectedVersion();
            }
        };
        _cancel.Click += (_, _) => { if (_operation is not null) _operation.Cancel(); else _gameOperation?.Cancel(); };
        Navigate("launch");
        AttachThemeListeners();
        UpdateTabs("launch");
    }

    private void AttachThemeListeners()
    {
        try
        {
            if (Application.Current?.PlatformSettings is { } platform)
                platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (_settings.Theme is "system" or "") { ApplyThemeColors(); ApplyAppearance(); }
                });
        }
        catch (Exception ex) { AppendLog("无法监听系统主题：" + ex.Message); }
    }

    private static string DefaultPageOf(string tab) => tab switch
    {
        "launch" => DefaultLaunchPage,
        "download" => DefaultDownloadPage,
        "settings" => DefaultSetupPage,
        _ => DefaultToolsPage
    };

    private static string TabOf(string page) => page switch
    {
        "launch" or "instances" or "instance-settings" or "mods" or "version-settings" => "launch",
        "download" or "catalog" or "packs" or "dl-detail" => "download",
        "settings" or "appearance" or "services" or "linux" or "wipe" or "diagnostics" => "settings",
        _ => "more"
    };

    /// <summary>子页面是否使用“返回箭头 + 标题”的内层标题栏。</summary>
    private static string? InnerTitleOf(string page) => page switch
    {
        "instances" => "版本选择",
        "instance-settings" => "实例设置",
        "mods" => "模组管理",
        "version-settings" => "版本设置",
        "catalog" => "资源下载",
        "dl-detail" => "资源下载",
        "packs" => "整合包",
        _ => null
    };

    // =====================================================================
    // 背景 / 外观
    // =====================================================================
    private IBrush BuildFormBackground() =>
        _settings.ColorfulBackground ? PclColors.BackgroundGradient() : PclUi.Brush("ColorBrushBackground");

    private void ApplyThemeColors()
    {
        var dark = _settings.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark
        };
        var theme = Enum.TryParse<PclThemeColor>(_settings.ThemeColor, true, out var parsed) ? parsed : PclThemeColor.CatBlue;
        PclColors.Apply(Application.Current!, dark, theme);
        RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        ApplyBackgroundImage();
    }

    private void ApplyBackgroundImage()
    {
        if (_windowFrame is null) return;
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        try
        {
            if (File.Exists(_settings.BackgroundPath))
            {
                _backgroundBitmap = new Bitmap(_settings.BackgroundPath);
                _windowFrame.Background = new ImageBrush(_backgroundBitmap) { Stretch = Stretch.UniformToFill };
                return;
            }
        }
        catch (Exception ex) { AppendLog("加载背景图片失败：" + ex.Message); }
        _windowFrame.Background = BuildFormBackground();
    }

    /// <summary>把窗口标题 Logo 更新为设置项（默认 PCL 手绘 Logo；可切换为文本）。</summary>
    private void RebuildTitleBrand()
    {
        _panTitleLeft.Children.Clear();
        _brandHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var type = _settings.TitleLogoType;
        if (type != 0)
        {
            Control brand;
            if (type == 2)
            {
                brand = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_settings.TitleLogoText) ? "Point Cloud Library" : _settings.TitleLogoText,
                    FontFamily = new FontFamily("PCL English, Noto Sans CJK SC, sans-serif"),
                    FontSize = 17,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(18, 0, 0, 0)
                };
            }
            else
            {
                brand = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M26,29 v-25 h6 a7,7 180 0 1 0,14 h-6 M62.5,6.5 a10,11.5 180 1 0 0,18 M71,2.5 v24.5 h13.5"),
                    Stroke = Brushes.White,
                    StrokeThickness = 2.2,
                    Stretch = Stretch.Fill,
                    Width = 39,
                    Height = 18,
                    Margin = new Thickness(19, 15.5, 0, 15.5),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            _brandHost.Children.Add(brand);
        }
        if (type != 0 && type != 4)
        {
            _ceLogo = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(5),
                Height = 18,
                Width = 20,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "CE",
                    FontFamily = new FontFamily("PCL English, sans-serif"),
                    FontSize = 10.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = PclUi.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                }
            };
            Grid.SetColumn(_ceLogo, 1);
            _brandHost.Children.Add(_ceLogo);
        }
        _panTitleLeft.Children.Add(_brandHost);
    }

    private void ApplyWindowDecorations()
    {
        // 默认使用原版自绘标题栏（外观与 Windows 版一致）；可在设置中切换为系统装饰。
        var native = _settings.UseSystemTitlebar;
        SystemDecorations = native ? SystemDecorations.Full : SystemDecorations.None;
        _panTitle.IsVisible = !native;
        _panForm.RowDefinitions = new RowDefinitions(native ? "*" : $"{TitleBarHeight},*");
        Grid.SetRow(_form, native ? 0 : 1);
        if (native) TransparencyLevelHint = [];
        else TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        UpdateWindowClip();
    }

    // =====================================================================
    // 位置/尺寸
    // =====================================================================
    private void AddResizeGrips(Grid root)
    {
        var overlay = new Grid { Margin = new Thickness(10) };
        Grid.SetRowSpan(overlay, 2);
        overlay.IsHitTestVisible = true;
        (Control Grip, WindowEdge Edge)[] grips =
        [
            (new Border { Height = 6, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.TopSide) }, WindowEdge.North),
            (new Border { Height = 6, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomSide) }, WindowEdge.South),
            (new Border { Width = 6, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.LeftSide) }, WindowEdge.West),
            (new Border { Width = 6, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.RightSide) }, WindowEdge.East),
            (new Border { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.TopLeftCorner) }, WindowEdge.NorthWest),
            (new Border { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.TopRightCorner) }, WindowEdge.NorthEast),
            (new Border { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomLeftCorner) }, WindowEdge.SouthWest),
            (new Border { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomRightCorner) }, WindowEdge.SouthEast)
        ];
        foreach (var (grip, edge) in grips)
        {
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                BeginResizeDrag(edge, e);
                e.Handled = true;
            };
            overlay.Children.Add(grip);
        }
        root.Children.Add(overlay);
    }

    // =====================================================================
    // 导航
    // =====================================================================
    private void UpdateTabs(string tab)
    {
        foreach (var pair in _tabs) pair.Value.Classes.Set("selected", pair.Key == tab);
    }

    private void UpdateLeftWidth(string page)
    {
        var width = page switch
        {
            "launch" => 300,
            "instances" or "instance-settings" or "mods" or "version-settings" or "catalog" or "dl-detail" or "packs" => 0,
            _ => 152
        };
        _left.Width = width;
        _left.IsVisible = width > 0;
        foreach (var child in _form.Children.OfType<Border>())
            if (child.Classes.Contains("leftshadow"))
            {
                child.Margin = new Thickness(width, 0, 0, 0);
                child.IsVisible = width > 0;
            }
        // 右侧页面始终占据星号列，左侧栏隐藏时列宽为 0，页面仍铺满整个窗口。
        Grid.SetColumn(_right, 1);
    }

    private void Navigate(string page)
    {
        // “实例设置”与“版本选择”共用同一页面与控件实例，避免控件被重复挂载。
        if (page == "instance-settings") page = "instances";
        if (!_pages.TryGetValue(page, out var content))
        {
            content = (BuildLeftPanel(page), BuildRightPage(page));
            _pages[page] = content;
        }
        var previousTab = _currentTab;
        _currentPage = page;
        _currentTab = TabOf(page);
        UpdateTabs(_currentTab);
        UpdateLeftWidth(page);
        _floatingHost.IsVisible = _currentTab == "launch" || _currentTab == "download";
        var inner = InnerTitleOf(page);
        _panTitleMain.IsVisible = inner is null;
        _panTitleInner.IsVisible = inner is not null;
        if (inner is not null)
        {
            var name = page switch
            {
                "instance-settings" => "实例设置 - " + (_installed.SelectedItem as string ?? _settings.SelectedVersion ?? "未选择"),
                "mods" => "模组管理 - " + (_installed.SelectedItem as string ?? "未选择"),
                "version-settings" => "版本设置 - " + (_installed.SelectedItem as string ?? "未选择"),
                "catalog" => "资源下载",
                _ => inner
            };
            _labTitleInner.Text = name;
        }
        if (previousTab != _currentTab && _pageStack.Count > 0) _pageStack.Clear();

        // 切换页面（先退出再进入，与原版节奏接近）
        var enterLeft = !ReferenceEquals(_left.Child, content.Left);
        var enterRight = !ReferenceEquals(_right.Content, content.Right);
        _ = AnimatePageChangeAsync(content, enterLeft, enterRight);

        // 与旧版一致的惰性刷新
        if (!_smoke)
        {
            if (page == "mods" && _operation is null) _ = RunAsync(RefreshModsAsync);
            else if (page == "version-settings" && _operation is null) _ = RunAsync(LoadVersionSettingsAsync);
            else if (page == "launch" || page == "instances") RefreshInstalled();
            else if (page == "download" && _releaseList.ItemsSource is null && _operation is null) _ = RunAsync(RefreshReleasesAsync);
        }
    }

    private async Task AnimatePageChangeAsync((Control Left, Control Right) content, bool changeLeft, bool changeRight)
    {
        if (_smoke || !MotionEnabled)
        {
            if (changeLeft) _left.Child = content.Left;
            if (changeRight) _right.Content = content.Right;
            return;
        }
        try
        {
            {
                var exits = new List<Task>();
                if (changeRight && _right.Content is Control oldRight) exits.Add(PclMotion.ExitAsync(oldRight, CancellationToken.None));
                if (changeLeft && _left.Child is Control oldLeft) exits.Add(PclMotion.ExitAsync(oldLeft, CancellationToken.None, x: -25));
                if (exits.Count > 0) await Task.WhenAll(exits);
            }
            if (changeLeft) _left.Child = content.Left;
            if (changeRight) _right.Content = content.Right;
            if (!MotionEnabled) return;
            var enters = new List<Task>();
            if (changeLeft) enters.Add(PclMotion.EnterAsync(content.Left, CancellationToken.None, x: -25, milliseconds: 200));
            if (changeRight)
            {
                enters.Add(PclMotion.EnterAsync(content.Right, CancellationToken.None, y: -16, milliseconds: 250));
                var cards = content.Right.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("card")).Take(6).ToArray();
                for (var i = 0; i < cards.Length; i++)
                    enters.Add(PclMotion.EnterAsync(cards[i], CancellationToken.None, y: 5, milliseconds: 250, delay: 25 * i));
            }
            if (enters.Count > 0) await Task.WhenAll(enters);
        }
        catch (Exception ex)
        {
            if (changeLeft) _left.Child = content.Left;
            if (changeRight) _right.Content = content.Right;
            AppendLog("界面动画已跳过：" + ex.Message);
        }
    }

    private void GoBack()
    {
        if (_pageStack.Count == 0) { Navigate(DefaultLaunchPage); return; }
        var (page, tab) = _pageStack.Pop();
        Navigate(page);
    }

    /// <summary>推入一个子页面（返回时可直接回到当前页）。</summary>
    private void PushPage(string page)
    {
        if (InnerTitleOf(page) is not null) _pageStack.Push((_currentPage, _currentTab));
        Navigate(page);
    }

    private Control BuildLeftPanel(string page) => page switch
    {
        "launch" => LaunchLeft(),
        "download" or "catalog" or "packs" => DownloadMenu(page),
        "settings" or "appearance" or "services" or "linux" or "wipe" or "diagnostics" => SetupMenu(page),
        "instances" or "instance-settings" or "mods" or "version-settings" or "catalog" or "dl-detail" or "packs" => new Grid(),
        _ => ToolsMenu(page)
    };

    private Control BuildRightPage(string page) => page switch
    {
        "launch" => LaunchRight(),
        "download" => DownloadPage(),
        "instances" => InstancesPage(),
        "instance-settings" => InstancesPage(),
        "mods" => ModsPage(),
        "version-settings" => VersionSettingsPage(),
        "catalog" => CatalogPage(),
        "packs" => PacksPage(),
        "settings" => SettingsPage(),
        "appearance" => AppearancePage(),
        "services" => ServicesPage(),
        "linux" => LinuxIntegrationPage(),
        "wipe" => DataWipePage(),
        "diagnostics" => DiagnosticsPage(),
        "lobby" => LobbyPage(),
        "logs" => LogsPage(),
        _ => AboutPage()
    };

    // =====================================================================
    // 左侧菜单
    // =====================================================================
    private Control MenuPanel(params Control[] items)
    {
        _menuItems.Clear();
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 0 };
        foreach (var item in items) stack.Children.Add(item);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
            Background = Brushes.Transparent
        };
    }

    private Control MenuItem(string page, string title, string icon, string currentPage)
    {
        var item = PclUi.MenuItem(title, icon, page == currentPage, () =>
        {
            if (InnerTitleOf(page) is not null) PushPage(page); else Navigate(page);
        });
        _menuItems.Add(item);
        return item;
    }

    private Control DownloadMenu(string active) => MenuPanel(
        PclUi.SectionHeader("Minecraft", download: true),
        MenuItem("download", "游戏下载", "boxes", active),
        MenuItem("packs", "手动安装包", "package", active),
        PclUi.SectionHeader("社区资源", download: true),
        MenuItem("catalog", "Mod 与资源", "puzzle", active));

    private Control SetupMenu(string active) => MenuPanel(
        PclUi.SectionHeader("游戏"),
        MenuItem("settings", "启动选项", "rocket", active),
        MenuItem("appearance", "个性化", "palette", active),
        MenuItem("services", "账号与登录", "user", active),
        PclUi.SectionHeader("启动器"),
        MenuItem("linux", "桌面集成", "monitor", active),
        MenuItem("wipe", "清理数据", "trash-2", active),
        MenuItem("diagnostics", "系统信息", "stethoscope", active));

    private Control ToolsMenu(string active) => MenuPanel(
        PclUi.SectionHeader("工具"),
        MenuItem("lobby", "联机大厅", "bubbles", active),
        MenuItem("logs", "运行日志", "scroll-text", active),
        MenuItem("diagnostics", "系统信息", "stethoscope", active),
        PclUi.SectionHeader("关于"),
        MenuItem("more", "关于与帮助", "info", active));

    // =====================================================================
    // 页面通用辅助（与旧版 API 兼容，全部输出 PCL CE 风格控件）
    // =====================================================================
    private Button Button(string label, Func<CancellationToken, Task> action)
    {
        var button = PclUi.TextButton(label, () => _ = RunAsync(action));
        return button;
    }

    private static Grid Row(string label, Control field, params Control[] actions) => PclUi.FormRow(label, field, actions);

    private static WrapPanel Actions(params Control[] controls) => PclUi.ButtonRow(controls);

    private static TextBlock Paragraph(string text) => PclUi.Paragraph(text);

    private static Border Card(string title, params Control[] controls)
    {
        var card = new PclUi.PclCard(title);
        foreach (var control in controls) card.Add(control);
        return card;
    }

    private static StackPanel PageStack() => new() { Spacing = 15 };

    private static ScrollViewer Scroll(Control content) => PclUi.PageScroll(content);

    private static Control CardPage(string title, params Control[] controls) =>
        PclUi.PageScroll(PclUi.Stack(Card(title, controls)));

    private Button NavigationButton(string label, Action action, string style)
    {
        var button = PclUi.TextButton(label, action);
        return button;
    }

    // =====================================================================
    // 启动页（原版 PageLaunchLeft 300 宽 + 右侧主页）
    // =====================================================================
    private Control LaunchLeft()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto,20"), Margin = new Thickness(20, 20, 20, 0) };
        var account = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _headHost.Width = 64;
        _headHost.Height = 64;
        account.Children.Add(_headHost);
        account.Children.Add(_accountName);
        account.Children.Add(_accountType);
        _accountName.Text = _settings.OfflineName;
        _accountType.Text = "离线账号";
        _loginPanel.Children.Clear();
        var loginRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        loginRow.Children.Add(PclUi.TextButton("登录 Microsoft 账号", () => _ = RunAsync(LoginAsync)));
        loginRow.Children.Add(PclUi.TextButton("账号设置", () => Navigate("services")));
        _loginPanel.Children.Add(loginRow);
        account.Children.Add(_loginPanel);
        root.Children.Add(account);

        // 启动按钮（54 高，原版为高亮描边样式，版本名标注在按钮内部下方）
        var launchHost = new Grid { Margin = new Thickness(20, 16, 20, 10) };
        var button = PclUi.LargeButton("", () => { }, icon: "play");
        button.Classes.Add("launch");
        button.Height = 54;
        button.MinHeight = 54;
        button.CornerRadius = new CornerRadius(6);
        button.Background = PclUi.Brush("ColorBrushHalfWhite");
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = PclUi.Brush("ColorBrush2");
        button.Foreground = PclUi.Brush("ColorBrush2");
        button.Margin = new Thickness(10, 0, 10, 0);
        button.Padding = new Thickness(30, 0, 30, 15);
        button.Click += async (_, _) => await RunAsync(LaunchAsync);
        button.Content = BuildLaunchButtonContent();
        launchHost.Children.Add(button);
        _launchVersion.Text = _installed.SelectedItem as string ?? "尚未选择版本";
        _launchVersion.Margin = new Thickness(0, 0, 0, 8);
        _launchVersion.VerticalAlignment = VerticalAlignment.Bottom;
        _launchVersion.HorizontalAlignment = HorizontalAlignment.Center;
        _launchVersion.FontSize = 11;
        _launchVersion.Foreground = PclUi.Brush("ColorBrushGray3");
        _launchVersion.IsHitTestVisible = false;
        launchHost.Children.Add(_launchVersion);
        Grid.SetRow(launchHost, 1);
        root.Children.Add(launchHost);

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Margin = new Thickness(10, 0, 10, 0) };
        var select = PclUi.TextButton("版本选择", () => PushPage("instances"));
        select.HorizontalAlignment = HorizontalAlignment.Stretch;
        bottom.Children.Add(select);
        var settings = PclUi.TextButton("版本设置", () => PushPage("version-settings"));
        settings.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(settings, 1);
        bottom.Children.Add(settings);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        _launchButton = button;
        RefreshAccountHeader();
        return root;
    }

    private Control BuildLaunchButtonContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(new SvgIcon { Icon = "play", Width = 16, Height = 16, IconBrush = PclUi.Brush("ColorBrush2") });
        panel.Children.Add(new TextBlock
        {
            Text = "启动游戏", FontSize = 18, VerticalAlignment = VerticalAlignment.Center,
            Foreground = PclUi.Brush("ColorBrush2")
        });
        return panel;
    }

    private void RefreshAccountHeader()
    {
        var microsoft = _settings.MicrosoftAccount;
        var authlib = _settings.AuthlibAccount;
        var name = _settings.UseAuthlibAccount && authlib is not null ? authlib.UserName
            : _settings.UseMicrosoftAccount && microsoft is not null ? microsoft.UserName
            : string.IsNullOrWhiteSpace(_settings.OfflineName) ? "Player" : _settings.OfflineName;
        _accountName.Text = name;
        _accountType.Text = _settings.UseAuthlibAccount && authlib is not null ? "第三方认证"
            : _settings.UseMicrosoftAccount && microsoft is not null ? "正版验证" : "离线账号";
        var skinName = name.Length > 0 && char.IsLower(name[0]) ? "Alex" : "Steve";
        var skin = PclUi.LoadSkin(skinName);
        _headHost.Content = skin is null ? null : PclUi.Head(skin, 64, 6);
        if (_launchButton.Content is not null) _launchButton.Content = BuildLaunchButtonContent();
        if (_installed.SelectedItem is string version) _launchVersion.Text = version;
        else _launchVersion.Text = "尚未选择版本";
        if (_lobby is not null) { }
    }

    private Control LaunchRight()
    {
        var stack = new StackPanel { Spacing = 15 };
        if (!string.IsNullOrWhiteSpace(_settings.HomepagePath))
        {
            try { stack.Children.Add(HomepageRenderer.Load(_settings.HomepagePath, HandleHomepageAction)); }
            catch (Exception ex) { stack.Children.Add(Card("自定义主页加载失败", Paragraph(ex.Message))); }
        }
        else
        {
            var tip = new PclUi.PclCard("你知道吗", canSwap: true);
            tip.Add(new TextBlock
            {
                Text = "你可以点击左侧“版本选择”安装或切换 Minecraft 版本；启动器会自动校验并补全游戏文件。\n" +
                       "在“设置”里可以调整 Java、内存、下载源与桌面集成，所有数据都按 XDG 规范保存在用户目录中。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            });
            stack.Children.Add(tip);
        }
        var info = new PclUi.PclCard("PCL-CE-Linux", canSwap: true);
        info.Add(new TextBlock
        {
            Text = "PCL CE 的 Linux 原生版本。界面与 Windows 版保持一致：顶部标签、左侧栏、卡片与主题色。" +
                   "使用系统窗口装饰可在“设置 → 个性化”中切换。",
            TextWrapping = TextWrapping.Wrap, LineHeight = 21
        });
        info.SetHeaderRight(new TextBlock
        {
            Text = Program.VersionText, FontSize = 11, Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(info);

        var logCard = new PclUi.PclCard("启动器日志", canSwap: true);
        _log.MinHeight = 120;
        _log.AcceptsReturn = true;
        _log.TextWrapping = TextWrapping.Wrap;
        _log.Background = Brushes.Transparent;
        _log.BorderThickness = new Thickness(0);
        _log.FontSize = 12;
        _log.IsReadOnly = true;
        if (string.IsNullOrEmpty(_log.Text)) _log.Text = "就绪\n";
        logCard.Add(_log);
        stack.Children.Add(logCard);
        return PclUi.PageScroll(stack, new Thickness(25, 25, 25, 10));
    }

    // =====================================================================
    // 下载 / 版本 / 设置等页面（在原页面上套用 PCL 卡片与表单样式）
    // =====================================================================
    private Control DownloadPage()
    {
        _releaseList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GameRelease>((item, _) =>
        {
            var panel = new StackPanel { Spacing = 1, Margin = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = item!.Id, Classes = { "rowTitle" } });
            panel.Children.Add(new TextBlock
            {
                Text = item.Type switch
                {
                    "release" => "正式版", "snapshot" => "快照", "old_beta" => "远古测试版",
                    "old_alpha" => "远古 Alpha", _ => item.Type
                },
                Classes = { "rowInfo" }
            });
            var row = new Border { CornerRadius = new CornerRadius(6), Child = panel };
            row.Classes.Add("row");
            return row;
        });
        return PclUi.PageScroll(new StackPanel
        {
            Spacing = 15,
            Children =
            {
                PclUi.Card("游戏下载",
                    PclUi.Hint("Minecraft 下载在中国大陆可以正常使用；官方源不可达时会自动回退到 BMCLAPI 公开镜像。", "info"),
                    PclUi.ButtonRow(
                        PclUi.TextButton("刷新版本列表", () => _ = RunAsync(RefreshReleasesAsync), "primary"),
                        _showAllVersions),
                    _releaseList),
                PclUi.Card("安装所选版本",
                    Row("Minecraft", _releases),
                    Row("加载器", _loaderKind, PclUi.TextButton("获取版本", () => _ = RunAsync(RefreshLoaderVersionsAsync))),
                    Row("加载器版本", _loaderVersions),
                    PclUi.ButtonRow(PclUi.TextButton("安装所选版本", () => _ = RunAsync(InstallSelectedAsync), "primary")),
                    PclUi.ButtonRow(PclUi.TextButton("手动安装包 / 整合包", () => Navigate("packs")),
                        PclUi.TextButton("下载 Mod 与资源", () => Navigate("catalog")))),
                PclUi.Card("下载源",
                    Row("下载源", _downloadSource),
                    Paragraph("选择“自动”时优先使用 Mojang 官方地址，失败后回退镜像。"))
            }
        });
    }

    private Control InstancesPage()
    {
        _installedList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((item, _) =>
        {
            var panel = new StackPanel { Spacing = 1, Margin = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = item!, Classes = { "rowTitle" } });
            var row = new Border { CornerRadius = new CornerRadius(6), Child = panel };
            row.Classes.Add("row");
            return row;
        });
        var stack = new StackPanel { Spacing = 15 };
        stack.Children.Add(PclUi.Card("版本选择",
            PclUi.ButtonRow(PclUi.TextButton("刷新", () =>
            {
                RefreshInstalled();
            })),
            _installedList,
            Paragraph("选择已安装的版本后即可启动。左侧“版本选择”按钮随时返回这里。")));
        stack.Children.Add(PclUi.Card("当前版本",
            _instanceDetails,
            Row("当前版本", _installed),
            PclUi.ButtonRow(
                PclUi.TextButton("启动游戏", () => _ = RunAsync(LaunchAsync), "primary"),
                PclUi.TextButton("打开游戏文件夹", () => _ = RunAsync(OpenInstanceAsync)),
                PclUi.TextButton("补全文件", () => _ = RunAsync(RepairInstanceAsync)))));
        stack.Children.Add(PclUi.Card("创建隔离实例",
            Row("新实例名称", _newInstance),
            PclUi.ButtonRow(
                PclUi.TextButton("创建隔离实例", () => _ = RunAsync(CreateInstanceAsync)),
                PclUi.TextButton("版本设置", () => PushPage("version-settings")),
                PclUi.TextButton("管理模组", () => PushPage("mods")))));
        return PclUi.PageScroll(stack);
    }

    private Control ModsPage()
    {
        _mods.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LocalMod>((item, _) =>
        {
            var panel = new StackPanel { Spacing = 1, Margin = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = item!.Name, Classes = { "rowTitle" } });
            panel.Children.Add(new TextBlock
            {
                Text = (item.Enabled ? "已启用" : "已禁用") + " · " + item.Version + " · " + Path.GetFileName(item.Path),
                Classes = { "rowInfo" }
            });
            var row = new Border { CornerRadius = new CornerRadius(6), Child = panel };
            row.Classes.Add("row");
            return row;
        });
        return PclUi.PageScroll(PclUi.Stack(
            PclUi.Card("模组管理",
                _modDetails,
                _mods,
                PclUi.ButtonRow(
                    PclUi.TextButton("刷新", () => _ = RunAsync(RefreshModsAsync)),
                    PclUi.TextButton("导入 JAR", () => _ = RunAsync(ImportModsAsync)),
                    PclUi.TextButton("启用 / 禁用", () => _ = RunAsync(ToggleModsAsync)),
                    PclUi.TextButton("移至回收站", () => _ = RunAsync(RemoveModsAsync))),
                Paragraph("移除的文件保存在 mods/.pcl-trash 中，可手动恢复。"),
                PclUi.ButtonRow(PclUi.TextButton("下载模组", () => Navigate("catalog"))))));
    }

    private Control VersionSettingsPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("版本设置",
            _versionConfigLabel,
            Row("Java 路径", _versionJava),
            Row("内存 (MiB)", _versionMemory),
            Row("JVM 参数", _versionJvm),
            Row("游戏参数", _versionGame),
            Paragraph("内存为 0 时使用全局设置。此处的额外参数会与全局参数一起传给游戏。"),
            PclUi.ButtonRow(
                PclUi.TextButton("保存版本设置", () => _ = RunAsync(SaveVersionSettingsAsync)),
                PclUi.TextButton("刷新", () => _ = RunAsync(LoadVersionSettingsAsync)),
                PclUi.TextButton("打开游戏目录", () => _ = RunAsync(OpenInstanceAsync))))));

    private Control CatalogPage()
    {
        _projects.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CatalogProject>((item, _) =>
        {
            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6) };
            panel.Children.Add(new TextBlock { Text = item!.Title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
            panel.Children.Add(new TextBlock
            {
                Text = item.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7,
                MaxHeight = 34
            });
            return new Border
            {
                Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(6),
                Child = panel
            };
        });
        _projects.SelectionChanged += async (_, _) => { if (_operation is null) await RunAsync(LoadContentVersionsAsync); };
        var search = PclUi.SearchBox(_search);
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_operation is null) await RunAsync(SearchContentAsync);
        };
        return PclUi.PageScroll(new StackPanel
        {
            Spacing = 15,
            Children =
            {
                PclUi.Card("搜索",
                    search,
                    PclUi.ButtonRow(_catalogSource, _contentKind,
                        PclUi.TextButton("搜索", () => _ = RunAsync(SearchContentAsync), "primary"))),
                PclUi.Card("搜索结果",
                    _projects,
                    Paragraph("Mod 搜索会按当前实例的 Minecraft 与加载器版本筛选。"),
                    Row("资源版本", _contentVersions),
                    PclUi.ButtonRow(
                        PclUi.TextButton("下载到当前实例", () => _ = RunAsync(InstallContentAsync), "primary"),
                        PclUi.TextButton("项目网站", () =>
                        {
                            if (_projects.SelectedItem is CatalogProject project)
                                OpenUrl(project.WebsiteUrl ?? "https://modrinth.com/project/" + project.Id);
                        })))
            }
        });
    }

    private Control PacksPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("整合包",
            Paragraph("支持 Modrinth、CurseForge、MultiMC / Prism、MCBBS、HMCL 格式。导入的整合包使用独立游戏目录。"),
            Row("实例名称", _packName),
            PclUi.ButtonRow(
                PclUi.TextButton("选择整合包并导入", () => _ = RunAsync(ImportPackAsync), "primary"),
                PclUi.TextButton("导出当前实例", () => _ = RunAsync(ExportPackAsync))),
            Paragraph("导出为 .mrpack，包含模组和配置；启动器缓存、账号、日志及服务器列表不会打包。"))));

    private Control SettingsPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("游戏与 Java",
            Row("游戏目录", _directory, PclUi.TextButton("浏览", () => _ = RunAsync(BrowseDirectoryAsync))),
            Row("Java", _java, PclUi.TextButton("选择", () => _ = RunAsync(BrowseJavaAsync)), PclUi.TextButton("检测", () => _ = RunAsync(DetectJavaAsync))),
            Row("内存 (MiB)", _memory),
            PclUi.ButtonRow(PclUi.TextButton("保存设置", () =>
            {
                SaveSettings(); RefreshInstalled();
            }, "primary"))),
        PclUi.Card("启动参数",
            Row("JVM 参数", _jvmArguments),
            Row("游戏参数", _gameArguments),
            PclUi.ButtonRow(PclUi.TextButton("下载当前版本所需 Java", () => _ = RunAsync(DownloadJavaAsync))))));

    private Control AppearancePage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("主题",
            Row("主题", _theme),
            Row("主题色", _themeColor),
            _colorfulBackground,
            Row("界面字体", _interfaceFont),
            Paragraph("主题色与深浅色由原版 OKLCH 配色系统生成；默认使用 PCL 内置英文字体与系统中文字体，留空表示跟随系统。"),
            PclUi.ButtonRow(PclUi.TextButton("应用", () =>
            {
                SaveSettings();
                ApplyThemeColors();
                ApplyAppearance();
                RefreshHomepage();
            }, "primary"))),
        PclUi.Card("窗口与标题栏",
            Row("标题栏 Logo", _logoType),
            Row("标题栏文本", _logoText),
            _systemTitlebar,
            Paragraph("默认与原版一致：左侧 PCL 手绘 Logo + CE 徽标 + 顶部四个标签；也可改为文本或系统窗口装饰。")),
        PclUi.Card("背景与主页",
            Row("背景图片", _background, PclUi.TextButton("选择", () => _ = RunAsync(BrowseBackgroundAsync))),
            Row("自定义主页", _homepage, PclUi.TextButton("选择", () => _ = RunAsync(BrowseHomepageAsync))),
            Paragraph("支持原 Windows 主页中的常用 MyCard、MyButton、MyHint、StackPanel、Grid 和文本控件，也支持主页 JSON。")),
        PclUi.Card("动画",
            _animations,
            Paragraph("“设置 → 个性化 → 启用界面动画”可即时开关页面切换、卡片进入与按钮反馈动画。"))));

    private Control ServicesPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("Microsoft 账号",
            Row("Client ID", _clientId),
            _microsoft,
            Paragraph("源码不包含官方应用凭据。Microsoft 登录需要获准使用 Minecraft 服务的 OAuth Client ID；登录使用设备码流程。"),
            PclUi.ButtonRow(
                PclUi.TextButton("登录 Microsoft 账号", () => _ = RunAsync(LoginAsync), "primary"),
                PclUi.TextButton("退出 Microsoft 账号", () =>
                {
                    _settings.MicrosoftAccount = null;
                    _microsoft.IsChecked = false;
                    RefreshAccountHeader();
                    SaveSettings();
                }))),
        PclUi.Card("第三方认证（authlib-injector）",
            Row("认证服务器", _authlibServer),
            Row("登录名", _authlibUsername),
            Row("密码", _authlibPassword),
            _authlib,
            PclUi.ButtonRow(
                PclUi.TextButton("登录第三方账号", () => _ = RunAsync(LoginAuthlibAsync), "primary"),
                PclUi.TextButton("退出第三方账号", () =>
                {
                    _settings.AuthlibAccount = null;
                    _authlib.IsChecked = false;
                    RefreshAccountHeader();
                    SaveSettings();
                }))),
        PclUi.Card("CurseForge",
            Row("API Key", _curseKey),
            PclUi.ButtonRow(PclUi.TextButton("保存", () => SaveSettings(), "primary"))),
        PclUi.Card("离线账号",
            Row("用户名", _name),
            PclUi.ButtonRow(PclUi.TextButton("保存", () =>
            {
                SaveSettings();
                RefreshAccountHeader();
            }, "primary")))));

    private Control LobbyPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("联机大厅",
            Paragraph("使用与 Windows 版相同的 U/ 房间编号和 Scaffolding 协议，通过 Linux 原生 EasyTier 建立连接。"),
            _roomStatus,
            Row("游戏端口", _roomPort),
            Row("大厅编号", _roomCode),
            Row("引导节点", _relays),
            PclUi.ButtonRow(
                PclUi.TextButton("创建大厅", () => _ = RunAsync(CreateLobbyAsync)),
                PclUi.TextButton("加入大厅", () => _ = RunAsync(JoinLobbyAsync)),
                PclUi.TextButton("离开大厅", () => _ = RunAsync(LeaveLobbyAsync)),
                PclUi.TextButton("复制编号", () => _ = RunAsync(CopyRoomAsync))),
            _roomPlayers,
            Paragraph("房主先在 Minecraft 中开放局域网，再填写游戏显示的端口。加入者可在游戏的局域网列表或显示的本地地址进入。"))));

    private Control LogsPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("运行日志",
            PclUi.ButtonRow(
                PclUi.TextButton("分析日志文件", () => _ = RunAsync(AnalyzeLogAsync)),
                PclUi.TextButton("分析当前日志", () => ShowDiagnosis(_logView.Text ?? ""))),
            _logView)));

    private Control AboutPage() => PclUi.PageScroll(PclUi.Stack(
        PclUi.Card("PCL-CE-Linux",
            Paragraph($"PCL 社区版的 Linux 原生移植，版本 {Program.VersionText}。界面按 Windows 版移植：顶部标签、左侧栏、卡片与主题色。"),
            PclUi.Hint("PCL 作者：龙腾猫跃；社区版由 PCL Community 开发与维护；Linux 版由 PCL-CE-Linux 贡献者维护。", "info"),
            PclUi.ButtonRow(
                PclUi.TextButton("项目仓库", () => OpenUrl("https://github.com/qfys521/PCL-CE")),
                PclUi.TextButton("上游项目", () => OpenUrl("https://github.com/PCL-Community/PCL-CE")),
                PclUi.TextButton("运行日志", () => Navigate("logs")))),
        PclUi.Card("数据与路径",
            Paragraph("配置：$XDG_CONFIG_HOME/pcl-ce；数据与 Java：$XDG_DATA_HOME/pcl-ce；缓存：$XDG_CACHE_HOME/pcl-ce；日志：$XDG_STATE_HOME/pcl-ce；游戏目录默认 ~/PCL-CE。"))));

    private void UpdateSelectedVersion()
    {
        var id = _installed.SelectedItem as string;
        _launchVersion.Text = id ?? "尚未选择版本";
        _instanceDetails.Text = id is null ? "还没有游戏版本，请前往下载页面安装。" : "当前选择：" + id;
    }

    private void SaveExtendedSettings()
    {
        _settings.EnableAnimations = MotionEnabled;
        _settings.JvmArguments = _jvmArguments.Text ?? "";
        _settings.GameArguments = _gameArguments.Text ?? "";
        _settings.CurseForgeKey = _curseKey.Text ?? "";
        _settings.HomepagePath = _homepage.Text ?? "";
        _settings.BackgroundPath = _background.Text ?? "";
        if (!Color.TryParse(_accent.Text, out var color)) color = Color.Parse("#147AD6");
        _settings.AccentColor = color.ToString();
        _settings.Theme = _theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _settings.DownloadSource = _downloadSource.SelectedIndex switch { 1 => "official", 2 => "mirror", _ => "auto" };
        DownloadPolicy.SourceOverride = _settings.DownloadSource;
        _settings.InterfaceFont = (_interfaceFont.Text ?? "").Trim();
        _settings.ColorfulBackground = _colorfulBackground.IsChecked != false;
        _settings.TitleLogoType = _logoType.SelectedIndex switch { 1 => 2, 2 => 0, _ => 1 };
        _settings.TitleLogoText = (_logoText.Text ?? "").Trim();
        _settings.ThemeColor = _themeColor.SelectedIndex switch { 1 => "SkyBlue", 2 => "DeathBlue", 3 => "HmclBlue", _ => "CatBlue" };
        _settings.UseSystemTitlebar = _systemTitlebar.IsChecked == true;
        _settings.RelayNodes = _relays.Text ?? "";
        _settings.UseAuthlibAccount = _authlib.IsChecked == true;
        _settings.AuthlibServer = _authlibServer.Text ?? "";
        _settings.AuthlibUsername = _authlibUsername.Text ?? "";
        if (_linuxSettingsControlsReady) SaveLinuxIntegrationSettings();
    }

    private bool _lastDark;
    private PclThemeColor _lastThemeColor = PclThemeColor.CatBlue;
    private bool _appearanceReady;

    private void ApplyAppearance()
    {
        ApplyThemeColors();
        _panTitle.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(PclColors.GetColor("ColorBrush2"), 0),
                new GradientStop(PclColors.GetColor("ColorBrush3"), 0.55),
                new GradientStop(PclColors.GetColor("ColorBrush2"), 1)
            }
        };
        RebuildTitleBrand();
        ApplyWindowDecorations();
        ApplyBackgroundImage();
        ApplyInterfaceFont();
        _lastDark = PclColors.IsDark;
        _lastThemeColor = PclColors.Theme;
    }
}
