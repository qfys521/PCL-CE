using System.Runtime.InteropServices;

namespace PCL.Portable;

/// <summary>
/// Linux 路径约定。所有持久化数据严格遵循 XDG Base Directory 规范：
/// <list type="bullet">
/// <item>配置：<c>$XDG_CONFIG_HOME/pcl-ce</c>（默认 <c>~/.config/pcl-ce</c>）</item>
/// <item>数据（Java 运行时、工具、整合包缓存）：<c>$XDG_DATA_HOME/pcl-ce</c>（默认 <c>~/.local/share/pcl-ce</c>）</item>
/// <item>缓存（下载缓存、原生库临时目录）：<c>$XDG_CACHE_HOME/pcl-ce</c>（默认 <c>~/.cache/pcl-ce</c>）</item>
/// <item>状态与日志：<c>$XDG_STATE_HOME/pcl-ce</c>（默认 <c>~/.local/state/pcl-ce</c>）</item>
/// <item>运行时目录（单实例锁、socket）：<c>$XDG_RUNTIME_DIR/pcl-ce</c></item>
/// </list>
/// 游戏目录不是应用数据，默认放在 <c>~/PCL-CE</c>，可在设置中修改。
/// 除用户明确选择的游戏目录外，应用不会写入 $HOME 根目录、/tmp 或当前工作目录。
/// </summary>
public static class LinuxPaths
{
    public const string AppId = "pcl-ce";
    public const string GameDirectoryName = "PCL-CE";
    public const string ManagedMarkerName = ".pcl-ce-managed";

    /// <summary>用户主目录；未设置 HOME 时抛出可读异常。</summary>
    public static string Home
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
                throw new InvalidOperationException("无法确定用户主目录：HOME 环境变量未设置。请以正常桌面会话启动。");
            return Path.GetFullPath(home);
        }
    }

    public static string ConfigHome => Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), AppId);
    public static string DataHome => Path.Combine(XdgDirectory("XDG_DATA_HOME", ".local/share"), AppId);
    public static string CacheHome => Path.Combine(XdgDirectory("XDG_CACHE_HOME", ".cache"), AppId);
    public static string StateHome => Path.Combine(XdgDirectory("XDG_STATE_HOME", ".local/state"), AppId);

    /// <summary>运行时目录。XDG_RUNTIME_DIR 未设置时（理论上仅非桌面会话）退回到 $XDG_CACHE_HOME/pcl-ce/run，保证不写入 /tmp。</summary>
    public static string RuntimeHome
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) && Directory.Exists(value))
                return Path.Combine(Path.GetFullPath(value), AppId);
            return Path.Combine(XdgDirectory("XDG_CACHE_HOME", ".cache"), AppId, "run");
        }
    }

    /// <summary>默认游戏目录 ~/PCL-CE。</summary>
    public static string DefaultGameDirectory => Path.Combine(Home, GameDirectoryName);

    /// <summary>Java 运行时目录 $XDG_DATA_HOME/pcl-ce/runtime。</summary>
    public static string JavaRuntimeHome => Path.Combine(DataHome, "runtime");

    /// <summary>authlib-injector / EasyTier 等启动器工具。</summary>
    public static string ToolsHome => Path.Combine(DataHome, "tools");

    /// <summary>启动器自身缓存（整合包下载、资源索引副本等）。</summary>
    public static string DownloadsCache => Path.Combine(CacheHome, "downloads");

    /// <summary>游戏原生库临时目录。</summary>
    public static string NativesCache => Path.Combine(CacheHome, "natives");

    /// <summary>应用临时目录，永不使用 /tmp。</summary>
    public static string TempHome => Path.Combine(CacheHome, "tmp");

    /// <summary>应用日志目录。</summary>
    public static string LogsHome => Path.Combine(StateHome, "logs");

    public static string AppLogPath => Path.Combine(LogsHome, "pcl-ce.log");

    /// <summary>单实例锁文件。</summary>
    public static string LockPath => Path.Combine(RuntimeHome, "instance.lock");

    /// <summary>单实例参数转发 socket。</summary>
    public static string SocketPath => Path.Combine(RuntimeHome, "instance.sock");

    /// <summary>桌面集成状态（记录上次安装的 AppImage 路径等）。</summary>
    public static string DesktopIntegrationStatePath => Path.Combine(StateHome, "desktop-integration.json");

    /// <summary>用户级桌面入口目录。</summary>
    public static string UserApplications => Path.Combine(XdgDirectory("XDG_DATA_HOME", ".local/share"), "applications");

    /// <summary>用户级图标目录。</summary>
    public static string UserIcons => Path.Combine(XdgDirectory("XDG_DATA_HOME", ".local/share"), "icons");

    /// <summary>用户级 MIME 目录。</summary>
    public static string UserMime => Path.Combine(XdgDirectory("XDG_DATA_HOME", ".local/share"), "mime");

    /// <summary>当前 AppImage 路径（由 AppImage 运行时注入），非 AppImage 运行时为空。</summary>
    public static string? AppImagePath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("APPIMAGE");
            return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ? null : Path.GetFullPath(value);
        }
    }

    /// <summary>应用可执行文件路径（AppImage 内为 APPDIR/AppRun 目标）。</summary>
    public static string AppExecutablePath
    {
        get
        {
            if (AppImagePath is { } image) return image;
            var executable = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executable) ? Path.Combine(AppContext.BaseDirectory, "PCL-CE") : Path.GetFullPath(executable);
        }
    }

    /// <summary>所有由应用完全拥有的数据根目录（删除数据时只允许这些目录及其子目录）。</summary>
    public static IReadOnlyList<string> ManagedRoots => [ConfigHome, DataHome, CacheHome, StateHome, RuntimeHome];

    /// <summary>创建应用需要的全部目录（0700）。</summary>
    public static void EnsureDirectories()
    {
        foreach (var directory in new[] { ConfigHome, DataHome, CacheHome, StateHome, RuntimeHome, JavaRuntimeHome, ToolsHome, LogsHome, TempHome })
            EnsureDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CleanupTemp();
    }

    /// <summary>创建目录并强制权限（仅属主可访问）。</summary>
    public static void EnsureDirectory(string path, UnixFileMode mode)
    {
        if (Directory.Exists(path))
        {
            // 已存在的目录补齐属主权限（不削弱已有权限），保证应用私有目录至少 0700。
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var current = File.GetUnixFileMode(path);
                    if ((current & mode) != mode) File.SetUnixFileMode(path, current | mode);
                }
                catch (Exception) { }
            }
            return;
        }
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, mode);
    }

    /// <summary>为本次运行创建独立的临时子目录（位于缓存目录内，退出后由调用方或下次启动清理）。</summary>
    public static string CreateTempDirectory(string prefix = "run")
    {
        EnsureDirectory(TempHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(TempHome, $"{prefix}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>清理超过 3 天或属于已退出进程的临时目录。</summary>
    public static void CleanupTemp()
    {
        if (!Directory.Exists(TempHome)) return;
        foreach (var directory in Directory.EnumerateDirectories(TempHome))
        {
            try
            {
                var name = Path.GetFileName(directory);
                var parts = name.Split('-');
                var stale = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromDays(3);
                if (!stale && parts.Length > 1 && int.TryParse(parts[1], out var pid))
                    stale = !Directory.Exists($"/proc/{pid}");
                if (stale) Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>判断路径是否位于某个应用数据根目录内（用于数据清理与安全校验）。</summary>
    public static bool IsManagedPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        foreach (var root in ManagedRoots)
            if (IsSameOrChild(full, root) && full.Length > root.TrimEnd('/').Length) return true;
        return false;
    }

    /// <summary>判断两个路径是否为相同或父子关系（区分大小写，与 Linux 文件系统一致）。</summary>
    public static bool IsSameOrChild(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return normalizedPath == normalizedRoot ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>游戏目录由启动器管理时写入标记文件；数据清理只会删除带标记的游戏目录。</summary>
    public static bool EnsureGameDirectoryMarker(string gameDirectory)
    {
        try
        {
            if (!Directory.Exists(gameDirectory)) Directory.CreateDirectory(gameDirectory);
            var marker = Path.Combine(gameDirectory, ManagedMarkerName);
            if (!File.Exists(marker)) File.WriteAllText(marker, "PCL-CE Linux managed game directory\n");
            return true;
        }
        catch (Exception) { return false; }
    }

    public static bool IsManagedGameDirectory(string gameDirectory) =>
        File.Exists(Path.Combine(gameDirectory, ManagedMarkerName));

    /// <summary>
    /// 迁移旧版本（0.2.x 预览）放在游戏目录里的应用数据到 XDG 目录。
    /// 仅移动启动器自己的子目录，不触碰游戏存档/模组。返回中文日志行。
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacyData(string gameDirectory)
    {
        var log = new List<string>();
        void MoveDirectory(string source, string target, string description)
        {
            try
            {
                if (!Directory.Exists(source)) return;
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return;
                EnsureDirectory(Path.GetDirectoryName(target)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Move(source, target);
                log.Add($"已迁移{description}：{source} → {target}");
            }
            catch (Exception ex) { log.Add($"迁移{description}失败（已保留原目录）：{ex.Message}"); }
        }
        MoveDirectory(Path.Combine(gameDirectory, ".pcl-java"), JavaRuntimeHome, "旧版 Java 运行时");
        MoveDirectory(Path.Combine(gameDirectory, ".pcl-tools"), ToolsHome, "旧版启动器工具");
        MoveDirectory(Path.Combine(gameDirectory, ".pcl-downloads"), DownloadsCache, "旧版下载缓存");
        try
        {
            var stale = Path.Combine(gameDirectory, ".pcl-natives");
            if (Directory.Exists(stale)) { Directory.Delete(stale, true); log.Add("已清理旧版原生库临时目录"); }
        }
        catch (Exception) { }
        return log;
    }

    private static string XdgDirectory(string variable, string fallbackRelativeToHome)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        return Path.Combine(Home, fallbackRelativeToHome);
    }

    /// <summary>当前进程是否运行在 Wayland 会话（通过 XWayland 使用 X11 协议）。</summary>
    public static bool IsWaylandSession =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    /// <summary>诊断信息：路径与后端，供 --diagnose 与“系统信息”页面使用。</summary>
    public static IReadOnlyList<(string Name, string Value)> Describe()
    {
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
        var runtime = RuntimeHome;
        return
        [
            ("配置目录", ConfigHome),
            ("数据目录", DataHome),
            ("缓存目录", CacheHome),
            ("状态/日志目录", StateHome),
            ("运行时目录", runtime),
            ("Java 运行时目录", JavaRuntimeHome),
            ("默认游戏目录", DefaultGameDirectory),
            ("AppImage", AppImagePath ?? "（非 AppImage 运行）"),
            ("桌面会话", session + (IsWaylandSession ? "（Wayland，经 XWayland 运行）" : "（X11）")),
            ("XDG_RUNTIME_DIR", Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "未设置"),
            ("架构", RuntimeInformation.ProcessArchitecture.ToString()),
            ("运行时", $".NET {Environment.Version} ({RuntimeInformation.RuntimeIdentifier})")
        ];
    }
}
