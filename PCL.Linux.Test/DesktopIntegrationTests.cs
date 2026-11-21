using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Portable;

namespace PCL.Linux.Test;

/// <summary>
/// 桌面集成只操作 XDG 用户目录：每个测试把 HOME / XDG_* 指向临时目录，结束后恢复，
/// 因此不会触碰真实桌面环境，也不依赖 D-Bus。
/// </summary>
[TestClass]
public sealed class DesktopIntegrationTests
{
    private static readonly string[] Variables =
    [
        "HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME",
        "XDG_RUNTIME_DIR", "APPIMAGE", "APPDIR"
    ];

    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512];

    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        foreach (var name in Variables) _saved[name] = Environment.GetEnvironmentVariable(name);
        _root = Directory.CreateTempSubdirectory("pcl desktop integration 中文 ").FullName;
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(_root, "state"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_root, "cache"));
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(_root, "run"));
        Environment.SetEnvironmentVariable("APPIMAGE", null);
        Environment.SetEnvironmentVariable("APPDIR", null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var pair in _saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (Exception) { }
    }

    [TestMethod]
    public async Task InstallWritesDesktopMimeIconsAndState()
    {
        var status = await DesktopIntegration.EnsureInstalledAsync(false, null, CancellationToken.None);
        Assert.IsTrue(status.Installed, status.Problem);
        Assert.IsNull(status.Problem);
        Assert.AreEqual(Path.Combine(LinuxPaths.UserApplications, "pcl-ce.desktop"), status.DesktopFile);
        Assert.IsTrue(File.Exists(status.DesktopFile));
        Assert.IsTrue(File.Exists(LinuxPaths.DesktopIntegrationStatePath));

        var exec = DesktopIntegration.CurrentExecutablePath ?? Path.GetFullPath(Environment.ProcessPath!);
        var desktop = File.ReadAllText(status.DesktopFile);
        StringAssert.Contains(desktop, "Name=PCL-CE-Linux");
        StringAssert.Contains(desktop, "GenericName=Minecraft Launcher");
        StringAssert.Contains(desktop, "Icon=pcl-ce");
        StringAssert.Contains(desktop, "Terminal=false");
        StringAssert.Contains(desktop, "Categories=Game;Utility;");
        StringAssert.Contains(desktop, "StartupWMClass=PCL-CE");
        StringAssert.Contains(desktop, "MimeType=application/x-mrpack;application/x-minecraft-package;x-scheme-handler/pcl-ce;");
        StringAssert.Contains(desktop, "Exec=\"" + EscapeExec(exec) + "\" %U");
        ValidateDesktopFile(status.DesktopFile);
        if (OperatingSystem.IsLinux())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(status.DesktopFile));

        var mime = File.ReadAllText(Path.Combine(LinuxPaths.UserMime, "packages", "pcl-ce.xml"));
        StringAssert.Contains(mime, "application/x-mrpack");
        StringAssert.Contains(mime, "*.mrpack");
        StringAssert.Contains(mime, "application/x-minecraft-package");
        StringAssert.Contains(mime, "*.mcworld");
        StringAssert.Contains(mime, "*.mcpack");
        StringAssert.Contains(mime, "*.mctemplate");
        StringAssert.Contains(mime, "*.mcaddon");

        foreach (var size in IconSizes)
        {
            var icon = Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}", "apps", "pcl-ce.png");
            Assert.IsTrue(File.Exists(icon), "缺少图标：" + icon);
            Assert.IsTrue(new FileInfo(icon).Length > 0);
        }

        var state = JsonNode.Parse(File.ReadAllText(LinuxPaths.DesktopIntegrationStatePath))!;
        Assert.AreEqual(exec, state["execPath"]!.ToString());
        Assert.IsNotNull(state["installedAt"]);
        Assert.IsNotNull(state["version"]);
        if (OperatingSystem.IsLinux())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(LinuxPaths.DesktopIntegrationStatePath));

        var inspected = DesktopIntegration.Inspect();
        Assert.IsTrue(inspected.Installed);
        Assert.IsNull(inspected.Problem, inspected.Problem);
        Assert.AreEqual(exec, inspected.ExecPath);

        // 幂等：未变化时再次安装不应重写状态文件。
        var installedAt = File.GetLastWriteTimeUtc(LinuxPaths.DesktopIntegrationStatePath);
        await Task.Delay(30);
        var second = await DesktopIntegration.EnsureInstalledAsync(false, null, CancellationToken.None);
        Assert.IsTrue(second.Installed);
        Assert.IsNull(second.Problem, second.Problem);
        Assert.AreEqual(installedAt, File.GetLastWriteTimeUtc(LinuxPaths.DesktopIntegrationStatePath));
    }

    [TestMethod]
    public async Task ChangedExecutablePathReinstalls()
    {
        var first = Path.Combine(_root, "PCL CE $HOME `bin` \"quoted\" back\\slash", "pcl-ce.AppImage");
        Environment.SetEnvironmentVariable("APPIMAGE", first);
        var initial = await DesktopIntegration.EnsureInstalledAsync(false, null, CancellationToken.None);
        Assert.IsTrue(initial.Installed, initial.Problem);
        Assert.IsNull(initial.Problem, initial.Problem);
        StringAssert.Contains(File.ReadAllText(initial.DesktopFile), "Exec=\"" + EscapeExec(first) + "\" %U");

        var second = Path.Combine(_root, "另一个 位置", "pcl-ce.AppImage");
        Environment.SetEnvironmentVariable("APPIMAGE", second);
        var stale = DesktopIntegration.Inspect();
        Assert.IsTrue(stale.Installed);
        Assert.IsNotNull(stale.Problem);

        var updated = await DesktopIntegration.EnsureInstalledAsync(false, null, CancellationToken.None);
        Assert.IsTrue(updated.Installed, updated.Problem);
        Assert.IsNull(updated.Problem, updated.Problem);
        StringAssert.Contains(File.ReadAllText(updated.DesktopFile), "Exec=\"" + EscapeExec(second) + "\" %U");
        var state = JsonNode.Parse(File.ReadAllText(LinuxPaths.DesktopIntegrationStatePath))!;
        Assert.AreEqual(second, state["execPath"]!.ToString());
        Assert.AreEqual(second, state["appImagePath"]!.ToString());
        Assert.AreEqual(second, DesktopIntegration.Inspect().ExecPath);
        Assert.IsNull(DesktopIntegration.Inspect().Problem);
    }

    [TestMethod]
    public async Task UninstallRemovesOnlyItsOwnArtifacts()
    {
        var unrelatedDesktop = Path.Combine(LinuxPaths.UserApplications, "other-launcher.desktop");
        var unrelatedIcon = Path.Combine(LinuxPaths.UserIcons, "hicolor", "16x16", "apps", "other.png");
        var unrelatedMime = Path.Combine(LinuxPaths.UserMime, "packages", "other.xml");
        var unrelatedState = Path.Combine(LinuxPaths.StateHome, "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedDesktop)!);
        File.WriteAllText(unrelatedDesktop, "[Desktop Entry]\nType=Application\nName=Other\nExec=other\n");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedIcon)!);
        File.WriteAllBytes(unrelatedIcon, [0x89, 0x50, 0x4E, 0x47]);
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedMime)!);
        File.WriteAllText(unrelatedMime, "<mime-info/>");
        Directory.CreateDirectory(LinuxPaths.StateHome);
        File.WriteAllText(unrelatedState, "keep");

        var installed = await DesktopIntegration.EnsureInstalledAsync(false, null, CancellationToken.None);
        Assert.IsNull(installed.Problem, installed.Problem);
        Assert.IsTrue(await DesktopIntegration.UninstallAsync());

        Assert.IsFalse(File.Exists(installed.DesktopFile));
        Assert.IsFalse(File.Exists(Path.Combine(LinuxPaths.UserMime, "packages", "pcl-ce.xml")));
        Assert.IsFalse(File.Exists(LinuxPaths.DesktopIntegrationStatePath));
        foreach (var size in IconSizes)
            Assert.IsFalse(File.Exists(Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}", "apps", "pcl-ce.png")));
        Assert.IsFalse(Directory.Exists(Path.Combine(LinuxPaths.UserIcons, "hicolor", "512x512")));

        Assert.IsTrue(File.Exists(unrelatedDesktop));
        Assert.IsTrue(File.Exists(unrelatedIcon));
        Assert.IsTrue(File.Exists(unrelatedMime));
        Assert.IsTrue(File.Exists(unrelatedState));
        Assert.AreEqual("[Desktop Entry]\nType=Application\nName=Other\nExec=other\n", File.ReadAllText(unrelatedDesktop));
        Assert.IsFalse(DesktopIntegration.Inspect().Installed);
    }

    [TestMethod]
    public async Task AppImageBundledIconsTakePriorityOverFallbackAsset()
    {
        var fallback = Path.Combine(AppContext.BaseDirectory, "Images", "Heads", "Logo-CE.png");
        Assert.IsTrue(File.Exists(fallback), "测试输出缺少内置图标素材：" + fallback);
        var appDir = Path.Combine(_root, "squashfs-root");
        Environment.SetEnvironmentVariable("APPDIR", appDir);
        Environment.SetEnvironmentVariable("APPIMAGE", Path.Combine(_root, "pcl-ce.AppImage"));
        var bundled = Path.Combine(appDir, "usr", "share", "icons", "hicolor", "48x48", "apps", "pcl-ce.png");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        byte[] bundledBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
        File.WriteAllBytes(bundled, bundledBytes);

        var status = await DesktopIntegration.EnsureInstalledAsync(false, null, CancellationToken.None);
        Assert.IsNull(status.Problem, status.Problem);
        CollectionAssert.AreEqual(bundledBytes,
            File.ReadAllBytes(Path.Combine(LinuxPaths.UserIcons, "hicolor", "48x48", "apps", "pcl-ce.png")));
        CollectionAssert.AreEqual(File.ReadAllBytes(fallback),
            File.ReadAllBytes(Path.Combine(LinuxPaths.UserIcons, "hicolor", "16x16", "apps", "pcl-ce.png")));
        foreach (var size in IconSizes)
            Assert.IsTrue(File.Exists(Path.Combine(LinuxPaths.UserIcons, "hicolor", $"{size}x{size}", "apps", "pcl-ce.png")));
    }

    [TestMethod]
    public async Task FailedInstallationReturnsProblemWithoutThrowing()
    {
        // XDG_DATA_HOME 指向一个普通文件，安装无法创建目录：必须返回 Problem 而不是抛异常。
        var blocked = Path.Combine(_root, "blocked-data");
        File.WriteAllText(blocked, "not a directory");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", blocked);
        var status = await DesktopIntegration.EnsureInstalledAsync(true, null, CancellationToken.None);
        Assert.IsFalse(status.Installed);
        Assert.IsNotNull(status.Problem);
        Assert.AreEqual(Path.Combine(blocked, "applications", "pcl-ce.desktop"), status.DesktopFile);
    }

    private static string EscapeExec(string path) => string.Concat(path.Select(character => character switch
    {
        '\\' => "\\\\",
        '"' => "\\\"",
        '`' => "\\`",
        '$' => "\\$",
        '%' => "%%",
        _ => character.ToString()
    }));

    private static void ValidateDesktopFile(string path)
    {
        var executable = FindInPath("desktop-file-validate");
        if (executable is null) return; // 未安装该工具时跳过（测试环境不强制依赖）
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(10_000), "desktop-file-validate 超时");
        Assert.AreEqual(0, process.ExitCode, output + error);
    }

    private static string? FindInPath(string name)
    {
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
}
