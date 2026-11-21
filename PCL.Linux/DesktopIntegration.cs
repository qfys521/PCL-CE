using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// 用户级桌面集成：桌面入口、应用图标、MIME 类型与安装状态。
/// 所有写入都限制在 <see cref="LinuxPaths"/> 定义的 XDG 目录内（applications / icons / mime / state），
/// 不修改系统目录、不覆盖其他应用的桌面文件；任何失败都以 <see cref="IntegrationStatus.Problem"/> 文本返回，不向调用方抛出异常。
/// </summary>
public static class DesktopIntegration
{
    /// <summary>桌面集成当前状态。</summary>
    /// <param name="Installed">桌面入口文件是否存在。</param>
    /// <param name="DesktopFile">桌面入口文件的绝对路径。</param>
    /// <param name="ExecPath">当前进程认为应写入 Exec 的可执行文件（AppImage 优先）。</param>
    /// <param name="Problem">需要用户处理的问题；为 null 表示一切正常。</param>
    public sealed record IntegrationStatus(bool Installed, string DesktopFile, string? ExecPath, string? Problem);

    private const string DesktopFileName = "pcl-ce.desktop";
    private const string MimeFileName = "pcl-ce.xml";
    private const string NotificationsService = "org.freedesktop.Notifications";

    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string DesktopEntryHeader =
        """
        [Desktop Entry]
        Type=Application
        Version=1.0
        Name=PCL-CE-Linux
        Name[zh_CN]=PCL-CE-Linux
        GenericName=Minecraft Launcher
        GenericName[zh_CN]=Minecraft 启动器
        Comment=Launch and manage Minecraft instances
        Comment[zh_CN]=启动与管理 Minecraft 游戏实例
        """;

    private const string DesktopEntryFooter =
        """
        Icon=pcl-ce
        Terminal=false
        Categories=Game;Utility;
        StartupWMClass=PCL-CE
        Keywords=minecraft;launcher;game;modpack;
        Keywords[zh_CN]=我的世界;启动器;游戏;整合包;
        MimeType=application/x-mrpack;application/x-minecraft-package;x-scheme-handler/pcl-ce;
        """;

    private const string MimeDefinition =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
          <mime-type type="application/x-mrpack">
            <comment>Modrinth modpack</comment>
            <comment xml:lang="zh_CN">Modrinth 整合包</comment>
            <glob pattern="*.mrpack"/>
            <icon name="pcl-ce"/>
            <generic-icon name="package-x-generic"/>
          </mime-type>
          <mime-type type="application/x-minecraft-package">
            <comment>Minecraft content package</comment>
            <comment xml:lang="zh_CN">Minecraft 内容包</comment>
            <glob pattern="*.mcworld"/>
            <glob pattern="*.mcpack"/>
            <glob pattern="*.mctemplate"/>
            <glob pattern="*.mcaddon"/>
            <icon name="pcl-ce"/>
            <generic-icon name="package-x-generic"/>
          </mime-type>
        </mime-info>
        """;

    private static string DesktopFile => Path.Combine(LinuxPaths.UserApplications, DesktopFileName);
    private static string MimeFile => Path.Combine(LinuxPaths.UserMime, "packages", MimeFileName);

    private static string DevelopmentVersion =>
        typeof(DesktopIntegration).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>读取当前安装状态（只读，不写文件，不抛出异常）。</summary>
    public static IntegrationStatus Inspect()
    {
        string desktop = "";
        string? exec = null;
        try
        {
            desktop = DesktopFile;
            exec = CurrentExecutable();
            if (!OperatingSystem.IsLinux())
                return new(false, desktop, exec, "桌面集成仅支持 Linux。");
            if (!SafeExists(desktop))
                return new(false, desktop, exec, "尚未安装桌面入口，点击“安装桌面入口”即可创建。");
            var state = ReadState();
            if (state is null)
                return new(true, desktop, exec, "缺少安装状态记录，请点击“重新安装”以匹配当前程序路径。");
            if (!Matches(state.ExecPath, exec) || !Matches(state.AppImagePath, LinuxPaths.AppImagePath))
                return new(true, desktop, exec,
                    $"桌面入口指向 {state.ExecPath ?? "未知路径"}，当前程序为 {exec ?? "未知路径"}，请点击“重新安装”。");
            if (!SafeExists(MimeFile))
                return new(true, desktop, exec, "桌面入口缺少 MIME 类型定义，请点击“重新安装”。");
            var missing = IconSizes.Count(size => !SafeExists(IconPath(size)));
            if (missing > 0)
                return new(true, desktop, exec, $"桌面入口缺少 {missing} 个尺寸的应用图标，请点击“重新安装”。");
            return new(true, desktop, exec, null);
        }
        catch (Exception ex)
        {
            return new(SafeExists(desktop), desktop, exec, "检查桌面集成状态失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 确保用户级桌面集成已安装。已安装且可执行文件路径未变化时直接返回；
    /// <paramref name="force"/> 为 true 时总是重写全部产物。失败信息放在返回值的 Problem 中。
    /// </summary>
    public static async Task<IntegrationStatus> EnsureInstalledAsync(bool force, IProgress<string>? progress, CancellationToken token)
    {
        string desktop;
        string? exec;
        try
        {
            desktop = DesktopFile;
            exec = CurrentExecutable();
        }
        catch (Exception ex)
        {
            return new(false, "", null, "无法确定桌面集成路径：" + ex.Message);
        }
        if (!OperatingSystem.IsLinux()) return new(false, desktop, exec, "桌面集成仅支持 Linux。");
        if (string.IsNullOrWhiteSpace(exec)) return new(false, desktop, null, "无法确定当前可执行文件路径，桌面入口无法生成。");
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(SafeExists(desktop), desktop, exec, "安装已取消。");
        }
        try
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("检查桌面集成状态…");
            var appImage = LinuxPaths.AppImagePath;
            var state = ReadState();
            var upToDate = state is not null && Matches(state.ExecPath, exec) && Matches(state.AppImagePath, appImage) && ArtifactsComplete();
            if (!force && upToDate)
            {
                progress?.Report("桌面集成已是最新状态");
                return new(true, desktop, exec, null);
            }
            var problems = new List<string>();

            token.ThrowIfCancellationRequested();
            progress?.Report("写入桌面入口…");
            WriteSharedFile(desktop, BuildDesktopEntry(exec));

            token.ThrowIfCancellationRequested();
            progress?.Report("安装应用图标（16–512 像素）…");
            if (InstallIcons(progress, token) is { } iconProblem) problems.Add(iconProblem);

            token.ThrowIfCancellationRequested();
            progress?.Report("注册 MIME 类型（.mrpack / .mcworld 等）…");
            WriteSharedFile(MimeFile, MimeDefinition);

            token.ThrowIfCancellationRequested();
            progress?.Report("刷新 MIME 与桌面数据库…");
            await RefreshDatabasesAsync(token).ConfigureAwait(false);
            await RegisterDefaultHandlerAsync(token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            try
            {
                WriteState(new IntegrationState
                {
                    ExecPath = exec,
                    AppImagePath = appImage,
                    InstalledAt = DateTimeOffset.Now,
                    Version = DevelopmentVersion
                });
            }
            catch (Exception ex)
            {
                problems.Add("写入安装状态失败：" + ex.Message);
            }
            AppLog.Info($"桌面集成已安装：{desktop}（Exec={exec}，AppImage={appImage ?? "无"}）");
            progress?.Report("桌面集成已安装");
            return new(true, desktop, exec, problems.Count == 0 ? null : string.Join("；", problems));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("桌面集成安装已取消。");
            return new(SafeExists(desktop), desktop, exec, "安装已取消。");
        }
        catch (Exception ex)
        {
            AppLog.Warn("安装桌面集成失败：" + ex.Message);
            return new(SafeExists(desktop), desktop, exec, "安装桌面集成失败：" + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 卸载本应用安装的全部桌面集成产物（只删除自己的固定文件名），并刷新数据库。
    /// 返回是否完整卸载；异常写入日志，不向调用方抛出。
    /// </summary>
    public static async Task<bool> UninstallAsync()
    {
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
        try
        {
            if (!OperatingSystem.IsLinux()) return false;
            DeleteFile(DesktopFile);
            DeleteFile(MimeFile);
            DeleteFile(LinuxPaths.DesktopIntegrationStatePath);
            foreach (var size in IconSizes)
            {
                DeleteFile(IconPath(size));
                TryDeleteEmptyDirectory(Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}", "apps"));
                TryDeleteEmptyDirectory(Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}"));
            }
            TryDeleteEmptyDirectory(Path.Combine(LinuxPaths.UserIcons, "hicolor"));
            TryDeleteEmptyDirectory(Path.Combine(LinuxPaths.UserMime, "packages"));
            await RefreshDatabasesAsync(CancellationToken.None).ConfigureAwait(false);
            AppLog.Info("已卸载桌面集成（桌面入口、图标与 MIME 定义）。");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("卸载桌面集成失败：" + ex.Message);
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>当前桌面入口应使用的可执行文件（AppImage 路径、发布目录 apphost 或 dotnet 宿主）。</summary>
    public static string? CurrentExecutablePath => CurrentExecutable();

    private static string? CurrentExecutable()
    {
        if (LinuxPaths.AppImagePath is { } appImage && !string.IsNullOrWhiteSpace(appImage)) return appImage;
        // 发布目录中的 apphost 优先于 dotnet 宿主（例如用 `dotnet PCL-CE.dll` 启动的开发模式）。
        var apphost = Path.Combine(AppContext.BaseDirectory, "PCL-CE");
        if (File.Exists(apphost) && IsNativeExecutable(apphost)) return Path.GetFullPath(apphost);
        var process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process)) return Path.GetFullPath(process);
        return null;
    }

    /// <summary>是否为本地可执行文件（apphost），而不是 dotnet 宿主。</summary>
    private static bool IsNativeExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) == 4 && header[0] == 0x7F && header[1] == (byte)'E' &&
                   header[2] == (byte)'L' && header[3] == (byte)'F';
        }
        catch (Exception) { return false; }
    }

    private static bool IsDotnetHost(string path) =>
        Path.GetFileName(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    private static string BuildDesktopEntry(string execPath)
    {
        var exec = QuoteExec(execPath);
        // 框架依赖方式（dotnet PCL-CE.dll）启动时，桌面入口需要带上程序集参数。
        var assembly = Path.Combine(AppContext.BaseDirectory, "PCL-CE.dll");
        if (IsDotnetHost(execPath) && File.Exists(assembly)) exec += " " + QuoteExec(assembly);
        return DesktopEntryHeader + "\nExec=" + exec + " %U\n" + DesktopEntryFooter + "\n";
    }

    /// <summary>按 Desktop Entry 规范给 Exec 引号与转义：双引号内的反斜杠、反引号、美元符号需要转义，% 需写成 %%。</summary>
    internal static string QuoteExec(string path)
    {
        var builder = new StringBuilder(path.Length + 8);
        foreach (var character in path)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '`': builder.Append("\\`"); break;
                case '$': builder.Append("\\$"); break;
                case '%': builder.Append("%%"); break;
                case '\n': case '\r': builder.Append(' '); break;
                default: builder.Append(character); break;
            }
        }
        return "\"" + builder + "\"";
    }

    /// <summary>安装全部尺寸图标；返回非 null 表示存在需要用户知道的图标问题。</summary>
    private static string? InstallIcons(IProgress<string>? progress, CancellationToken token)
    {
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        var appDirIcons = IconSizes.Count(size => HasAppImageIcon(appDir, size));
        var fallback = ResolveFallbackAsset();
        var usedFallback = false;
        var missing = 0;
        foreach (var size in IconSizes)
        {
            token.ThrowIfCancellationRequested();
            var bundled = HasAppImageIcon(appDir, size) ? AppImageIcon(appDir!, size) : null;
            var source = bundled ?? fallback;
            if (source is null)
            {
                missing++;
                continue;
            }
            if (bundled is null) usedFallback = true;
            var target = IconPath(size);
            EnsureSharedDirectory(Path.GetDirectoryName(target)!);
            // 先删除可能预先存在的文件/符号链接，避免 File.Copy 跟随符号链接写到目标之外。
            try { if (File.Exists(target) || new FileInfo(target).LinkTarget is not null) File.Delete(target); }
            catch (Exception) { }
            File.Copy(source, target, false);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        if (usedFallback && appDirIcons < IconSizes.Length)
        {
            // 只有一个分辨率时按要求记录日志，便于发行版后续在 AppImage 中补齐多尺寸图标。
            AppLog.Info("桌面集成：仅找到单一分辨率的图标素材，已复制到全部尺寸（建议在 AppImage 中提供 hicolor 各尺寸图标）。");
            progress?.Report("提示：仅找到单一分辨率图标，已复制到全部尺寸。");
        }
        if (fallback is null && appDirIcons == 0)
            return "未找到图标素材（Images/Heads/Logo-CE.png 或 Images/icon.png），桌面入口将没有图标。";
        return missing > 0 ? $"有 {missing} 个尺寸的图标缺失。" : null;
    }

    private static bool HasAppImageIcon(string? appDir, int size) =>
        !string.IsNullOrWhiteSpace(appDir) && File.Exists(AppImageIcon(appDir!, size));

    private static string AppImageIcon(string appDir, int size) =>
        Path.Combine(appDir, "usr", "share", "icons", "hicolor", $"{size}x{size}", "apps", "pcl-ce.png");

    private static string? ResolveFallbackAsset()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Images", "Heads", "Logo-CE.png"),
            Path.Combine(AppContext.BaseDirectory, "Images", "icon.png"),
            Path.Combine(AppContext.BaseDirectory, "pcl-ce.png")
        ];
        foreach (var candidate in candidates)
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch (Exception) { }
        }
        return null;
    }

    private static string IconPath(int size) =>
        Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}", "apps", "pcl-ce.png");

    private static bool ArtifactsComplete() =>
        SafeExists(DesktopFile) && SafeExists(MimeFile) && IconSizes.All(size => SafeExists(IconPath(size)));

    private static void WriteSharedFile(string path, string content)
    {
        EnsureSharedDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew, Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   }))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(content);
            File.Move(temporary, path, true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteState(IntegrationState state)
    {
        var path = LinuxPaths.DesktopIntegrationStatePath;
        EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew, Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   }))
                JsonSerializer.Serialize(stream, state, StateJson);
            File.Move(temporary, path, true);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IntegrationState? ReadState()
    {
        try
        {
            var path = LinuxPaths.DesktopIntegrationStatePath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<IntegrationState>(File.ReadAllText(path), StateJson);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>刷新 MIME 与桌面入口数据库（卸载与安装都会调用；不会修改默认程序关联）。</summary>
    private static async Task RefreshDatabasesAsync(CancellationToken token)
    {
        await RunToolAsync("update-mime-database", [LinuxPaths.UserMime], token).ConfigureAwait(false);
        await RunToolAsync("update-desktop-database", [LinuxPaths.UserApplications], token).ConfigureAwait(false);
    }

    /// <summary>安装时把 .mrpack 默认关联到本应用（尽力而为，仅安装时调用）。</summary>
    private static async Task RegisterDefaultHandlerAsync(CancellationToken token) =>
        await RunToolAsync("xdg-mime", ["default", DesktopFileName, "application/x-mrpack"], token).ConfigureAwait(false);

    /// <summary>尽力执行外部工具；工具不存在时静默跳过，失败只写日志。</summary>
    private static async Task RunToolAsync(string name, string[] arguments, CancellationToken token)
    {
        string? executable;
        try { executable = FindInPath(name); }
        catch (Exception) { executable = null; }
        if (executable is null) return;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return;
            // 必须持续读取被重定向的输出，否则输出量大的工具可能因管道写满而卡住。
            var drainOutput = process.StandardOutput.ReadToEndAsync();
            var drainError = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(drainOutput, drainError).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception) { }
                if (token.IsCancellationRequested) throw;
                AppLog.Info($"{name} 执行超时（已忽略）。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Info($"{name} 执行失败（已忽略）：{ex.Message}");
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

    private static void EnsureSharedDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception)
        {
            // 目录可能被其他程序同时使用，保留即可。
        }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool Matches(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>installation.json 的持久化结构（XDG 状态目录，权限 0600）。</summary>
    private sealed class IntegrationState
    {
        public string? ExecPath { get; set; }
        public string? AppImagePath { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
        public string? Version { get; set; }
    }
}
