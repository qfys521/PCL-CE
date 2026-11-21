using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// 桌面集成与系统诊断页面。全部控件在代码中构建，遵循现有 MainWindow 的卡片 / 行 / 按钮风格。
/// </summary>
internal sealed partial class MainWindow
{
    private readonly CheckBox _desktopIntegrationSetting = new() { Content = "启用桌面集成（桌面入口、图标与 MIME 关联）" };
    private readonly CheckBox _notificationsSetting = new() { Content = "任务完成或出错时发送系统通知（org.freedesktop.Notifications）" };
    private readonly CheckBox _portalFilePickerSetting = new() { Content = "文件选择器优先使用 XDG Desktop Portal（失败时回退 GTK）" };
    private TextBlock? _desktopIntegrationStatusText;
    private TextBlock? _desktopIntegrationIconText;
    private TextBox? _desktopDiagnosticsOutput;
    private StackPanel? _diagnosticsList;
    private string _diagnosticsReport = "";
    private bool _linuxSettingsControlsReady;

    /// <summary>桌面集成 + 系统信息页面（建议注册为设置分组下的“桌面集成”）。</summary>
    internal Control LinuxIntegrationPage()
    {
        _desktopIntegrationStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        _desktopIntegrationIconText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _desktopIntegrationIconText.Classes.Add("muted");

        _desktopIntegrationSetting.IsChecked = _settings.EnableDesktopIntegration;
        _notificationsSetting.IsChecked = _settings.EnableNotifications;
        _portalFilePickerSetting.IsChecked = _settings.EnablePortalFilePicker;
        _linuxSettingsControlsReady = true;

        _desktopDiagnosticsOutput = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            FontSize = 12,
            FontFamily = new FontFamily("monospace"),
            Watermark = "点击“运行系统诊断”后在此显示结果"
        };
        if (_diagnosticsReport.Length > 0) _desktopDiagnosticsOutput.Text = _diagnosticsReport;

        var integrationCard = Card("桌面集成",
            _desktopIntegrationStatusText,
            _desktopIntegrationIconText,
            _desktopIntegrationSetting,
            _notificationsSetting,
            _portalFilePickerSetting,
            Actions(
                Button("安装桌面入口", token => EnsureDesktopIntegrationAsync(false, token)),
                Button("重新安装", token => EnsureDesktopIntegrationAsync(true, token)),
                Button("卸载", UninstallDesktopIntegrationAsync),
                Button("保存设置", _ => { SaveSettings(); return Task.CompletedTask; })),
            Paragraph("安装会写入 $XDG_DATA_HOME/applications/pcl-ce.desktop、hicolor 图标（16–512 像素）与 MIME 定义（.mrpack、.mcworld、.mcpack 等），并刷新 update-mime-database / update-desktop-database；卸载只删除这些文件。所有路径均来自 LinuxPaths。"),
            Paragraph("系统通知由桌面通知中心显示，启动器不会创建自制通知窗口；可用性可在“系统诊断”中查看。"));

        var diagnosticsCard = Card("系统信息",
            Paragraph("收集会话（是否经 XWayland 运行）、OpenGL / Vulkan、中文字体、Java、文件选择器、通知、输入法、缩放与 XDG 路径等真实信息，不联网。"),
            Actions(
                Button("运行系统诊断", RunDiagnosticsAsync),
                Button("复制报告", CopyDiagnosticsReportAsync),
                Button("打开日志目录", OpenLogDirectoryAsync)),
            _desktopDiagnosticsOutput);

        var page = PageStack();
        page.Children.Add(integrationCard);
        page.Children.Add(diagnosticsCard);
        RefreshDesktopIntegrationStatus();
        // 页面被缓存复用，每次重新显示时刷新一次安装状态（只读检查）。
        page.AttachedToVisualTree += (_, _) => RefreshDesktopIntegrationStatus();
        return Scroll(page);
    }

    /// <summary>完整系统诊断页面（建议注册为“更多”分组下的“系统诊断”）。</summary>
    internal Control DiagnosticsPage()
    {
        _diagnosticsList = new StackPanel { Spacing = 10 };
        var card = Card("系统诊断",
            Paragraph("逐项检查运行环境并给出可操作的修复建议。诊断不会修改任何文件，也不会启动游戏。"),
            Actions(
                Button("刷新", RunDiagnosticsAsync),
                Button("复制报告", CopyDiagnosticsReportAsync),
                Button("打开日志目录", OpenLogDirectoryAsync)));
        var page = PageStack();
        page.Children.Add(card);
        page.Children.Add(_diagnosticsList);
        // 冒烟测试不主动收集（避免测试期间访问 D-Bus / 外部命令），正常使用时进入页面即刷新。
        if (!_smoke)
            Dispatcher.UIThread.Post(() => { if (_operation is null) _ = RunAsync(RunDiagnosticsAsync); }, DispatcherPriority.Background);
        return Scroll(page);
    }

    /// <summary>由 SaveExtendedSettings 调用，把三个 Linux 集成开关写回设置并保存。</summary>
    internal void SaveLinuxIntegrationSettings()
    {
        if (!_linuxSettingsControlsReady) return;
        _settings.EnableDesktopIntegration = _desktopIntegrationSetting.IsChecked == true;
        _settings.EnableNotifications = _notificationsSetting.IsChecked == true;
        _settings.EnablePortalFilePicker = _portalFilePickerSetting.IsChecked == true;
        _settings.Save();
    }

    private void RefreshDesktopIntegrationStatus()
    {
        if (_desktopIntegrationStatusText is null || _desktopIntegrationIconText is null) return;
        var status = DesktopIntegration.Inspect();
        var icons = CountInstalledIcons();
        if (status.Problem is { } problem)
        {
            _desktopIntegrationStatusText.Text = (status.Installed ? "状态：已安装，但需要注意——" : "状态：未安装——") + problem;
            _desktopIntegrationStatusText.Foreground = new SolidColorBrush(Color.Parse("#C46A1B"));
        }
        else
        {
            _desktopIntegrationStatusText.Text = "状态：已安装，桌面入口与当前程序路径一致。";
            _desktopIntegrationStatusText.Foreground = new SolidColorBrush(Color.Parse("#3F8F4A"));
        }
        _desktopIntegrationIconText.Text =
            $"图标：{icons}/8 个尺寸 · 桌面入口：{status.DesktopFile}" +
            (status.ExecPath is { } exec ? $" · 程序路径：{exec}" : " · 程序路径未知");
    }

    private static int CountInstalledIcons()
    {
        var count = 0;
        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512 })
        {
            try
            {
                if (File.Exists(Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}", "apps", "pcl-ce.png"))) count++;
            }
            catch (Exception) { }
        }
        return count;
    }

    private async Task EnsureDesktopIntegrationAsync(bool force, CancellationToken token)
    {
        var progress = new Progress<string>(message =>
        {
            AppendLog(message);
            if (_desktopIntegrationStatusText is not null) _desktopIntegrationStatusText.Text = message;
        });
        var status = await DesktopIntegration.EnsureInstalledAsync(force, progress, token);
        RefreshDesktopIntegrationStatus();
        if (status.Problem is { } problem) throw new IOException(problem);
        AppendLog("桌面集成已安装：" + status.DesktopFile);
        await NotifyIfEnabledAsync("PCL-CE-Linux", "桌面入口已安装，可在应用菜单中找到 PCL-CE-Linux。");
    }

    private async Task UninstallDesktopIntegrationAsync(CancellationToken token)
    {
        if (!await ConfirmActionAsync("卸载桌面集成", "将删除用户级桌面入口、应用图标、MIME 定义与安装状态，并刷新桌面数据库。不会影响游戏文件或系统目录，是否继续？"))
            return;
        var removed = await DesktopIntegration.UninstallAsync();
        RefreshDesktopIntegrationStatus();
        if (!removed) throw new IOException("卸载桌面集成失败，详情见日志。");
        AppendLog("桌面集成已卸载。");
        await NotifyIfEnabledAsync("PCL-CE-Linux", "桌面入口已卸载。");
    }

    private async Task NotifyIfEnabledAsync(string summary, string body)
    {
        if (_notificationsSetting.IsChecked != true) return;
        try { await LinuxNotifications.NotifyAsync(summary, body); }
        catch (Exception ex) { AppendLog("发送系统通知失败：" + ex.Message); }
    }

    private async Task RunDiagnosticsAsync(CancellationToken token)
    {
        if (_desktopDiagnosticsOutput is not null) _desktopDiagnosticsOutput.Text = "正在收集系统信息（最多约 5 秒）…";
        var items = await SystemDiagnostics.CollectAsync(token);
        _diagnosticsReport = SystemDiagnostics.FormatReport(items);
        if (_desktopDiagnosticsOutput is not null) _desktopDiagnosticsOutput.Text = _diagnosticsReport;
        RenderDiagnosticsList(items);
        var critical = SystemDiagnostics.CriticalIssues(items);
        AppendLog(critical.Count == 0
            ? "系统诊断完成：未发现需要处理的问题。"
            : $"系统诊断完成：{critical.Count} 项需要处理——" + string.Join("、", critical.Select(item => item.Name)));
        if (critical.Count > 0 && _notificationsSetting.IsChecked == true)
        {
            var summary = $"PCL-CE-Linux 诊断发现 {critical.Count} 项问题";
            var body = string.Join("、", critical.Select(item => item.Name)) + "；详情可在“系统诊断”页面查看。";
            _ = LinuxNotifications.NotifyErrorAsync(summary, body);
        }
    }

    private void RenderDiagnosticsList(IReadOnlyList<DiagnosticItem> items)
    {
        if (_diagnosticsList is null) return;
        _diagnosticsList.Children.Clear();
        foreach (var item in items)
        {
            var body = new StackPanel { Spacing = 6 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 1, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(StatusColor(item.Status)),
                Child = new TextBlock { Text = item.Status, FontSize = 11, Foreground = Brushes.White }
            };
            Grid.SetColumn(badge, 1);
            header.Children.Add(badge);
            body.Children.Add(header);
            body.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var card = new Border { Child = body };
            card.Classes.Add("card");
            _diagnosticsList.Children.Add(card);
        }
    }

    private static Color StatusColor(string status) => status switch
    {
        SystemDiagnostics.Normal => Color.Parse("#3F8F4A"),
        SystemDiagnostics.Warning => Color.Parse("#C46A1B"),
        SystemDiagnostics.Unavailable => Color.Parse("#B23A32"),
        _ => Color.Parse("#4A6E9E")
    };

    private async Task CopyDiagnosticsReportAsync(CancellationToken token)
    {
        var report = _diagnosticsReport;
        if (string.IsNullOrWhiteSpace(report))
        {
            report = SystemDiagnostics.FormatReport(await SystemDiagnostics.CollectAsync(token));
            _diagnosticsReport = report;
            if (_desktopDiagnosticsOutput is not null) _desktopDiagnosticsOutput.Text = report;
        }
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(report);
        AppendLog("系统诊断报告已复制到剪贴板。");
    }

    private async Task OpenLogDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            LinuxPaths.EnsureDirectory(LinuxPaths.LogsHome,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception) { }
        OpenLocal(LinuxPaths.LogsHome);
        await Task.CompletedTask;
    }
}
