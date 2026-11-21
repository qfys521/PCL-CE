using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Threading;
using PCL.Portable;

namespace PCL.Linux;

internal sealed partial class MainWindow
{
    private readonly Border _left = new();
    private readonly ContentControl _right = new();
    private readonly Border _titleBar = new();
    private readonly Border _bodyBackground = new();
    private readonly TextBlock _launchVersion = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _instanceDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, (Control Left, Control Right)> _pages = new();
    private readonly Dictionary<string, Button> _tabs = new();
    private string _currentPage = "launch";
    private Bitmap? _backgroundBitmap;
    private readonly TextBox _jvmArguments = new() { Watermark = "例如 -XX:+UseG1GC" };
    private readonly TextBox _gameArguments = new() { Watermark = "额外的 Minecraft 参数" };
    private readonly TextBox _curseKey = new() { PasswordChar = '●', Watermark = "CurseForge API Key（可选）" };
    private readonly TextBox _homepage = new() { Watermark = "本地 Custom.xaml 或主页 JSON 文件" };
    private readonly TextBox _background = new() { Watermark = "背景图片路径（留空使用原版背景）" };
    private readonly TextBox _accent = new();
    private readonly ComboBox _downloadSource = new()
    {
        ItemsSource = new[] { "自动（官方优先，失败时回退镜像）", "仅官方源", "仅镜像（BMCLAPI）" },
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _interfaceFont = new() { Watermark = "留空跟随系统字体" };
    private readonly ComboBox _theme = new() { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _showAllVersions = new() { Content = "显示快照、远古版和测试版" };
    private readonly ComboBox _loaderKind = new() { ItemsSource = Enum.GetValues<LoaderKind>(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _loaderVersions = new() { PlaceholderText = "加载器版本", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _newInstance = new() { Watermark = "新实例名称" };
    private readonly TextBox _packName = new() { Watermark = "导入后的实例名称" };
    private readonly TextBox _search = new() { Watermark = "搜索模组、整合包或资源" };
    private readonly ComboBox _catalogSource = new() { ItemsSource = Enum.GetValues<CatalogSource>(), SelectedIndex = 0 };
    private readonly ComboBox _contentKind = new() { ItemsSource = new[] { "mod", "modpack", "resourcepack", "shader" }, SelectedIndex = 0 };
    private readonly ListBox _projects = new() { MinHeight = 180 };
    private readonly ComboBox _contentVersions = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择资源版本" };
    private readonly ListBox _mods = new() { MinHeight = 200, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _modDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _roomCode = new() { Watermark = "U/ 开头的大厅编号" };
    private readonly NumericUpDown _roomPort = new() { Minimum = 1, Maximum = 65535, Value = 25565, FormatString = "0" };
    private readonly TextBox _relays = new() { AcceptsReturn = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _roomStatus = new() { Text = "尚未连接大厅", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _roomPlayers = new() { IsReadOnly = true, AcceptsReturn = true, MinHeight = 100 };
    private LinuxLobby? _lobby;
    private readonly CheckBox _authlib = new() { Content = "第三方认证账号" };
    private readonly TextBox _authlibServer = new() { Watermark = "https://服务器/api/yggdrasil" };
    private readonly TextBox _authlibUsername = new() { Watermark = "登录名 / 邮箱" };
    private readonly TextBox _authlibPassword = new() { PasswordChar = '●', Watermark = "密码（不保存）" };
    private readonly TextBlock _versionConfigLabel = new();
    private readonly TextBox _versionJava = new() { Watermark = "留空自动选择 Java" };
    private readonly NumericUpDown _versionMemory = new() { Minimum = 0, Maximum = 131072, Increment = 512, FormatString = "0" };
    private readonly TextBox _versionJvm = new();
    private readonly TextBox _versionGame = new();

    private void InitializeLayout()
    {
        Title = "PCL-CE-Linux";
        MinWidth = 850; MinHeight = 500;
        // Linux 适配：使用系统窗口装饰（X11/Wayland 下由 WM/合成器提供的标题栏与边框），
        // 不再绘制 Windows 风格的标题栏三键；应用内保留 PCL 的顶部导航条。
        SystemDecorations = SystemDecorations.Full;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TransparencyLevelHint = [];
        Background = Brushes.Transparent;
        Icon = LoadWindowIcon();
        InitializeMotion();
        _jvmArguments.Text = _settings.JvmArguments; _gameArguments.Text = _settings.GameArguments;
        _curseKey.Text = _settings.CurseForgeKey; _homepage.Text = _settings.HomepagePath;
        _background.Text = _settings.BackgroundPath; _accent.Text = _settings.AccentColor;
        _theme.SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _downloadSource.SelectedIndex = _settings.DownloadSource switch { "official" => 1, "mirror" => 2, _ => 0 };
        _interfaceFont.Text = _settings.InterfaceFont;
        _relays.Text = _settings.RelayNodes;
        _authlibServer.Text = _settings.AuthlibServer; _authlibUsername.Text = _settings.AuthlibUsername;
        _authlib.IsChecked = _settings.UseAuthlibAccount;
        _authlib.IsCheckedChanged += (_, _) => { if (_authlib.IsChecked == true) _microsoft.IsChecked = false; RefreshAccount(); };
        _microsoft.IsCheckedChanged += (_, _) => { if (_microsoft.IsChecked == true) _authlib.IsChecked = false; RefreshAccount(); };
        var root = new Grid { RowDefinitions = new RowDefinitions("48,*,30") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(19, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M26,29 v-25 h6 a7,7 180 0 1 0,14 h-6 M62.5,6.5 a10,11.5 180 1 0 0,18 M71,2.5 v24.5 h13.5"),
            Stroke = Brushes.White, StrokeThickness = 2.2, Stretch = Stretch.Fill, Width = 39, Height = 18
        });
        brand.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 0), Child = new TextBlock { Text = "CE", FontSize = 11, Foreground = new SolidColorBrush(Color.TryParse(_settings.AccentColor, out var savedAccent) ? savedAccent : Color.Parse("#147AD6")), FontWeight = FontWeight.SemiBold } });
        header.Children.Add(brand);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, label) in new[] { ("launch", "启动"), ("download", "下载"), ("settings", "设置"), ("more", "更多") })
        {
            var button = NavigationButton(label, () => Navigate(id), "nav"); _tabs[id] = button; navigation.Children.Add(button);
        }
        var navigationHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        navigationHost.Children.Add(_tabIndicator); navigationHost.Children.Add(navigation);
        Grid.SetColumn(navigationHost, 1); header.Children.Add(navigationHost);
        header.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        _titleBar.Child = header;
        root.Children.Add(_titleBar);
        _form.ColumnDefinitions = new ColumnDefinitions("300,*");
        _left.Bind(Border.BackgroundProperty, this.GetResourceObservable("PclSidebar"));
        _left.BorderThickness = new Thickness(0, 0, 1, 0); _left.BorderBrush = new SolidColorBrush(Color.Parse("#0C000000"));
        _form.Children.Add(_left);
        Grid.SetColumn(_right, 1); _form.Children.Add(_right);
        _bodyBackground.Child = _form;
        Grid.SetRow(_bodyBackground, 1); root.Children.Add(_bodyBackground);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70,Auto"), Margin = new Thickness(18, 0, 18, 0) };
        _status.FontSize = 11; _status.Classes.Add("muted"); footer.Children.Add(_status);
        _cancel.Classes.Add("pcl"); _cancel.Padding = new Thickness(10, 2); _cancel.FontSize = 11;
        _activity.Bind(ProgressBar.ForegroundProperty, this.GetResourceObservable("PclAccent"));
        Grid.SetColumn(_activity, 1); footer.Children.Add(_activity);
        Grid.SetColumn(_cancel, 2); footer.Children.Add(_cancel);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        _windowFrame.Child = root; _windowFrame.ClipToBounds = true;
        _windowFrame.Bind(Border.BackgroundProperty, this.GetResourceObservable("PclPage"));
        Content = _windowFrame;
        ApplyAppearance();
        ApplyInterfaceFont();
        try
        {
            if (Application.Current?.PlatformSettings is { } platform)
                platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (_settings.Theme is "system" or "") ApplyAppearance();
                });
        }
        catch (Exception ex) { AppendLog("无法监听系统主题：" + ex.Message); }
        Navigate("launch");
    }

    private Button NavigationButton(string label, Action action, string style)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Classes.Add("pcl"); button.Classes.Add(style);
        button.Click += (_, _) => action();
        return button;
    }

    private void Navigate(string page)
    {
        _currentPage = page;
        if (!_pages.TryGetValue(page, out var content))
        {
            content = page switch
            {
                "launch" => (LaunchSidebar(), LaunchPage()),
                "download" => (Menu("download"), DownloadPage()),
                "catalog" => (Menu("catalog"), CatalogPage()),
                "packs" => (Menu("packs"), PacksPage()),
                "instances" => (Menu("instances"), InstancesPage()),
                "version-settings" => (Menu("version-settings"), VersionSettingsPage()),
                "mods" => (Menu("mods"), ModsPage()),
                "settings" => (Menu("settings"), SettingsPage()),
                "wipe" => (Menu("wipe"), DataWipePage()),
                "linux" => (Menu("linux"), LinuxIntegrationPage()),
                "diagnostics" => (Menu("diagnostics"), DiagnosticsPage()),
                "appearance" => (Menu("appearance"), AppearancePage()),
                "services" => (Menu("services"), ServicesPage()),
                "lobby" => (Menu("lobby"), LobbyPage()),
                "logs" => (Menu("logs"), CardPage("启动与运行日志", Actions(Button("分析日志文件", AnalyzeLogAsync),
                    Button("分析当前日志", _ => { ShowDiagnosis(_log.Text ?? ""); return Task.CompletedTask; })), _log)),
                _ => (Menu("more"), AboutPage())
            };
            _pages[page] = content;
        }
        SelectSidebar(page);
        QueuePageChange(content);
        var tab = page switch { "download" or "catalog" or "packs" => "download", "settings" or "appearance" or "services" or "wipe" or "linux" => "settings", "launch" or "instances" or "mods" or "version-settings" => "launch", _ => "more" };
        foreach (var pair in _tabs) pair.Value.Classes.Set("selected", pair.Key == tab);
        Dispatcher.UIThread.Post(UpdateTabIndicator, DispatcherPriority.Loaded);
        if (!_smoke && page == "mods" && _operation is null) _ = RunAsync(RefreshModsAsync);
        if (!_smoke && page == "version-settings" && _operation is null) _ = RunAsync(LoadVersionSettingsAsync);
    }

    private Control Menu(string active)
    {
        var group = active switch
        {
            "download" or "catalog" or "packs" => "download",
            "settings" or "appearance" or "services" or "wipe" or "linux" => "settings",
            "instances" or "mods" or "version-settings" => "instances", _ => "more"
        };
        if (_menus.TryGetValue(group, out var existing)) return existing.Root;
        var menu = new StackPanel { Margin = new Thickness(0, 18, 0, 0), Spacing = 4 };
        var items = group switch
        {
            "download" => new[] { ("download", "游戏下载"), ("catalog", "模组与资源"), ("packs", "整合包导入 / 导出") },
            "settings" => new[] { ("settings", "启动选项"), ("appearance", "个性化"), ("services", "账号与服务"), ("linux", "桌面集成"), ("wipe", "清理数据") },
            "instances" => new[] { ("launch", "返回启动"), ("instances", "版本管理"), ("version-settings", "版本设置"), ("mods", "模组管理") },
            _ => new[] { ("more", "关于与帮助"), ("diagnostics", "系统信息"), ("lobby", "联机大厅"), ("logs", "运行日志") }
        };
        var buttons = new Dictionary<string, Button>();
        foreach (var (id, label) in items)
        {
            var button = NavigationButton(label, () => Navigate(id), "sidebar");
            button.Classes.Set("selected", id == active); buttons[id] = button; menu.Children.Add(button);
        }
        var position = new TranslateTransform(0, 18 + Math.Max(0, Array.FindIndex(items, item => item.Item1 == active)) * 48 + 8);
        position.Transitions = PclMotion.SlideTransitions(TranslateTransform.YProperty, MotionEnabled);
        var indicator = new Border
        {
            Width = 3, Height = 28, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, RenderTransform = position, IsHitTestVisible = false
        };
        indicator.Bind(Border.BackgroundProperty, this.GetResourceObservable("PclAccent"));
        var root = new Grid(); root.Children.Add(menu); root.Children.Add(indicator);
        _menus[group] = new SidebarMenu(root, buttons, indicator, position);
        return root;
    }

    private Control LaunchSidebar()
    {
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(20) };
        var account = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        account.Children.Add(new TextBlock { Text = "离线 / Microsoft 登录", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center });
        account.Children.Add(_name); account.Children.Add(_microsoft); account.Children.Add(_authlib);
        _account.FontSize = 12; _account.Classes.Add("muted"); account.Children.Add(_account);
        account.Children.Add(Button("登录 Microsoft 账号", LoginAsync));
        var options = NavigationButton("账号设置", () => Navigate("services"), "pcl"); account.Children.Add(options);
        sidebar.Children.Add(account);
        var launch = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 16, 0, 10) };
        var button = Button("启动游戏", LaunchAsync); button.Classes.Add("primary"); button.Height = 54;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.FontSize = 17; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(0, 0, 0, 12);
        launch.Children.Add(button);
        _launchVersion.Margin = new Thickness(10, 0, 10, 8); _launchVersion.VerticalAlignment = VerticalAlignment.Bottom; _launchVersion.IsHitTestVisible = false;
        _launchVersion.Foreground = Brushes.White;
        launch.Children.Add(_launchVersion);
        Grid.SetRow(launch, 1); sidebar.Children.Add(launch);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        var select = NavigationButton("版本选择", () => Navigate("instances"), "pcl"); select.HorizontalAlignment = HorizontalAlignment.Stretch;
        bottom.Children.Add(select);
        var settings = NavigationButton("版本设置", () => Navigate("version-settings"), "pcl"); Grid.SetColumn(settings, 1); bottom.Children.Add(settings);
        Grid.SetRow(bottom, 2); sidebar.Children.Add(bottom);
        return sidebar;
    }

    private Control LaunchPage()
    {
        var stack = PageStack();
        if (!string.IsNullOrWhiteSpace(_settings.HomepagePath))
        {
            try { stack.Children.Add(HomepageRenderer.Load(_settings.HomepagePath, HandleHomepageAction)); }
            catch (Exception ex) { stack.Children.Add(Card("自定义主页加载失败", Paragraph(ex.Message))); }
        }
        else
        {
            stack.Children.Add(Card("PCL 社区版", Paragraph("欢迎使用 Plain Craft Launcher Community Edition！"),
                Paragraph("选择游戏版本，然后点击左侧“启动游戏”。所有实例、模组与整合包都可以在熟悉的启动器布局中管理。"),
                Actions(NavigationButton("版本管理", () => Navigate("instances"), "pcl"), NavigationButton("下载游戏", () => Navigate("download"), "pcl"))));
        }
        stack.Children.Add(Card("社区版提示", Paragraph("游戏文件会在安装和启动前校验。遇到问题可前往“更多 → 运行日志”查看并分析。"),
            Paragraph("在“版本设置”调整单个实例的 Java、内存与启动参数；Microsoft 登录和 CurseForge 服务可在账号与服务中配置。")));
        stack.Children.Add(Card("常用功能", Actions(NavigationButton("导入整合包", () => Navigate("packs"), "pcl"),
            NavigationButton("模组管理", () => Navigate("mods"), "pcl"), NavigationButton("联机大厅", () => Navigate("lobby"), "pcl"))));
        return Scroll(stack);
    }

    private Control DownloadPage() => CardPage("自动安装", Paragraph("选择 Minecraft 和加载器；安装器会自动补全依赖与资源。"),
        Row("Minecraft", _releases, Button("刷新列表", RefreshReleasesAsync)), _showAllVersions,
        Row("加载器", _loaderKind, Button("获取版本", RefreshLoaderVersionsAsync)), Row("加载器版本", _loaderVersions),
        Actions(Button("安装所选版本", InstallSelectedAsync)));

    private Control InstancesPage() => CardPage("版本选择", _instanceDetails,
        Row("当前版本", _installed, Button("刷新", _ => { RefreshInstalled(); return Task.CompletedTask; })),
        Actions(Button("启动", LaunchAsync), Button("打开游戏文件夹", OpenInstanceAsync), Button("补全文件", RepairInstanceAsync)),
        Row("新实例名称", _newInstance), Actions(Button("创建隔离实例", CreateInstanceAsync), NavigationButton("管理模组", () => Navigate("mods"), "pcl")));

    private Control ModsPage() => CardPage("模组管理", _modDetails, _mods,
        Actions(Button("刷新", RefreshModsAsync), Button("导入 JAR", ImportModsAsync), Button("启用 / 禁用", ToggleModsAsync), Button("移至回收站", RemoveModsAsync)),
        Paragraph("移除的文件保存在 mods/.pcl-trash 中，可手动恢复。"), NavigationButton("下载模组", () => Navigate("catalog"), "pcl"));

    private Control VersionSettingsPage() => CardPage("版本设置", _versionConfigLabel,
        Row("Java 路径", _versionJava), Row("内存 (MiB)", _versionMemory), Row("JVM 参数", _versionJvm), Row("游戏参数", _versionGame),
        Paragraph("内存为 0 时使用全局设置。此处的额外参数会与全局参数一起传给游戏。"),
        Actions(Button("保存版本设置", SaveVersionSettingsAsync), Button("刷新", LoadVersionSettingsAsync),
            Button("打开游戏目录", OpenInstanceAsync), NavigationButton("管理模组", () => Navigate("mods"), "pcl")));

    private Control CatalogPage()
    {
        _projects.ItemTemplate = new FuncDataTemplate<CatalogProject>((item, _) => new StackPanel
        {
            Spacing = 4, Children = { new TextBlock { Text = item!.Title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, FontSize = 12 } }
        });
        _projects.SelectionChanged += async (_, _) => { if (_operation is null) await RunAsync(LoadContentVersionsAsync); };
        return CardPage("模组与资源下载", Actions(_catalogSource, _contentKind), Row("关键词", _search, Button("搜索", SearchContentAsync)),
            Paragraph("模组搜索会按当前实例的 Minecraft 与加载器版本筛选。"), _projects,
            Row("资源版本", _contentVersions), Actions(Button("下载到当前实例", InstallContentAsync),
                Button("项目网站", _ => { if (_projects.SelectedItem is CatalogProject project) OpenUrl(project.WebsiteUrl ?? "https://modrinth.com/project/" + project.Id); return Task.CompletedTask; })));
    }

    private Control PacksPage() => CardPage("整合包", Paragraph("支持 Modrinth、CurseForge、MultiMC / Prism、MCBBS、HMCL 格式。导入的整合包使用独立游戏目录。"),
        Row("实例名称", _packName), Actions(Button("选择整合包并导入", ImportPackAsync), Button("导出当前实例", ExportPackAsync)),
        Paragraph("导出为 .mrpack，包含模组和配置；启动器缓存、账号、日志及服务器列表不会打包。"));

    private Control SettingsPage() => CardPage("启动选项",
        Row("游戏目录", _directory, Button("浏览", BrowseDirectoryAsync)), Row("Java", _java, Button("选择", BrowseJavaAsync), Button("检测", DetectJavaAsync)),
        Row("内存 (MiB)", _memory), Row("下载源", _downloadSource), Row("JVM 参数", _jvmArguments), Row("游戏参数", _gameArguments),
        Paragraph("下载源“自动”会优先使用 Mojang 官方地址，网络不可达时回退到 BMCLAPI 公开镜像；Minecraft 下载在中国大陆不受功能限制。"),
        Actions(Button("保存设置", _ => { SaveSettings(); RefreshInstalled(); return Task.CompletedTask; }), Button("下载当前版本所需 Java", DownloadJavaAsync)));

    private Control AppearancePage() => CardPage("个性化", Row("主题", _theme), Row("主题颜色", _accent), Row("界面字体", _interfaceFont),
        Row("背景图片", _background, Button("选择", BrowseBackgroundAsync)), Row("自定义主页", _homepage, Button("选择", BrowseHomepageAsync)),
        _animations, Paragraph("支持原 Windows 主页中的常用 MyCard、MyButton、MyHint、StackPanel、Grid 和文本控件，也支持主页 JSON。"),
        Actions(Button("应用", _ => { SaveSettings(); ApplyAppearance(); RefreshHomepage(); return Task.CompletedTask; })));

    private Control ServicesPage() => CardPage("账号与服务", Row("Microsoft", _clientId), Row("CurseForge", _curseKey),
        Paragraph("源码不包含官方应用凭据。Microsoft 登录需要获准使用 Minecraft 服务的 Client ID；CurseForge API Key 用于该平台的搜索和整合包下载。"),
        Actions(Button("退出 Microsoft 账号", _ => { _settings.MicrosoftAccount = null; _microsoft.IsChecked = false; RefreshAccount(); SaveSettings(); return Task.CompletedTask; }),
            Button("保存", _ => { SaveSettings(); return Task.CompletedTask; })),
        Row("认证服务器", _authlibServer), Row("登录名", _authlibUsername), Row("密码", _authlibPassword),
        Actions(Button("登录第三方账号", LoginAuthlibAsync), Button("退出第三方账号", _ =>
        { _settings.AuthlibAccount = null; _authlib.IsChecked = false; RefreshAccount(); SaveSettings(); return Task.CompletedTask; })));

    private Control LobbyPage() => CardPage("联机大厅", Paragraph("使用与 Windows 版相同的 U/ 房间编号和 Scaffolding 协议，通过 Linux 原生 EasyTier 建立连接。"),
        _roomStatus, Row("游戏端口", _roomPort), Row("大厅编号", _roomCode), Row("引导节点", _relays),
        Actions(Button("创建大厅", CreateLobbyAsync), Button("加入大厅", JoinLobbyAsync), Button("离开大厅", LeaveLobbyAsync), Button("复制编号", CopyRoomAsync)), _roomPlayers,
        Paragraph("房主先在 Minecraft 中开放局域网，再填写游戏显示的端口。加入者可在游戏的局域网列表或显示的本地地址进入。"));

    private Control AboutPage() => CardPage("PCL-CE-Linux", Paragraph($"PCL 社区版的 Linux 原生移植，版本 {Program.VersionText}。界面沿用 Windows 版的顶部导航、账号区、启动按钮、卡片与主题设计，窗口使用系统提供的装饰。"),
        Actions(NavigationButton("项目仓库", () => OpenUrl("https://github.com/qfys521/PCL-CE"), "pcl"), NavigationButton("上游项目", () => OpenUrl("https://github.com/PCL-Community/PCL-CE"), "pcl"), NavigationButton("运行日志", () => Navigate("logs"), "pcl")),
        Paragraph("PCL 作者：龙腾猫跃；社区版由 PCL Community 开发与维护；Linux 版由 PCL-CE-Linux 贡献者维护。许可文件随发布包提供。"),
        Paragraph("应用数据严格遵循 XDG 目录规范：配置在 ~/.config/pcl-ce，数据与 Java 运行时在 ~/.local/share/pcl-ce，缓存在 ~/.cache/pcl-ce，日志在 ~/.local/state/pcl-ce，游戏目录默认 ~/PCL-CE。"));

    private static StackPanel PageStack() => new() { Margin = new Thickness(25, 25, 25, 10), Spacing = 0 };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    private static WrapPanel Actions(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private static Border Card(string title, params Control[] controls)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        foreach (var control in controls) body.Children.Add(control);
        var card = new Border { Child = body }; card.Classes.Add("card"); return card;
    }
    private static Control CardPage(string title, params Control[] controls)
    {
        var page = PageStack(); page.Children.Add(Card(title, controls)); return Scroll(page);
    }

    private void UpdateSelectedVersion()
    {
        var id = _installed.SelectedItem as string;
        _launchVersion.Text = id ?? "尚未选择版本";
        _instanceDetails.Text = id is null ? "还没有游戏版本，请前往下载页面安装。" : "当前选择：" + id;
    }

    private void SaveExtendedSettings()
    {
        _settings.EnableAnimations = MotionEnabled;
        _settings.JvmArguments = _jvmArguments.Text ?? ""; _settings.GameArguments = _gameArguments.Text ?? "";
        _settings.CurseForgeKey = _curseKey.Text ?? ""; _settings.HomepagePath = _homepage.Text ?? "";
        _settings.BackgroundPath = _background.Text ?? "";
        if (!Color.TryParse(_accent.Text, out var color)) throw new ArgumentException("主题颜色无效，请输入 #147AD6 这样的颜色值。");
        _settings.AccentColor = color.ToString();
        _settings.Theme = _theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _settings.DownloadSource = _downloadSource.SelectedIndex switch { 1 => "official", 2 => "mirror", _ => "auto" };
        DownloadPolicy.SourceOverride = _settings.DownloadSource;
        _settings.InterfaceFont = (_interfaceFont.Text ?? "").Trim();
        if (_linuxSettingsControlsReady) SaveLinuxIntegrationSettings();
        _settings.RelayNodes = _relays.Text ?? "";
        _settings.UseAuthlibAccount = _authlib.IsChecked == true;
        _settings.AuthlibServer = _authlibServer.Text ?? ""; _settings.AuthlibUsername = _authlibUsername.Text ?? "";
    }

    private void ApplyAppearance()
    {
        var color = Color.TryParse(_settings.AccentColor, out var savedColor) ? savedColor : Color.Parse("#147AD6");
        var dark = _settings.Theme == "dark" || _settings.Theme == "system" &&
            Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Resources["PclAccent"] = new SolidColorBrush(color);
        Resources["PclAccentHover"] = new SolidColorBrush(Color.FromRgb((byte)(color.R + (255 - color.R) * 0.12), (byte)(color.G + (255 - color.G) * 0.12), (byte)(color.B + (255 - color.B) * 0.12)));
        Resources["PclAccentPressed"] = new SolidColorBrush(Color.FromRgb((byte)(color.R * 0.85), (byte)(color.G * 0.85), (byte)(color.B * 0.85)));
        Resources["PclHover"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)40 : (byte)20, color.R, color.G, color.B));
        Resources["PclLine"] = new SolidColorBrush(Color.Parse(dark ? "#464C56" : "#DAE1E8"));
        Resources["PclPage"] = new SolidColorBrush(Color.Parse(dark ? "#202329" : "#F3F4F5"));
        Resources["PclSurface"] = new SolidColorBrush(Color.Parse(dark ? "#2B2F36" : "#FFFFFF"));
        Resources["PclSidebar"] = new SolidColorBrush(Color.Parse(dark ? "#252930" : "#FAFAFA"));
        Resources["PclText"] = new SolidColorBrush(Color.Parse(dark ? "#E5E7EB" : "#30343B"));
        Resources["PclMuted"] = new SolidColorBrush(Color.Parse(dark ? "#B0B6C0" : "#7B818A"));
        _titleBar.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(color, 0), new GradientStop(Color.FromRgb((byte)(color.R * 0.8), (byte)(color.G * 0.8), (byte)(color.B * 0.9)), 1) }
        };
        _backgroundBitmap?.Dispose(); _backgroundBitmap = null;
        if (File.Exists(_settings.BackgroundPath))
        {
            _backgroundBitmap = new Bitmap(_settings.BackgroundPath);
            _bodyBackground.Background = new ImageBrush(_backgroundBitmap) { Stretch = Stretch.UniformToFill };
        }
        else _bodyBackground.Background = null;
        ApplyInterfaceFont();
    }
}
