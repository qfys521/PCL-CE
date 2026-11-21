using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Linux;

/// <summary>
/// 启动器设置，位于 <c>$XDG_CONFIG_HOME/pcl-ce/linux.json</c>（权限 0600）。
/// 账号与令牌单独存放在 <c>accounts.json</c>，便于“清空登录状态”时精确删除。
/// </summary>
public sealed class LinuxSettings
{
    /// <summary>游戏目录，默认为 ~/PCL-CE，可在设置中修改。</summary>
    public string GameDirectory { get; set; } = PCL.Portable.LinuxPaths.DefaultGameDirectory;
    /// <summary>全局 Java 路径；留空表示自动检测。</summary>
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 2048;
    public string OfflineName { get; set; } = "Player";
    public string? SelectedVersion { get; set; }
    public string MicrosoftClientId { get; set; } = Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID") ?? "";
    [JsonIgnore] public McProfile? MicrosoftAccount { get; set; }
    public bool UseMicrosoftAccount { get; set; }
    public bool UseAuthlibAccount { get; set; }
    public string AuthlibServer { get; set; } = "";
    public string AuthlibUsername { get; set; } = "";
    [JsonIgnore] public McProfile? AuthlibAccount { get; set; }
    public string JvmArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    public string CurseForgeKey { get; set; } = Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY") ?? "";
    public string HomepagePath { get; set; } = "";
    public string BackgroundPath { get; set; } = "";
    public string AccentColor { get; set; } = "#147AD6";
    public string Theme { get; set; } = "system";
    /// <summary>PCL 配色主题：CatBlue（默认猫蓝）/ SkyBlue（天蓝）/ DeathBlue（崩蓝）/ HmclBlue。</summary>
    public string ThemeColor { get; set; } = "CatBlue";
    /// <summary>标题栏 Logo 类型：0 无、1 PCL 手绘 Logo（默认）、2 文本、3 图片（未实现时回退默认）、4 HMCL。</summary>
    public int TitleLogoType { get; set; } = 1;
    public string TitleLogoText { get; set; } = "Point Cloud Library";
    /// <summary>使用原版彩色渐变背景（关闭则为纯色背景）。</summary>
    public bool ColorfulBackground { get; set; } = true;
    /// <summary>使用系统窗口装饰而非原版自绘标题栏。</summary>
    public bool UseSystemTitlebar { get; set; }
    public bool EnableAnimations { get; set; } = true;
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");
    public string RelayNodes { get; set; } = "tcp://public.easytier.top:11010;tcp://public2.easytier.cn:54321";
    /// <summary>启动时按需安装 ~/.local/share/applications 桌面入口、图标、MIME 与 pcl-ce:// 协议。</summary>
    public bool EnableDesktopIntegration { get; set; } = true;
    /// <summary>使用 org.freedesktop.Notifications 发送系统通知（不使用自制弹窗）。</summary>
    public bool EnableNotifications { get; set; } = true;
    /// <summary>文件选择器优先使用 XDG Desktop Portal，失败时回退 GTK 原生对话框（由 Avalonia 处理）。</summary>
    public bool EnablePortalFilePicker { get; set; } = true;
    /// <summary>界面字体；“”表示跟随系统，内置 Noto Sans CJK 作为中文兜底。</summary>
    public string InterfaceFont { get; set; } = "";
    /// <summary>下载源：auto 表示官方优先、失败时回退到公开镜像。</summary>
    public string DownloadSource { get; set; } = "auto";
    public double WindowWidth { get; set; } = 900;
    public double WindowHeight { get; set; } = 550;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool WindowMaximized { get; set; }

    private static string SettingsDirectory => PCL.Portable.LinuxPaths.ConfigHome;
    private static string SettingsPath => Path.Combine(SettingsDirectory, "linux.json");

    public static LinuxSettings Load()
    {
        LinuxSettings settings;
        if (File.Exists(SettingsPath))
        {
            settings = JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        else
        {
            settings = new LinuxSettings();
            // 兼容 0.2.x 预览：旧版默认游戏目录在 XDG 数据目录内。
            var legacy = Path.Combine(PCL.Portable.LinuxPaths.DataHome, "minecraft");
            if (Directory.Exists(legacy) && !Directory.Exists(settings.GameDirectory)) settings.GameDirectory = legacy;
        }
        var accounts = LinuxAccounts.Load();
        settings.MicrosoftAccount = accounts.Microsoft;
        settings.AuthlibAccount = accounts.Authlib;
        if (string.IsNullOrWhiteSpace(settings.GameDirectory))
            settings.GameDirectory = PCL.Portable.LinuxPaths.DefaultGameDirectory;
        try { settings.GameDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.GameDirectory)); }
        catch (Exception) { settings.GameDirectory = PCL.Portable.LinuxPaths.DefaultGameDirectory; }
        return settings;
    }

    public void Save()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此设置存储仅支持 Linux。");
        WritePrivateJson(SettingsPath, this);
        LinuxAccounts.Save(MicrosoftAccount, AuthlibAccount);
    }

    /// <summary>以 0600 权限原子写入 JSON（先写同目录临时文件再改名）。</summary>
    internal static void WritePrivateJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        PCL.Portable.LinuxPaths.EnsureDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew, Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   }))
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>账号令牌存储（<c>$XDG_CONFIG_HOME/pcl-ce/accounts.json</c>，权限 0600）。</summary>
public sealed class LinuxAccounts
{
    public McProfile? Microsoft { get; set; }
    public McProfile? Authlib { get; set; }

    private static string Path2 => Path.Combine(PCL.Portable.LinuxPaths.ConfigHome, "accounts.json");

    public static LinuxAccounts Load()
    {
        try
        {
            return File.Exists(Path2)
                ? JsonSerializer.Deserialize<LinuxAccounts>(File.ReadAllText(Path2)) ?? new()
                : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    public static void Save(McProfile? microsoft, McProfile? authlib)
    {
        if (microsoft is null && authlib is null)
        {
            try { if (File.Exists(Path2)) File.Delete(Path2); }
            catch (Exception) { }
            return;
        }
        LinuxSettings.WritePrivateJson(Path2, new LinuxAccounts { Microsoft = microsoft, Authlib = authlib });
    }

    public static bool Exists => File.Exists(Path2);
    public static string FilePath => Path2;
}
