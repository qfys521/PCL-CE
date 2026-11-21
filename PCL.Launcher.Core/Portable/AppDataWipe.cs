using System.Globalization;

namespace PCL.Portable;

/// <summary>“清空启动器数据”中用户可勾选的数据分类。</summary>
public enum WipeCategory
{
    /// <summary>启动器配置：<c>$XDG_CONFIG_HOME/pcl-ce/linux.json</c>。</summary>
    Config,
    /// <summary>账号令牌：<c>$XDG_CONFIG_HOME/pcl-ce/accounts.json</c>（见 <c>LinuxAccounts.FilePath</c>）。</summary>
    Accounts,
    /// <summary>自动下载的 Java 运行环境：<c>$XDG_DATA_HOME/pcl-ce/runtime</c>。</summary>
    JavaRuntime,
    /// <summary>启动器工具（authlib-injector、EasyTier 等）：<c>$XDG_DATA_HOME/pcl-ce/tools</c>。</summary>
    Tools,
    /// <summary>下载缓存：<c>$XDG_CACHE_HOME/pcl-ce/downloads</c>。</summary>
    DownloadsCache,
    /// <summary>缓存目录中除下载缓存与运行时目录以外的内容（原生库、临时文件等）。</summary>
    Cache,
    /// <summary>启动器日志：<c>$XDG_STATE_HOME/pcl-ce/logs</c>。</summary>
    Logs,
    /// <summary>运行时状态（单实例锁、socket）：<c>$XDG_RUNTIME_DIR/pcl-ce</c>。</summary>
    RuntimeState,
    /// <summary>游戏目录中的版本、存档、模组与整合包；仅在目录带启动器管理标记时可用。</summary>
    GameData
}

/// <summary>一个数据分类的当前状态：路径、占用空间以及是否还存在。</summary>
/// <param name="Category">分类。</param>
/// <param name="Label">界面与确认框中的中文名称。</param>
/// <param name="Description">中文说明。</param>
/// <param name="Paths">实际会被删除的路径（可能不止一个，例如缓存分类）。</param>
/// <param name="SizeBytes">递归统计的占用字节数；不存在时为 0，符号链接本身不计入。</param>
/// <param name="Exists">是否还有可删除的内容。</param>
/// <param name="ExtraWarning">是否需要额外的危险提示（游戏数据）。</param>
public sealed record WipeEntry(
    WipeCategory Category,
    string Label,
    string Description,
    IReadOnlyList<string> Paths,
    long SizeBytes,
    bool Exists,
    bool ExtraWarning);

/// <summary>一次清空操作的结果。</summary>
/// <param name="Deleted">已经成功删除的路径（分类的顶层目标）。</param>
/// <param name="Skipped">因安全策略或无法完全删除而保留的内容，附带中文说明。</param>
/// <param name="FreedBytes">实际释放的字节数。</param>
/// <param name="Errors">删除过程中遇到的错误，附带中文说明。</param>
public sealed record WipeResult(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Skipped,
    long FreedBytes,
    IReadOnlyList<string> Errors);

/// <summary>
/// “清空全部 Linux 数据”的实现。
/// <para>只删除 <see cref="LinuxPaths"/> 中由启动器完全拥有的路径，以及带
/// <c>.pcl-ce-managed</c> 标记的游戏目录；系统目录、用户主目录、其他应用与其他未知文件一律不碰。</para>
/// <para>所有 IO 错误都会被记录到 <see cref="WipeResult.Errors"/> 而不是抛出，便于界面完整展示。</para>
/// </summary>
public static class AppDataWipe
{
    /// <summary>启动器配置文件名（<c>LinuxSettings</c> 使用）。</summary>
    public const string ConfigFileName = "linux.json";

    /// <summary>账号令牌文件名（<c>LinuxAccounts.FilePath</c> 使用同一路径）。</summary>
    public const string AccountsFileName = "accounts.json";

    /// <summary>配置文件完整路径。</summary>
    public static string ConfigFilePath => Path.Combine(LinuxPaths.ConfigHome, ConfigFileName);

    /// <summary>账号令牌完整路径，与 <c>PCL.Linux.LinuxAccounts.FilePath</c>（$XDG_CONFIG_HOME/pcl-ce/accounts.json）保持一致。</summary>
    public static string AccountsFilePath => Path.Combine(LinuxPaths.ConfigHome, AccountsFileName);

    /// <summary>不跟随符号链接、跳过不可访问条目的目录枚举选项。</summary>
    private static readonly EnumerationOptions SafeEnumeration = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple
    };

    /// <summary>
    /// 统计各个分类当前的路径与占用空间。只读操作：不创建、不删除任何文件。
    /// <paramref name="gameDirectory"/> 只有在带 <see cref="LinuxPaths.ManagedMarkerName"/> 标记时才会作为
    /// <see cref="WipeCategory.GameData"/> 出现在结果中。
    /// </summary>
    public static IReadOnlyList<WipeEntry> Inspect(string gameDirectory)
    {
        var entries = new List<WipeEntry>(9);

        entries.Add(FileEntry(WipeCategory.Config, "配置文件",
            "启动选项、游戏目录、Java、内存与外观等设置（$XDG_CONFIG_HOME/pcl-ce/linux.json）", ConfigFilePath));
        entries.Add(FileEntry(WipeCategory.Accounts, "账号与令牌",
            "Microsoft 与第三方认证账号的登录令牌（$XDG_CONFIG_HOME/pcl-ce/accounts.json）", AccountsFilePath));
        entries.Add(DirectoryEntry(WipeCategory.JavaRuntime, "Java 运行时",
            "自动下载的 Java 运行环境，删除后需要时会重新下载（$XDG_DATA_HOME/pcl-ce/runtime）", LinuxPaths.JavaRuntimeHome));
        entries.Add(DirectoryEntry(WipeCategory.Tools, "启动器工具",
            "authlib-injector、EasyTier 等启动器工具，删除后会按需重新下载（$XDG_DATA_HOME/pcl-ce/tools）", LinuxPaths.ToolsHome));
        entries.Add(DirectoryEntry(WipeCategory.DownloadsCache, "下载缓存",
            "整合包与资源文件的下载缓存，删除后只影响下次下载速度（$XDG_CACHE_HOME/pcl-ce/downloads）", LinuxPaths.DownloadsCache));

        var cachePaths = CachePaths();
        entries.Add(new WipeEntry(WipeCategory.Cache, "其他缓存与临时文件",
            "原生库、临时文件等缓存；不含下载缓存与运行时状态（$XDG_CACHE_HOME/pcl-ce）",
            cachePaths, cachePaths.Sum(MeasureTree), cachePaths.Count > 0, false));

        entries.Add(DirectoryEntry(WipeCategory.Logs, "运行日志",
            "启动器与游戏日志；排查问题前建议保留（$XDG_STATE_HOME/pcl-ce/logs）", LinuxPaths.LogsHome));
        entries.Add(DirectoryEntry(WipeCategory.RuntimeState, "运行时状态",
            "单实例锁与转发 socket，退出启动器后也会重新创建（$XDG_RUNTIME_DIR/pcl-ce）", LinuxPaths.RuntimeHome));

        // 游戏目录不是应用数据：必须由启动器写入标记文件后才允许清理。
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            var game = Normalize(gameDirectory);
            if (game is not null && LinuxPaths.IsManagedGameDirectory(game))
                entries.Add(new WipeEntry(WipeCategory.GameData, "游戏数据",
                    $"游戏目录中的版本、存档、模组与整合包：{game}（仅当目录带 {LinuxPaths.ManagedMarkerName} 标记时才会删除）",
                    [game], MeasureTree(game), true, true));
        }

        return entries;
    }

    /// <summary>
    /// 清空选中的分类。只删除属于启动器管理的路径；游戏目录还需要管理标记，并且只删除配置的目录本身。
    /// 遇到 IO 错误会记录到结果中，不会向外抛出（取消操作除外）。
    /// </summary>
    public static Task<WipeResult> WipeAsync(IEnumerable<WipeCategory> categories, string gameDirectory,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var selected = categories.Distinct().ToArray();
        return Task.Run(() => WipeCore(selected, gameDirectory, progress, token), token);
    }

    /// <summary>把字节数格式化为中文界面常用的容量文本（如 <c>12.3 MB</c>）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
            : value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static WipeResult WipeCore(IReadOnlyList<WipeCategory> categories, string gameDirectory,
        IProgress<string>? progress, CancellationToken token)
    {
        var context = new WipeContext(progress, token);
        foreach (var category in categories)
        {
            token.ThrowIfCancellationRequested();
            switch (category)
            {
                case WipeCategory.Config:
                    WipeTarget(context, ConfigFilePath);
                    break;
                case WipeCategory.Accounts:
                    WipeTarget(context, AccountsFilePath);
                    break;
                case WipeCategory.JavaRuntime:
                    WipeTarget(context, LinuxPaths.JavaRuntimeHome);
                    break;
                case WipeCategory.Tools:
                    WipeTarget(context, LinuxPaths.ToolsHome);
                    break;
                case WipeCategory.DownloadsCache:
                    WipeTarget(context, LinuxPaths.DownloadsCache);
                    break;
                case WipeCategory.Cache:
                    foreach (var path in CachePaths()) WipeTarget(context, path);
                    break;
                case WipeCategory.Logs:
                    WipeTarget(context, LinuxPaths.LogsHome);
                    break;
                case WipeCategory.RuntimeState:
                    WipeTarget(context, LinuxPaths.RuntimeHome);
                    break;
                case WipeCategory.GameData:
                    WipeGameData(context, gameDirectory);
                    break;
            }
        }
        // 账号令牌可能就在配置目录中，等其他分类都处理完再决定是否移除空的配置目录。
        if (categories.Contains(WipeCategory.Config)) PruneConfigDirectory(context);
        return new WipeResult(context.Deleted.ToArray(), context.Skipped.ToArray(),
            context.FreedBytes, context.Errors.ToArray());
    }

    /// <summary>删除一个分类目标；不是启动器管理路径时只记录跳过，不执行删除。</summary>
    private static void WipeTarget(WipeContext context, string path)
    {
        var full = Normalize(path);
        if (full is null)
        {
            context.Errors.Add($"路径无效：{path}");
            return;
        }
        if (!PathExists(full))
        {
            context.Progress?.Report("跳过不存在的路径：" + full);
            return;
        }
        bool managed;
        try { managed = IsWipeablePath(full); }
        catch (Exception ex)
        {
            context.Skipped.Add($"无法校验路径归属，已跳过：{full}（{ex.Message}）");
            return;
        }
        if (!managed)
        {
            context.Skipped.Add($"已跳过非启动器管理路径：{full}");
            return;
        }
        context.Progress?.Report("正在删除：" + full);
        context.FreedBytes += DeleteTree(full, context.Errors, context.Token);
        if (PathExists(full)) context.Skipped.Add($"未能完全删除，已保留剩余内容：{full}");
        else context.Deleted.Add(full);
    }

    /// <summary>删除游戏目录：必须带管理标记，只删除该目录本身，绝不删除父目录或符号链接指向的目标。</summary>
    private static void WipeGameData(WipeContext context, string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            context.Skipped.Add("未配置游戏目录，已跳过游戏数据。");
            return;
        }
        var game = Normalize(gameDirectory);
        if (game is null)
        {
            context.Skipped.Add($"游戏目录无效，已跳过：{gameDirectory}");
            return;
        }
        if (!PathExists(game))
        {
            context.Progress?.Report("游戏目录不存在，已跳过：" + game);
            return;
        }
        // 只有启动器自己创建过标记文件的目录才允许删除；用户手动选择的普通 Minecraft 目录不会被碰。
        if (!LinuxPaths.IsManagedGameDirectory(game))
        {
            context.Skipped.Add($"游戏目录缺少 {LinuxPaths.ManagedMarkerName} 标记，拒绝删除：{game}");
            return;
        }
        if (IsProtectedDirectory(game))
        {
            context.Skipped.Add($"该路径是用户主目录、系统目录或应用数据目录的上级，拒绝删除：{game}");
            return;
        }
        var link = LinkTargetOf(game);
        if (link is not null)
        {
            context.Skipped.Add($"游戏目录是符号链接，拒绝删除：{game}");
            return;
        }
        context.Progress?.Report("正在删除游戏数据：" + game);
        context.FreedBytes += DeleteTree(game, context.Errors, context.Token);
        if (PathExists(game)) context.Skipped.Add($"未能完全删除，已保留剩余内容：{game}");
        else context.Deleted.Add(game);
    }

    /// <summary>配置目录里可能还有未识别文件；只有在完全为空时才移除目录。</summary>
    private static void PruneConfigDirectory(WipeContext context)
    {
        var directory = LinuxPaths.ConfigHome;
        try
        {
            if (!Directory.Exists(directory)) return;
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                context.Skipped.Add($"配置目录是符号链接，已保留：{directory}");
                return;
            }
            if (new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SafeEnumeration).Any())
            {
                context.Skipped.Add($"配置目录中还有其他内容（例如未勾选的账号令牌），已保留：{directory}");
                return;
            }
            Directory.Delete(directory);
            context.Progress?.Report("已移除空的配置目录：" + directory);
        }
        catch (Exception ex)
        {
            context.Skipped.Add($"已保留配置目录：{directory}（{ex.Message}）");
        }
    }

    /// <summary>缓存目录的直接子项，排除单独成类的下载缓存与运行时目录，避免重复统计或删除正在使用的状态。</summary>
    private static IReadOnlyList<string> CachePaths()
    {
        var result = new List<string>();
        var root = LinuxPaths.CacheHome;
        var downloads = Path.TrimEndingDirectorySeparator(LinuxPaths.DownloadsCache);
        var runtime = Path.TrimEndingDirectorySeparator(LinuxPaths.RuntimeHome);
        try
        {
            if (!Directory.Exists(root)) return result;
            foreach (var child in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SafeEnumeration))
            {
                var full = Normalize(child.FullName);
                if (full is null) continue;
                if (LinuxPaths.IsSameOrChild(full, downloads) || LinuxPaths.IsSameOrChild(downloads, full)) continue;
                if (LinuxPaths.IsSameOrChild(full, runtime) || LinuxPaths.IsSameOrChild(runtime, full)) continue;
                result.Add(full);
            }
        }
        catch (Exception) { }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// 目标是否允许删除。<see cref="LinuxPaths.IsManagedPath"/> 只接受数据根目录下的子路径，
    /// 而运行时状态目录（$XDG_RUNTIME_DIR/pcl-ce）本身就是启动器完全拥有的根目录，需要单独放行。
    /// </summary>
    private static bool IsWipeablePath(string path)
    {
        if (LinuxPaths.IsManagedPath(path)) return true;
        var runtime = Path.TrimEndingDirectorySeparator(LinuxPaths.RuntimeHome);
        return string.Equals(Path.TrimEndingDirectorySeparator(path), runtime, StringComparison.Ordinal);
    }

    /// <summary>判断路径是否为不允许删除的上级目录：根目录、用户主目录，或任一应用数据根目录的上级（含根目录本身）。</summary>
    private static bool IsProtectedDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(path);
        if (full.Length == 0 || string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal)) return true;
        string home;
        try { home = Path.TrimEndingDirectorySeparator(LinuxPaths.Home); }
        catch (Exception) { home = ""; }
        if (home.Length > 0 && string.Equals(full, home, StringComparison.Ordinal)) return true;
        foreach (var root in LinuxPaths.ManagedRoots)
            if (LinuxPaths.IsSameOrChild(Path.TrimEndingDirectorySeparator(root), full)) return true;
        return false;
    }

    /// <summary>递归统计大小：忽略错误，不跟随符号链接（链接本身按 0 字节计）。</summary>
    private static long MeasureTree(string path)
    {
        var full = Normalize(path);
        if (full is null) return 0;
        try
        {
            if (LinkTargetOf(full) is not null) return 0;
            if (Directory.Exists(full))
            {
                long total = 0;
                foreach (var child in new DirectoryInfo(full).EnumerateFileSystemInfos("*", SafeEnumeration))
                {
                    try
                    {
                        if (LinkTargetOf(child.FullName) is not null) continue;
                        if (child is DirectoryInfo) total += MeasureTree(child.FullName);
                        else if (child is FileInfo file) total += file.Length;
                    }
                    catch (Exception) { }
                }
                return total;
            }
            var info = new FileInfo(full);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// 递归删除，返回实际释放的字节数。符号链接只删除链接本身；无法删除的条目记录错误后继续处理其余内容。
    /// </summary>
    private static long DeleteTree(string path, List<string> errors, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var full = Normalize(path);
        if (full is null)
        {
            errors.Add($"路径无效：{path}");
            return 0;
        }
        if (LinkTargetOf(full) is not null)
        {
            // 绝不进入符号链接目标，只移除链接本身。
            try { File.Delete(full); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { errors.Add($"符号链接 {full}：{ex.Message}"); }
            return 0;
        }
        if (Directory.Exists(full))
        {
            long freed = 0;
            try
            {
                foreach (var child in new DirectoryInfo(full).EnumerateFileSystemInfos("*", SafeEnumeration))
                    freed += DeleteTree(child.FullName, errors, token);
            }
            catch (Exception ex) { errors.Add($"目录 {full}：{ex.Message}"); }
            try { Directory.Delete(full); }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { errors.Add($"目录 {full}：{ex.Message}"); }
            return freed;
        }
        if (!File.Exists(full)) return 0;
        long length = 0;
        try { length = new FileInfo(full).Length; } catch (Exception) { }
        try { File.Delete(full); return Math.Max(0, length); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
        catch (Exception ex)
        {
            errors.Add($"文件 {full}：{ex.Message}");
            return 0;
        }
    }

    /// <summary>符号链接目标；不是符号链接（或路径不存在）时返回 null。</summary>
    private static string? LinkTargetOf(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception) { return null; }
    }

    /// <summary>路径是否存在；悬空符号链接也算存在（需要单独删除链接本身）。</summary>
    private static bool PathExists(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) return true;
            return LinkTargetOf(path) is not null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>规范化为完整路径并去掉末尾分隔符；失败时返回 null。</summary>
    private static string? Normalize(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception) { return null; }
    }

    private static WipeEntry FileEntry(WipeCategory category, string label, string description, string path)
    {
        var full = Normalize(path) ?? path;
        return new WipeEntry(category, label, description, [full], MeasureTree(full), File.Exists(full), false);
    }

    private static WipeEntry DirectoryEntry(WipeCategory category, string label, string description, string path)
    {
        var full = Normalize(path) ?? path;
        return new WipeEntry(category, label, description, [full], MeasureTree(full), Directory.Exists(full), false);
    }

    private sealed class WipeContext(IProgress<string>? progress, CancellationToken token)
    {
        public IProgress<string>? Progress { get; } = progress;
        public CancellationToken Token { get; } = token;
        public List<string> Deleted { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> Errors { get; } = [];
        public long FreedBytes { get; set; }
    }
}
