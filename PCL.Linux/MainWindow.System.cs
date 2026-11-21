using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PCL.Portable;

namespace PCL.Linux;

internal sealed partial class MainWindow
{
    /// <summary>从发布目录加载窗口图标（hicolor / PCL 图标）。</summary>
    private static WindowIcon? LoadWindowIcon()
    {
        foreach (var name in new[] { Path.Combine("Images", "icon.png"), Path.Combine("Images", "Heads", "Logo-CE.png"), "pcl-ce.png" })
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                return new WindowIcon(stream);
            }
            catch (Exception ex) { AppLog.Warn($"加载窗口图标失败（{name}）：{ex.Message}"); }
        }
        return null;
    }

    /// <summary>应用界面字体：优先用户设置，其次系统默认，最后内置 Noto Sans CJK 兜底。</summary>
    private void ApplyInterfaceFont()
    {
        try
        {
            var family = LinuxFonts.Resolve(_settings.InterfaceFont);
            TextElement.SetFontFamily(this, new FontFamily(family));
        }
        catch (Exception ex) { AppendLog("应用界面字体失败：" + ex.Message); }
    }

    /// <summary>通过 org.freedesktop.Notifications 发送系统通知；不使用自制弹窗。</summary>
    private void Notify(string title, string body, bool error = false)
    {
        if (!_settings.EnableNotifications) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (error) await LinuxNotifications.NotifyErrorAsync(title, body);
                else await LinuxNotifications.NotifyAsync(title, body);
            }
            catch (Exception ex) { AppLog.Warn("发送系统通知失败：" + ex.Message); }
        });
    }

    /// <summary>启动时做一次系统诊断，并在缺少 OpenGL/Mesa/显示时向用户给出可读提示（而不是直接崩溃）。</summary>
    private async Task RunStartupDiagnosticsAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var items = await SystemDiagnostics.CollectAsync(timeout.Token);
            var critical = SystemDiagnostics.CriticalIssues(items);
            if (critical.Count == 0) return;
            var text = string.Join("\n", critical.Select(item => $"{item.Name}：{item.Detail}"));
            AppLog.Warn("系统诊断发现问题：" + text.Replace('\n', ' '));
            AppendLog("系统环境提示：" + text);
            Notify("PCL-CE-Linux 运行环境提示", text, error: true);
            AddLaunchWarning(text);
        }
        catch (Exception ex) { AppLog.Warn("启动诊断失败：" + ex.Message); }
    }

    /// <summary>把环境提示卡片放到主页顶部（保留原有主页内容）。</summary>
    private void AddLaunchWarning(string text)
    {
        if (!_pages.TryGetValue("launch", out var page)) return;
        var host = page.Right is ScrollViewer { Content: StackPanel stack } ? stack : null;
        if (host is null) return;
        var card = Card("运行环境提示", Paragraph(text),
            Actions(NavigationButton("系统信息", () => Navigate("diagnostics"), "pcl")));
        if (host.Children.Count > 0) host.Children.Insert(0, card); else host.Children.Add(card);
        _pages["launch"] = (page.Left, page.Right);
        if (_currentPage == "launch") Navigate("launch");
    }

    /// <summary>窗口装饰是否使用系统原生标题栏（Linux 适配；可在设置中切换回应用内标题栏）。</summary>
    private void ApplyWindowDecorations()
    {
        SystemDecorations = SystemDecorations.Full;
        TransparencyLevelHint = [];
    }
}

/// <summary>Linux 字体检测：没有内置字体时也不会出现豆腐块（内置/系统 Noto Sans CJK 兜底）。</summary>
public static class LinuxFonts
{
    public const string Bundled = "Noto Sans CJK SC";
    private static string? _system;

    /// <summary>返回 Avalonia 字体回退列表：<c>系统字体, Noto Sans CJK SC, Noto Sans SC, sans-serif</c>。</summary>
    public static string Resolve(string configured)
    {
        var fallbacks = new[] { Bundled, "Noto Sans SC", "Noto Sans CJK TC", "WenQuanYi Micro Hei", "sans-serif" };
        var primary = !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : DetectSystemFont();
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) all.Add(primary);
        foreach (var fallback in fallbacks)
            if (!all.Contains(fallback, StringComparer.OrdinalIgnoreCase)) all.Add(fallback);
        return string.Join(", ", all);
    }

    /// <summary>通过 fontconfig 的 fc-match 读取系统默认无衬线字体；失败时返回内置字体名。</summary>
    public static string DetectSystemFont()
    {
        if (_system is not null) return _system;
        try
        {
            var start = new ProcessStartInfo("fc-match")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("%{family[0]}");
            start.ArgumentList.Add("sans-serif");
            using var process = Process.Start(start);
            if (process is not null && process.WaitForExit(1500) && process.ExitCode == 0)
            {
                var family = process.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(family) && family.Length < 96 && !family.Contains('\n'))
                    return _system = family;
            }
        }
        catch (Exception) { }
        return _system = Bundled;
    }

    public static bool HasChineseFont()
    {
        try
        {
            var start = new ProcessStartInfo("fc-match")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("%{family}");
            start.ArgumentList.Add(":lang=zh-cn");
            using var process = Process.Start(start);
            if (process is not null && process.WaitForExit(1500) && process.ExitCode == 0)
                return !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd());
        }
        catch (Exception) { }
        return false;
    }
}
