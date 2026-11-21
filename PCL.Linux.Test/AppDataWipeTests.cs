using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Portable;

namespace PCL.Linux.Test;

/// <summary>
/// “清空启动器数据”的测试：每个用例把 HOME 与全部 XDG 目录切换到临时目录，
/// 结束后恢复环境变量，保证不会碰到真实用户数据。
/// </summary>
[TestClass]
public sealed class AppDataWipeTests
{
    private static readonly string[] Variables =
        ["HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR"];

    private readonly Dictionary<string, string?> _previous = new();
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Directory.CreateTempSubdirectory("pcl wipe 中文 ").FullName;
        foreach (var name in Variables) _previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable("HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_root, "cache"));
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(_root, "state"));
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(runtime);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>同步收集进度消息，避免 <see cref="Progress{T}"/> 在工作线程上异步回调造成测试竞态。</summary>
    private sealed class Collector : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) { lock (Messages) Messages.Add(value); }
    }

    private static string ManagedGameDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Assert.IsTrue(LinuxPaths.EnsureGameDirectoryMarker(path));
        WriteFile(Path.Combine(path, "saves", "world", "level.dat"), "world");
        WriteFile(Path.Combine(path, "versions", "1.21", "1.21.jar"), "jar");
        return path;
    }

    /// <summary>创建一整套已知大小的假启动器数据。</summary>
    private void CreateAppData()
    {
        WriteFile(Path.Combine(LinuxPaths.ConfigHome, "linux.json"), "{}");
        WriteFile(Path.Combine(LinuxPaths.ConfigHome, "accounts.json"), """{"Microsoft":{"AccessToken":"secret-token"}}""");
        WriteFile(Path.Combine(LinuxPaths.JavaRuntimeHome, "bin", "java"), "#!java");
        WriteFile(Path.Combine(LinuxPaths.ToolsHome, "authlib-injector.jar"), "tool");
        WriteFile(Path.Combine(LinuxPaths.DownloadsCache, "pack.mrpack"), "download");
        WriteFile(Path.Combine(LinuxPaths.CacheHome, "natives", "lib.so"), "native");
        WriteFile(Path.Combine(LinuxPaths.TempHome, "run.tmp"), "temp");
        WriteFile(Path.Combine(LinuxPaths.LogsHome, "pcl-ce.log"), "log");
        WriteFile(Path.Combine(LinuxPaths.RuntimeHome, "instance.lock"), "lock");
    }

    [TestMethod]
    public void InspectReportsPathsSizesAndMissingCategories()
    {
        CreateAppData();
        var game = ManagedGameDirectory(Path.Combine(_root, "game"));
        var entries = AppDataWipe.Inspect(game);

        Assert.AreEqual(9, entries.Count);
        var config = entries.Single(entry => entry.Category == WipeCategory.Config);
        Assert.IsTrue(config.Exists);
        Assert.AreEqual(2, config.SizeBytes);
        Assert.AreEqual(Path.Combine(LinuxPaths.ConfigHome, "linux.json"), config.Paths.Single());
        Assert.IsFalse(config.ExtraWarning);

        var accounts = entries.Single(entry => entry.Category == WipeCategory.Accounts);
        Assert.AreEqual("accounts.json", Path.GetFileName(accounts.Paths.Single()));
        Assert.AreEqual(new FileInfo(accounts.Paths.Single()).Length, accounts.SizeBytes);

        var cache = entries.Single(entry => entry.Category == WipeCategory.Cache);
        Assert.AreEqual(10, cache.SizeBytes); // natives 6 字节 + temp 4 字节，下载缓存不重复统计
        Assert.AreEqual(2, cache.Paths.Count);
        Assert.IsFalse(cache.Paths.Any(path => LinuxPaths.IsSameOrChild(path, LinuxPaths.DownloadsCache)));

        var gameData = entries.Single(entry => entry.Category == WipeCategory.GameData);
        Assert.IsTrue(gameData.ExtraWarning);
        Assert.AreEqual(game, gameData.Paths.Single());
        Assert.IsTrue(gameData.SizeBytes > 0);

        Directory.Delete(LinuxPaths.LogsHome, true);
        var missingLogs = AppDataWipe.Inspect(game).Single(entry => entry.Category == WipeCategory.Logs);
        Assert.IsFalse(missingLogs.Exists);
        Assert.AreEqual(0, missingLogs.SizeBytes);

        var unmarked = AppDataWipe.Inspect(Path.Combine(_root, "unmarked"));
        Assert.IsFalse(unmarked.Any(entry => entry.Category == WipeCategory.GameData));

        Assert.AreEqual("2 B", AppDataWipe.FormatSize(2));
        Assert.AreEqual("1 KB", AppDataWipe.FormatSize(1024));
    }

    [TestMethod]
    public async Task WipeDeletesOnlySelectedCategoriesAndReportsFreedBytes()
    {
        CreateAppData();
        var game = ManagedGameDirectory(Path.Combine(_root, "game"));
        var outside = WriteFile(Path.Combine(_root, "outside.txt"), "keep");
        var logsSize = new FileInfo(Path.Combine(LinuxPaths.LogsHome, "pcl-ce.log")).Length;
        var lockSize = new FileInfo(Path.Combine(LinuxPaths.RuntimeHome, "instance.lock")).Length;
        var accountsSize = new FileInfo(Path.Combine(LinuxPaths.ConfigHome, "accounts.json")).Length;
        var progress = new Collector();

        var result = await AppDataWipe.WipeAsync(
            [WipeCategory.Logs, WipeCategory.RuntimeState, WipeCategory.Accounts], game, progress, default);

        Assert.IsTrue(progress.Messages.Any(message => message.Contains("正在删除", StringComparison.Ordinal)));
        Assert.IsFalse(Directory.Exists(LinuxPaths.LogsHome));
        Assert.IsFalse(Directory.Exists(LinuxPaths.RuntimeHome));
        Assert.IsFalse(File.Exists(Path.Combine(LinuxPaths.ConfigHome, "accounts.json")));
        Assert.IsTrue(File.Exists(Path.Combine(LinuxPaths.ConfigHome, "linux.json")));
        Assert.IsTrue(Directory.Exists(LinuxPaths.JavaRuntimeHome));
        Assert.IsTrue(Directory.Exists(LinuxPaths.ToolsHome));
        Assert.IsTrue(Directory.Exists(game));
        Assert.IsTrue(File.Exists(Path.Combine(game, "saves", "world", "level.dat")));
        Assert.IsTrue(File.Exists(outside));
        Assert.AreEqual(logsSize + lockSize + accountsSize, result.FreedBytes);
        Assert.AreEqual(3, result.Deleted.Count);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.AreEqual(0, result.Skipped.Count);
    }

    [TestMethod]
    public async Task ConfigWipeKeepsUnknownFilesAndPrunesOnlyEmptyDirectory()
    {
        CreateAppData();
        var unknown = WriteFile(Path.Combine(LinuxPaths.ConfigHome, "unknown.txt"), "keep");
        var configSize = new FileInfo(Path.Combine(LinuxPaths.ConfigHome, "linux.json")).Length;

        var result = await AppDataWipe.WipeAsync([WipeCategory.Config], Path.Combine(_root, "missing-game"), null, default);

        Assert.IsFalse(File.Exists(Path.Combine(LinuxPaths.ConfigHome, "linux.json")));
        Assert.IsTrue(File.Exists(unknown));
        Assert.IsTrue(Directory.Exists(LinuxPaths.ConfigHome));
        Assert.IsTrue(result.Skipped.Any(message => message.Contains(LinuxPaths.ConfigHome, StringComparison.Ordinal)));
        Assert.AreEqual(configSize, result.FreedBytes);

        // 未知文件移除后，配置目录完全为空，再清空一次就应该连目录一起删除。
        File.Delete(unknown);
        File.Delete(Path.Combine(LinuxPaths.ConfigHome, "accounts.json"));
        WriteFile(Path.Combine(LinuxPaths.ConfigHome, "linux.json"), "{}");
        var second = await AppDataWipe.WipeAsync([WipeCategory.Config], Path.Combine(_root, "missing-game"), null, default);
        Assert.IsFalse(Directory.Exists(LinuxPaths.ConfigHome));
        Assert.AreEqual(2, second.FreedBytes);
        Assert.AreEqual(1, second.Deleted.Count);
        Assert.AreEqual(0, second.Errors.Count);
    }

    [TestMethod]
    public async Task GameDataRequiresManagedMarkerAndNeverDeletesParentDirectories()
    {
        CreateAppData();
        var unmarked = Path.Combine(_root, "unmarked");
        WriteFile(Path.Combine(unmarked, "saves", "level.dat"), "world");
        var refused = await AppDataWipe.WipeAsync([WipeCategory.GameData], unmarked, null, default);
        Assert.IsTrue(Directory.Exists(unmarked));
        Assert.IsTrue(File.Exists(Path.Combine(unmarked, "saves", "level.dat")));
        Assert.IsTrue(refused.Skipped.Count > 0);
        Assert.AreEqual(0, refused.FreedBytes);
        Assert.AreEqual(0, refused.Deleted.Count);

        // 即使带标记，包含 XDG 数据目录的父目录也绝不允许删除。
        Assert.IsTrue(LinuxPaths.EnsureGameDirectoryMarker(_root));
        var parent = await AppDataWipe.WipeAsync([WipeCategory.GameData], _root, null, default);
        Assert.IsTrue(File.Exists(Path.Combine(LinuxPaths.ConfigHome, "linux.json")));
        Assert.IsTrue(File.Exists(Path.Combine(LinuxPaths.DataHome, "runtime", "bin", "java")));
        Assert.AreEqual(0, parent.FreedBytes);
        Assert.IsTrue(parent.Skipped.Count > 0);

        var managed = ManagedGameDirectory(Path.Combine(_root, "game"));
        var result = await AppDataWipe.WipeAsync([WipeCategory.GameData], managed, null, default);
        Assert.IsFalse(Directory.Exists(managed));
        Assert.AreEqual(managed, result.Deleted.Single());
        Assert.IsTrue(result.FreedBytes > 0);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public async Task SymlinkTargetsAreNeverFollowedOrDeleted()
    {
        var outside = WriteFile(Path.Combine(_root, "outside", "keep.txt"), "keep");
        WriteFile(Path.Combine(LinuxPaths.ToolsHome, "authlib-injector.jar"), "tool");
        Assert.IsNotNull(Directory.CreateSymbolicLink(Path.Combine(LinuxPaths.ToolsHome, "outside-link"), Path.Combine(_root, "outside")));

        var tools = AppDataWipe.Inspect(Path.Combine(_root, "missing-game")).Single(entry => entry.Category == WipeCategory.Tools);
        Assert.AreEqual(4, tools.SizeBytes); // 只统计真实文件，符号链接不计入、也不跟随

        var result = await AppDataWipe.WipeAsync([WipeCategory.Tools], Path.Combine(_root, "missing-game"), null, default);

        Assert.IsFalse(Directory.Exists(LinuxPaths.ToolsHome));
        Assert.IsTrue(File.Exists(outside));
        Assert.AreEqual("keep", File.ReadAllText(outside));
        Assert.AreEqual(LinuxPaths.ToolsHome, result.Deleted.Single());
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public async Task FullWipeRemovesTokensAndSecondRunIsIdempotent()
    {
        CreateAppData();
        var game = ManagedGameDirectory(Path.Combine(_root, "game"));
        var all = Enum.GetValues<WipeCategory>();

        var first = await AppDataWipe.WipeAsync(all, game, null, default);
        Assert.IsTrue(first.FreedBytes > 0, "应释放出空间");
        Assert.AreEqual(0, first.Errors.Count);
        Assert.IsFalse(File.Exists(Path.Combine(LinuxPaths.ConfigHome, "accounts.json")), "账号令牌文件必须被删除");
        Assert.IsFalse(Directory.Exists(LinuxPaths.ConfigHome));
        Assert.IsFalse(Directory.Exists(LinuxPaths.JavaRuntimeHome));
        Assert.IsFalse(Directory.Exists(LinuxPaths.ToolsHome));
        Assert.IsFalse(Directory.Exists(LinuxPaths.DownloadsCache));
        Assert.IsFalse(Directory.Exists(LinuxPaths.LogsHome));
        Assert.IsFalse(Directory.Exists(LinuxPaths.RuntimeHome));
        Assert.IsFalse(Directory.Exists(game));

        var second = await AppDataWipe.WipeAsync(all, game, null, default);
        Assert.AreEqual(0, second.FreedBytes);
        Assert.AreEqual(0, second.Deleted.Count);
        Assert.AreEqual(0, second.Errors.Count);
    }
}
