using System;
using System.IO;
using PCL.Core.Logging;
using PCL.Core.Utils.OS;
using Special = System.Environment.SpecialFolder;

namespace PCL.Core.App;

/// <summary>
/// Application global paths.<br/>
/// <b>NOTE</b>: The behaviors of all path strings depends on <see cref="Path"/> API provided by
/// .NET standard library. You should use <see cref="Path"/> and other APIs relative to it to process
/// any path string from this service, rather than concat paths manually.
/// </summary>
public static class Paths
{
    /// <summary>
    /// The default directory used for relative path combining.
    /// </summary>
    public static string DefaultDirectory => Basics.ExecutableDirectory;

    /// <summary>Linux 应用标识，与 <c>PCL.Portable.LinuxPaths.AppId</c> 一致。</summary>
    private const string LinuxAppId = "pcl-ce";

    private const string LogModule = "Paths";

    private static string _data;
    private static string _sharedData;
    private static string _sharedLocalData;
    private static string _temp;
    
    /// <summary>
    /// Per-instance data directory.
    /// </summary>
    public static string Data { get => _data; set => _data = value; }

    /// <summary>
    /// Shared synchronized data directory.
    /// </summary>
    public static string SharedData { get => _sharedData; set => _sharedData = value; }
    
    /// <summary>
    /// Shared synchronized data directory of old versions.<br/>
    /// Keep the value just for migration, DO NOT USE IT.
    /// </summary>
    public static string OldSharedData { get; set; }

    /// <summary>
    /// Shared local data directory, used to put some large files that can be released or downloaded back anytime.
    /// </summary>
    public static string SharedLocalData { get => _sharedLocalData; set => _sharedLocalData = value; }
    
    /// <summary>
    /// Temporary files directory (can be deleted anytime, except when the program is running).
    /// </summary>
    public static string Temp { get => _temp; set => _temp = value; }

    /// <summary>
    /// Get path string relative to a special folder.
    /// </summary>
    /// <param name="folder">the special folder</param>
    /// <param name="relative">the relative path</param>
    /// <returns>the path string relative to the special folder</returns>
    public static string GetSpecialPath(Special folder, string relative)
    {
        var folderPath = Environment.GetFolderPath(folder);
        return Path.Combine(folderPath, relative);
    }

    static Paths()
    {
#if DEBUG
        const string name = "PCLCE_Debug";
        const string oldName = ".PCLCEDebug";
#else
        const string name = "PCLCE";
        const string oldName = ".PCLCE";
#endif
        if (OperatingSystem.IsLinux())
        {
            // Linux 严格遵循 XDG Base Directory 规范，目录与 PCL.Portable.LinuxPaths 完全一致：
            // 配置 → $XDG_CONFIG_HOME/pcl-ce，数据 → $XDG_DATA_HOME/pcl-ce，
            // 缓存与临时文件 → $XDG_CACHE_HOME/pcl-ce，日志/状态 → $XDG_STATE_HOME/pcl-ce。
            // 注意：PCL.Launcher.Core 引用 PCL.Core，因此这里不能直接调用 LinuxPaths（会形成循环引用），
            // 修改目录布局时必须同时更新两处。
            //
            // Linux 上不读取 PCL_PATH* 调试环境变量，避免把数据写回 /tmp 或可执行文件目录；
            // 需要自定义目录时请使用标准 XDG_* 变量。
            _data = LinuxXdgRoot("XDG_CONFIG_HOME", ".config", "config");
            _sharedData = LinuxXdgRoot("XDG_DATA_HOME", ".local/share", "data");
            _sharedLocalData = LinuxXdgRoot("XDG_CACHE_HOME", ".cache", "cache");
            _temp = Path.Combine(_sharedLocalData, "tmp");
            // 仅用于旧版本迁移的路径；Linux 上并不存在这些旧数据。
            OldSharedData = Path.Combine(_sharedData, "legacy");
            // 目录创建尽力而为：缺少 HOME 等异常环境时不能因此让类型初始化失败。
            EnsureDirectory(_data);
            EnsureDirectory(_sharedData);
            EnsureDirectory(_sharedLocalData);
            EnsureDirectory(_temp);
            return;
        }
        // fill paths
        _data = Path.Combine(DefaultDirectory, "PCL");
        _sharedData = GetSpecialPath(Special.ApplicationData, name);
        _sharedLocalData = GetSpecialPath(Special.LocalApplicationData, name);
        _temp = Path.Combine(Path.GetTempPath(), name);
        OldSharedData = GetSpecialPath(Special.ApplicationData, oldName);
#if DEBUG
        // read environment variables
        EnvironmentInterop.ReadVariable("PCL_PATH", ref _data);
        EnvironmentInterop.ReadVariable("PCL_PATH_SHARED", ref _sharedData);
        EnvironmentInterop.ReadVariable("PCL_PATH_LOCAL", ref _sharedLocalData);
        EnvironmentInterop.ReadVariable("PCL_PATH_TEMP", ref _temp);
#endif
        // create directories
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_sharedData);
        Directory.CreateDirectory(_sharedLocalData);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>
    /// 解析 Linux 下的 XDG 根目录（<c>$XDG_*_HOME/pcl-ce</c>）。
    /// 环境异常（HOME 未设置等）时绝不抛异常：优先使用 passwd 中的主目录；
    /// 仍不可用时退回到 <c>XDG_RUNTIME_DIR</c>，最后才使用程序目录下的隐藏目录，
    /// 任何情况下都不会写入 /tmp。
    /// </summary>
    private static string LinuxXdgRoot(string variable, string fallbackRelativeToHome, string kind)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (IsUsableRoot(value)) return Path.Combine(Path.GetFullPath(value!), LinuxAppId);

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!IsUsableRoot(home)) home = Environment.GetFolderPath(Special.UserProfile);
        if (IsUsableRoot(home)) return Path.Combine(Path.GetFullPath(home!), fallbackRelativeToHome, LinuxAppId);

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (IsUsableRoot(runtime))
        {
            Warn($"{variable} 与 HOME 均不可用，临时改用运行时目录 {runtime}。");
            return Path.Combine(Path.GetFullPath(runtime!), LinuxAppId, kind);
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, ".pcl-ce-data", kind);
        Warn($"无法确定用户主目录（HOME 未设置），暂时使用 {fallback}。请以正常桌面会话启动。");
        return fallback;
    }

    private static bool IsUsableRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    /// <summary>创建目录并收紧为仅属主可访问；失败时记录警告而不是抛出（无 HOME 等环境下也能继续）。</summary>
    private static void EnsureDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) return;
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Warn($"无法创建目录 {path}：{ex.Message}");
        }
    }

    /// <summary>
    /// 记录 Linux 路径回退警告。PCL.Portable.AppLog 位于 PCL.Launcher.Core（引用了本程序集），
    /// 此处无法直接调用，故使用 PCL.Core 的日志门面并同时输出到标准错误。
    /// </summary>
    private static void Warn(string message)
    {
        try { LogWrapper.Warn(LogModule, message); }
        catch (Exception) { }
        try { Console.Error.WriteLine($"[Paths] {message}"); }
        catch (Exception) { }
    }
}
