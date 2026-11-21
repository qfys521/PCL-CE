using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// 主窗口控件字段。视觉布局在 MainWindow.Layout.cs，页面内容在各 Page 分部类中构建；
/// 这些字段与各业务分部类（Features/Maintenance/Linux/External）共享。
/// </summary>
internal sealed partial class MainWindow
{
    // ---------- 窗口骨架（对齐原版 PCL CE FormMain） ----------
    private Border _windowFrame = new();
    private Grid _panRoot = new();
    private Border _panBack = new();
    private Grid _panForm = new();
    private Border _panTitle = new();
    private Grid _panTitleMain = new();
    private Grid _panTitleInner = new();
    private TextBlock _labTitleInner = new();
    private Grid _panTitleLeft = new();
    private StackPanel _panTitleSelect = new();
    private StackPanel _panTitleButtons = new();
    private Border _ceLogo = new();
    private Grid _brandHost = new();
    private Button _btnTitleMin = new();
    private Button _btnTitleClose = new();
    private StackPanel _panExtraButtons = new();
    private Button _floatingPlay = new();
    private Border _floatingHost = new();
    private Grid _statusBar = new();
    private readonly Border _left = new();
    private readonly ContentControl _right = new();
    private readonly Border _bodyBackground = new();
    private Grid _form = new();
    private readonly TextBlock _status = new() { Text = "就绪", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "取消", IsVisible = false };
    private readonly ProgressBar _activity = new() { Height = 3, Width = 58, CornerRadius = new CornerRadius(1.5), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false };
    private readonly TextBlock _launchVersion = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
    private readonly TextBlock _instanceDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, (Control Left, Control Right)> _pages = new();
    private readonly Dictionary<string, Button> _tabs = new();
    private readonly List<Button> _menuItems = new();
    private readonly Stack<(string Page, string Tab)> _pageStack = new();
    private string _currentPage = "launch";
    private string _currentTab = "launch";
    private Bitmap? _backgroundBitmap;

    // ---------- 启动页（原版 PageLaunchLeft，宽 300） ----------
    private readonly ContentControl _headHost = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
    private readonly TextBlock _accountName = new() { FontSize = 16, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _accountType = new() { FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Opacity = 0.6 };
    private Button _launchButton = new();
    private readonly Border _launchPanel = new();
    private readonly StackPanel _loginPanel = new() { Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };

    // ---------- 设置与下载 ----------
    private readonly TextBox _directory = new() { Watermark = "Minecraft 游戏目录" };
    private readonly TextBox _java = new() { Watermark = "Java 可执行文件（bin/java）" };
    private readonly TextBox _name = new() { Watermark = "离线用户名" };
    private readonly TextBox _clientId = new() { Watermark = "Microsoft OAuth Client ID" };
    private readonly CheckBox _microsoft = new() { Content = "Microsoft 账号" };
    private readonly NumericUpDown _memory = new() { Minimum = 512, Maximum = 131072, Increment = 512, FormatString = "0" };
    private readonly ComboBox _installed = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "选择版本" };
    private readonly ComboBox _releases = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "选择 Minecraft 版本" };
    private readonly ListBox _releaseList = new() { MinHeight = 200 };
    private readonly ListBox _installedList = new() { MinHeight = 120 };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180 };
    private readonly TextBox _logView = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180 };
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _gameOperation;
    private bool _closeAfterGame;
    private readonly bool _smoke;
    private readonly string? _screenshotDirectory;

    private readonly TextBox _jvmArguments = new() { Watermark = "例如 -XX:+UseG1GC" };
    private readonly TextBox _gameArguments = new() { Watermark = "额外的 Minecraft 参数" };
    private readonly TextBox _curseKey = new() { PasswordChar = '●', Watermark = "CurseForge API Key（可选）" };
    private readonly TextBox _homepage = new() { Watermark = "本地 Custom.xaml 或主页 JSON 文件" };
    private readonly TextBox _background = new() { Watermark = "背景图片路径（留空使用原版彩色背景）" };
    private readonly TextBox _accent = new() { IsVisible = false };
    private readonly ComboBox _downloadSource = new()
    {
        ItemsSource = new[] { "自动（官方优先，失败时回退镜像）", "仅官方源", "仅镜像（BMCLAPI）" },
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
    };
    private readonly TextBox _interfaceFont = new() { Watermark = "留空跟随系统字体" };
    private readonly ComboBox _theme = new() { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly CheckBox _showAllVersions = new() { Content = "显示快照、远古版和测试版" };
    private readonly CheckBox _colorfulBackground = new() { Content = "使用彩色背景" };
    private readonly ComboBox _logoType = new()
    {
        ItemsSource = new[] { "默认 PCL Logo", "文本", "无" }, SelectedIndex = 0,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
    };
    private readonly TextBox _logoText = new() { Watermark = "标题栏文本（例如 Point Cloud Library）" };
    private readonly ComboBox _themeColor = new()
    {
        ItemsSource = new[] { "猫蓝（默认）", "天蓝", "崩蓝", "HMCL 蓝" }, SelectedIndex = 0,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
    };
    private readonly CheckBox _systemTitlebar = new() { Content = "使用系统窗口装饰（关闭则与原版一致，使用自绘标题栏）" };
    private readonly ComboBox _loaderKind = new() { ItemsSource = Enum.GetValues<LoaderKind>(), SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly ComboBox _loaderVersions = new() { PlaceholderText = "加载器版本", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBox _newInstance = new() { Watermark = "新实例名称" };
    private readonly TextBox _packName = new() { Watermark = "导入后的实例名称" };
    private readonly TextBox _search = new() { Watermark = "搜索 Mod、整合包或资源，按 Enter 开始搜索" };
    private readonly ComboBox _catalogSource = new() { ItemsSource = Enum.GetValues<CatalogSource>(), SelectedIndex = 0 };
    private readonly ComboBox _contentKind = new() { ItemsSource = new[] { "mod", "modpack", "resourcepack", "shader" }, SelectedIndex = 0 };
    private readonly ListBox _projects = new() { MinHeight = 180 };
    private readonly ComboBox _contentVersions = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "选择资源版本" };
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
}
