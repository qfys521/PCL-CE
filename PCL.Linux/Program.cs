using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using PCL.Core.IO.Net;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Minecraft.Java.Scanner;
using PCL.Portable;

[assembly: SupportedOSPlatform("linux")]

namespace PCL.Linux;

internal static class Program
{
    private static SingleInstance? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.Error.WriteLine("PCL-CE-Linux 仅支持 Linux x86_64（AppImage 版本）。");
            return 1;
        }
        try
        {
            LinuxPaths.EnsureDirectories();
            AppLog.Initialize();
            AppLog.Info(VersionText + " 启动，参数：" + string.Join(' ', args.Select(Quote)));
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLog.Error("未处理的异常", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLog.Error("未观察到的任务异常", e.Exception);
                e.SetObserved();
            };

            LinuxSettings settings;
            try { settings = LinuxSettings.Load(); }
            catch (Exception ex) { AppLog.Error("读取设置失败，使用默认设置", ex); settings = new LinuxSettings(); }
            DownloadPolicy.SourceOverride = settings.DownloadSource;
            ConfigureStartup(settings);
            PrepareGameDirectory(settings);

            if (HasFlag(args, "--help") || HasFlag(args, "-h")) { PrintHelp(); return 0; }
            if (HasFlag(args, "--version") || HasFlag(args, "-v")) { Console.WriteLine(VersionText); return 0; }
            if (HasFlag(args, "--diagnose")) return Diagnose(settings);
            if (HasFlag(args, "--list")) return ListReleases(settings);
            if (HasFlag(args, "--install")) return InstallCli(args, settings);
            if (HasFlag(args, "--launch")) return LaunchCli(args, settings);
            if (HasFlag(args, "--import-pack")) return ImportPackCli(args, settings);
            if (HasFlag(args, "--verify-network")) return VerifyNetwork(settings);
            if (HasFlag(args, "--wipe-data")) return WipeData(args, settings);
            if (HasFlag(args, "--register-desktop")) return RegisterDesktop();
            if (HasFlag(args, "--unregister-desktop")) return UnregisterDesktop();

            var displayProblem = CheckDisplay();
            if (displayProblem is not null)
            {
                Console.Error.WriteLine(displayProblem);
                AppLog.Error(displayProblem);
                // 没有可用显示时，仍给出可读的诊断信息，而不是让框架抛异常。
                Console.Error.WriteLine("提示：可使用 ./PCL-CE-Linux-*.AppImage --diagnose 检查 Java、显卡与路径配置。");
                return 2;
            }
            if (!HasFlag(args, "--no-single-instance"))
            {
                if (!SingleInstance.TryAcquire(args, out var instance)) return 0;
                _singleInstance = instance;
            }
            return BuildAvaloniaApp(settings).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            AppLog.Error("启动失败", exception);
            var message = exception.Message + " " + exception.InnerException?.Message;
            if (message.Contains("XOpenDisplay", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("display", StringComparison.OrdinalIgnoreCase) && message.Contains("X11", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("无法连接到 X 显示。");
                Console.Error.WriteLine($"  DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "(未设置)"}");
                Console.Error.WriteLine($"  XAUTHORITY={Environment.GetEnvironmentVariable("XAUTHORITY") ?? "(未设置)"}");
                Console.Error.WriteLine("  PCL-CE-Linux 通过 XWayland 使用 X11 协议（Avalonia 11 暂无原生 Wayland 后端）。");
                Console.Error.WriteLine("  请检查：1) 是否在图形桌面会话中运行；2) XWayland/X11 是否启动；3) DISPLAY 与 XAUTHORITY 是否正确；");
                Console.Error.WriteLine("  4) 纯 Wayland 环境请启用 XWayland。可用 --diagnose 查看环境，或用 --help 查看命令行选项。");
                return 2;
            }
            Console.Error.WriteLine($"PCL-CE-Linux 启动失败：{exception.Message}");
            if (exception.InnerException is { } inner) Console.Error.WriteLine("  " + inner.Message);
            Console.Error.WriteLine("日志：" + (AppLog.LogPath ?? "(无法写入)"));
            return 1;
        }
        finally
        {
            _singleInstance?.Dispose();
        }
    }

    internal static SingleInstance? Instance => _singleInstance;

    internal static string VersionText
    {
        get
        {
            var assembly = typeof(Program).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return "PCL-CE-Linux " + (string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString(3) : informational);
        }
    }

    internal static AppBuilder BuildAvaloniaApp(LinuxSettings settings) =>
        AppBuilder.Configure<LinuxApp>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                // 中文兜底：系统字体缺失时也能显示中文，避免豆腐块。
                DefaultFamilyName = LinuxFonts.Resolve(settings.InterfaceFont)
            })
            .With(new X11PlatformOptions
            {
                // Avalonia 11.3 暂无原生 Wayland 后端：在 Wayland 会话中通过 XWayland 使用 X11 协议。
                // XWayland 提供真实 X11 坐标，窗口定位/拖拽与 fcitx5/ibus 候选框位置都正常。
                UseDBusFilePicker = settings.EnablePortalFilePicker,
                UseDBusMenu = true,
                EnableIme = true,
                RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software]
            })
            .LogToTrace(LogEventLevel.Warning);

    /// <summary>窗口创建后由 LinuxApp 调用，把外部参数（文件、pcl-ce:// 链接）交给主窗口。</summary>
    internal static void ForwardExternalArguments(MainWindow window, string[] args)
    {
        var external = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (external.Length > 0) window.HandleExternalArguments(external);
    }

    private static LinuxSettings? _settingsForIntegration;

    internal static void ConfigureStartup(LinuxSettings settings) => _settingsForIntegration = settings;

    /// <summary>启动后按需安装/刷新桌面入口、图标、MIME 与 pcl-ce:// 协议。</summary>
    internal static void StartDesktopIntegration()
    {
        if (_settingsForIntegration is not { EnableDesktopIntegration: true } settings) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var status = await DesktopIntegration.EnsureInstalledAsync(false, null, timeout.Token);
                if (status.Installed) AppLog.Info("桌面集成：" + status.DesktopFile);
                else AppLog.Warn("桌面集成未安装：" + status.Problem);
            }
            catch (Exception ex) { AppLog.Warn("桌面集成失败：" + ex.Message); }
        });
    }

    private static void PrepareGameDirectory(LinuxSettings settings)
    {
        try
        {
            // 只有“由启动器新建”的游戏目录才会写入管理标记；用户手工选择的既有目录不会被自动接管，
            // 从而保证“清空游戏数据”不会误删用户自己管理的 Minecraft 目录。
            var existed = Directory.Exists(settings.GameDirectory);
            if (!existed)
            {
                Directory.CreateDirectory(settings.GameDirectory);
                LinuxPaths.EnsureGameDirectoryMarker(settings.GameDirectory);
            }
            foreach (var line in LinuxPaths.MigrateLegacyData(settings.GameDirectory)) AppLog.Info(line);
        }
        catch (Exception ex) { AppLog.Warn("游戏目录准备失败：" + ex.Message); }
    }

    private static string? CheckDisplay()
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (!string.IsNullOrWhiteSpace(display)) return null;
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrWhiteSpace(wayland))
            return """
                   未找到 X 显示（DISPLAY 为空）。
                   PCL-CE-Linux 使用 Avalonia 11，Avalonia 11 暂无原生 Wayland 后端，需要通过 XWayland 运行；
                   你的 Wayland 会话没有提供 XWayland。请启用 XWayland（大多数桌面默认启用）或改用 X11 会话。
                   """;
        return "未找到图形显示：DISPLAY 与 WAYLAND_DISPLAY 均未设置。请在桌面会话中运行，或使用 --diagnose 检查环境。";
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag, StringComparer.Ordinal);

    private static void PrintHelp()
    {
        Console.WriteLine(VersionText);
        Console.WriteLine("""
                直接运行：打开图形界面
                --diagnose              输出路径、Java、显卡与桌面环境诊断信息
                --list                  列出 Mojang Minecraft 正式版
                --smoke-test            检查全部界面页面与动画后自动退出（不联网、不启动游戏）
                --screenshot <目录>     渲染界面截图（浅色/深色/2x）后退出，用于验证
                --import-pack <文件>    导入 .mrpack / CurseForge / HMCL 等整合包（可加 --instance <名称>）
                --verify-network        联网验证 Mojang / Modrinth 下载与 .mrpack 解析（开发者用）
                --wipe-data <类别...>   清空启动器数据（类别：config accounts java tools cache logs runtime game all）
                                        必须与 --yes 一起使用；仅删除启动器自己管理的路径
                --install <版本>        安装 Minecraft（可加 --loader fabric|quilt 与 --loader-version <版本>）
                --launch <版本>         启动已安装版本（可加 --java <路径>、--memory <MiB>、--timeout <秒>）
                --register-desktop     安装/刷新桌面入口、图标、MIME 与 pcl-ce:// 协议
                --unregister-desktop   移除上面安装的桌面集成文件
                --help                  显示此帮助
                --version               显示版本

                也可以把 .mrpack 整合包或 pcl-ce:// 链接作为参数传入；启动器已在运行时会把参数转发给已运行实例。
                """);
    }

    private static int Diagnose(LinuxSettings settings)
    {
        Console.WriteLine(VersionText);
        Console.WriteLine($"主机：{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
        foreach (var (name, value) in LinuxPaths.Describe()) Console.WriteLine($"{name}：{value}");
        Console.WriteLine($"日志：{AppLog.LogPath}");
        Console.WriteLine("Java：");
        var paths = new List<string>();
        new LinuxJavaScanner().Scan(paths);
        if (!string.IsNullOrWhiteSpace(settings.JavaPath)) paths.Insert(0, settings.JavaPath);
        var any = false;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var result = new CommandJavaParser().Parse(path);
            Console.WriteLine("  " + (result.IsSuccess ? result.Value.ToDetailedString() : $"{path}: {result.Error.Message}"));
            any = true;
        }
        if (!any) Console.WriteLine("  未找到 Java（游戏启动前会自动下载 Temurin；也可安装 OpenJDK）");
        Console.WriteLine("已安装版本：");
        using var client = LinuxHttpClientFactory.Create();
        var versions = new LinuxGameLauncher(settings.GameDirectory, client).GetInstalledVersions();
        if (versions.Count == 0) Console.WriteLine("  （无）");
        foreach (var version in versions) Console.WriteLine("  " + version);
        Console.WriteLine($"系统通知：{(LinuxNotifications.IsAvailable ? "可用" : "不可用（将退化为界面内提示）")}");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var diagnostics = SystemDiagnostics.CollectAsync(timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine(SystemDiagnostics.FormatReport(diagnostics));
        }
        catch (Exception ex) { Console.WriteLine("系统诊断失败：" + ex.Message); }
        return 0;
    }

    private static int ListReleases(LinuxSettings settings)
    {
        using var client = LinuxHttpClientFactory.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var releases = new LinuxGameLauncher(settings.GameDirectory, client)
            .GetReleasesAsync(timeout.Token).GetAwaiter().GetResult();
        foreach (var release in releases) Console.WriteLine(release.Id);
        return 0;
    }

    private static string RequireValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{flag} 需要一个参数");
        return args[index + 1];
    }

    private static string? OptionalValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1] : null;
    }

    /// <summary>命令行安装：便于自动化验证和没有图形环境时预装版本。</summary>
    private static int InstallCli(string[] args, LinuxSettings settings)
    {
        try
        {
            var id = RequireValue(args, "--install");
            using var client = LinuxHttpClientFactory.Create();
            var progress = new Progress<string>(message => Console.WriteLine("  " + message));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var launcher = new LinuxGameLauncher(settings.GameDirectory, client);
            var loader = OptionalValue(args, "--loader");
            if (string.IsNullOrWhiteSpace(loader))
            {
                var release = launcher.GetVersionsAsync(timeout.Token).GetAwaiter().GetResult()
                    .FirstOrDefault(version => version.Id == id) ?? throw new InvalidOperationException($"找不到版本 {id}，可用 --list 查看。");
                launcher.InstallAsync(release, progress, timeout.Token).GetAwaiter().GetResult();
                Console.WriteLine($"{id} 安装完成：{launcher.GameDirectory}");
                return 0;
            }
            var kind = Enum.Parse<LoaderKind>(loader, true);
            launcher.InstallMinecraftAsync(id, progress, timeout.Token).GetAwaiter().GetResult();
            var java = FindRequiredJavaOrInstall(launcher, id, settings, timeout.Token);
            var loaderVersion = OptionalValue(args, "--loader-version")
                                ?? new LoaderInstaller(launcher, client).GetVersionsAsync(kind, id, timeout.Token).GetAwaiter().GetResult().FirstOrDefault();
            var installed = new LoaderInstaller(launcher, client)
                .InstallAsync(kind, id, loaderVersion, java, progress, timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine($"{installed} 安装完成：{launcher.GameDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("安装失败：" + ex.Message);
            AppLog.Error("命令行安装失败", ex);
            return 1;
        }
    }

    private static string FindRequiredJavaOrInstall(LinuxGameLauncher launcher, string id, LinuxSettings settings, CancellationToken token)
    {
        var required = launcher.GetRequiredJavaVersionAsync(id, token).GetAwaiter().GetResult();
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.JavaPath)) paths.Add(settings.JavaPath);
        new LinuxJavaScanner().Scan(paths);
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var parsed = new CommandJavaParser().Parse(path);
            if (parsed.IsSuccess && parsed.Value.MajorVersion == required) return parsed.Value.JavaExePath;
        }
        Console.WriteLine($"未找到 Java {required}，正在下载 Eclipse Temurin Linux 运行时…");
        var installer = new JavaRuntimeInstaller(LinuxPaths.JavaRuntimeHome, LinuxHttpClientFactory.Create());
        return installer.InstallAsync(required, new Progress<string>(Console.WriteLine), token).GetAwaiter().GetResult();
    }

    /// <summary>命令行启动：用于验证启动链路（原生库、资源、日志）与自动化冒烟测试。</summary>
    private static int LaunchCli(string[] args, LinuxSettings settings)
    {
        try
        {
            var id = RequireValue(args, "--launch");
            using var client = LinuxHttpClientFactory.Create();
            var launcher = new LinuxGameLauncher(settings.GameDirectory, client);
            var progress = new Progress<string>(message => Console.WriteLine("  " + message));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var java = OptionalValue(args, "--java")
                       ?? FindRequiredJavaOrInstall(launcher, id, settings, timeout.Token);
            var memory = int.TryParse(OptionalValue(args, "--memory"), out var value) ? value : settings.MemoryMb;
            var account = LaunchAccount.Offline(string.IsNullOrWhiteSpace(settings.OfflineName) ? "Player" : settings.OfflineName);
            Console.WriteLine($"准备启动 {id}：{java}，内存 {memory} MiB");
            var prepared = launcher.PrepareAsync(id, new(java, memory, account, settings.JvmArguments, settings.GameArguments), progress, timeout.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine("启动命令：" + prepared.StartInfo.FileName + " " + string.Join(' ', prepared.StartInfo.ArgumentList.Take(6)) + " …");
            Console.WriteLine("游戏日志：" + prepared.LogPath);
            using var process = System.Diagnostics.Process.Start(prepared.StartInfo)
                                ?? throw new InvalidOperationException("无法启动 Java 进程");
            var log = new StreamWriter(prepared.LogPath) { AutoFlush = true };
            var pump = new[] { process.StandardOutput, process.StandardError }.Select(async reader =>
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    Console.WriteLine("  [游戏] " + line);
                    await log.WriteLineAsync(line);
                }
            }).ToArray();
            var seconds = int.TryParse(OptionalValue(args, "--timeout"), out var timeoutSeconds) ? timeoutSeconds : 25;
            var exited = process.WaitForExit(TimeSpan.FromSeconds(Math.Max(5, seconds)));
            if (!exited)
            {
                Console.WriteLine($"运行 {seconds} 秒正常，正在结束进程…");
                process.Kill(true);
            }
            Task.WaitAll(pump, TimeSpan.FromSeconds(5));
            log.Dispose();
            try { Directory.Delete(prepared.NativesDirectory, true); } catch (Exception) { }
            Console.WriteLine(exited ? $"游戏已退出，退出码 {process.ExitCode}（日志：{prepared.LogPath}）" : "启动验证完成。");
            return exited && process.ExitCode != 0 ? process.ExitCode : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败：" + ex.Message);
            AppLog.Error("命令行启动失败", ex);
            return 1;
        }
    }

    /// <summary>命令行导入整合包，便于自动化验证与脚本使用。</summary>
    private static int ImportPackCli(string[] args, LinuxSettings settings)
    {
        try
        {
            var path = Path.GetFullPath(RequireValue(args, "--import-pack"));
            if (!File.Exists(path)) throw new FileNotFoundException("整合包不存在", path);
            using var client = LinuxHttpClientFactory.Create();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var progress = new Progress<string>(message => Console.WriteLine("  " + message));
            var launcher = new LinuxGameLauncher(settings.GameDirectory, client);
            var plan = ModpackInstaller.Inspect(path);
            Console.WriteLine($"整合包：{plan.Name} · Minecraft {plan.Minecraft} · 加载器 {plan.Loader} {plan.LoaderVersion} · 格式 {plan.Format}");
            var id = OptionalValue(args, "--instance");
            if (string.IsNullOrWhiteSpace(id))
                id = string.Concat(plan.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' or ':' ? '_' : character));
            VersionResolver.ValidateId(id);
            if (plan.Format != "bundled")
                launcher.InstallMinecraftAsync(plan.Minecraft, progress, timeout.Token).GetAwaiter().GetResult();
            var java = plan.Loader is LoaderKind.Forge or LoaderKind.NeoForge
                ? FindRequiredJavaOrInstall(launcher, plan.Minecraft, settings, timeout.Token)
                : "java";
            var installer = new ModpackInstaller(launcher, client, settings.CurseForgeKey);
            installer.ImportAsync(path, id, java, progress, timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine($"已导入整合包：{plan.Name} → {id}（{launcher.GameDirectory}）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("导入整合包失败：" + ex.Message);
            AppLog.Error("命令行导入整合包失败", ex);
            return 1;
        }
    }

    /// <summary>联网自检：Mojang 版本列表、Modrinth 搜索/版本/下载校验、.mrpack 解析。</summary>
    private static int VerifyNetwork(LinuxSettings settings)
    {
        try
        {
            using var client = LinuxHttpClientFactory.Create();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var launcher = new LinuxGameLauncher(settings.GameDirectory, client);
            var releases = launcher.GetReleasesAsync(timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine($"Mojang 版本列表：{releases.Count} 个正式版，最新 {releases.FirstOrDefault()?.Id}");
            var catalog = new ContentCatalog(client, settings.CurseForgeKey);
            var projects = catalog.SearchAsync(CatalogSource.Modrinth, "mod", "sodium", "1.21.1", "fabric", timeout.Token)
                .GetAwaiter().GetResult();
            var project = projects.FirstOrDefault() ?? throw new InvalidOperationException("Modrinth 搜索没有结果");
            Console.WriteLine($"Modrinth 搜索：{project.Title}（{project.Id}）");
            var versions = catalog.VersionsAsync(project, "1.21.1", "fabric", timeout.Token).GetAwaiter().GetResult();
            var version = versions.FirstOrDefault(candidate => candidate.Files.Count > 0)
                          ?? throw new InvalidOperationException("Modrinth 没有可下载文件");
            var file = version.Files[0];
            var target = Path.Combine(LinuxPaths.CreateTempDirectory("selftest"), file.Name);
            new VerifiedDownloads(client).FetchAsync(file.Url, target, file.Hash, timeout.Token, new Progress<string>(Console.WriteLine))
                .GetAwaiter().GetResult();
            Console.WriteLine($"Modrinth 下载并校验：{file.Name}（{new FileInfo(target).Length} 字节）→ OK");
            try { Directory.Delete(Path.GetDirectoryName(target)!, true); } catch (Exception) { }
            var pack = Path.Combine(LinuxPaths.CreateTempDirectory("selftest"), "selftest.mrpack");
            using (var archive = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("modrinth.index.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""{"formatVersion":1,"game":"minecraft","versionId":"1.0.0","name":"PCL CE Self Test","summary":"","files":[],"dependencies":{"minecraft":"1.21.1"}}""");
            }
            var plan = ModpackInstaller.Inspect(pack);
            Console.WriteLine($"整合包解析：{plan.Name} · Minecraft {plan.Minecraft} · 格式 {plan.Format} → OK");
            try { Directory.Delete(Path.GetDirectoryName(pack)!, true); } catch (Exception) { }
            Console.WriteLine("网络自检通过。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("网络自检失败：" + ex.Message);
            AppLog.Error("网络自检失败", ex);
            return 1;
        }
    }

    private static int WipeData(string[] args, LinuxSettings settings)
    {
        var aliases = new Dictionary<string, WipeCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["config"] = WipeCategory.Config, ["settings"] = WipeCategory.Config,
            ["accounts"] = WipeCategory.Accounts, ["login"] = WipeCategory.Accounts,
            ["java"] = WipeCategory.JavaRuntime, ["runtime"] = WipeCategory.RuntimeState,
            ["tools"] = WipeCategory.Tools, ["downloads"] = WipeCategory.DownloadsCache,
            ["cache"] = WipeCategory.Cache, ["logs"] = WipeCategory.Logs,
            ["game"] = WipeCategory.GameData, ["games"] = WipeCategory.GameData, ["minecraft"] = WipeCategory.GameData
        };
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--wipe-data") continue;
            for (var next = index + 1; next < args.Length && !args[next].StartsWith("--", StringComparison.Ordinal); next++)
                values.Add(args[next]);
        }
        var categories = new List<WipeCategory>();
        if (values.Count == 0 || values.Contains("all", StringComparer.OrdinalIgnoreCase))
            categories.AddRange(Enum.GetValues<WipeCategory>());
        else
            foreach (var value in values)
                if (aliases.TryGetValue(value, out var category) && !categories.Contains(category)) categories.Add(category);
        if (categories.Count == 0)
        {
            Console.Error.WriteLine("未识别的清空类别。可用：" + string.Join(' ', aliases.Keys) + " all");
            return 2;
        }
        var plan = AppDataWipe.Inspect(settings.GameDirectory);
        Console.WriteLine("将清空以下启动器数据：");
        foreach (var entry in plan.Where(entry => categories.Contains(entry.Category)))
        {
            Console.WriteLine($"  [{entry.Category}] {entry.Label}（{AppDataWipe.FormatSize(entry.SizeBytes)}）");
            foreach (var path in entry.Paths) Console.WriteLine("      " + path);
        }
        if (!args.Contains("--yes"))
        {
            Console.Error.WriteLine("为避免误删，命令行清空数据需要同时传入 --yes。");
            return 2;
        }
        var result = AppDataWipe.WipeAsync(categories, settings.GameDirectory).GetAwaiter().GetResult();
        Console.WriteLine($"已删除 {result.Deleted.Count} 项，释放 {AppDataWipe.FormatSize(result.FreedBytes)}。");
        foreach (var skipped in result.Skipped) Console.WriteLine("跳过：" + skipped);
        foreach (var error in result.Errors) Console.Error.WriteLine("错误：" + error);
        Console.WriteLine("设置与账号已清空，请重新启动启动器。");
        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static int RegisterDesktop()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var status = DesktopIntegration.EnsureInstalledAsync(true, null, timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine(status.Installed ? $"桌面集成已安装：{status.DesktopFile}" : $"桌面集成安装失败：{status.Problem}");
            if (status.ExecPath is not null) Console.WriteLine("Exec：" + status.ExecPath);
            return status.Installed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("桌面集成失败：" + ex.Message);
            return 1;
        }
    }

    private static int UnregisterDesktop()
    {
        try
        {
            var removed = DesktopIntegration.UninstallAsync().GetAwaiter().GetResult();
            Console.WriteLine(removed ? "已移除桌面集成。" : "未发现可移除的桌面集成。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("移除桌面集成失败：" + ex.Message);
            return 1;
        }
    }

    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;
}

internal sealed class LinuxApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://PCL-CE/")) { Source = new Uri("avares://PCL-CE/Themes/PclTheme.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var smoke = args.Contains("--smoke-test");
            var screenshot = TakeValue(args, "--screenshot");
            var window = new MainWindow(smoke, screenshot);
            desktop.MainWindow = window;
            if (Program.Instance is { } single)
                single.ArgumentsReceived += forwarded => Dispatcher.UIThread.Post(() => window.HandleExternalArguments(forwarded));
            window.Opened += (_, _) =>
            {
                Program.ForwardExternalArguments(window, args);
                if (!smoke && screenshot is null) Program.StartDesktopIntegration();
                if (smoke)
                {
                    _ = RunSmokeAsync(window, desktop);
                }
                else if (screenshot is not null)
                {
                    _ = RunScreenshotAsync(window, desktop, screenshot);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string? TakeValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task RunSmokeAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try { await window.VerifyPagesAsync(); desktop.Shutdown(0); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("界面检查失败：" + ex);
            AppLog.Error("界面检查失败", ex);
            desktop.Shutdown(1);
        }
    }

    private static async Task RunScreenshotAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, string directory)
    {
        try { await window.CaptureScreensAsync(directory); desktop.Shutdown(0); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("截图失败：" + ex);
            AppLog.Error("截图失败", ex);
            desktop.Shutdown(1);
        }
    }
}

internal sealed class LinuxHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client = Create();
    public HttpClient CreateClient(string name) => _client;
    public void Dispose() => _client.Dispose();
    public static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        }) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-Linux/" + typeof(LinuxApp).Assembly.GetName().Version?.ToString(3));
        return client;
    }
}
