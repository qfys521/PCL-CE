using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Portable;

namespace PCL.Linux.Test;

/// <summary>
/// XDG 路径规范测试：环境变量覆盖、默认值、路径安全判断、旧数据迁移、
/// 启动器缓存/日志目录，以及 AppLog 落盘位置。
/// 这些测试会临时修改进程环境变量，因此必须顺序执行（MSTest 默认即顺序执行）。
/// </summary>
[TestClass]
public sealed class XdgPathsTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateTempSubdirectory("pcl xdg ").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>临时设置/清除环境变量，并在 Dispose 时恢复原值。</summary>
    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly List<(string Name, string? Value)> _original = [];

        public EnvironmentOverride(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _original.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private EnvironmentOverride AllXdgSet(string home)
    {
        Directory.CreateDirectory(Path.Combine(_root, "run"));
        return new EnvironmentOverride(
            ("HOME", home),
            ("XDG_CONFIG_HOME", Path.Combine(_root, "config")),
            ("XDG_DATA_HOME", Path.Combine(_root, "data")),
            ("XDG_CACHE_HOME", Path.Combine(_root, "cache")),
            ("XDG_STATE_HOME", Path.Combine(_root, "state")),
            ("XDG_RUNTIME_DIR", Path.Combine(_root, "run")));
    }

    private static EnvironmentOverride AllXdgUnset(string home) => new(
        ("HOME", home),
        ("XDG_CONFIG_HOME", null),
        ("XDG_DATA_HOME", null),
        ("XDG_CACHE_HOME", null),
        ("XDG_STATE_HOME", null),
        ("XDG_RUNTIME_DIR", null));

    [TestMethod]
    public void XdgEnvironmentOverridesResolveEveryPath()
    {
        var home = Path.Combine(_root, "home");
        using var env = AllXdgSet(home);
        Assert.AreEqual(Path.Combine(_root, "config", "pcl-ce"), LinuxPaths.ConfigHome);
        Assert.AreEqual(Path.Combine(_root, "data", "pcl-ce"), LinuxPaths.DataHome);
        Assert.AreEqual(Path.Combine(_root, "cache", "pcl-ce"), LinuxPaths.CacheHome);
        Assert.AreEqual(Path.Combine(_root, "state", "pcl-ce"), LinuxPaths.StateHome);
        Assert.AreEqual(Path.Combine(_root, "run", "pcl-ce"), LinuxPaths.RuntimeHome);
        Assert.AreEqual(Path.Combine(LinuxPaths.DataHome, "runtime"), LinuxPaths.JavaRuntimeHome);
        Assert.AreEqual(Path.Combine(LinuxPaths.DataHome, "tools"), LinuxPaths.ToolsHome);
        Assert.AreEqual(Path.Combine(LinuxPaths.CacheHome, "downloads"), LinuxPaths.DownloadsCache);
        Assert.AreEqual(Path.Combine(LinuxPaths.CacheHome, "natives"), LinuxPaths.NativesCache);
        Assert.AreEqual(Path.Combine(LinuxPaths.CacheHome, "tmp"), LinuxPaths.TempHome);
        Assert.AreEqual(Path.Combine(LinuxPaths.StateHome, "logs"), LinuxPaths.LogsHome);
        Assert.AreEqual(Path.Combine(LinuxPaths.LogsHome, "pcl-ce.log"), LinuxPaths.AppLogPath);
        Assert.AreEqual(Path.Combine(home, "PCL-CE"), LinuxPaths.DefaultGameDirectory);
        CollectionAssert.AreEquivalent(
            new[] { LinuxPaths.ConfigHome, LinuxPaths.DataHome, LinuxPaths.CacheHome, LinuxPaths.StateHome, LinuxPaths.RuntimeHome },
            LinuxPaths.ManagedRoots.ToArray());
    }

    [TestMethod]
    public void XdgDefaultsApplyWhenVariablesAreUnsetAndRelativeValuesAreIgnored()
    {
        var home = Path.Combine(_root, "home");
        using (AllXdgUnset(home))
        {
            Assert.AreEqual(Path.Combine(home, ".config", "pcl-ce"), LinuxPaths.ConfigHome);
            Assert.AreEqual(Path.Combine(home, ".local", "share", "pcl-ce"), LinuxPaths.DataHome);
            Assert.AreEqual(Path.Combine(home, ".cache", "pcl-ce"), LinuxPaths.CacheHome);
            Assert.AreEqual(Path.Combine(home, ".local", "state", "pcl-ce"), LinuxPaths.StateHome);
            // XDG_RUNTIME_DIR 缺失时运行时目录退回到缓存目录内，绝不使用系统临时目录。
            StringAssert.StartsWith(LinuxPaths.RuntimeHome, LinuxPaths.CacheHome);
            Assert.AreEqual(Path.Combine(home, ".local", "share", "pcl-ce", "runtime"), LinuxPaths.JavaRuntimeHome);
        }
        // 相对路径不符合 XDG 规范，必须被忽略并退回默认值。
        using var relative = new EnvironmentOverride(
            ("HOME", home),
            ("XDG_CONFIG_HOME", "relative/config"),
            ("XDG_DATA_HOME", "relative/data"));
        Assert.AreEqual(Path.Combine(home, ".config", "pcl-ce"), LinuxPaths.ConfigHome);
        Assert.AreEqual(Path.Combine(home, ".local", "share", "pcl-ce"), LinuxPaths.DataHome);
    }

    [TestMethod]
    public void RuntimeHomeFallsBackToCacheDirectoryWhenRuntimeDirIsMissing()
    {
        var home = Path.Combine(_root, "home");
        using (AllXdgUnset(home))
        {
            var runtime = LinuxPaths.RuntimeHome;
            // 必须落在 XDG 缓存目录内，绝不直接使用系统临时目录。
            StringAssert.StartsWith(runtime, LinuxPaths.CacheHome);
            Assert.IsTrue(LinuxPaths.IsSameOrChild(runtime, LinuxPaths.CacheHome));
        }
        // XDG_RUNTIME_DIR 指向不存在的目录时同样退回缓存目录。
        using var missing = new EnvironmentOverride(
            ("HOME", home),
            ("XDG_CACHE_HOME", null),
            ("XDG_RUNTIME_DIR", Path.Combine(_root, "does-not-exist")));
        StringAssert.StartsWith(LinuxPaths.RuntimeHome, LinuxPaths.CacheHome);
    }

    [TestMethod]
    public void ManagedPathAndSameOrChildHandleTraversalAndSiblingPrefix()
    {
        using var env = AllXdgSet(Path.Combine(_root, "home"));
        foreach (var root in LinuxPaths.ManagedRoots)
        {
            Assert.IsFalse(LinuxPaths.IsManagedPath(root), "应用数据根目录本身不应被视为可清理项：" + root);
            Assert.IsTrue(LinuxPaths.IsManagedPath(Path.Combine(root, "child")));
            Assert.IsTrue(LinuxPaths.IsManagedPath(Path.Combine(root, "child", "deeper", "file.txt")));
            Assert.IsFalse(LinuxPaths.IsManagedPath(Path.Combine(root, "..", "escape")));
            Assert.IsFalse(LinuxPaths.IsManagedPath(root + "-other"), "同名前缀的兄弟目录必须被拒绝：" + root);
            Assert.IsFalse(LinuxPaths.IsManagedPath(root + "/../" + Path.GetFileName(root) + "-other"));
            Assert.IsTrue(LinuxPaths.IsSameOrChild(root, root));
            Assert.IsTrue(LinuxPaths.IsSameOrChild(Path.Combine(root, "a", "b"), root));
            Assert.IsFalse(LinuxPaths.IsSameOrChild(root + "x", root));
            Assert.IsFalse(LinuxPaths.IsSameOrChild(root + "/../y", root));
        }
        Assert.IsFalse(LinuxPaths.IsManagedPath("/tmp/pcl-ce/natives"));
        Assert.IsFalse(LinuxPaths.IsManagedPath("/"));
    }

    [TestMethod]
    public void EnsureDirectoriesCreatesPrivateXdgDirectories()
    {
        using var env = AllXdgSet(Path.Combine(_root, "home"));
        LinuxPaths.EnsureDirectories();
        foreach (var directory in new[]
                 {
                     LinuxPaths.ConfigHome, LinuxPaths.DataHome, LinuxPaths.CacheHome, LinuxPaths.StateHome,
                     LinuxPaths.RuntimeHome, LinuxPaths.JavaRuntimeHome, LinuxPaths.ToolsHome,
                     LinuxPaths.LogsHome, LinuxPaths.TempHome
                 })
        {
            Assert.IsTrue(Directory.Exists(directory), "目录未创建：" + directory);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory), "目录权限必须为 0700：" + directory);
        }
    }

    [TestMethod]
    public void GameDirectoryMarkerIdentifiesManagedDirectories()
    {
        var game = Path.Combine(_root, "game");
        Assert.IsFalse(LinuxPaths.IsManagedGameDirectory(game));
        Assert.IsTrue(LinuxPaths.EnsureGameDirectoryMarker(game));
        Assert.IsTrue(LinuxPaths.IsManagedGameDirectory(game));
        Assert.IsTrue(File.Exists(Path.Combine(game, LinuxPaths.ManagedMarkerName)));
        Assert.IsTrue(LinuxPaths.EnsureGameDirectoryMarker(game));
        Assert.IsFalse(LinuxPaths.IsManagedGameDirectory(Path.Combine(_root, "not-game")));
    }

    [TestMethod]
    public void MigrateLegacyDataMovesJavaRuntimeIntoXdgDataHome()
    {
        using var env = new EnvironmentOverride(
            ("HOME", Path.Combine(_root, "home")),
            ("XDG_DATA_HOME", Path.Combine(_root, "data")),
            ("XDG_CACHE_HOME", Path.Combine(_root, "cache")));
        var game = Path.Combine(_root, "game");
        var legacyJava = Path.Combine(game, ".pcl-java", "bin", "java");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyJava)!);
        File.WriteAllText(legacyJava, "#!/bin/sh\n");
        var legacyNatives = Path.Combine(game, ".pcl-natives");
        Directory.CreateDirectory(legacyNatives);
        File.WriteAllText(Path.Combine(legacyNatives, "old.so"), "old");

        var log = LinuxPaths.MigrateLegacyData(game);

        Assert.IsFalse(Directory.Exists(Path.Combine(game, ".pcl-java")));
        Assert.IsFalse(Directory.Exists(legacyNatives));
        var moved = Path.Combine(LinuxPaths.JavaRuntimeHome, "bin", "java");
        Assert.IsTrue(File.Exists(moved));
        Assert.AreEqual("#!/bin/sh\n", File.ReadAllText(moved));
        Assert.IsTrue(LinuxPaths.IsSameOrChild(LinuxPaths.JavaRuntimeHome, LinuxPaths.DataHome));
        Assert.IsTrue(log.Count >= 1);
    }

    [TestMethod]
    public void MavenValidationUsesAppTempHomeWithoutCreatingIt()
    {
        using var env = new EnvironmentOverride(
            ("HOME", Path.Combine(_root, "home")),
            ("XDG_CACHE_HOME", Path.Combine(_root, "cache")));
        Assert.AreEqual("org/test/artifact/1.0/artifact-1.0.jar", PortableMaven.PathOf("org.test:artifact:1.0"));
        Assert.Throws<InvalidDataException>(() => PortableMaven.PathOf("group:../../bad:1"));
        // 仅用于校验的临时基路径不应被创建。
        Assert.IsFalse(Directory.Exists(LinuxPaths.TempHome));
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetTempPath(), "pcl-ce")));
    }

    [TestMethod]
    public void LauncherDefaultsUseXdgCacheAndLogs()
    {
        using var env = new EnvironmentOverride(
            ("HOME", Path.Combine(_root, "home")),
            ("XDG_CONFIG_HOME", Path.Combine(_root, "config")),
            ("XDG_DATA_HOME", Path.Combine(_root, "data")),
            ("XDG_CACHE_HOME", Path.Combine(_root, "cache")),
            ("XDG_STATE_HOME", Path.Combine(_root, "state")));
        using var client = new HttpClient();
        var launcher = new LinuxGameLauncher(Path.Combine(_root, "game"), client);
        Assert.AreEqual(LinuxPaths.NativesCache, launcher.CacheDirectory);
        Assert.AreEqual(LinuxPaths.LogsHome, launcher.LogDirectory);
        Assert.IsTrue(LinuxPaths.IsSameOrChild(launcher.CacheDirectory, Path.Combine(_root, "cache", "pcl-ce")));
        Assert.IsTrue(LinuxPaths.IsSameOrChild(launcher.LogDirectory, Path.Combine(_root, "state", "pcl-ce")));
        Assert.IsFalse(launcher.CacheDirectory.StartsWith(Path.GetTempPath() + "pcl-ce", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LauncherWritesNativesAndLogsOutsideGameDirectory()
    {
        var game = Path.Combine(_root, "game");
        LocalProfile(game);
        var cache = Path.Combine(_root, "custom-cache");
        var logs = Path.Combine(_root, "custom-logs");
        using var client = new HttpClient();
        var launcher = new LinuxGameLauncher(game, client, cache, logs);
        var prepared = await launcher.PrepareAsync("local", new("/bin/java", 1024, LaunchAccount.Offline("Test")), null, default);

        Assert.IsTrue(LinuxPaths.IsSameOrChild(prepared.NativesDirectory, cache));
        Assert.AreEqual(32, Path.GetFileName(prepared.NativesDirectory).Length);
        Assert.IsTrue(Path.GetFileName(prepared.NativesDirectory).All(Uri.IsHexDigit));
        Assert.IsTrue(Directory.Exists(prepared.NativesDirectory));
        Assert.IsTrue(LinuxPaths.IsSameOrChild(prepared.LogPath, logs));
        Assert.IsTrue(Regex.IsMatch(Path.GetFileName(prepared.LogPath),
            @"^game-local-\d{8}-\d{6}-[0-9a-f]{8}\.log$"), prepared.LogPath);
        Assert.IsFalse(Directory.Exists(Path.Combine(game, ".pcl-natives")));
        Assert.IsFalse(Directory.Exists(Path.Combine(game, "logs")));
    }

    [TestMethod]
    public async Task OldGameLogsArePrunedWhenPreparingLaunch()
    {
        var game = Path.Combine(_root, "game");
        LocalProfile(game);
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        var stale = DateTime.UtcNow.AddDays(-20);
        for (var index = 0; index < 40; index++)
        {
            var path = Path.Combine(logs, $"game-old-20200101-000000-{index:x8}.log");
            File.WriteAllText(path, "log");
            File.SetLastWriteTimeUtc(path, index < 10 ? stale : DateTime.UtcNow.AddMinutes(-index));
        }
        File.WriteAllText(Path.Combine(logs, "pcl-ce.log"), "keep me");

        using var client = new HttpClient();
        var launcher = new LinuxGameLauncher(game, client, Path.Combine(_root, "cache"), logs);
        await launcher.PrepareAsync("local", new("/bin/java", 1024, LaunchAccount.Offline("Test")), null, default);

        var remaining = Directory.GetFiles(logs, "game-*.log");
        Assert.AreEqual(30, remaining.Length);
        Assert.IsTrue(File.Exists(Path.Combine(logs, "pcl-ce.log")));
        Assert.IsTrue(remaining.All(path => File.GetLastWriteTimeUtc(path) > stale));
    }

    [TestMethod]
    public void AppLogWritesIntoStateHomeLogs()
    {
        using var env = new EnvironmentOverride(
            ("HOME", Path.Combine(_root, "home")),
            ("XDG_STATE_HOME", Path.Combine(_root, "state")),
            ("XDG_CACHE_HOME", Path.Combine(_root, "cache")));
        AppLog.Initialize();
        AppLog.Info("xdg-path-test-entry");
        Assert.IsNotNull(AppLog.LogPath);
        Assert.AreEqual(LinuxPaths.AppLogPath, AppLog.LogPath);
        Assert.IsTrue(LinuxPaths.IsSameOrChild(AppLog.LogPath!, LinuxPaths.LogsHome));
        Assert.IsTrue(LinuxPaths.IsSameOrChild(LinuxPaths.LogsHome, Path.Combine(_root, "state")));
        Assert.IsTrue(File.ReadAllText(AppLog.LogPath!).Contains("xdg-path-test-entry", StringComparison.Ordinal));
    }

    private static void LocalProfile(string gameDirectory, string id = "local")
    {
        var resolver = new VersionResolver(gameDirectory);
        var path = resolver.MetadataPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string template = """{"id":"__ID__","mainClass":"Main","libraries":[],"arguments":{"jvm":[],"game":[]}}""";
        File.WriteAllText(path, template.Replace("__ID__", id, StringComparison.Ordinal));
        File.WriteAllText(resolver.JarPath(id), "jar");
    }
}
