using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Minecraft.Java.Scanner;
using PCL.Portable;
using Tmds.DBus.Protocol;

namespace PCL.Linux;

/// <summary>一条系统诊断结果。Status 取值见 <see cref="SystemDiagnostics"/> 的常量。</summary>
public sealed record DiagnosticItem(string Name, string Status, string Detail, bool Problem);

/// <summary>
/// 收集 Linux 桌面会话的真实环境信息，供“系统信息 / 系统诊断”页面展示。
/// 不依赖任何外部命令也能工作；fc-match、gsettings、xrdb 等仅在存在时以 2 秒超时调用。
/// 全程异常安全，总耗时约 5 秒以内。
/// </summary>
public static class SystemDiagnostics
{
    public const string Normal = "正常";
    public const string Warning = "警告";
    public const string Unavailable = "不可用";
    public const string Info = "信息";

    private const string NotificationsService = "org.freedesktop.Notifications";
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string IbusService = "org.freedesktop.IBus";
    private const string FcitxService = "org.fcitx.Fcitx5";

    /// <summary>采集全部诊断项（并发执行每个检查，单项超时不会拖垮整体）。</summary>
    public static async Task<IReadOnlyList<DiagnosticItem>> CollectAsync(CancellationToken token)
    {
        using var connection = await TryConnectAsync(token).ConfigureAwait(false);
        var checks = new (string Name, Func<CancellationToken, Task<DiagnosticItem>> Run)[]
        {
            ("会话", CheckSessionAsync),
            ("OpenGL", CheckOpenGlAsync),
            ("Vulkan", CheckVulkanAsync),
            ("字体", CheckFontsAsync),
            ("Java", CheckJavaAsync),
            ("文件选择器", cancellation => CheckFilePickerAsync(connection, cancellation)),
            ("通知", CheckNotificationsAsync),
            ("输入法", cancellation => CheckInputMethodAsync(connection, cancellation)),
            ("缩放/主题", cancellation => CheckScalingThemeAsync(connection, cancellation)),
            ("路径", CheckPathsAsync)
        };

        var tasks = checks.Select(check => Task.Run(() => RunCheckAsync(check.Name, check.Run, token), token)).ToArray();
        try
        {
            return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var completed = tasks.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result).ToList();
            completed.Add(new DiagnosticItem("诊断", Warning, "部分检查超过 6 秒仍未完成，已跳过；可稍后点击“刷新”重试。", false));
            return completed;
        }
    }

    /// <summary>把诊断结果格式化为可复制的纯文本报告。</summary>
    public static string FormatReport(IReadOnlyList<DiagnosticItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PCL-CE-Linux 系统诊断报告");
        builder.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"运行环境：{RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} | .NET {Environment.Version}");
        builder.AppendLine(new string('=', 52));
        foreach (var item in items)
        {
            builder.Append('[').Append(item.Status).Append("] ").AppendLine(item.Name);
            foreach (var line in item.Detail.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                builder.Append("  ").AppendLine(line.Trim());
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>需要用户处理的问题项（Problem 为 true）。</summary>
    public static IReadOnlyList<DiagnosticItem> CriticalIssues(IReadOnlyList<DiagnosticItem> items) =>
        items.Where(item => item.Problem).ToArray();

    private static async Task<DiagnosticItem> RunCheckAsync(string name, Func<CancellationToken, Task<DiagnosticItem>> check, CancellationToken token)
    {
        try
        {
            return await check(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiagnosticItem(name, Warning, "检查失败：" + ex.Message, false);
        }
    }

    private static async Task<Connection?> TryConnectAsync(CancellationToken token)
    {
        try
        {
            var address = Address.Session;
            if (string.IsNullOrWhiteSpace(address)) return null;
            var connection = new Connection(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                return connection;
            }
            catch (Exception)
            {
                connection.Dispose();
                return null;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 会话

    private static Task<DiagnosticItem> CheckSessionAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
                      ?? Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP") ?? "未知";
        var wayland = LinuxPaths.IsWaylandSession;
        var displaySet = !string.IsNullOrWhiteSpace(display);
        var xwayland = wayland && ProcessRunning("Xwayland");

        var details = new List<string>();
        details.Add(sessionType.Length > 0 ? "会话类型 " + sessionType : "会话类型未知");
        details.Add(displaySet ? "DISPLAY=" + display : "DISPLAY 未设置");
        if (!string.IsNullOrWhiteSpace(waylandDisplay)) details.Add("WAYLAND_DISPLAY=" + waylandDisplay);
        details.Add("桌面环境 " + desktop);

        if (wayland)
        {
            details.Add(xwayland
                ? "检测到 Xwayland 进程：Avalonia 11.3 没有原生 Wayland 后端，程序通过 XWayland 以 X11 协议运行"
                : "Wayland 会话；未在进程列表中确认 Xwayland，但设置 DISPLAY 时 Avalonia 仍通过 XWayland 以 X11 协议运行");
            details.Add("因此输入法候选窗、全局缩放与剪贴板都按 X11 语义工作；这是已知限制，不是故障");
        }
        else if (displaySet)
        {
            details.Add("Avalonia 使用原生 X11 后端");
        }

        if (!displaySet)
        {
            details.Add("没有可用的 X 显示，无法创建窗口。请从图形桌面会话启动；Wayland 用户需要安装 xorg-xwayland，并确保 DISPLAY 已设置");
            return new DiagnosticItem("会话", Warning, string.Join("；", details), true);
        }
        return new DiagnosticItem("会话", Normal, string.Join("；", details), false);
    }, token);

    // ---------------------------------------------------------------- OpenGL

    private static Task<DiagnosticItem> CheckOpenGlAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var gl = TryLoadLibrary("libGL.so.1") || KnownLibraryFile("libGL.so.1");
        var egl = TryLoadLibrary("libEGL.so.1") || KnownLibraryFile("libEGL.so.1");
        var driEntries = ListDirectory("/dev/dri");
        var renderNodes = driEntries.Where(name => name.StartsWith("renderD", StringComparison.Ordinal)).ToArray();
        var cardNodes = driEntries.Where(name => name.StartsWith("card", StringComparison.Ordinal)).ToArray();
        var nvidia = SafeFileExists("/proc/driver/nvidia/version") || DirectoryExists("/sys/module/nvidia");
        var amdgpu = DirectoryExists("/sys/module/amdgpu");
        var intel = DirectoryExists("/sys/module/i915") || DirectoryExists("/sys/module/xe");
        var mesa = TryLoadLibrary("libGLX_mesa.so.0") || TryLoadLibrary("libEGL_mesa.so.0") || DriDrivers().Count > 0;

        var details = new List<string>
        {
            gl ? "libGL.so.1 可加载" : "缺少 libGL.so.1",
            egl ? "libEGL.so.1 可加载" : "缺少 libEGL.so.1",
            cardNodes.Length + renderNodes.Length > 0
                ? $"DRM 设备：{string.Join("、", cardNodes.Concat(renderNodes))}"
                : "没有 /dev/dri/card* 或 renderD* 设备节点"
        };
        var drivers = new List<string>();
        if (nvidia) drivers.Add("NVIDIA");
        if (amdgpu) drivers.Add("AMDGPU");
        if (intel) drivers.Add("Intel i915/xe");
        if (mesa) drivers.Add("Mesa");
        details.Add(drivers.Count > 0 ? "显卡驱动：" + string.Join("、", drivers) : "未识别到常见显卡驱动模块");
        var driDrivers = DriDrivers();
        if (driDrivers.Count > 0) details.Add("DRI 驱动：" + string.Join("、", driDrivers.Take(4)));

        if (!gl)
        {
            details.Add("Minecraft 需要 OpenGL 上下文。请安装 Mesa（Debian/Ubuntu：sudo apt install libgl1-mesa-dri libglx-mesa0；Fedora：mesa-libGL mesa-dri-drivers）");
            return new DiagnosticItem("OpenGL", Warning, string.Join("；", details), true);
        }
        if (cardNodes.Length == 0 && renderNodes.Length == 0)
        {
            details.Add("没有 GPU 设备节点时通常会回退到 llvmpipe 软件渲染，游戏帧率会明显偏低；请检查显卡驱动、虚拟机 GPU 直通或 /dev/dri 权限");
            return new DiagnosticItem("OpenGL", Warning, string.Join("；", details), false);
        }
        return new DiagnosticItem("OpenGL", Normal, string.Join("；", details), false);
    }, token);

    // ---------------------------------------------------------------- Vulkan

    private static Task<DiagnosticItem> CheckVulkanAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var loaded = TryLoadLibrary("libvulkan.so.1") || KnownLibraryFile("libvulkan.so.1");
        var icds = new List<string>();
        foreach (var directory in new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d", "/usr/local/share/vulkan/icd.d" })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                icds.AddRange(Directory.GetFiles(directory, "*.json").Select(Path.GetFileName).OfType<string>());
            }
            catch (Exception) { }
        }
        var devices = loaded && icds.Count > 0 ? EnumerateVulkanDevices() : [];
        var details = new List<string>
        {
            loaded ? "libvulkan.so.1 可加载" : "缺少 libvulkan.so.1",
            icds.Count > 0 ? "ICD：" + string.Join("、", icds) : "未找到 Vulkan ICD（/usr/share/vulkan/icd.d）"
        };
        if (devices.Count > 0) details.Add("物理设备：" + string.Join("、", devices));
        else if (loaded && icds.Count > 0) details.Add("未能枚举到 Vulkan 物理设备（驱动可能未正确加载）");

        if (!loaded)
        {
            details.Add("原版 Minecraft 使用 OpenGL，不需要 Vulkan；仅在使用 VulkanMod 等模组时需要。安装：sudo apt install libvulkan1 mesa-vulkan-drivers");
            return new DiagnosticItem("Vulkan", Info, string.Join("；", details), false);
        }
        if (icds.Count == 0)
            return new DiagnosticItem("Vulkan", Info, string.Join("；", details), false);
        if (devices.Count == 0)
            return new DiagnosticItem("Vulkan", Warning, string.Join("；", details), false);
        return new DiagnosticItem("Vulkan", Normal, string.Join("；", details), false);
    }, token);

    // ---------------------------------------------------------------- 字体

    private static async Task<DiagnosticItem> CheckFontsAsync(CancellationToken token)
    {
        var cjkNames = new[]
        {
            "NotoSansCJK", "NotoSansSC", "NotoSansHans", "NotoSansTC", "NotoSerifCJK",
            "SourceHanSans", "SourceHanSerif", "WenQuanYi", "DroidSansFallback", "NotoSansMonoCJK"
        };
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        var bundledDirectory = string.IsNullOrWhiteSpace(appDir) ? null : Path.Combine(appDir, "usr", "share", "fonts");
        var directories = new List<string>();
        if (bundledDirectory is not null) directories.Add(bundledDirectory);
        directories.Add("/usr/share/fonts");
        directories.Add("/usr/local/share/fonts");
        try
        {
            directories.Add(Path.Combine(LinuxPaths.Home, ".local", "share", "fonts"));
            directories.Add(Path.Combine(LinuxPaths.Home, ".fonts"));
        }
        catch (Exception) { }

        var found = new List<string>();
        var bundled = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var scanned = 0;
        foreach (var directory in directories.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(2)) break;
            if (!DirectoryExists(directory)) continue;
            try
            {
                foreach (var file in EnumerateFilesSafe(directory, token, stopwatch))
                {
                    scanned++;
                    var fileName = Path.GetFileName(file);
                    if (!cjkNames.Any(name => fileName.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;
                    var list = bundledDirectory is not null && file.StartsWith(bundledDirectory, StringComparison.Ordinal) ? bundled : found;
                    if (!list.Contains(fileName)) list.Add(fileName);
                    if (found.Count + bundled.Count >= 6) break;
                }
            }
            catch (Exception) { }
            if (found.Count + bundled.Count >= 6) break;
        }

        var fontconfigMatch = await QueryAsync("fc-match", ["-f", "%{file}", "sans-serif:lang=zh-cn"],
            TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        var details = new List<string>();
        details.Add($"扫描 {scanned} 个字体文件");
        if (bundled.Count > 0) details.Add("AppImage 内置中文字体：" + string.Join("、", bundled));
        if (found.Count > 0) details.Add("系统中文字体：" + string.Join("、", found));
        if (!string.IsNullOrWhiteSpace(fontconfigMatch))
            details.Add("fontconfig 中文首选：" + fontconfigMatch.Trim());

        if (found.Count > 0 || bundled.Count > 0)
            return new DiagnosticItem("字体", Normal, string.Join("；", details), false);
        details.Add("未找到 Noto Sans CJK / 思源黑体 / 文泉驿等中文字体，界面和游戏内中文可能显示为方框。安装：sudo apt install fonts-noto-cjk（或 fonts-wqy-microhei）");
        return new DiagnosticItem("字体", Warning, string.Join("；", details), true);
    }

    // ---------------------------------------------------------------- Java

    private static async Task<DiagnosticItem> CheckJavaAsync(CancellationToken token)
    {
        var paths = new List<string>();
        try
        {
            new LinuxJavaScanner().Scan(paths);
        }
        catch (Exception ex)
        {
            return new DiagnosticItem("Java", Warning, "扫描 Java 失败：" + ex.Message, false);
        }
        var detected = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList();
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JAVA_HOME")))
            details.Add("JAVA_HOME=" + Environment.GetEnvironmentVariable("JAVA_HOME"));
        details.Add($"扫描到 {detected.Count} 个 Java 可执行文件");

        if (detected.Count > 0)
        {
            var descriptions = new List<string>();
            try
            {
                var parseTasks = detected.Take(3)
                    .Select(path => Task.Run(() => (Path: path, Result: new CommandJavaParser().Parse(path)), token))
                    .ToArray();
                var parsed = await Task.WhenAll(parseTasks).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                foreach (var (path, result) in parsed)
                {
                    if (result.IsSuccess)
                    {
                        var java = result.Value;
                        descriptions.Add($"Java {java.MajorVersion}（{java.Version}，{(java.IsJre ? "JRE" : "JDK")}，{(java.Is64Bit ? "64 位" : "32 位")}）");
                    }
                    else
                    {
                        descriptions.Add($"{Path.GetFileName(path)}（无法读取版本：{result.Error.Message}）");
                    }
                }
            }
            catch (TimeoutException)
            {
                details.Add("部分 Java 版本检测超时");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                details.Add("Java 版本检测失败：" + ex.Message);
            }
            if (descriptions.Count > 0) details.Add(string.Join("、", descriptions));
            details.Add("常用路径：" + string.Join("、", detected.Take(3)));
        }

        var runtimeHome = "";
        var runtimes = new List<string>();
        try
        {
            runtimeHome = LinuxPaths.JavaRuntimeHome;
            if (DirectoryExists(runtimeHome))
            {
                foreach (var directory in Directory.GetDirectories(runtimeHome))
                {
                    try
                    {
                        var java = Path.Combine(directory, "bin", "java");
                        if (File.Exists(java)) runtimes.Add(Path.GetFileName(directory));
                    }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception) { }
        if (runtimes.Count > 0)
            details.Add($"启动器下载的 Java 运行时 {runtimes.Count} 个（{string.Join("、", runtimes.Take(4))}，目录 {runtimeHome}）");
        else
            details.Add("尚未下载启动器管理的 Java 运行时（" + (runtimeHome.Length > 0 ? runtimeHome : "未知目录") + "）");

        if (detected.Count > 0 || runtimes.Count > 0)
            return new DiagnosticItem("Java", Normal, string.Join("；", details), false);
        details.Add("没有 Java 无法启动 Minecraft。请在“启动选项”点击“下载当前版本所需 Java”，或安装系统 OpenJDK（例如 sudo apt install openjdk-21-jre）");
        return new DiagnosticItem("Java", Warning, string.Join("；", details), true);
    }

    // ---------------------------------------------------------------- 文件选择器

    private static async Task<DiagnosticItem> CheckFilePickerAsync(Connection? connection, CancellationToken token)
    {
        var portal = connection is not null && await NameHasOwnerAsync(connection, PortalService, token).ConfigureAwait(false);
        var gtk = TryLoadLibrary("libgtk-3.so.0");
        var details = new List<string>
        {
            portal ? "org.freedesktop.portal.Desktop 可用" : "org.freedesktop.portal.Desktop 不可用",
            gtk ? "libgtk-3.so.0 可加载" : "缺少 libgtk-3.so.0"
        };
        if (portal)
        {
            details.Add("导入整合包、选择 Java / 背景图等文件对话框优先通过 XDG Desktop Portal 打开");
            return new DiagnosticItem("文件选择器", Normal, string.Join("；", details), false);
        }
        if (gtk)
        {
            details.Add("Portal 不可用时 Avalonia 会回退到 GTK 原生对话框");
            return new DiagnosticItem("文件选择器", Info, string.Join("；", details), false);
        }
        details.Add("Portal 与 GTK3 都不可用，可能无法打开文件对话框。请安装 xdg-desktop-portal 与 libgtk-3-0");
        return new DiagnosticItem("文件选择器", Warning, string.Join("；", details), true);
    }

    // ---------------------------------------------------------------- 通知

    private static Task<DiagnosticItem> CheckNotificationsAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var available = LinuxNotifications.IsAvailable;
        if (available)
            return new DiagnosticItem("通知", Normal,
                "org.freedesktop.Notifications 可用，任务完成或出错时使用系统通知中心，不会弹出自制窗口", false);
        return new DiagnosticItem("通知", Warning,
            "未找到 org.freedesktop.Notifications 服务，通知只会写入运行日志；可在轻量桌面安装 dunst 等通知守护进程", false);
    }, token);

    // ---------------------------------------------------------------- 输入法

    private static async Task<DiagnosticItem> CheckInputMethodAsync(Connection? connection, CancellationToken token)
    {
        var environment = new[] { "GTK_IM_MODULE", "QT_IM_MODULE", "XMODIFIERS", "SDL_IM_MODULE", "INPUT_METHOD" }
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToArray();
        var ibus = connection is not null && await NameHasOwnerAsync(connection, IbusService, token).ConfigureAwait(false);
        var fcitx = connection is not null && await NameHasOwnerAsync(connection, FcitxService, token).ConfigureAwait(false);
        var wayland = LinuxPaths.IsWaylandSession;

        var details = new List<string>();
        if (environment.Length > 0)
            details.Add("环境变量：" + string.Join("，", environment.Select(pair => pair.Name + "=" + pair.Value)));
        else
            details.Add("未设置 GTK_IM_MODULE / QT_IM_MODULE / XMODIFIERS");
        details.Add(ibus ? "IBus 服务正在运行" : "未检测到 org.freedesktop.IBus");
        details.Add(fcitx ? "Fcitx5 服务正在运行" : "未检测到 org.fcitx.Fcitx5");
        if (wayland)
            details.Add("Wayland 会话下本程序经 XWayland 运行，输入法候选窗跟随 X11 坐标；请确保安装 fcitx5-frontend-gtk3 / ibus-gtk3 等 X11 输入模块");

        if (ibus || fcitx || environment.Length > 0)
            return new DiagnosticItem("输入法", Normal, string.Join("；", details), false);
        details.Add("没有检测到输入法框架（不影响英文与粘贴输入）");
        return new DiagnosticItem("输入法", Info, string.Join("；", details), false);
    }

    // ---------------------------------------------------------------- 缩放 / 主题

    private static async Task<DiagnosticItem> CheckScalingThemeAsync(Connection? connection, CancellationToken token)
    {
        var details = new List<string>();
        var gtkTheme = Environment.GetEnvironmentVariable("GTK_THEME");
        if (!string.IsNullOrWhiteSpace(gtkTheme)) details.Add("GTK_THEME=" + gtkTheme);
        var gdkScale = Environment.GetEnvironmentVariable("GDK_SCALE");
        if (!string.IsNullOrWhiteSpace(gdkScale)) details.Add("GDK_SCALE=" + gdkScale);

        var scaling = await QueryAsync("gsettings",
            ["get", "org.gnome.desktop.interface", "text-scaling-factor"], TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        details.Add(string.IsNullOrWhiteSpace(scaling)
            ? "无法读取 gsettings text-scaling-factor（非 GNOME 桌面时正常）"
            : "text-scaling-factor=" + scaling.Trim());

        var colorScheme = connection is null
            ? null
            : await ReadPortalSettingAsync(connection, "org.freedesktop.appearance", "color-scheme", token).ConfigureAwait(false);
        details.Add(colorScheme switch
        {
            "0" => "XDG Portal 配色：未指定（跟随应用主题）",
            "1" => "XDG Portal 配色：偏好深色",
            "2" => "XDG Portal 配色：偏好浅色",
            _ => "XDG Portal 配色：不可用"
        });

        if (!LinuxPaths.IsWaylandSession)
        {
            var xrdb = await QueryAsync("xrdb", ["-query"], TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            var dpi = xrdb?
                .Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.TrimStart().StartsWith("Xft.dpi:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2).ElementAtOrDefault(1)?.Trim();
            details.Add(string.IsNullOrWhiteSpace(dpi) ? "Xft.dpi 未设置（X11 缩放为 100%）" : "Xft.dpi=" + dpi);
        }
        else
        {
            details.Add("Wayland 会话下缩放由 XWayland 以 X11 DPI 提供给 Avalonia（11.3 无原生 Wayland 后端）");
        }
        return new DiagnosticItem("缩放/主题", Info, string.Join("；", details), false);
    }

    // ---------------------------------------------------------------- 路径

    private static Task<DiagnosticItem> CheckPathsAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var details = new List<string>();
        try
        {
            foreach (var (name, value) in LinuxPaths.Describe())
                details.Add(name + "：" + value);
        }
        catch (Exception ex)
        {
            details.Add("读取 XDG 路径失败：" + ex.Message);
        }
        var locale = Environment.GetEnvironmentVariable("LC_ALL")
                     ?? Environment.GetEnvironmentVariable("LANG")
                     ?? Environment.GetEnvironmentVariable("LANGUAGE") ?? "未设置";
        details.Add("语言环境：" + locale);
        try
        {
            var culture = CultureInfo.CurrentUICulture.Name;
            details.Add(culture.Length > 0
                ? "当前区域性：" + culture
                : "当前区域性：Invariant（发布包启用 InvariantGlobalization，免除对系统 libicu 的依赖）");
            if (OperatingSystem.IsLinux() && !LinuxFonts.HasChineseFont())
                details.Add("警告：未检测到系统中文字体，界面将使用内置 Noto Sans CJK 兜底。");
        }
        catch (Exception) { }
        return new DiagnosticItem("路径", Info, string.Join("\n", details), false);
    }, token);

    // ---------------------------------------------------------------- D-Bus 辅助

    private static async Task<bool> NameHasOwnerAsync(Connection connection, string name, CancellationToken token)
    {
        try
        {
            var message = CreateNameHasOwnerMessage(connection, name);
            return await connection.CallMethodAsync(message,
                    static (Message reply, object? _) => reply.GetBodyReader().ReadBool())
                .WaitAsync(TimeSpan.FromSeconds(2), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MessageBuffer CreateNameHasOwnerMessage(Connection connection, string name)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus", "NameHasOwner", "s");
            writer.WriteString(name);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static async Task<string?> ReadPortalSettingAsync(Connection connection, string nameSpace, string key, CancellationToken token)
    {
        try
        {
            var message = CreatePortalReadOneMessage(connection, nameSpace, key);
            var value = await connection.CallMethodAsync(message,
                    static (Message reply, object? _) => reply.GetBodyReader().ReadVariantValue())
                .WaitAsync(TimeSpan.FromSeconds(2), token)
                .ConfigureAwait(false);
            return value.Type switch
            {
                VariantValueType.UInt32 => value.GetUInt32().ToString(CultureInfo.InvariantCulture),
                VariantValueType.Int32 => value.GetInt32().ToString(CultureInfo.InvariantCulture),
                VariantValueType.String => value.GetString(),
                VariantValueType.Bool => value.GetBool() ? "true" : "false",
                VariantValueType.Variant => value.GetVariantValue().ToString(),
                _ => value.ToString()
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static MessageBuffer CreatePortalReadOneMessage(Connection connection, string nameSpace, string key)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(PortalService, "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.Settings", "ReadOne", "ss");
            writer.WriteString(nameSpace);
            writer.WriteString(key);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // ---------------------------------------------------------------- 外部工具

    /// <summary>运行短命外部命令（2 秒超时）；命令不存在或失败返回 null。</summary>
    private static async Task<string?> QueryAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token)
    {
        string? path;
        try { path = FindInPath(executable); }
        catch (Exception) { path = null; }
        if (path is null) return null;
        try
        {
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(token).WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception) { }
                if (token.IsCancellationRequested) throw;
                return null;
            }
            var standardOutput = await output.ConfigureAwait(false);
            var standardError = await error.ConfigureAwait(false);
            var text = string.IsNullOrWhiteSpace(standardOutput) ? standardError : standardOutput;
            text = text.Trim();
            return text.Length > 4000 ? text[..4000] : text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindInPath(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }
        return null;
    }

    // ---------------------------------------------------------------- 系统探测辅助

    private static bool ProcessRunning(string processName)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
                try
                {
                    var comm = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
                    if (string.Equals(comm, processName, StringComparison.Ordinal)) return true;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return false;
    }

    private static bool TryLoadLibrary(string name)
    {
        try
        {
            if (!NativeLibrary.TryLoad(name, out var handle) || handle == IntPtr.Zero) return false;
            NativeLibrary.Free(handle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool KnownLibraryFile(string fileName)
    {
        foreach (var directory in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib" })
        {
            try
            {
                if (File.Exists(Path.Combine(directory, fileName))) return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    private static IReadOnlyList<string> DriDrivers()
    {
        var drivers = new List<string>();
        foreach (var directory in new[] { "/usr/lib/x86_64-linux-gnu/dri", "/usr/lib64/dri", "/usr/lib/dri" })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.GetFiles(directory, "*_dri.so"))
                {
                    var name = Path.GetFileName(file);
                    if (!drivers.Contains(name)) drivers.Add(name);
                }
            }
            catch (Exception) { }
        }
        return drivers;
    }

    private static IReadOnlyList<string> ListDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToArray() : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken token, Stopwatch stopwatch, int maxDepth = 6)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(2)) yield break;
            var (directory, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (Exception) { continue; }
            foreach (var file in files) yield return file;
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var child in Directory.GetDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception) { }
                    stack.Push((child, depth + 1));
                }
            }
            catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- Vulkan 最小 P/Invoke

    private static List<string> EnumerateVulkanDevices()
    {
        var devices = new List<string>();
        IntPtr instance = IntPtr.Zero;
        var applicationPointer = IntPtr.Zero;
        var deviceHandles = IntPtr.Zero;
        var properties = IntPtr.Zero;
        try
        {
            var application = new VkApplicationInfo
            {
                SType = 0,
                ApiVersion = 1u << 22 // VK_API_VERSION_1_0
            };
            applicationPointer = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(application, applicationPointer, false);
            var createInfo = new VkInstanceCreateInfo
            {
                SType = 1, // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
                PApplicationInfo = applicationPointer
            };
            if (VulkanNative.vkCreateInstance(ref createInfo, IntPtr.Zero, out instance) != 0 || instance == IntPtr.Zero)
                return devices;
            uint count = 0;
            if (VulkanNative.vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != 0 || count == 0)
                return devices;
            count = Math.Min(count, 8);
            deviceHandles = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
            if (VulkanNative.vkEnumeratePhysicalDevices(instance, ref count, deviceHandles) != 0)
                return devices;
            properties = Marshal.AllocHGlobal(2048);
            for (var index = 0; index < count; index++)
            {
                try
                {
                    var device = Marshal.ReadIntPtr(deviceHandles, index * IntPtr.Size);
                    if (device == IntPtr.Zero) continue;
                    VulkanNative.vkGetPhysicalDeviceProperties(device, properties);
                    var apiVersion = (uint)Marshal.ReadInt32(properties, 0);
                    var vendorId = (uint)Marshal.ReadInt32(properties, 8);
                    var deviceType = Marshal.ReadInt32(properties, 16);
                    var name = Marshal.PtrToStringUTF8(IntPtr.Add(properties, 20)) ?? "未知设备";
                    devices.Add($"{name}（{VulkanVendor(vendorId)} · {VulkanDeviceType(deviceType)} · Vulkan {apiVersion >> 22}.{(apiVersion >> 12) & 0x3FF}）");
                }
                catch (Exception) { }
            }
            return devices;
        }
        catch (Exception)
        {
            return devices;
        }
        finally
        {
            if (properties != IntPtr.Zero) { try { Marshal.FreeHGlobal(properties); } catch (Exception) { } }
            if (deviceHandles != IntPtr.Zero) { try { Marshal.FreeHGlobal(deviceHandles); } catch (Exception) { } }
            if (applicationPointer != IntPtr.Zero) { try { Marshal.FreeHGlobal(applicationPointer); } catch (Exception) { } }
            if (instance != IntPtr.Zero)
            {
                try { VulkanNative.vkDestroyInstance(instance, IntPtr.Zero); }
                catch (Exception) { }
            }
        }
    }

    private static string VulkanVendor(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x13B5 => "ARM",
        0x5143 => "Qualcomm",
        0x1010 => "Imagination",
        0x10005 => "Mesa",
        _ => $"厂商 0x{vendorId:X}"
    };

    private static string VulkanDeviceType(int deviceType) => deviceType switch
    {
        1 => "集成显卡",
        2 => "独立显卡",
        3 => "虚拟 GPU",
        4 => "CPU",
        _ => "其它设备"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint SType;
        public IntPtr PNext;
        public IntPtr PApplicationName;
        public uint ApplicationVersion;
        public IntPtr PEngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    private static class VulkanNative
    {
        private const string Library = "libvulkan.so.1";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint deviceCount, IntPtr devices);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void vkGetPhysicalDeviceProperties(IntPtr physicalDevice, IntPtr properties);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
    }
}
