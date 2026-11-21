using System.Diagnostics;
using System.Text;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using PCL.Portable;

namespace PCL.Linux;

internal sealed partial class MainWindow
{
    /// <summary>
    /// 处理外部参数：双击 .mrpack、文件管理器打开的其他文件、或者 pcl-ce:// 链接。
    /// 之所以支持这些，是因为桌面集成会在启动时把本程序注册为对应 MIME 与协议的默认打开方式。
    /// </summary>
    internal void HandleExternalArguments(string[] args)
    {
        if (_smoke || args.Length == 0) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleExternalArguments(args));
            return;
        }
        _ = ProcessExternalArgumentsAsync(args);
    }

    private async Task ProcessExternalArgumentsAsync(string[] args)
    {
        foreach (var raw in args)
        {
            var argument = raw.Trim();
            if (argument.Length == 0 || argument.StartsWith("--", StringComparison.Ordinal)) continue;
            try
            {
                if (argument.StartsWith("pcl-ce://", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleProtocolAsync(argument);
                    continue;
                }
                if (File.Exists(argument))
                {
                    await HandleFileAsync(argument);
                    continue;
                }
                if (Directory.Exists(argument))
                {
                    _directory.Text = argument;
                    SaveSettings();
                    RefreshInstalled();
                    Navigate("instances");
                    AppendLog("已切换游戏目录：" + argument);
                    continue;
                }
                AppendLog("无法识别的启动参数：" + argument);
            }
            catch (Exception ex)
            {
                AppendLog("打开外部内容失败：" + ex.Message);
                AppLog.Error("打开外部内容失败", ex);
            }
        }
    }

    private async Task HandleFileAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".mrpack":
            case ".zip":
                Navigate("packs");
                _packName.Text = Path.GetFileNameWithoutExtension(path);
                await RunAsync(token => ImportPackPathAsync(path, token));
                break;
            case ".jar":
                if (_installed.SelectedItem is null)
                {
                    Navigate("mods");
                    throw new InvalidOperationException("请先在“版本管理”中选择一个版本，再双击或导入模组文件。");
                }
                var directory = await GameLauncher.GetInstanceDirectoryAsync(SelectedId, CancellationToken.None);
                LocalMods.Import(path, directory);
                Navigate("mods");
                AppendLog("已导入模组：" + Path.GetFileName(path));
                break;
            case ".mcworld":
            case ".mcpack":
            case ".mctemplate":
            case ".mcaddon":
                throw new InvalidOperationException("这是 Minecraft 基岩版的存档/资源包格式，PCL-CE-Linux 目前只支持 Java 版。");
            case ".minecraft":
                _directory.Text = Path.GetDirectoryName(path) ?? path;
                SaveSettings();
                RefreshInstalled();
                Navigate("instances");
                break;
            default:
                OpenLocal(path);
                break;
        }
    }

    /// <summary>
    /// 支持 pcl-ce:// 协议：
    /// <list type="bullet">
    /// <item><c>pcl-ce://import?url=https%3A%2F%2F…/pack.mrpack</c> 下载并导入整合包</item>
    /// <item><c>pcl-ce://launch?version=1.21.1&amp;start=1</c> 选择并启动版本</item>
    /// <item><c>pcl-ce://page/download</c> 打开页面（launch/download/settings/more/packs/lobby/instances/logs）</item>
    /// <item><c>pcl-ce://open?path=/home/…</c> 用系统默认程序打开本地路径</item>
    /// </list>
    /// </summary>
    private async Task HandleProtocolAsync(string value)
    {
        var uri = new Uri(value);
        var action = uri.Host.ToLowerInvariant();
        var query = HttpUtility.ParseQueryString(uri.Query);
        switch (action)
        {
            case "import":
            case "modpack":
            {
                var url = query["url"];
                if (string.IsNullOrWhiteSpace(url))
                {
                    Navigate("packs");
                    await RunAsync(ImportPackAsync);
                    return;
                }
                var target = Path.Combine(LinuxPaths.DownloadsCache, "protocol-" + Guid.NewGuid().ToString("N") + ".mrpack");
                await RunAsync(async token =>
                {
                    await new VerifiedDownloads(_client).FetchAsync(url, target, null, token, Progress);
                    await ImportPackPathAsync(target, token);
                });
                break;
            }
            case "launch":
            {
                var version = query["version"];
                if (!string.IsNullOrWhiteSpace(version))
                {
                    RefreshInstalled();
                    _installed.SelectedItem = version;
                }
                Navigate("launch");
                if (query["start"] is "1" or "true") await RunAsync(LaunchAsync);
                break;
            }
            case "page":
            {
                var page = uri.AbsolutePath.Trim('/').ToLowerInvariant();
                Navigate(page switch
                {
                    "" or "launch" or "start" => "launch",
                    "download" or "install" => "download",
                    "settings" or "config" => "settings",
                    "packs" or "modpack" => "packs",
                    "mods" => "mods",
                    "lobby" or "room" => "lobby",
                    "logs" => "logs",
                    _ => "more"
                });
                break;
            }
            case "open":
            {
                var path = query["path"];
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("pcl-ce://open 缺少 path 参数");
                OpenLocal(path);
                break;
            }
            default:
                throw new NotSupportedException("不支持的 pcl-ce:// 操作：" + action);
        }
    }

    /// <summary>渲染界面截图（浅色/深色/2x），用于验证 Linux 下的字体、主题与 HiDPI。开发用 --screenshot 触发。</summary>
    internal async Task CaptureScreensAsync(string directory)
    {
        var target = Path.GetFullPath(directory);
        Directory.CreateDirectory(target);
        await _navigationTask;
        await Task.Delay(600);
        var originalTheme = RequestedThemeVariant;
        var originalSetting = _settings.Theme;
        try
        {
            foreach (var dark in new[] { false, true })
            {
                _settings.Theme = dark ? "dark" : "light";
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                ApplyAppearance();
                await Task.Delay(500);
                foreach (var scale in new[] { 1.0, 2.0 })
                {
                    var suffix = scale > 1.5 ? "@2x" : "";
                    var path = Path.Combine(target, $"pcl-ce-{(dark ? "dark" : "light")}{suffix}.png");
                    CaptureTo(path, scale);
                    Console.WriteLine("SCREENSHOT: " + path);
                    AppLog.Info("界面截图：" + path);
                }
            }
            // 逐页截图，便于人工核对新页面（Linux 集成、数据清理、系统信息）。
            _settings.Theme = "light";
            ApplyAppearance();
            foreach (var page in new[] { "download", "settings", "linux", "wipe", "diagnostics", "more" })
            {
                Navigate(page);
                await _navigationTask;
                await Task.Delay(250);
                var path = Path.Combine(target, $"page-{page}.png");
                CaptureTo(path, 1.0);
                Console.WriteLine("SCREENSHOT: " + path);
            }
            Navigate("launch");
            await _navigationTask;
        }
        finally
        {
            _settings.Theme = originalSetting;
            RequestedThemeVariant = originalTheme;
            ApplyAppearance();
        }
    }

    private void CaptureTo(string path, double scale)
    {
        var width = Math.Max(100, (int)Math.Ceiling(ClientSize.Width * scale));
        var height = Math.Max(100, (int)Math.Ceiling(ClientSize.Height * scale));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
        bitmap.Render(_windowFrame);
        bitmap.Save(path);
    }
}
